using System.IO.Compression;
using System.Net;
using System.Text;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// One package, several builds of the web app at the same URLs: which one a request gets is the app's selector's decision,
/// and every asset has to come from the build its page came from.
/// </summary>
public class VariantTests
{
    const string PhoneAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) Mobile/15E148";
    const string DesktopAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_0) Safari/605.1.15";

    /// <summary>The app's rule: a cookie wins, then the client hint, then the user agent.</summary>
    static string? Select(HttpContext context)
    {
        if (context.Request.Cookies["view"] is { Length: > 0 } view)
            return view;

        if (context.Request.Headers["Sec-CH-UA-Mobile"].ToString() == "?1")
            return "mobile";

        return context.Request.Headers["User-Agent"].ToString().Contains("Mobile", StringComparison.Ordinal) ? "mobile" : "desktop";
    }

    [Fact]
    public async Task EveryRequestFromOneBrowserGetsTheSameBuild()
    {
        await using var fixture = await Fixture.StartAsync(o => o.SelectVariant = Select);

        foreach (var (agent, cookie, expected) in new[]
        {
            (PhoneAgent, (string?)null, "mobile"),
            (DesktopAgent, null, "desktop"),
            (PhoneAgent, "desktop", "desktop"),
            (DesktopAgent, "mobile", "mobile")
        })
        {
            foreach (var path in new[] { "/", "/index.html", "/deep/link", "/app.js", "/_framework/dotnet.js" })
            {
                using var response = await fixture.GetAsync(path, agent, cookie);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Contains($"{expected} build", await response.Content.ReadAsStringAsync());
            }
        }

        using var hinted = await fixture.GetAsync("/_framework/dotnet.js", DesktopAgent, hint: "?1");
        Assert.Contains("mobile build", await hinted.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NamesWhatTheChoiceDependsOn()
    {
        await using var fixture = await Fixture.StartAsync(o => o.SelectVariant = Select);

        using var document = await fixture.GetAsync("/", DesktopAgent);
        Assert.Equal(["User-Agent", "Sec-CH-UA-Mobile", "Cookie"], document.Headers.Vary);
        Assert.Equal("Sec-CH-UA-Mobile", Assert.Single(document.Headers.GetValues("Accept-CH")));
        Assert.True(document.Headers.CacheControl?.NoCache);

        using var asset = await fixture.GetAsync("/app.js", DesktopAgent);
        Assert.Equal(["User-Agent", "Sec-CH-UA-Mobile", "Cookie"], asset.Headers.Vary);
        Assert.False(asset.Headers.Contains("Accept-CH"));
    }

    [Theory]
    [InlineData("tablet")]
    [InlineData(null)]
    [InlineData("throw")]
    public async Task AnUnusableChoiceServesTheDefault(string? answer)
    {
        await using var fixture = await Fixture.StartAsync(o => o.SelectVariant = _ => answer == "throw"
            ? throw new InvalidOperationException("selector bug")
            : answer);

        using var response = await fixture.GetAsync("/app.js", DesktopAgent);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("mobile build", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WithoutASelectorEveryoneGetsTheFirst()
    {
        await using var fixture = await Fixture.StartAsync();

        using var response = await fixture.GetAsync("/", DesktopAgent);
        Assert.Contains("mobile build", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RefusesADownloadMissingAVariantsEntryDocument()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", Zip(("mobile", true), ("desktop", false)));
        await app.StartReleaseServerAsync();

        var options = app.Options();
        options.Variants("mobile", "desktop");
        await using var host = app.CreateHost(options);

        var check = await host.Updater.CheckAsync(null);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => host.Updater.InstallAsync(check));
        Assert.Contains("desktop", error.Message);
    }

    [Fact]
    public async Task FindsEachVariantUnderItsOwnWwwroot()
    {
        await using var fixture = await Fixture.StartAsync(o => o.SelectVariant = Select, zip: Zip([("mobile", true), ("desktop", true)], wwwroot: true));

        using var response = await fixture.GetAsync("/_framework/dotnet.js", DesktopAgent);
        Assert.Contains("desktop build", await response.Content.ReadAsStringAsync());
    }

    /// <summary>Under a mount point the entry document is rewritten, and it has to be the chosen build's document.</summary>
    [Fact]
    public async Task RewritesTheChosenBuildsDocumentUnderAMountPoint()
    {
        await using var fixture = await Fixture.StartAsync(o => o.SelectVariant = Select, basePath: "/kiosk");

        using var desktop = await fixture.GetAsync("/kiosk/deep/link", DesktopAgent);
        var html = await desktop.Content.ReadAsStringAsync();
        Assert.Contains("desktop build", html);
        Assert.Contains("""<base href="/kiosk/" />""", html);
        Assert.Equal(["User-Agent", "Sec-CH-UA-Mobile", "Cookie"], desktop.Headers.Vary);

        using var phone = await fixture.GetAsync("/kiosk/", PhoneAgent);
        Assert.Contains("mobile build", await phone.Content.ReadAsStringAsync());
    }

    /// <summary>The single-build setup is untouched: no Vary, nothing to choose.</summary>
    [Fact]
    public async Task WithoutVariantsNothingVaries()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync();

        var options = app.Options();
        options.ServeWebAppLocally = true;
        await using var host = app.CreateHost(options);
        await host.StartAsync();

        using var client = new HttpClient();
        using var response = await client.GetAsync(host.Origin!);

        Assert.Contains("version 1.0.0", await response.Content.ReadAsStringAsync());
        Assert.Empty(response.Headers.Vary);
        Assert.False(response.Headers.Contains("Accept-CH"));
    }

    [Fact]
    public void RefusesVariantOptionsThatCannotWork()
    {
        Assert.Throws<InvalidOperationException>(() => Validate(o => o.SelectVariant = _ => "mobile"));
        Assert.Throws<InvalidOperationException>(() => Validate(o => o.Variants("mobile", "mobile")));
        Assert.Throws<InvalidOperationException>(() => Validate(o => o.Variants("mobile", "../desktop")));
        Assert.Throws<InvalidOperationException>(() => Validate(o => o.Variants("")));

        static void Validate(Action<WebAppHostOptions> configure)
        {
            var options = new WebAppHostOptions { DevServer = new Uri("http://localhost:5000") };
            configure(options);
            options.Validate();
        }
    }

    /// <summary><c>mobile/</c> and <c>desktop/</c>, each with its own index, app.js and _framework/dotnet.js naming its build.</summary>
    static byte[] Zip(params (string Variant, bool WithIndex)[] variants) => Zip(variants, wwwroot: false);

    static byte[] Zip((string Variant, bool WithIndex)[] variants, bool wwwroot)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (variant, withIndex) in variants)
            {
                var prefix = wwwroot ? $"{variant}/wwwroot/" : $"{variant}/";

                if (withIndex)
                    Add(zip, prefix + "index.html", $"<h1>{variant} build</h1>");

                Add(zip, prefix + "app.js", $"// {variant} build");
                Add(zip, prefix + "_framework/dotnet.js", $"// {variant} build");
            }
        }

        return buffer.ToArray();

        static void Add(ZipArchive zip, string name, string content)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }
    }

    sealed class Fixture : IAsyncDisposable
    {
        TestApp app = null!;
        WebAppHost host = null!;

        public static async Task<Fixture> StartAsync(Action<WebAppHostOptions>? configure = null, byte[]? zip = null, string? basePath = null)
        {
            var fixture = new Fixture { app = new TestApp() };
            fixture.app.Store.Add("1.0.0", zip ?? Zip(("mobile", true), ("desktop", true)));
            await fixture.app.StartReleaseServerAsync();

            var options = fixture.app.Options();
            options.Variants("mobile", "desktop");

            // A browser on the machine, which is who a selector is for; the WebView goes through the same path.
            options.ServeWebAppLocally = true;
            configure?.Invoke(options);

            var bridge = fixture.app.BridgeOptions();
            if (basePath is not null)
                bridge.BasePath = basePath;

            fixture.host = fixture.app.CreateHost(options, bridge, null, []);
            await fixture.host.StartAsync();
            return fixture;
        }

        public async Task<HttpResponseMessage> GetAsync(string path, string agent, string? cookie = null, string? hint = null)
        {
            using var client = new HttpClient(new HttpClientHandler { UseCookies = false });
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(this.host.Origin!, path));
            request.Headers.UserAgent.ParseAdd(agent);

            if (cookie is not null)
                request.Headers.Add("Cookie", $"view={cookie}");

            if (hint is not null)
                request.Headers.Add("Sec-CH-UA-Mobile", hint);

            var response = await client.SendAsync(request);
            await response.Content.LoadIntoBufferAsync();
            return response;
        }

        public async ValueTask DisposeAsync()
        {
            await this.host.DisposeAsync();
            await this.app.DisposeAsync();
        }
    }
}
