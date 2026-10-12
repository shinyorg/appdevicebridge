using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppStore;
using Shiny.Net;
using Shiny.Net.HttpServer;
using Shiny.Power;
using Contracts = Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>The app bridge over fake Shiny.Core services, through the typed client and the real event stream.</summary>
public class AppBridgeTests
{
    [Fact]
    public async Task Reports_the_device_screen_battery_and_connectivity()
    {
        await using var fixture = await AppFixture.StartAsync();

        var info = await fixture.Client.GetInfoAsync();
        Assert.Equal(
            new Contracts.AppInfo("2.3.4", "Shiny", "Phone 1", "17.0", "iOS", "Phone", Contracts.DisplayOrientation.Portrait, "fr-CA", "UTC"),
            info
        );

        Assert.Equal(new ScreenInfo(1170, 2532, 3, Contracts.DisplayOrientation.Portrait, 120), await fixture.Client.GetScreenAsync());

        // Shiny's "no battery" is the contract's NotPresent; the rest carry over by name.
        fixture.Battery.Status = Shiny.Power.BatteryState.None;
        Assert.Equal(
            new BatteryInfo(0.5, Contracts.BatteryState.NotPresent, Contracts.BatteryPowerSource.AC, Contracts.EnergySaverStatus.On),
            await fixture.Client.GetBatteryAsync()
        );

        var connectivity = await fixture.Client.GetConnectivityAsync();
        Assert.Equal(Contracts.NetworkAccess.Internet, connectivity.NetworkAccess);
        Assert.Equal([Contracts.ConnectionProfile.WiFi, Contracts.ConnectionProfile.Ethernet], connectivity.ConnectionProfiles);
    }

    [Fact]
    public async Task Turns_the_app_and_refuses_an_unknown_orientation()
    {
        await using var fixture = await AppFixture.StartAsync();

        Assert.True((await fixture.Client.SetOrientationAsync(new OrientationRequest(Contracts.DisplayOrientation.Landscape))).Success);
        Assert.Equal(DeviceOrientation.Landscape, fixture.Screen.Requested);

        Assert.True((await fixture.Client.ResetOrientationAsync()).Success);
        Assert.Null(fixture.Screen.Requested);

        var bad = await fixture.WebView.PostAsync("/_bridge/app/orientation", Json("""{ "orientation": 7 }"""));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        await fixture.Client.OpenSettingsAsync();
        Assert.Equal(1, fixture.Platform.SettingsOpened);
    }

    [Fact]
    public async Task Looks_the_app_up_in_its_store()
    {
        await using var fixture = await AppFixture.StartAsync();

        var missing = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetStoreListingAsync());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        fixture.Store.Current = new AppStoreResult(new Version(2, 4), new Version(2, 3, 4), true, "https://store/app", "Fixes");
        var listing = await fixture.Client.GetStoreListingAsync();
        Assert.Equal(("2.4", "2.3.4", true, "Fixes"), (listing.StoreVersion, listing.CurrentVersion, listing.NeedsUpdate, listing.ReleaseNotes));

