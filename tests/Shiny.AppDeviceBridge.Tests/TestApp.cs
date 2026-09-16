using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.AspNetCore;

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

    readonly List<AppDeviceBridgeServer> servers = [];

    /// <summary>Options pointing at the TestServer. Each client gets its own handler, since the updater disposes the one it is given.</summary>
    public WebAppHostOptions Options(Func<HttpMessageHandler>? handler = null) => new()
    {
        UpdateServer = new Uri("http://localhost/webapps"),
        PublicKey = this.PublicKey,
        InstallDirectory = this.InstallDirectory,
        HttpMessageHandlerFactory = handler ?? (() => this.server!.GetTestServer().CreateHandler())
    };

    /// <summary>
    /// Bridge server options for a release build: any port, data beside the installs, and debug's any-caller default off
    /// so the tests see the rules an app ships with.
    /// </summary>
    public AppDeviceBridgeOptions BridgeOptions(Action<AppDeviceBridgeOptions>? configure = null)
    {
        var options = new AppDeviceBridgeOptions
        {
            AppId = AppId,
            DataDirectory = this.InstallDirectory,
            IsDebug = false
        };
        options.Server.Port = 0;
        configure?.Invoke(options);
        return options;
    }

    public WebAppHost CreateHost(WebAppHostOptions? options = null, params IWebAppBridge[] bridges)
        => this.CreateHost(options, null, null, bridges);

    /// <summary>A server with the host as its extension, wired the way <c>AddWebAppHost</c> wires them. Disposed with the app.</summary>
    public WebAppHost CreateHost(
        WebAppHostOptions? options,
        AppDeviceBridgeOptions? bridgeOptions,
        WebAppEventHub? events,
        IEnumerable<IWebAppBridge> bridges,
        IServiceProvider? services = null,
        WebAppSession? session = null
    )
    {
        WebAppHost? host = null;
        var server = new AppDeviceBridgeServer(
            bridgeOptions ?? this.BridgeOptions(),
            bridges,
            events ?? new WebAppEventHub(),
            () => [host!],
            services
        );

        lock (this.servers)
            this.servers.Add(server);

        host = new WebAppHost(options ?? this.Options(), server, session ?? new WebAppSession());
        return host;
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
        foreach (var bridgeServer in this.servers)
            await bridgeServer.DisposeAsync();

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
