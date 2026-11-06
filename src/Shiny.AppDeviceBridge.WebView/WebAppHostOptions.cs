using System.Globalization;
using System.Reflection;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.StaticFiles;

namespace Shiny.AppDeviceBridge.WebView;

/// <summary>A web app zip compiled into the app, so there is always something to show — offline, on first launch.</summary>
/// <param name="Assembly">The assembly holding the resource.</param>
/// <param name="ResourceName">The resource's manifest name — its <c>LogicalName</c> when one is set.</param>
/// <param name="Version">The version of the web build inside it. Compared against installed downloads.</param>
public sealed record WebAppBaseline(Assembly Assembly, string ResourceName, string Version);

public sealed class WebAppHostOptions
{
    /// <summary>
    /// Where releases come from: <see cref="ReleaseServerUpdateProvider"/>, <see cref="GitHubReleasesUpdateProvider"/>, or
    /// an <see cref="IUpdateProvider"/> of the app's own. Null never checks, and only the baseline is served. The host owns
    /// it from here, and disposes it when it is disposable.
    /// </summary>
    public IUpdateProvider? UpdateProvider { get; set; }

    /// <summary>Where downloads are kept. Defaults to a folder named after the app id under local application data.</summary>
    public string? InstallDirectory { get; set; }

    public WebAppBaseline? Baseline { get; set; }

    /// <summary>How long the startup check may take before the installed version is shown anyway.</summary>
    public TimeSpan CheckTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The user's language, sent with every update check so release notes — the web app's and the store's — come back in it
    /// when the server has them. <see cref="CultureInfo.CurrentUICulture"/> by default.
    /// </summary>
    public Func<CultureInfo> Culture { get; set; } = () => CultureInfo.CurrentUICulture;

    /// <summary>
    /// Keep the splash screen up until the page says it has rendered — <c>POST /_bridge/host/ready</c>, which the Blazor
    /// client sends by itself once Blazor has started — rather than taking it down the moment the WebView is handed the URL.
    /// On by default; turn it off for a page that never calls it, and the splash comes down when the WebView finishes
    /// navigating.
    /// </summary>
    public bool WaitForPageReady { get; set; } = true;

    /// <summary>
    /// How long the splash screen waits for <see cref="WaitForPageReady"/>'s signal before showing the page anyway: a page
    /// that throws during startup, or one built before it knew to call, must not leave the user looking at a splash forever.
    /// </summary>
    public TimeSpan PageReadyTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Opens the store for a native app update — the splash screen's <b>Update</b> button and <c>POST /_bridge/host/open-store</c>.
    /// Null, the default, uses Shiny's <c>IAppStore</c> when the app registers it (<c>AddAppSupportBridge</c>'s
    /// <c>appStore</c> argument does), then the server's store URL. Receives the update the server sent, if any.
    /// </summary>
    public Func<HostUpdateInfo?, Task<bool>>? OpenStore { get; set; }

    /// <summary>The page to load, and the file an archive must contain to be accepted.</summary>
    public string EntryDocument { get; set; } = "index.html";

    /// <summary>
    /// Serves <see cref="EntryDocument"/> for paths that match no file, so client-side routes survive
    /// a reload. On by default; every SPA router needs it.
    /// </summary>
    public bool SpaFallback { get; set; } = true;

    /// <summary>
    /// The folder inside the zip to serve from. Null looks for <see cref="EntryDocument"/> at the root
    /// and then under <c>wwwroot/</c>, which covers a zipped Blazor publish either way.
    /// </summary>
    public string? ArchiveBasePath { get; set; }

    /// <summary>
    /// When a required update fails to download, the splash screen stops with the error and a <b>Try again</b> button instead
    /// of showing the installed build. On by default: the server said the installed build is no longer acceptable. Turn it
    /// off to treat a failed download like being offline at the check — the installed build is served and the update is
    /// retried next launch. With nothing installed at all, a failure always stops.
    /// </summary>
    public bool BlockOnRequiredUpdateFailure { get; set; } = true;

