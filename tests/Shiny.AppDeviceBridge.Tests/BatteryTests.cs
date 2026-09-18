using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Devices;
using Shiny.AppDeviceBridge.AppSupport;
using Shiny.AppDeviceBridge.AppSupport.Linux;
using Contracts = Shiny.AppDeviceBridge.AppSupport.Client;

namespace Shiny.AppDeviceBridge.Tests;

// Inside the namespace: Shiny.Core's own BatteryState is otherwise found first, through the enclosing Shiny namespace.
using BatteryState = Microsoft.Maui.Devices.BatteryState;

/// <summary>
/// The app bridge's battery comes from the <see cref="IBattery"/> in the container when there is one — how the maui-labs
/// heads and <see cref="UPowerBattery"/> supply theirs — and its events are hooked only while a page listens.
/// </summary>
public class BatteryTests
{
    [Fact]
    public async Task Reads_the_battery_the_container_supplies()
    {
        var battery = new FakeBattery { ChargeLevel = 0.42, State = BatteryState.Discharging, PowerSource = BatteryPowerSource.Battery, EnergySaverStatus = EnergySaverStatus.On };
        var (fixture, webView) = await StartAsync(battery);
        await using var _ = fixture;

        var info = await webView.GetFromJsonAsync("/_bridge/app/battery", Contracts.AppJsonContext.Default.BatteryInfo, TestContext.Current.CancellationToken);

        Assert.Equal(new Contracts.BatteryInfo(0.42, Contracts.BatteryState.Discharging, Contracts.BatteryPowerSource.Battery, Contracts.EnergySaverStatus.On), info);
    }

    [Fact]
    public async Task Streams_changes_only_while_the_page_listens()
    {
        var battery = new FakeBattery();
        var (fixture, webView) = await StartAsync(battery);
        await using var _ = fixture;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Assert.Equal((0, 0), (battery.InfoHandlers, battery.EnergySaverHandlers));

        var stream = await TestEventStream.OpenAsync(webView, "app.battery,app.energysaver", timeout.Token);
        Assert.Equal((1, 1), (battery.InfoHandlers, battery.EnergySaverHandlers));

        battery.RaiseInfo(0.9, BatteryState.Charging, BatteryPowerSource.Usb);
        Assert.Equal(("app.battery", """{"chargeLevel":0.9,"state":"Charging","powerSource":"Usb"}"""), await stream.NextAsync(timeout.Token));

        battery.RaiseEnergySaver(EnergySaverStatus.On);
        Assert.Equal(("app.energysaver", """{"energySaverStatus":"On"}"""), await stream.NextAsync(timeout.Token));

        await stream.DisposeAsync();
        while (battery.InfoHandlers + battery.EnergySaverHandlers > 0)
            await Task.Delay(10, timeout.Token);
    }

    [Theory]
    [InlineData(true, 55.0, 1u, false, 0.55, BatteryState.Charging, BatteryPowerSource.AC)]
    [InlineData(true, 30.0, 2u, true, 0.30, BatteryState.Discharging, BatteryPowerSource.Battery)]
    [InlineData(true, 0.0, 3u, true, 0.0, BatteryState.Discharging, BatteryPowerSource.Battery)]
    [InlineData(true, 100.0, 4u, false, 1.0, BatteryState.Full, BatteryPowerSource.AC)]
    [InlineData(true, 80.0, 5u, false, 0.8, BatteryState.NotCharging, BatteryPowerSource.AC)]
    [InlineData(true, 80.0, 6u, false, 0.8, BatteryState.NotCharging, BatteryPowerSource.AC)]
    [InlineData(true, 80.0, 0u, false, 0.8, BatteryState.Unknown, BatteryPowerSource.AC)]
    [InlineData(false, 0.0, 0u, false, 1.0, BatteryState.NotPresent, BatteryPowerSource.AC)]
    public void Maps_upower_onto_essentials(bool present, double percentage, uint state, bool onBattery, double level, BatteryState expected, BatteryPowerSource source)
        => Assert.Equal(new UPowerBattery.BatteryReading(level, expected, source), UPowerBattery.ToReading(present, percentage, state, onBattery));

    [Theory]
    [InlineData("power-saver", EnergySaverStatus.On)]
    [InlineData("balanced", EnergySaverStatus.Off)]
    [InlineData("performance", EnergySaverStatus.Off)]
    [InlineData(null, EnergySaverStatus.Unknown)]
    public void Maps_the_power_profile_onto_energy_saver(string? profile, EnergySaverStatus expected)
        => Assert.Equal(expected, UPowerBattery.ToEnergySaver(profile));

    static async Task<(BuiltInClientTests.HostFixture Host, HttpClient WebView)> StartAsync(IBattery battery)
    {
        var services = new ServiceCollection().AddSingleton(battery).BuildServiceProvider();
        HttpClient? webView = null;

        var host = await BuiltInClientTests.HostFixture.StartAsync(_ => [new AppSupportBridge(services)], onStarted: client => webView = client);
        return (host, webView!);
    }

    sealed class FakeBattery : IBattery
    {
        EventHandler<BatteryInfoChangedEventArgs>? info;
        EventHandler<EnergySaverStatusChangedEventArgs>? energySaver;

        public double ChargeLevel { get; set; } = 1;

        public BatteryState State { get; set; } = BatteryState.Full;

        public BatteryPowerSource PowerSource { get; set; } = BatteryPowerSource.AC;

        public EnergySaverStatus EnergySaverStatus { get; set; } = EnergySaverStatus.Off;

        public int InfoHandlers => this.info?.GetInvocationList().Length ?? 0;

        public int EnergySaverHandlers => this.energySaver?.GetInvocationList().Length ?? 0;

        public event EventHandler<BatteryInfoChangedEventArgs> BatteryInfoChanged
        {
            add => this.info += value;
            remove => this.info -= value;
        }

        public event EventHandler<EnergySaverStatusChangedEventArgs> EnergySaverStatusChanged
        {
            add => this.energySaver += value;
            remove => this.energySaver -= value;
        }

        public void RaiseInfo(double level, BatteryState state, BatteryPowerSource source)
            => this.info?.Invoke(this, new BatteryInfoChangedEventArgs(level, state, source));

        public void RaiseEnergySaver(EnergySaverStatus status)
            => this.energySaver?.Invoke(this, new EnergySaverStatusChangedEventArgs(status));
    }
}
