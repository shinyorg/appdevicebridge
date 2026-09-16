using System.Reflection;

namespace Shiny.WebAppHost;

/// <summary>A web app zip compiled into the app, so there is always something to show — offline, on first launch.</summary>
/// <param name="Assembly">The assembly holding the resource.</param>
/// <param name="ResourceName">The resource's manifest name — its <c>LogicalName</c> when one is set.</param>
/// <param name="Version">The version of the web build inside it. Compared against installed downloads.</param>
public sealed record WebAppBaseline(Assembly Assembly, string ResourceName, string Version);

public sealed class WebAppHostOptions
{
    /// <summary>The app id the release server knows this app by.</summary>
    public string AppId { get; set; } = String.Empty;

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

    /// <summary>
    /// The native app's own version. A release can demand a minimum, for when it depends on bridge
    /// endpoints an older app does not have. <c>Shiny.WebAppHost.Maui</c> fills this in from the
    /// app's display version.
    /// </summary>
    public string HostVersion { get; set; } = "1.0.0";

    /// <summary>Sent with every check so releases can be limited per platform. Detected by default.</summary>
    public string Platform { get; set; } = DetectPlatform();

    /// <summary>Where downloads are kept. Defaults to a folder named after the app id under local application data.</summary>
    public string? InstallDirectory { get; set; }

    public WebAppBaseline? Baseline { get; set; }

    /// <summary>
    /// The loopback port the app is served on.
    /// <para>
    /// Fixed rather than chosen by the OS, and this matters: the page's origin includes the port, and
    /// localStorage, IndexedDB, cookies and service workers all belong to the origin. A new port on
    /// every launch is a web app that forgets everything on every launch.
    /// </para>
    /// </summary>
    public int Port { get; set; } = 5780;

    /// <summary>
    /// When <see cref="Port"/> is taken, serve on any free port instead of failing. The app runs, but
    /// its web storage is empty for that launch. On by default because a blank screen is worse.
    /// </summary>
    public bool AllowPortFallback { get; set; } = true;

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
    /// Serves <c>/_bridge/settings/local</c> and <c>/_bridge/settings/secure</c> over
    /// Shiny.Extensions.Stores. On by default.
    /// </summary>
    public bool EnableSettings { get; set; } = true;

    /// <summary>
    /// Serves <c>/_bridge/files/{root}</c>. On by default. Every path is confined to one of
    /// <see cref="FileRoots"/>; the page cannot name a location outside them.
    /// </summary>
    public bool EnableFiles { get; set; } = true;

    /// <summary>
    /// The directories the page can use, by the name it uses for them. Left empty, the page gets
    /// <c>data</c> (persistent, beside the installed builds but not containing them) and <c>cache</c>
    /// (temporary, which the OS may clear). Adding any entry replaces both defaults.
    /// </summary>
    public IDictionary<string, string> FileRoots { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The largest file the page can write in one request. The loopback server's request body limit is
    /// raised to match, since its 30 MB default would otherwise decide instead.
    /// </summary>
    public long MaxFileWriteBytes { get; set; } = 256 * 1024 * 1024;

    /// <summary>
    /// The script that handles native calls — background jobs, GPS, geofences, push — when the page cannot:
    /// a classic script (not a module) in the archive, registering handlers with <c>webapphost.on(name, fn)</c>.
    /// </summary>
    public string BackgroundScript { get; set; } = "background.js";

    /// <summary>
    /// How long a page that declared a handler has to accept a call before it goes to
    /// <see cref="BackgroundScript"/> instead. Short, because a page that cannot accept promptly is usually a
    /// WebView the OS has suspended.
    /// </summary>
    public TimeSpan PageAcceptTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a page that accepted a call has to finish it.</summary>
    public TimeSpan PageInvocationTimeout { get; set; } = TimeSpan.FromSeconds(25);

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
    /// Whether, and how far, the server is open to other machines. Loopback only until you say otherwise, and
    /// every bridge stays loopback-only even then until it is named. See <see cref="WebAppRemoteAccessOptions"/>.
    /// </summary>
    public WebAppRemoteAccessOptions RemoteAccess { get; } = new();

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

    internal string ResolveInstallDirectory()
        => this.InstallDirectory
           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "webapphost", this.AppId);

