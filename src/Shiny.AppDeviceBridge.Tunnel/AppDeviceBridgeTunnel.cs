using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Ssh;

namespace Shiny.AppDeviceBridge.Tunnel;

public static class AppDeviceBridgeTunnelExtensions
{
    /// <summary>
    /// Registers <see cref="AppDeviceBridgeTunnel"/>: a public HTTPS address for the app's server, closed until the app opens
    /// it. There are no routes — turning it on is a decision for the app's own screens or endpoints, behind whatever
    /// authorization the app requires.
    /// <code>
    /// services.AddShinyHttpServer(http => http.AddAppDeviceBridge(bridge => bridge
    ///     .Configure(o => o.AppId = "devicecloud")
    ///     .AddTunnel(o => o.Host = QuickTunnelHost.Pinggy)
    /// ));
    ///
    /// public sealed class SharingEndpoints(AppDeviceBridgeTunnel tunnel)
    /// {
    ///     public Task&lt;Uri?&gt; OnAsync(string? token) => tunnel.StartAsync(token);
    ///     public Task OffAsync() => tunnel.StopAsync();
    /// }
    /// </code>
    /// <para>
    /// Everything that arrives through the tunnel is a caller from the network. The default bridge policy refuses it, the
    /// WebView's session is never granted to it, <see cref="AppDeviceBridgeOptions.AllowAnyCallerInDebug"/> does not apply to
    /// it, and the web app's own files reach it only with <c>ServeWebAppRemotely</c>. The app's own endpoints decide for
    /// themselves, as they do for any remote caller.
    /// </para>
    /// </summary>
    public static TBuilder AddTunnel<TBuilder>(this TBuilder bridge, Action<AppDeviceBridgeTunnelOptions>? configure = null)
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

        var services = bridge.Services;

        var options = services.FirstOrDefault(x => x.ServiceType == typeof(AppDeviceBridgeTunnelOptions))?.ImplementationInstance as AppDeviceBridgeTunnelOptions;
        if (options is null)
        {
            options = new AppDeviceBridgeTunnelOptions();
            services.AddSingleton(options);
        }

        configure?.Invoke(options);

        services.TryAddSingleton(sp => new AppDeviceBridgeTunnel(
            sp.GetRequiredService<AppDeviceBridgeServer>(),
            sp.GetRequiredService<AppDeviceBridgeTunnelOptions>(),
            sp.GetService<ILoggerFactory>()
        ));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAppDeviceBridgeTunnel, AppDeviceBridgeTunnel>(sp => sp.GetRequiredService<AppDeviceBridgeTunnel>()));

        return bridge;
    }
}

public sealed class AppDeviceBridgeTunnelOptions
{
    /// <summary>
    /// The public SSH host to tunnel through. Pinggy by default: it reports its address reliably, and a token from pinggy.io
    /// buys a tunnel that is not cut off after an hour.
    /// </summary>
    public QuickTunnelHost Host { get; set; } = QuickTunnelHost.Pinggy;

    /// <summary>Adjusts the SSH connection — keep-alives, timeouts, a known public URL — after the host's defaults are applied.</summary>
    public Action<SshTunnelOptions>? ConfigureSsh { get; set; }
}

public enum TunnelState
{
    Stopped,
    Connecting,
    Connected,

    /// <summary>The connection dropped and is being re-established. A free tunnel usually comes back at a different address.</summary>
    Reconnecting,

    /// <summary>It could not be opened, or could not be kept open. <see cref="AppDeviceBridgeTunnel.LastError"/> says why.</summary>
    Failed
}

