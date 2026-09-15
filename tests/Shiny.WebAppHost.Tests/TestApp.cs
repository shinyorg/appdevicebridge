using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shiny.WebAppHost.AspNetCore;

namespace Shiny.WebAppHost.Tests;

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

    public string InstallDirectory { get; } = Path.Combine(Path.GetTempPath(), "webapphost-tests", Guid.NewGuid().ToString("n"));

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

    /// <summary>Options pointing at the TestServer. Each client gets its own handler, since the updater disposes the one it is given.</summary>
    public WebAppHostOptions Options(Func<HttpMessageHandler>? handler = null) => new()
    {
        AppId = AppId,
        UpdateServer = new Uri("http://localhost/webapps"),
        PublicKey = this.PublicKey,
        InstallDirectory = this.InstallDirectory,
        Port = 0,
        HttpMessageHandlerFactory = handler ?? (() => this.server!.GetTestServer().CreateHandler())
    };

    public WebAppHost CreateHost(WebAppHostOptions? options = null, params IWebAppBridge[] bridges)
        => new(options ?? this.Options(), new WebAppSession(), new WebAppEventHub(), bridges);

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