    internal WebAppVersion ParsedHostVersion => WebAppVersion.Parse(this.HostVersion);

    /// <summary>
    /// The roots the page's paths are confined to: <see cref="FileRoots"/>, or the <c>data</c> and <c>cache</c>
    /// defaults. Bridges that accept a file from the page — a notification image, an upload — resolve it here, so
    /// they refuse exactly what the files bridge refuses.
    /// </summary>
    public IReadOnlyList<WebAppFileRoot> ResolveFileRoots()
        => this.FileRoots.Count > 0
            ? [.. this.FileRoots.Select(x => new WebAppFileRoot(x.Key, x.Value))]
            :
            [
                new WebAppFileRoot("data", Path.Combine(this.ResolveInstallDirectory(), "files")),
                new WebAppFileRoot("cache", Path.Combine(Path.GetTempPath(), "webapphost", this.AppId))
            ];

    /// <summary>Fails at registration for the mistakes that would otherwise surface as a blank WebView.</summary>
    internal void Validate()
    {
        if (!WebAppProtocol.IsValidAppId(this.AppId))
            throw new InvalidOperationException($"WebAppHostOptions.AppId '{this.AppId}' is not valid. Use letters, digits, '.', '-' and '_'.");

        if (!WebAppVersion.TryParse(this.HostVersion, out _))
            throw new InvalidOperationException($"WebAppHostOptions.HostVersion '{this.HostVersion}' is not a valid version.");

        if (this.UpdateServer is not null && String.IsNullOrWhiteSpace(this.PublicKey))
            throw new InvalidOperationException("WebAppHostOptions.PublicKey is required when UpdateServer is set.");

        if (this.UpdateServer is null && this.Baseline is null && this.DevServer is null)
            throw new InvalidOperationException("Set a Baseline, an UpdateServer or a DevServer — otherwise there is nothing to serve.");

        foreach (var (name, path) in this.FileRoots)
        {
            if (!WebAppFileRoot.IsValidName(name))
                throw new InvalidOperationException($"File root name '{name}' is not valid. Use letters, digits, '-' and '_'.");

            if (!Path.IsPathFullyQualified(path))
                throw new InvalidOperationException($"File root '{name}' must be an absolute path; '{path}' is not.");
        }

        if (this.PageAcceptTimeout <= TimeSpan.Zero || this.PageInvocationTimeout <= TimeSpan.Zero || this.BackgroundScriptTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("WebAppHostOptions invocation timeouts must be positive.");

        if (String.IsNullOrWhiteSpace(this.BackgroundScript))
            throw new InvalidOperationException("WebAppHostOptions.BackgroundScript is required.");

        if (this.MaxFileWriteBytes <= 0)
            throw new InvalidOperationException("WebAppHostOptions.MaxFileWriteBytes must be positive.");

        if (this.Port is < 0 or > 65535)
            throw new InvalidOperationException($"WebAppHostOptions.Port {this.Port} is out of range.");

        foreach (var name in this.RemoteAccess.Bridges)
        {
            if (!WebAppProtocol.IsValidAppId(name))
                throw new InvalidOperationException($"RemoteAccess bridge name '{name}' is not valid.");
        }

        if (this.RemoteAccess is { Enabled: false } remote && (remote.Bridges.Count > 0 || remote.ServeWebApp))
            throw new InvalidOperationException("RemoteAccess names bridges or serves the web app, but RemoteAccess.Enabled is false — nothing off this device can reach the server.");
    }

    static string DetectPlatform()
    {
        // Order matters: Mac Catalyst also reports itself as iOS.
        if (OperatingSystem.IsMacCatalyst()) return "maccatalyst";
        if (OperatingSystem.IsIOS()) return "ios";
        if (OperatingSystem.IsAndroid()) return "android";
        if (OperatingSystem.IsMacOS()) return "macos";
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsLinux()) return "linux";
        return "unknown";
    }
}