/// <summary>
/// A public HTTPS address for the bridge server that the app turns on and off while it runs. Built on Shiny.Net.HttpServer's
/// quick tunnel: an SSH reverse forward to a public host, all managed code, so it runs on iOS and Android as well as the
/// desktop heads.
/// <para>
/// Bind to <see cref="PublicUrl"/> and <see cref="State"/> rather than reading them once — a free tunnel hands out a new
/// address every time it reconnects. <see cref="PropertyChanged"/> is raised on a background thread.
/// </para>
/// </summary>
public sealed class AppDeviceBridgeTunnel : IAppDeviceBridgeTunnel, INotifyPropertyChanged, IAsyncDisposable
{
    readonly AppDeviceBridgeServer server;
    readonly Func<HttpServer, string?, ITunnelSession> open;
    readonly ILogger logger;
    readonly SemaphoreSlim gate = new(1, 1);

    ITunnelSession? session;
    volatile Uri? publicUrl;
    TunnelState state = TunnelState.Stopped;
    string? lastError;
    int disposed;

    public AppDeviceBridgeTunnel(AppDeviceBridgeServer server, AppDeviceBridgeTunnelOptions options, ILoggerFactory? loggerFactory = null)
        : this(
            server,
            (http, subdomain) => new QuickTunnelSession(QuickTunnel.For(http, options.Host, subdomain, options.ConfigureSsh, loggerFactory)),
            loggerFactory
        )
    {
        ArgumentNullException.ThrowIfNull(options);
    }

    internal AppDeviceBridgeTunnel(AppDeviceBridgeServer server, Func<HttpServer, string?, ITunnelSession> open, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(open);

        this.server = server;
        this.open = open;
        this.logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<AppDeviceBridgeTunnel>();
    }

    /// <summary>The address to hand out while the tunnel is open; null otherwise. Changes on reconnect.</summary>
    public Uri? PublicUrl
    {
        get => this.publicUrl;
        private set
        {
            if (this.publicUrl == value)
                return;

            this.publicUrl = value;
            this.Raise();
        }
    }

    public TunnelState State
    {
        get => this.state;
        private set => this.Set(ref this.state, value);
    }

    /// <summary>Why the tunnel is <see cref="TunnelState.Failed"/>. Cleared when it is opened again.</summary>
    public string? LastError
    {
        get => this.lastError;
        private set => this.Set(ref this.lastError, value);
    }

    /// <summary>True from the moment the tunnel is asked for until it is stopped or fails.</summary>
    public bool IsOn => this.State is TunnelState.Connecting or TunnelState.Connected or TunnelState.Reconnecting;

    /// <summary>Raised on a background thread. Marshal to the UI thread before touching anything bound to it.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Opens the tunnel and returns the public address. Null when it could not be opened — <see cref="State"/> is then
    /// <see cref="TunnelState.Failed"/> and <see cref="LastError"/> says why. Already open, it returns the current address and
    /// changes nothing.
    /// <para>
    /// The server's own listener is left as it is: tunneled requests are served straight into the server's pipeline, so an
    /// app whose server is stopped — sharing on the local network switched off — can still open a tunnel without starting
    /// to listen on the network.
    /// </para>
    /// </summary>
    /// <param name="subdomain">
    /// Handed to the host: Pinggy reads it as an access token, which buys a tunnel that is not cut off after an hour; sish
    /// and Serveo read it as the subdomain to ask for; localhost.run ignores it. Read at each start, so a token the user
    /// changed applies the next time the tunnel is opened.
    /// </param>
    public async Task<Uri?> StartAsync(string? subdomain = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed != 0, this);
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (this.session is not null)
                return this.PublicUrl;

            this.LastError = null;
            this.State = TunnelState.Connecting;