        Assert.True((await fixture.Client.OpenStoreAsync()).Success);
        Assert.True((await fixture.Client.RequestReviewAsync()).Success);
        Assert.False((await fixture.Client.OpenReviewPageAsync()).Success);
        Assert.Equal(["open", "review", "review-page"], fixture.Store.Calls);
    }

    [Fact]
    public async Task Registers_for_launch_at_login_and_reports_the_os_refusing()
    {
        await using var fixture = await AppFixture.StartAsync();

        Assert.Equal(new StartupStatus(true, StartupState.NotRegistered), await fixture.Client.GetStartupAsync());
        Assert.Equal(new StartupStatus(true, StartupState.RequiresApproval), await fixture.Client.RegisterStartupAsync());
        Assert.True((await fixture.Client.OpenStartupSettingsAsync()).Opened);

        fixture.Startup.Refuse = true;
        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.UnregisterStartupAsync());
        Assert.Equal((HttpStatusCode.Conflict, "startup_failed"), (refused.StatusCode, refused.Code));
    }

    [Fact]
    public async Task Answers_501_for_what_the_host_did_not_register()
    {
        await using var fixture = await AppFixture.StartAsync(full: false);

        foreach (var call in new Func<Task>[]
        {
            () => fixture.Client.GetBatteryAsync(),
            () => fixture.Client.GetConnectivityAsync(),
            () => fixture.Client.GetStoreListingAsync(),
            () => fixture.Client.OpenStoreAsync(),
            () => fixture.Client.RegisterStartupAsync(),
            () => fixture.Client.OpenStartupSettingsAsync()
        })
        {
            Assert.True((await Assert.ThrowsAsync<BridgeException>(call)).IsNotSupported);
        }

        // Answered rather than refused, so a page can decide whether to show the toggle.
        Assert.Equal(new StartupStatus(false, StartupState.NotSupported), await fixture.Client.GetStartupAsync());
    }

    [Fact]
    public async Task Sends_changes_as_events_and_unhooks_when_the_page_leaves()
    {
        await using var fixture = await AppFixture.StartAsync();

        await using (var events = await fixture.OpenEventsAsync("app.battery,app.screen,app.orientation,app.connectivity,app.culture"))
        {
            fixture.Battery.Level = 0.25;
            fixture.Battery.RaiseChanged();
            Assert.Equal("app.battery", (await events.NextAsync(fixture.Timeout)).Name);

            fixture.Screen.Raise(new ScreenMetrics(2532, 1170, 3, DeviceOrientation.Landscape, 120));
            Assert.Equal("app.screen", (await events.NextAsync(fixture.Timeout)).Name);
            Assert.Equal("app.orientation", (await events.NextAsync(fixture.Timeout)).Name);

            fixture.Connectivity.RaiseChanged();
            Assert.Equal("app.connectivity", (await events.NextAsync(fixture.Timeout)).Name);

            fixture.Platform.RaiseCulture(new CultureInfo("en-GB"));
            var (name, data) = await events.NextAsync(fixture.Timeout);
            Assert.Equal("app.culture", name);
            Assert.Contains("en-GB", data);
        }

        await WaitUntil(() => !fixture.Battery.HasListeners && !fixture.Screen.HasListeners && !fixture.Connectivity.HasListeners, fixture.Timeout);
    }

    [Fact]
    public void AddAppBridge_registers_shiny_core_launch_at_login_and_only_a_store_it_was_asked_for()
    {
        var services = new ServiceCollection();
        new AppDeviceBridgeBuilder(new ShinyHttpServerBuilder(services)).AddAppBridge(startup: o => o.Identifier = "sample");

        Assert.Contains(services, x => x.ServiceType == typeof(IPlatform));
        Assert.Contains(services, x => x.ServiceType == typeof(IScreenInfo));
        Assert.Contains(services, x => x.ServiceType == typeof(IAppStartupInstaller));
        Assert.DoesNotContain(services, x => x.ServiceType == typeof(IAppStore));

        var withStore = new ServiceCollection();
        new AppDeviceBridgeBuilder(new ShinyHttpServerBuilder(withStore)).AddAppBridge(store => store.AppleAppId = "123");
        Assert.Contains(withStore, x => x.ServiceType == typeof(IAppStore));
    }

    static StringContent Json(string json) => new(json, System.Text.Encoding.UTF8, "application/json");

    static async Task WaitUntil(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
            await Task.Delay(10, cancellationToken);
    }

    sealed class AppFixture(BuiltInClientTests.HostFixture host, HttpClient webView, ServiceProvider services) : IAsyncDisposable
    {
        readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        public AppBridgeClient Client { get; } = new(host.Transport);

        public HttpClient WebView => webView;

        public CancellationToken Timeout => this.timeout.Token;

        public FakePlatform Platform => services.GetRequiredService<FakePlatform>();

        public FakeScreen Screen => services.GetRequiredService<FakeScreen>();

        public FakeBattery Battery => services.GetRequiredService<FakeBattery>();

        public FakeConnectivity Connectivity => services.GetRequiredService<FakeConnectivity>();

        public FakeStore Store => services.GetRequiredService<FakeStore>();

        public FakeStartup Startup => services.GetRequiredService<FakeStartup>();

        /// <summary>
        /// A host with every Shiny service, or — <paramref name="full"/> false — only what Shiny's core services always
        /// provide: no battery, connectivity or store, and a platform with no startup list.
        /// </summary>
        public static async Task<AppFixture> StartAsync(bool full = true)
        {
            var services = new ServiceCollection()
                .AddSingleton<IWebAppMainThread, InlineWebAppMainThread>()
                .AddSingleton<FakePlatform>()
                .AddSingleton<IPlatform>(sp => sp.GetRequiredService<FakePlatform>())
                .AddSingleton<FakeScreen>()
                .AddSingleton<IScreenInfo>(sp => sp.GetRequiredService<FakeScreen>())
                .AddSingleton<FakeBattery>()
                .AddSingleton<FakeConnectivity>()
                .AddSingleton<FakeStore>()
                .AddSingleton(new FakeStartup(supported: full))
                .AddSingleton<IAppStartupInstaller>(sp => sp.GetRequiredService<FakeStartup>());

            if (full)
            {
                services
                    .AddSingleton<Shiny.Power.IBattery>(sp => sp.GetRequiredService<FakeBattery>())
                    .AddSingleton<Shiny.Net.IConnectivity>(sp => sp.GetRequiredService<FakeConnectivity>())
                    .AddSingleton<IAppStore>(sp => sp.GetRequiredService<FakeStore>());
            }

            var provider = services.BuildServiceProvider();
            HttpClient? webView = null;
            var host = await BuiltInClientTests.HostFixture.StartAsync(_ => [new AppBridge(provider)], new WebAppEventHub(), client => webView = client);
            return new AppFixture(host, webView!, provider);
        }

        public Task<TestEventStream> OpenEventsAsync(string topics) => TestEventStream.OpenAsync(webView, topics, this.Timeout);

        public async ValueTask DisposeAsync()
        {
            await host.DisposeAsync();
            await services.DisposeAsync();
            this.timeout.Dispose();
        }
    }

    sealed class FakePlatform : IPlatform
    {
        public DirectoryInfo AppData { get; } = new(Path.GetTempPath());

        public DirectoryInfo Cache { get; } = new(Path.GetTempPath());

        public DirectoryInfo Public { get; } = new(Path.GetTempPath());

        public Version AppVersion { get; } = new(2, 3, 4);

        public string DeviceManufacturer => "Shiny";

        public string DeviceModel => "Phone 1";

        public Version? PlatformVersion { get; } = new(17, 0);

        public string Platform => "iOS";

        public DeviceIdiom DeviceIdiom => DeviceIdiom.Phone;

        public CultureInfo CurrentCulture { get; } = new("fr-CA");

        public TimeZoneInfo CurrentTimeZone => TimeZoneInfo.Utc;

        public int SettingsOpened { get; private set; }

        public event EventHandler<CultureInfo>? CultureChanged;

        public event EventHandler<TimeZoneInfo>? TimeZoneChanged { add { } remove { } }

        public void RaiseCulture(CultureInfo culture) => this.CultureChanged?.Invoke(this, culture);

        public void InvokeOnMainThread(Action action) => action();

        public void OpenAppSettings() => this.SettingsOpened++;
    }

    sealed class FakeScreen : IScreenInfo
    {
        public ScreenMetrics Metrics { get; private set; } = new(1170, 2532, 3, DeviceOrientation.Portrait, 120);

        public DeviceOrientation Orientation => this.Metrics.Orientation;

        public DeviceOrientation? Requested { get; private set; }

        public bool HasListeners => this.MetricsChanged is not null || this.OrientationChanged is not null;

        public event EventHandler<ScreenMetrics>? MetricsChanged;

        public event EventHandler<DeviceOrientation>? OrientationChanged;

        public void Raise(ScreenMetrics metrics)
        {
            var turned = metrics.Orientation != this.Metrics.Orientation;
            this.Metrics = metrics;
            this.MetricsChanged?.Invoke(this, metrics);

            if (turned)
                this.OrientationChanged?.Invoke(this, metrics.Orientation);
        }

        public Task<bool> SetOrientation(DeviceOrientation orientation)
        {
            this.Requested = orientation;
            return Task.FromResult(true);
        }

        public Task<bool> ResetOrientation()
        {
            this.Requested = null;
            return Task.FromResult(true);
        }
    }

    sealed class FakeBattery : Shiny.Power.IBattery
    {
        public Shiny.Power.BatteryState Status { get; set; } = Shiny.Power.BatteryState.Charging;

        public double Level { get; set; } = 0.5;

        public Shiny.Power.BatteryPowerSource PowerSource => Shiny.Power.BatteryPowerSource.AC;

        public Shiny.Power.EnergySaverStatus EnergySaverStatus => Shiny.Power.EnergySaverStatus.On;

        public bool HasListeners => this.Changed is not null;

        public event EventHandler? Changed;

        public void RaiseChanged() => this.Changed?.Invoke(this, EventArgs.Empty);
    }

    sealed class FakeConnectivity : Shiny.Net.IConnectivity
    {
        public ConnectionTypes ConnectionTypes => ConnectionTypes.Wifi | ConnectionTypes.Wired;

        public Shiny.Net.NetworkAccess Access => Shiny.Net.NetworkAccess.Internet;

        public bool HasListeners => this.Changed is not null;

        public event EventHandler? Changed;

        public void RaiseChanged() => this.Changed?.Invoke(this, EventArgs.Empty);
    }

    sealed class FakeStore : IAppStore
    {
        public AppStoreResult? Current { get; set; }

        public List<string> Calls { get; } = [];

        public Task<AppStoreResult?> GetCurrent(CancellationToken cancellationToken = default) => Task.FromResult(this.Current);

        public Task<bool> OpenStore() => this.Call("open", true);

        public Task<bool> RequestReview() => this.Call("review", true);

        public Task<bool> OpenReviewPage() => this.Call("review-page", false);

        Task<bool> Call(string name, bool result)
        {
            this.Calls.Add(name);
            return Task.FromResult(result);
        }
    }

    sealed class FakeStartup(bool supported) : IAppStartupInstaller
    {
        AppStartupState state = AppStartupState.NotRegistered;

        public bool IsSupported => supported;

        public bool Refuse { get; set; }

        public Task<AppStartupState> GetState(CancellationToken cancellationToken = default) => Task.FromResult(this.state);

        public Task<AppStartupState> Register(CancellationToken cancellationToken = default)
            => this.Settle(AppStartupState.RequiresApproval);

        public Task<AppStartupState> Unregister(CancellationToken cancellationToken = default)
            => this.Settle(AppStartupState.NotRegistered);

        public Task<bool> OpenSettings() => Task.FromResult(true);

        Task<AppStartupState> Settle(AppStartupState next)
        {
            if (this.Refuse)
                throw new InvalidOperationException("The OS refused.");

            this.state = next;
            return Task.FromResult(next);
        }
    }
}
