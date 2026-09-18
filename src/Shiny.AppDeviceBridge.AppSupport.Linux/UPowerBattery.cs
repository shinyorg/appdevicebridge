using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Maui.Devices;
using Tmds.DBus.Protocol;

namespace Shiny.AppDeviceBridge.AppSupport.Linux;

/// <summary>
/// The battery on a Linux desktop, as .NET MAUI Essentials' <see cref="IBattery"/>: UPower's display device for the charge,
/// state and power source, and power-profiles-daemon for energy saver, both on the system bus.
/// <para>
/// UPower is only watched while something is subscribed to <see cref="BatteryInfoChanged"/>, and power-profiles-daemon
/// while something is subscribed to <see cref="EnergySaverStatusChanged"/>, so a page that stops listening leaves no
/// match rule behind. A machine without UPower reads as <see cref="BatteryState.Unknown"/> and raises nothing; one
/// without power-profiles-daemon reports energy saver as <see cref="EnergySaverStatus.Unknown"/>.
/// </para>
/// </summary>
public sealed class UPowerBattery : IBattery, IDisposable
{
    const string UPowerService = "org.freedesktop.UPower";
    const string UPowerPath = "/org/freedesktop/UPower";
    const string UPowerInterface = "org.freedesktop.UPower";
    const string DisplayDevicePath = "/org/freedesktop/UPower/devices/DisplayDevice";
    const string DeviceInterface = "org.freedesktop.UPower.Device";

    const string ProfilesService = "net.hadess.PowerProfiles";
    const string ProfilesPath = "/net/hadess/PowerProfiles";
    const string ProfilesInterface = "net.hadess.PowerProfiles";

    const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    // A property read blocks its caller; a system bus that does not answer in this long is not going to.
    static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    readonly ILogger logger;
    readonly Lock gate = new();
    readonly Func<Task<DBusConnection>> connect;
    Task<DBusConnection>? connection;

    EventHandler<BatteryInfoChangedEventArgs>? batteryInfoChanged;
    EventHandler<EnergySaverStatusChangedEventArgs>? energySaverChanged;
    Watch? batteryWatch;
    Watch? energySaverWatch;
    BatteryReading? lastBattery;
    EnergySaverStatus? lastEnergySaver;

    public UPowerBattery(ILogger<UPowerBattery>? logger = null)
    {
        this.logger = logger ?? NullLogger<UPowerBattery>.Instance;
        this.connect = static async () =>
        {
            var address = DBusAddress.System
                ?? throw new InvalidOperationException("No D-Bus system bus: DBUS_SYSTEM_BUS_ADDRESS is empty and the default socket is missing.");

            var connection = new DBusConnection(address);
            await connection.ConnectAsync().ConfigureAwait(false);
            return connection;
        };
    }

    public double ChargeLevel => this.ReadBattery().ChargeLevel;

    public BatteryState State => this.ReadBattery().State;

    public BatteryPowerSource PowerSource => this.ReadBattery().PowerSource;

    public EnergySaverStatus EnergySaverStatus => this.ReadEnergySaver();

    public event EventHandler<BatteryInfoChangedEventArgs> BatteryInfoChanged
    {
        add
        {
            lock (this.gate)
            {
                var first = this.batteryInfoChanged is null;
                this.batteryInfoChanged += value;

                if (first)
                    this.batteryWatch = new Watch(this, this.WatchBatteryAsync);
            }
        }
        remove
        {
            lock (this.gate)
            {
                this.batteryInfoChanged -= value;

                if (this.batteryInfoChanged is null)
                {
                    this.batteryWatch?.Dispose();
                    this.batteryWatch = null;
                    this.lastBattery = null;
                }
            }
        }
    }

    public event EventHandler<EnergySaverStatusChangedEventArgs> EnergySaverStatusChanged
    {
        add
        {
            lock (this.gate)
            {
                var first = this.energySaverChanged is null;
                this.energySaverChanged += value;

                if (first)
                    this.energySaverWatch = new Watch(this, this.WatchEnergySaverAsync);
            }
        }
        remove
        {
            lock (this.gate)
            {
                this.energySaverChanged -= value;

                if (this.energySaverChanged is null)
                {
                    this.energySaverWatch?.Dispose();
                    this.energySaverWatch = null;
                    this.lastEnergySaver = null;
                }
            }
        }
    }