            try
            {
                // Resolving the server is what composes the bridges onto it; the listener itself is not needed.
                var opened = this.open(this.server.Http, String.IsNullOrWhiteSpace(subdomain) ? null : subdomain.Trim());
                opened.Changed += this.OnSessionChanged;
                this.session = opened;

                var url = ToUri(await opened.StartAsync(cancellationToken).ConfigureAwait(false) ?? opened.PublicUrl);
                if (url is null)
                {
                    await this.CloseAsync(opened).ConfigureAwait(false);
                    this.LastError = opened.LastError ?? "The tunnel connected but never reported a public address.";
                    this.State = TunnelState.Failed;
                    this.logger.LogWarning("The tunnel opened without an address: {Error}", this.LastError);
                    return null;
                }

                this.PublicUrl = url;
                this.State = TunnelState.Connected;
                this.logger.LogInformation("Tunnel open at {Url}", url);
                return url;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                if (this.session is { } failed)
                    await this.CloseAsync(failed).ConfigureAwait(false);

                this.LastError = ex.Message;
                this.State = TunnelState.Failed;
                this.logger.LogError(ex, "The tunnel could not be opened");
                return null;
            }
            catch (OperationCanceledException)
            {
                if (this.session is { } cancelled)
                    await this.CloseAsync(cancelled).ConfigureAwait(false);

                this.State = TunnelState.Stopped;
                throw;
            }
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <summary>Closes the tunnel. The server's listener is untouched, and callers on this device and the local network are unaffected.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (this.session is { } open)
                await this.CloseAsync(open).ConfigureAwait(false);

            this.LastError = null;
            this.State = TunnelState.Stopped;
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <summary>Tears a session down and forgets its address, so the server stops accepting its host at once.</summary>
    async Task CloseAsync(ITunnelSession open)
    {
        open.Changed -= this.OnSessionChanged;

        if (ReferenceEquals(this.session, open))
            this.session = null;

        this.PublicUrl = null;

        try
        {
            await open.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogDebug(ex, "Closing the tunnel failed");
        }
    }

    void OnSessionChanged(object? sender, EventArgs e)
    {
        if (sender is not ITunnelSession open || !ReferenceEquals(open, this.session))
            return;

        // Followed, not read once: a reconnect usually comes back at a new address, and the old host must stop being
        // accepted the moment it is gone.
        this.PublicUrl = open.State is TunnelState.Connected ? ToUri(open.PublicUrl) : null;
        this.LastError = open.LastError;

        // Starting and stopping set these themselves; only what happens in between is followed here.
        if (open.State is TunnelState.Connected or TunnelState.Reconnecting or TunnelState.Failed)
            this.State = open.State;
    }

    static Uri? ToUri(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? uri : null;

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        this.Raise(name);
    }

    void Raise([CallerMemberName] string? name = null)
    {
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        if (name is nameof(this.State))
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.IsOn)));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            return;

        await this.StopAsync(CancellationToken.None).ConfigureAwait(false);
        this.gate.Dispose();
    }
}

/// <summary>One opened tunnel. The seam between <see cref="AppDeviceBridgeTunnel"/> and the SSH transport underneath.</summary>
interface ITunnelSession : IAsyncDisposable
{
    string? PublicUrl { get; }

    TunnelState State { get; }

    string? LastError { get; }

    /// <summary>Any of the three above changed. Raised on a background thread.</summary>
    event EventHandler? Changed;

    Task<string?> StartAsync(CancellationToken cancellationToken);
}

sealed class QuickTunnelSession : ITunnelSession
{
    readonly QuickTunnel tunnel;

    public QuickTunnelSession(QuickTunnel tunnel)
    {
        this.tunnel = tunnel;
        tunnel.PropertyChanged += (_, _) => this.Changed?.Invoke(this, EventArgs.Empty);
    }

    public string? PublicUrl => this.tunnel.PublicUrl;

    public TunnelState State => this.tunnel.State switch
    {
        QuickTunnelState.Connected => TunnelState.Connected,
        QuickTunnelState.Connecting => TunnelState.Connecting,
        QuickTunnelState.Reconnecting => TunnelState.Reconnecting,
        QuickTunnelState.Failed => TunnelState.Failed,
        _ => TunnelState.Stopped
    };

    public string? LastError => this.tunnel.LastError;

    public event EventHandler? Changed;

    public Task<string?> StartAsync(CancellationToken cancellationToken) => this.tunnel.StartAsync(cancellationToken);

    public ValueTask DisposeAsync() => this.tunnel.DisposeAsync();
}
