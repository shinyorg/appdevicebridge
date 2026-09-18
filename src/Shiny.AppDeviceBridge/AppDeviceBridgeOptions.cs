using System.Diagnostics;
using System.Reflection;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Security;

namespace Shiny.AppDeviceBridge;

/// <summary>
/// The bridges on the app's Shiny.Net.HttpServer: who may call them, where they mount, and the built-in settings and
/// files. The server itself — address, port, TLS, limits, the app's own endpoints, authentication — is configured on the
/// same <see cref="ShinyHttpServerBuilder"/> the bridges are added to.
/// <code>
/// services.AddShinyHttpServer(http =>
/// {
///     http.Options.Port = 5780;
///     http.AddAppDeviceBridge(o => o.AppId = "field-app");
///     http.Configure(server => server.MapGet("/api/orders", ...));
/// });
/// </code>
/// </summary>
public sealed class AppDeviceBridgeOptions
{
    /// <summary>The app's id: namespaces settings, names the data directory, and is what a release server knows the app by.</summary>
    public string AppId { get; set; } = String.Empty;

    /// <summary>The native app's own version, reported to the page. <c>Shiny.AppDeviceBridge.Maui</c> fills it in from the app's display version.</summary>
    public string HostVersion { get; set; } = "1.0.0";

    /// <summary>Reported to the page. Detected by default.</summary>
    public string Platform { get; set; } = DetectPlatform();

    /// <summary>
    /// When the server's port is taken as <see cref="AppDeviceBridgeServer.StartAsync"/> starts it, serve on any free port
    /// instead of failing. On by default, because a blank screen is worse than a page whose web storage is empty for one
    /// launch — a page's origin includes the port, which is why the port is fixed in the first place.
    /// </summary>
    public bool AllowPortFallback { get; set; } = true;

    /// <summary>The path everything is served under — pages, assets and bridges alike. <c>/</c> by default.</summary>
    public string BasePath { get; set; } = WebAppPaths.DefaultBasePath;

    /// <summary>
    /// Where the bridges mount, under <see cref="BasePath"/>. <c>/_bridge</c> by default. Pages discover it from
    /// <c>{base}/_host/config</c>, so it can move without the page being rebuilt.
    /// </summary>
    public string BridgePrefix { get; set; } = WebAppPaths.DefaultBridgePrefix;

    /// <summary>
    /// Host names a caller from another machine may use besides a bare IP address — an mDNS name such as
    /// <c>kiosk.local</c>, or a custom domain in front of a tunnel. Anything else from off the device is turned away with
    /// <c>421</c>, which is what stops DNS rebinding: a hostile site that points its own name at this device arrives carrying
    /// that name. An open tunnel's own public host needs no entry here (see <see cref="IAppDeviceBridgeTunnel"/>).
    /// </summary>
    public ISet<string> AllowedHosts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this is a debug build, which decides <see cref="AllowAnyCallerInDebug"/>. Detected from the entry
    /// assembly's <see cref="DebuggableAttribute"/> and an attached debugger; set it yourself where neither is reliable.
    /// </summary>
    public bool IsDebug { get; set; } = DetectDebug();

    /// <summary>
    /// In a debug build, let any caller reach the bridges — a browser on the development machine, a script, another
    /// device — instead of only callers on this device. On by default. Only the default bridge policy reads it: once
    /// <see cref="AuthorizeBridges"/> is called, the policy given there decides in every build.
    /// </summary>
    public bool AllowAnyCallerInDebug { get; set; } = true;

    /// <summary>
    /// Serves <c>/_bridge/settings/local</c> and <c>/_bridge/settings/secure</c> over Shiny.Extensions.Stores. On by default.
    /// </summary>
    public bool EnableSettings { get; set; } = true;

    /// <summary>Serves <c>/_bridge/files/{root}</c>. On by default. Every path is confined to one of <see cref="FileRoots"/>.</summary>
    public bool EnableFiles { get; set; } = true;