    /// <summary>
    /// What UPower's display device says, as Essentials names it. <paramref name="state"/> is UPower's <c>State</c>:
    /// 1 charging, 2 discharging, 3 empty, 4 fully charged, 5 pending charge, 6 pending discharge.
    /// </summary>
    internal static BatteryReading ToReading(bool isPresent, double percentage, uint state, bool onBattery)
    {
        if (!isPresent)
            return new BatteryReading(1.0, BatteryState.NotPresent, BatteryPowerSource.AC);

        var mapped = state switch
        {
            1 => BatteryState.Charging,
            2 or 3 => BatteryState.Discharging,
            4 => BatteryState.Full,
            5 or 6 => BatteryState.NotCharging,
            _ => BatteryState.Unknown
        };

        // UPower does not say whether mains power arrives over AC or USB, only whether the machine runs on its battery.
        return new BatteryReading(Math.Clamp(percentage / 100.0, 0, 1), mapped, onBattery ? BatteryPowerSource.Battery : BatteryPowerSource.AC);
    }

    /// <summary>power-profiles-daemon's <c>ActiveProfile</c>: <c>power-saver</c>, <c>balanced</c> or <c>performance</c>.</summary>
    internal static EnergySaverStatus ToEnergySaver(string? profile) => profile switch
    {
        null => EnergySaverStatus.Unknown,
        "power-saver" => EnergySaverStatus.On,
        _ => EnergySaverStatus.Off
    };

    BatteryReading ReadBattery()
    {
        lock (this.gate)
        {
            // Kept current by UPower's signals while something listens.
            if (this.batteryWatch is not null && this.lastBattery is { } watched)
                return watched;
        }

        return this.Block(this.ReadBatteryAsync(), BatteryReading.Unknown, "UPower");
    }

    EnergySaverStatus ReadEnergySaver()
    {
        lock (this.gate)
        {
            if (this.energySaverWatch is not null && this.lastEnergySaver is { } watched)
                return watched;
        }

        return this.Block(this.ReadEnergySaverAsync(), EnergySaverStatus.Unknown, "power-profiles-daemon");
    }