    /// <summary>
    /// Switch to an optional update the moment it finishes downloading, reloading the WebView. Off by
    /// default, because a reload the user did not ask for loses whatever they were doing; the update is
    /// served from the next launch, or when the page calls <c>POST /_bridge/host/apply-update</c>.
    /// </summary>
    public bool ApplyOptionalUpdatesImmediately { get; set; }


    /// <summary>
    /// Development only: a web server on the development machine — typically <c>dotnet watch</c> — to take pages
    /// from instead of the installed build. The page keeps its loopback origin, so the bridge, settings, files and
    /// the session all stay on the device; only the requests for the web app's own files cross to the dev server.
    /// <para>
    /// Probed at startup. When it answers, pages come from it and no update check runs; when it does not, the
    /// installed build is served as though this were never set. The Android emulator reaches the development
    /// machine at <c>10.0.2.2</c>, the iOS simulator at <c>localhost</c>, a physical device at the machine's LAN
    /// address.
    /// </para>
    /// </summary>
    public Uri? DevServer { get; set; }

    /// <summary>How long startup waits for <see cref="DevServer"/> before serving the installed build instead.</summary>
    public TimeSpan DevServerProbeTimeout { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// The web app to serve, as a zip compiled into <paramref name="assembly"/>. This alone is a complete
    /// setup — with no <see cref="UpdateProvider"/> there is no check, and the
    /// embedded build is simply what the app serves.
    /// <code>
    /// o.AppId = "field-app";
    /// o.UseBaseline(typeof(App).Assembly, "MyApp.webapp.zip");
    /// </code>
    /// </summary>
    /// <param name="assembly">The assembly the zip is compiled into.</param>
    /// <param name="resourceName">The resource's manifest name — its <c>LogicalName</c> when one is set.</param>
    /// <param name="version">
    /// What the embedded build is called. Only ordering against downloads needs it, so it defaults to
    /// <c>1.0.0</c>; set it once you have an <see cref="UpdateProvider"/> to compare against.
    /// </param>
    public WebAppHostOptions UseBaseline(Assembly assembly, string resourceName, string version = "1.0.0")
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        if (!WebAppVersion.TryParse(version, out _))
            throw new ArgumentException($"'{version}' is not a valid version.", nameof(version));

        this.Baseline = new WebAppBaseline(assembly, resourceName, version);
        return this;
    }

    /// <summary>Where installed builds live: <see cref="InstallDirectory"/>, or the bridge server's data directory.</summary>
    public string ResolveInstallDirectory(AppDeviceBridgeOptions bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        return this.InstallDirectory ?? bridge.ResolveDataDirectory();
    }

    /// <summary>
    /// Serve the web app's files to callers on other machines too. Off by default. Bridges are not affected: who may call
    /// those is <see cref="AppDeviceBridgeOptions.AuthorizeBridges"/>'s decision, and the launch session this host adds to
    /// them never leaves the device. Needs the server bound to more than loopback, or a tunnel — the server's <c>Options.Address</c>
    /// on its builder, or <c>Shiny.AppDeviceBridge.Tunnel</c>.
    /// </summary>
    public bool ServeWebAppRemotely { get; set; }

    /// <summary>
    /// Serve the web app's files to any caller on this device, not only the app's own WebView — a browser on the same
    /// machine opening the loopback address. Off by default. A tunneled caller is never local, however it arrives. Bridges
    /// are not affected: a local browser without the launch session reaches them only if
    /// <see cref="AppDeviceBridgeOptions.AuthorizeBridges"/> admits it.
    /// </summary>
    public bool ServeWebAppLocally { get; set; }