    /// <summary>
    /// The directories the page can use, by the name it uses for them. Left empty, the page gets <c>data</c> (persistent)
    /// and <c>cache</c> (temporary, which the OS may clear). Adding any entry replaces both defaults.
    /// </summary>
    public IDictionary<string, string> FileRoots { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The largest file the page can write in one request, through the files bridge or any other bridge that takes a file.
    /// The server's request body limit (<see cref="HttpServerOptions.Limits"/>) is raised to at least this when the server is
    /// built, whichever bridges are registered — the limit is the server's, so it also applies to the app's own upload
    /// endpoints. A limit set higher, or turned off, is left alone; set this lower to keep the server's limit smaller.
    /// </summary>
    public long MaxFileWriteBytes { get; set; } = 256 * 1024 * 1024;

    /// <summary>
    /// Where the app keeps its own data — the <c>data</c> root, picked folders, installed web app builds. Defaults to a
    /// folder named after <see cref="AppId"/> under local application data.
    /// </summary>
    public string? DataDirectory { get; set; }

    /// <summary>
    /// How long a page that declared a native call handler has to accept a call before it goes elsewhere. Short, because
    /// a page that cannot accept promptly is usually a WebView the OS has suspended.
    /// </summary>
    public TimeSpan PageAcceptTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a page that accepted a native call has to finish it.</summary>
    public TimeSpan PageInvocationTimeout { get; set; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// How often the event stream writes a comment when it has nothing else to send. A page that went away without a
    /// word is only noticed when a write to it fails, so this bounds how long a native event stays hooked for a page
    /// that is gone.
    /// </summary>
    public TimeSpan EventStreamHeartbeat { get; set; } = TimeSpan.FromSeconds(15);

    internal Action<AuthorizationPolicyBuilder>? BridgePolicy { get; private set; }

    /// <summary>
    /// Replaces who may call the bridges. The bridges are device access, so this is a security decision: by default only a
    /// caller on this device may use them (any caller in a debug build — see <see cref="AllowAnyCallerInDebug"/>), and a
    /// WebView host adds its launch session to that. Whatever this policy allows can reach the device. The schemes it asks
    /// for are the ones added to the server's builder.
    /// <code>
    /// http.AddAuthentication().AddApiKey(k => k.AddKey(key, "kiosk"));
    /// http.AddAppDeviceBridge(o => o.AuthorizeBridges(p => p.RequireAuthenticatedUser()));
    ///
    /// o.AuthorizeBridges(p => p.RequireAssertion(ctx => BridgeCallers.IsOnDevice(ctx.HttpContext) || ctx.User.IsInRole("admin")));
    /// </code>
    /// </summary>
    public AppDeviceBridgeOptions AuthorizeBridges(Action<AuthorizationPolicyBuilder> policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        this.BridgePolicy = policy;
        return this;
    }

    /// <summary>The app's data directory: <see cref="DataDirectory"/>, or a folder under local application data.</summary>
    public string ResolveDataDirectory()
        => this.DataDirectory
           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "appdevicebridge", this.AppId);

    /// <summary>The roots the page's paths are confined to: <see cref="FileRoots"/>, or the <c>data</c> and <c>cache</c> defaults.</summary>
    public IReadOnlyList<WebAppFileRoot> ResolveFileRoots()
        => this.FileRoots.Count > 0
            ? [.. this.FileRoots.Select(x => new WebAppFileRoot(x.Key, x.Value))]
            :
            [
                new WebAppFileRoot("data", Path.Combine(this.ResolveDataDirectory(), "files")),
                new WebAppFileRoot("cache", Path.Combine(Path.GetTempPath(), "appdevicebridge", this.AppId))
            ];

    /// <summary>Fails when the server is created, for the mistakes that would otherwise surface as a 404 nobody can explain.</summary>
    public void Validate()
    {
        if (!WebAppProtocol.IsValidAppId(this.AppId))
            throw new InvalidOperationException($"AppDeviceBridgeOptions.AppId '{this.AppId}' is not valid. Use letters, digits, '.', '-' and '_'.");

        if (!WebAppVersion.TryParse(this.HostVersion, out _))
            throw new InvalidOperationException($"AppDeviceBridgeOptions.HostVersion '{this.HostVersion}' is not a valid version.");

        foreach (var (name, path) in this.FileRoots)
        {
            if (!WebAppFileStore.IsValidName(name))
                throw new InvalidOperationException($"File root name '{name}' is not valid. Use letters, digits, '-' and '_'.");

            if (!Path.IsPathFullyQualified(path))
                throw new InvalidOperationException($"File root '{name}' must be an absolute path; '{path}' is not.");
        }

        if (this.PageAcceptTimeout <= TimeSpan.Zero || this.PageInvocationTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("AppDeviceBridgeOptions invocation timeouts must be positive.");

        if (this.EventStreamHeartbeat <= TimeSpan.Zero)
            throw new InvalidOperationException("AppDeviceBridgeOptions.EventStreamHeartbeat must be positive.");

        if (this.MaxFileWriteBytes <= 0)
            throw new InvalidOperationException("AppDeviceBridgeOptions.MaxFileWriteBytes must be positive.");

        if (!WebAppPaths.IsValid(this.BasePath))
            throw new InvalidOperationException($"AppDeviceBridgeOptions.BasePath '{this.BasePath}' is not valid. Use path segments of letters, digits, '-', '_', '.' and '~'.");

        if (!WebAppPaths.IsValid(this.BridgePrefix))
            throw new InvalidOperationException($"AppDeviceBridgeOptions.BridgePrefix '{this.BridgePrefix}' is not valid. Use path segments of letters, digits, '-', '_', '.' and '~'.");

        var bridgePrefix = WebAppPaths.Normalize(this.BridgePrefix);
        if (bridgePrefix.Length == 0)
            throw new InvalidOperationException("AppDeviceBridgeOptions.BridgePrefix cannot be the root: the bridges need a prefix of their own to sit under.");

        if (bridgePrefix.Equals(WebAppPaths.HostSegment, StringComparison.OrdinalIgnoreCase)
            || bridgePrefix.StartsWith(WebAppPaths.HostSegment + "/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"AppDeviceBridgeOptions.BridgePrefix cannot be '{WebAppPaths.HostSegment}' or sit under it — that is where the mount points themselves are served.");
    }

    static bool DetectDebug()
    {
        if (Debugger.IsAttached)
            return true;

        try
        {
            return Assembly.GetEntryAssembly()?.GetCustomAttribute<DebuggableAttribute>()?.IsJITTrackingEnabled == true;
        }
        catch (Exception)
        {
            return false;
        }
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
