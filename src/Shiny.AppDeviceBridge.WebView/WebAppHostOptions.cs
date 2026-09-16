using System.Reflection;

namespace Shiny.AppDeviceBridge.WebView;

/// <summary>A web app zip compiled into the app, so there is always something to show — offline, on first launch.</summary>
/// <param name="Assembly">The assembly holding the resource.</param>
/// <param name="ResourceName">The resource's manifest name — its <c>LogicalName</c> when one is set.</param>
/// <param name="Version">The version of the web build inside it. Compared against installed downloads.</param>
public sealed record WebAppBaseline(Assembly Assembly, string ResourceName, string Version);

public sealed class WebAppHostOptions
{
    /// <summary>
    /// The release server's base address — the prefix given to <c>MapWebAppReleases</c>, such as
    /// <c>https://api.example.com/webapps</c>. Null never checks, and only the baseline is served.
    /// </summary>
    public Uri? UpdateServer { get; set; }

    /// <summary>
    /// The ECDSA P-256 public key releases are signed with, as PEM or base64. Required with
    /// <see cref="UpdateServer"/>: nothing downloaded is served without a valid signature.
    /// </summary>
    public string? PublicKey { get; set; }

    /// <summary>A prerelease channel to follow, such as <c>beta</c>. Null follows stable releases only.</summary>
    public string? Channel { get; set; }

    /// <summary>Where downloads are kept. Defaults to a folder named after the app id under local application data.</summary>
    public string? InstallDirectory { get; set; }

    public WebAppBaseline? Baseline { get; set; }

    /// <summary>How long the startup check may take before the installed version is shown anyway.</summary>
    public TimeSpan CheckTimeout { get; set; } = TimeSpan.FromSeconds(5);

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
    /// When a required update fails to download, show an error instead of the installed build. Off by
    /// default: a failed download mid-way is treated like being offline at the check, and the update is
    /// retried next launch.
    /// </summary>
    public bool BlockOnRequiredUpdateFailure { get; set; }

    /// <summary>
    /// Switch to an optional update the moment it finishes downloading, reloading the WebView. Off by
    /// default, because a reload the user did not ask for loses whatever they were doing; the update is
    /// served from the next launch, or when the page calls <c>POST /_bridge/host/apply-update</c>.
    /// </summary>
    public bool ApplyOptionalUpdatesImmediately { get; set; }

    /// <summary>
    /// The script that handles native calls — background jobs, GPS, geofences, push — when the page cannot:
    /// a classic script (not a module) in the archive, registering handlers with <c>appdevicebridge.on(name, fn)</c>.
    /// </summary>
    public string BackgroundScript { get; set; } = "background.js";

    /// <summary>
    /// How long one run of <see cref="BackgroundScript"/> may take. iOS gives a geofence wake-up about ten
    /// seconds and a background task about thirty, so the default fits inside the longer and a caller with
    /// less time passes its own cancellation.
    /// </summary>
    public TimeSpan BackgroundScriptTimeout { get; set; } = TimeSpan.FromSeconds(25);

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

    /// <summary>Supplies the handler for update checks and downloads — for certificate pinning, a proxy, or tests.</summary>
    public Func<HttpMessageHandler>? HttpMessageHandlerFactory { get; set; }

    /// <summary>
    /// The web app to serve, as a zip compiled into <paramref name="assembly"/>. This alone is a complete
    /// setup — with no <see cref="UpdateServer"/> there is no check, no manifest and no signing key, and the
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
    /// <c>1.0.0</c>; set it once you have an <see cref="UpdateServer"/> to compare against.
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
    /// them never leaves the device. Needs <see cref="AppDeviceBridgeOptions.Server"/> bound to more than loopback.
    /// </summary>
    public bool ServeWebAppRemotely { get; set; }

    /// <summary>Fails when the host is created, for the mistakes that would otherwise surface as a blank WebView.</summary>
    internal void Validate()
    {
        if (this.UpdateServer is not null && String.IsNullOrWhiteSpace(this.PublicKey))
            throw new InvalidOperationException("WebAppHostOptions.PublicKey is required when UpdateServer is set.");

        if (this.UpdateServer is null && this.Baseline is null && this.DevServer is null)
            throw new InvalidOperationException("Set a Baseline, an UpdateServer or a DevServer — otherwise there is nothing to serve.");

        if (this.BackgroundScriptTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("WebAppHostOptions.BackgroundScriptTimeout must be positive.");

        if (String.IsNullOrWhiteSpace(this.BackgroundScript))
            throw new InvalidOperationException("WebAppHostOptions.BackgroundScript is required.");
    }
}