    /// <summary>
    /// Runs for every file of the web app just before it is written, after the host's own headers — the entry document's
    /// <c>Cache-Control: no-cache</c> — so it can replace them. For a cache policy of the app's own, such as caching
    /// fingerprinted assets for a year when they are served over a LAN or a tunnel, or a security header.
    /// <para>
    /// Not called for a response the host writes itself: an entry document rewritten for a mount point, or a page proxied from
    /// the dev server.
    /// </para>
    /// </summary>
    public Action<StaticFileResponseContext>? OnPrepareResponse { get; set; }

    /// <summary>
    /// Content types by file extension, for files the built-in map does not know or gets wrong for this app — pdf.js's
    /// <c>.bcmap</c> and <c>.pfb</c>, say. Keys are extensions with the leading dot.
    /// </summary>
    public IDictionary<string, string> ContentTypeOverrides { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    readonly List<string> variantNames = [];

    /// <summary>The client variants in every package, in the order given to <see cref="Variants"/>. Empty without variants.</summary>
    public IReadOnlyList<string> VariantNames => this.variantNames;

    /// <summary>
    /// Every package holds several builds of the web app — a phone client and a desktop client, say — one folder each, and
    /// <see cref="SelectVariant"/> picks one for every request. They share one app id, one version and one update check,
    /// and are served at the same URLs: <c>/</c>, <c>/_framework/…</c> and deep links alike. The first is the default.
    /// <code>
    /// o.UseBaseline(typeof(App).Assembly, "webapp.zip");   // mobile/index.html, desktop/index.html, …
    /// o.Variants("mobile", "desktop");
    /// o.SelectVariant = ctx => ctx.Request.Cookies["view"] ?? (IsPhone(ctx) ? "mobile" : "desktop");
    /// </code>
    /// <para>
    /// Each variant is a folder at the root of the archive (under <see cref="ArchiveBasePath"/> when that is set), holding
    /// the entry document directly or under <c>wwwroot/</c>. A package missing any variant's entry document is refused,
    /// the baseline as much as a download.
    /// </para>
    /// </summary>
    public WebAppHostOptions Variants(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);

        this.variantNames.Clear();
        this.variantNames.AddRange(names);
        return this;
    }

    /// <summary>
    /// Chooses the variant for a request to the web app — documents, the SPA fallback and every asset, because the same
    /// asset path holds different bytes in each build. It must answer the same for every request from one browser: derive
    /// it from the <c>User-Agent</c>, the <c>Sec-CH-UA-Mobile</c> client hint and a cookie of the app's own, which the
    /// host's <c>Vary</c> header names. Null, an unknown name, or an exception serves the default variant.
    /// <para>
    /// The WebView goes through it too. Pages from the <see cref="DevServer"/> do not: there is one dev server.
    /// </para>
    /// </summary>
    public Func<HttpContext, string?>? SelectVariant { get; set; }

    /// <summary>Fails when the host is created, for the mistakes that would otherwise surface as a blank WebView.</summary>
    internal void Validate()
    {
        if (this.UpdateProvider is null && this.Baseline is null && this.DevServer is null)
            throw new InvalidOperationException("Set a Baseline, an UpdateProvider or a DevServer — otherwise there is nothing to serve.");

        if (this.PageReadyTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("WebAppHostOptions.PageReadyTimeout must be positive.");

        ArgumentNullException.ThrowIfNull(this.Culture, nameof(this.Culture));

        foreach (var name in this.variantNames)
        {
            if (String.IsNullOrWhiteSpace(name) || name.IndexOfAny(['/', '\\']) >= 0 || name is "." or "..")
                throw new InvalidOperationException($"'{name}' is not a valid variant name: it names a folder in the archive.");
        }

        if (this.variantNames.Distinct(StringComparer.Ordinal).Count() != this.variantNames.Count)
            throw new InvalidOperationException("WebAppHostOptions.Variants names a variant twice.");

        if (this.SelectVariant is not null && this.variantNames.Count == 0)
            throw new InvalidOperationException("WebAppHostOptions.SelectVariant is set, but no Variants are declared to choose from.");
    }
}