    T Block<T>(Task<T> read, T fallback, string service)
    {
        try
        {
            return read.WaitAsync(ReadTimeout).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "Could not read from {Service}", service);
            return fallback;
        }
    }

    async Task<BatteryReading> ReadBatteryAsync()
    {
        var connection = await this.ConnectionAsync().ConfigureAwait(false);
        var device = await GetAllAsync(connection, UPowerService, DisplayDevicePath, DeviceInterface).ConfigureAwait(false);
        var root = await GetAllAsync(connection, UPowerService, UPowerPath, UPowerInterface).ConfigureAwait(false);

        return ToReading(
            device.TryGetValue("IsPresent", out var present) && present.GetBool(),
            device.TryGetValue("Percentage", out var percentage) ? percentage.GetDouble() : 100,
            device.TryGetValue("State", out var state) ? state.GetUInt32() : 0,
            root.TryGetValue("OnBattery", out var onBattery) && onBattery.GetBool()
        );
    }

    async Task<EnergySaverStatus> ReadEnergySaverAsync()
    {
        var connection = await this.ConnectionAsync().ConfigureAwait(false);
        var profiles = await GetAllAsync(connection, ProfilesService, ProfilesPath, ProfilesInterface).ConfigureAwait(false);

        return ToEnergySaver(profiles.TryGetValue("ActiveProfile", out var profile) ? profile.GetString() : null);
    }

    async Task<IDisposable> WatchBatteryAsync()
    {
        var connection = await this.ConnectionAsync().ConfigureAwait(false);

        // The display device and UPower itself (OnBattery) both matter; one rule on the sender covers both.
        var watch = await WatchPropertiesAsync(connection, UPowerService, () => _ = this.RefreshBatteryAsync()).ConfigureAwait(false);
        await this.RefreshBatteryAsync(raise: false).ConfigureAwait(false);
        return watch;
    }

    async Task<IDisposable> WatchEnergySaverAsync()
    {
        var connection = await this.ConnectionAsync().ConfigureAwait(false);
        var watch = await WatchPropertiesAsync(connection, ProfilesService, () => _ = this.RefreshEnergySaverAsync()).ConfigureAwait(false);
        await this.RefreshEnergySaverAsync(raise: false).ConfigureAwait(false);
        return watch;
    }

    async Task RefreshBatteryAsync(bool raise = true)
    {
        BatteryReading reading;
        try
        {
            reading = await this.ReadBatteryAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "Could not read from UPower");
            return;
        }

        EventHandler<BatteryInfoChangedEventArgs>? handlers;
        lock (this.gate)
        {
            if (this.batteryWatch is null || this.lastBattery == reading)
                return;

            this.lastBattery = reading;
            handlers = raise ? this.batteryInfoChanged : null;
        }

        handlers?.Invoke(this, new BatteryInfoChangedEventArgs(reading.ChargeLevel, reading.State, reading.PowerSource));
    }

    async Task RefreshEnergySaverAsync(bool raise = true)
    {
        EnergySaverStatus status;
        try
        {
            status = await this.ReadEnergySaverAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "Could not read from power-profiles-daemon");
            return;
        }

        EventHandler<EnergySaverStatusChangedEventArgs>? handlers;
        lock (this.gate)
        {
            if (this.energySaverWatch is null || this.lastEnergySaver == status)
                return;

            this.lastEnergySaver = status;
            handlers = raise ? this.energySaverChanged : null;
        }

        handlers?.Invoke(this, new EnergySaverStatusChangedEventArgs(status));
    }

    Task<DBusConnection> ConnectionAsync()
    {
        lock (this.gate)
        {
            // A failed connection is not kept, so a bus that comes up later is found on the next read.
            if (this.connection is null || this.connection.IsFaulted || this.connection.IsCanceled)
                this.connection = this.connect();

            return this.connection;
        }
    }

    static Task<Dictionary<string, VariantValue>> GetAllAsync(DBusConnection connection, string service, string path, string @interface)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: service, path: path, @interface: PropertiesInterface, member: "GetAll", signature: "s");
        writer.WriteString(@interface);

        return connection.CallMethodAsync(writer.CreateMessage(), static (Message reply, object? _) =>
        {
            var reader = reply.GetBodyReader();
            return reader.ReadDictionaryOfStringToVariantValue();
        });
    }

    static async Task<IDisposable> WatchPropertiesAsync(DBusConnection connection, string sender, Action changed)
        => await connection.AddMatchAsync(
            new MatchRule
            {
                Type = MessageType.Signal,
                Sender = sender,
                Interface = PropertiesInterface,
                Member = "PropertiesChanged"
            },
            static (Message message, object? _) => true,
            static (Notification<bool> notification) =>
            {
                if (notification.Exception is null)
                    ((Action)notification.State!).Invoke();
            },
            emitOnCapturedContext: false,
            ObserverFlags.None,
            changed
        ).ConfigureAwait(false);

    public void Dispose()
    {
        Task<DBusConnection>? open;

        lock (this.gate)
        {
            this.batteryWatch?.Dispose();
            this.energySaverWatch?.Dispose();
            this.batteryWatch = null;
            this.energySaverWatch = null;
            this.batteryInfoChanged = null;
            this.energySaverChanged = null;
            open = this.connection;
            this.connection = null;
        }

        if (open is { IsCompletedSuccessfully: true })
            open.Result.Dispose();
    }

    internal readonly record struct BatteryReading(double ChargeLevel, BatteryState State, BatteryPowerSource PowerSource)
    {
        public static BatteryReading Unknown { get; } = new(1.0, BatteryState.Unknown, BatteryPowerSource.Unknown);
    }

    /// <summary>A match rule being added in the background, removed when disposed — even before it finished adding.</summary>
    sealed class Watch : IDisposable
    {
        readonly Task<IDisposable?> subscription;
        int disposed;

        public Watch(UPowerBattery owner, Func<Task<IDisposable>> start)
            => this.subscription = StartAsync(owner, start);

        static async Task<IDisposable?> StartAsync(UPowerBattery owner, Func<Task<IDisposable>> start)
        {
            try
            {
                return await start().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // No daemon, no bus: there is nothing to hear, and a property read reports Unknown.
                owner.logger.LogDebug(ex, "Could not watch for battery changes");
                return null;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) == 1)
                return;

            _ = this.subscription.ContinueWith(
                static t => t.Result?.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
        }
    }
}
