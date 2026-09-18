using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.AspNetCore;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>A release server on TestServer, a key pair, and a scratch install directory.</summary>
sealed class TestApp : IAsyncDisposable
{
    public const string AppId = "demo";

    readonly string privateKey;
    WebApplication? server;

    public TestApp()
    {
        (this.PublicKey, this.privateKey) = WebAppReleaseSignature.CreateKeyPair();
        Directory.CreateDirectory(this.InstallDirectory);
    }

    public MemoryReleaseStore Store { get; } = new();

    public string PublicKey { get; }

    public string InstallDirectory { get; } = Path.Combine(Path.GetTempPath(), "appdevicebridge-tests", Guid.NewGuid().ToString("n"));

    public async Task StartReleaseServerAsync(string? signingKey = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IWebAppReleaseStore>(this.Store);
        builder.Services.AddWebAppReleases(o => o.SigningKey = signingKey ?? this.privateKey);

        this.server = builder.Build();
        this.server.MapWebAppReleases();
        await this.server.StartAsync();
    }

    readonly List<ServiceProvider> containers = [];

    /// <summary>Options pointing at the TestServer. Each client gets its own handler, since the updater disposes the one it is given.</summary>
    public WebAppHostOptions Options(Func<HttpMessageHandler>? handler = null) => new()
    {
        UpdateServer = new Uri("http://localhost/webapps"),
        PublicKey = this.PublicKey,
        InstallDirectory = this.InstallDirectory,
        HttpMessageHandlerFactory = handler ?? (() => this.server!.GetTestServer().CreateHandler())
    };

    /// <summary>
    /// Bridge options for a release build: data beside the installs, and debug's any-caller default off so the tests see the
    /// rules an app ships with.
    /// </summary>
    public AppDeviceBridgeOptions BridgeOptions(Action<AppDeviceBridgeOptions>? configure = null)
    {
        var options = new AppDeviceBridgeOptions
        {
            AppId = AppId,
            DataDirectory = this.InstallDirectory,
            IsDebug = false,

            // A page that leaves is noticed on the next write; tests should not wait the default for it.
            EventStreamHeartbeat = TimeSpan.FromMilliseconds(100)
        };
        configure?.Invoke(options);
        return options;
    }

    public WebAppHost CreateHost(WebAppHostOptions? options = null, params IWebAppBridge[] bridges)
        => this.CreateHost(options, null, null, bridges);

    /// <summary>
    /// The app's server with the bridges and the WebView host on it, registered through the builder exactly as an app
    /// registers them — except that the bridges are the ones given rather than every one in the container. Any port. Disposed
    /// with the app.
    /// </summary>
    public WebAppHost CreateHost(
        WebAppHostOptions? options,
        AppDeviceBridgeOptions? bridgeOptions,
        WebAppEventHub? events,
        IEnumerable<IWebAppBridge> bridges,
        Action<ShinyHttpServerBuilder>? http = null,
        WebAppSession? session = null
    )
    {
        var web = options ?? this.Options();
        var provider = this.Build(bridgeOptions, events, bridges, builder =>
        {
            builder.Services.AddSingleton(web);
            builder.Services.AddSingleton(session ?? new WebAppSession());
            builder.AddWebAppHost(_ => { });
            http?.Invoke(builder);
        });

        return provider.GetRequiredService<WebAppHost>();
    }

    /// <summary>The app's server with only the bridges on it: no WebView host. Any port. Disposed with the app.</summary>
    public AppDeviceBridgeServer CreateServer(AppDeviceBridgeOptions? bridgeOptions, IEnumerable<IWebAppBridge> bridges, Action<ShinyHttpServerBuilder>? http = null)
        => this.Build(bridgeOptions, null, bridges, http).GetRequiredService<AppDeviceBridgeServer>();

    ServiceProvider Build(AppDeviceBridgeOptions? bridgeOptions, WebAppEventHub? events, IEnumerable<IWebAppBridge> bridges, Action<ShinyHttpServerBuilder>? http)
    {
        var options = bridgeOptions ?? this.BridgeOptions();
        var hub = events ?? new WebAppEventHub();
        IReadOnlyList<IWebAppBridge> list = [.. bridges];

        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton(hub);

        // Registered before AddAppDeviceBridge, which keeps it: the server carries exactly these bridges.
        services.AddSingleton(sp => new AppDeviceBridgeServer(options, list, hub, sp));

        services.AddShinyHttpServer(
            builder =>
            {
                builder.Options.Port = 0;
                builder.AddAppDeviceBridge();
                http?.Invoke(builder);
            },
            autoStart: false
        );

        var provider = services.BuildServiceProvider();
        lock (this.containers)
            this.containers.Add(provider);

        return provider;
    }

    public string Sign(WebAppRelease release)
    {
        using var key = WebAppReleaseSignature.ImportPrivateKey(this.privateKey);
        return WebAppReleaseSignature.Sign(release, key);
    }

    public static byte[] Zip(string version, string? folder = null, bool withIndex = true, string? backgroundScript = null)
    {
        var prefix = folder is null ? String.Empty : folder + "/";

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (withIndex)
                Add(zip, prefix + "index.html", $"<h1>version {version}</h1>");

            Add(zip, prefix + "app.js", "console.log('hello');");

            if (backgroundScript is not null)
                Add(zip, prefix + "background.js", backgroundScript);
        }

        return buffer.ToArray();
    }

    static void Add(ZipArchive zip, string name, string content)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var container in this.containers)
            await container.DisposeAsync();

        if (this.server is not null)
            await this.server.DisposeAsync();

        try
        {
            Directory.Delete(this.InstallDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

sealed class MemoryReleaseStore : IWebAppReleaseStore
{
    readonly List<(WebAppReleaseEntry Entry, byte[] Zip)> releases = [];

    public WebAppPolicy? Policy { get; set; }

    public WebAppRelease Add(string version, byte[] zip, string? minimumHostVersion = null, string? sha256 = null)
    {
        var release = new WebAppRelease
        {
            AppId = TestApp.AppId,
            Version = version,
            Sha256 = sha256 ?? Convert.ToHexStringLower(SHA256.HashData(zip)),
            Size = zip.Length,
            MinimumHostVersion = minimumHostVersion
        };

        lock (this.releases)
            this.releases.Add((new WebAppReleaseEntry(release), zip));

        return release;
    }

    public Task<IReadOnlyList<WebAppReleaseEntry>?> GetReleasesAsync(string appId, CancellationToken cancellationToken)
    {
        if (appId != TestApp.AppId)
            return Task.FromResult<IReadOnlyList<WebAppReleaseEntry>?>(null);

        lock (this.releases)
            return Task.FromResult<IReadOnlyList<WebAppReleaseEntry>?>(this.releases.Select(x => x.Entry).ToList());
    }

    public Task<WebAppPolicy?> GetPolicyAsync(string appId, CancellationToken cancellationToken) => Task.FromResult(this.Policy);

    public Task<Stream?> OpenReleaseAsync(string appId, string version, CancellationToken cancellationToken)
    {
        lock (this.releases)
        {
            var match = this.releases.FirstOrDefault(x => x.Entry.Release.Version == version);
            return Task.FromResult<Stream?>(match.Zip is null ? null : new MemoryStream(match.Zip));
        }
    }
}

sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(respond(request));
}

sealed class EchoBridge : IWebAppBridge
{
    public string Name => "echo";

    public bool IsSupported => true;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("/ping", WebAppBridgeResults.NoContent)
        .MapGet("/boom", _ => throw new InvalidOperationException("native failure"));
}
