using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.AgeSignals;
using Shiny.AppDeviceBridge.AgeSignals.Client;
using Shiny.AppDeviceBridge.Client;
using Native = Shiny.AgeSignals;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The age signals bridge over a fake <see cref="Native.IAgeSignals"/>: the routes, gate validation, the mapping and the
/// store's errors. Declared Age Range and Play Age Signals themselves are Shiny.AgeSignals' and need a store build.
/// </summary>
public class AgeSignalsBridgeTests
{
    [Fact]
    public async Task Answers_501_off_the_store_platforms()
    {
        await using var fixture = await AgeSignalsFixture.StartAsync(new FakeAgeSignals(), storePlatform: false);

        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetEligibilityAsync())).IsNotSupported);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.RequestAgeRangeAsync(new AgeRangeRequest([18])))).IsNotSupported);
        Assert.False(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "agesignals").IsSupported);
    }

    [Fact]
    public async Task Answers_501_without_age_signals_registered()
    {
        await using var fixture = await AgeSignalsFixture.StartAsync(null);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetEligibilityAsync())).IsNotSupported);
    }

    [Fact]
    public async Task Reports_eligibility()
    {
        var signals = new FakeAgeSignals
        {
            Eligibility = new Native.AgeSignalsEligibility(true, IsEligible: true, AgeRangeRequired: true, SignificantChangeRequiresAdultNotification: false)
        };
        await using var fixture = await AgeSignalsFixture.StartAsync(signals);

        Assert.Equal(
            new AgeSignalsEligibility(true, true, true, null, false),
            await fixture.Client.GetEligibilityAsync()
        );
        Assert.True(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "agesignals").IsSupported);
    }

    [Fact]
    public async Task Requests_the_age_range_on_the_main_thread_with_the_pages_gates()
    {
        var approved = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var signals = new FakeAgeSignals
        {
            Signal = new Native.AgeSignal(
                Native.AgeSignalStatus.Shared,
                13,
                15,
                Native.AgeRangeSource.GuardianDeclared,
                CommunicationLimits: true,
                Native.SignificantChangeStatus.Pending,
                approved,
                "install-1"
            )
        };
        await using var fixture = await AgeSignalsFixture.StartAsync(signals);

        var signal = await fixture.Client.RequestAgeRangeAsync(new AgeRangeRequest([13, 16, 18]));

        Assert.Equal(
            new AgeSignal(AgeSignalStatus.Shared, 13, 15, AgeRangeSource.GuardianDeclared, true, SignificantChangeStatus.Pending, approved, "install-1"),
            signal
        );
        Assert.Equal((13, (int?)16, (int?)18), signals.Gates);
        Assert.Equal(1, fixture.MainThread.Calls);

        Assert.True(signal.IsAtLeast(13));
        Assert.False(signal.IsAtLeast(16));
        Assert.Null(signal.IsAtLeast(14));
    }

    [Fact]
    public async Task Declining_is_a_result_not_an_error()
    {
        var signals = new FakeAgeSignals { Signal = new Native.AgeSignal(Native.AgeSignalStatus.Declined) };
        await using var fixture = await AgeSignalsFixture.StartAsync(signals);

        var signal = await fixture.Client.RequestAgeRangeAsync(new AgeRangeRequest([18]));

        Assert.Equal(AgeSignalStatus.Declined, signal.Status);
        Assert.Equal((18, (int?)null, (int?)null), signals.Gates);
        Assert.Null(signal.IsAtLeast(18));
    }

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 13, 16, 18, 21 })]
    [InlineData(new[] { 16, 13 })]
    [InlineData(new[] { 13, 13 })]
    [InlineData(new[] { 0 })]
    [InlineData(new[] { -1, 18 })]
    public async Task Refuses_gates_that_are_not_one_to_three_ascending_ages(int[] gates)
    {
        var signals = new FakeAgeSignals();
        await using var fixture = await AgeSignalsFixture.StartAsync(signals);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.RequestAgeRangeAsync(new AgeRangeRequest(gates)));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Null(signals.Gates);
    }

    [Theory]
    [InlineData(Native.AgeSignalsErrorReason.Network, HttpStatusCode.ServiceUnavailable, "network")]
    [InlineData(Native.AgeSignalsErrorReason.AppNotOwned, HttpStatusCode.Forbidden, "app_not_owned")]
    [InlineData(Native.AgeSignalsErrorReason.InvalidAccount, HttpStatusCode.Conflict, "invalid_account")]
    [InlineData(Native.AgeSignalsErrorReason.NoUserInterface, HttpStatusCode.Conflict, "no_user_interface")]
    [InlineData(Native.AgeSignalsErrorReason.StoreUnavailable, HttpStatusCode.ServiceUnavailable, "store_unavailable")]
    [InlineData(Native.AgeSignalsErrorReason.Unknown, HttpStatusCode.BadGateway, "age_signals_failed")]
    public async Task Store_failures_become_errors_the_page_can_switch_on(Native.AgeSignalsErrorReason reason, HttpStatusCode status, string code)
    {
        var signals = new FakeAgeSignals { Failure = new Native.AgeSignalsException(reason, "The store said no", nativeCode: -3) };
        await using var fixture = await AgeSignalsFixture.StartAsync(signals);

        var failed = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.RequestAgeRangeAsync(new AgeRangeRequest([18])));

        Assert.Equal(status, failed.StatusCode);
        Assert.Equal(code, failed.Code);
        Assert.Contains("store code -3", failed.Message);
    }

    [Fact]
    public async Task Shows_the_significant_update_sheet()
    {
        var signals = new FakeAgeSignals { SignificantUpdateShown = true };
        await using var fixture = await AgeSignalsFixture.StartAsync(signals);

        Assert.True((await fixture.Client.ShowSignificantUpdateAsync(new SignificantUpdateRequest("Adds chat with other players"))).Shown);
        Assert.Equal("Adds chat with other players", signals.SignificantUpdate);
        Assert.Equal(1, fixture.MainThread.Calls);

        var blank = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.ShowSignificantUpdateAsync(new SignificantUpdateRequest(" ")));
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

        var tooLong = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.ShowSignificantUpdateAsync(new SignificantUpdateRequest(new string('x', 1025))));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    sealed class AgeSignalsFixture : IAsyncDisposable
    {
        BuiltInClientTests.HostFixture host = null!;

        public IBridgeTransport Transport => this.host.Transport;
        public AgeSignalsBridgeClient Client { get; private set; } = null!;
        public CountingMainThread MainThread { get; } = new();

        public static async Task<AgeSignalsFixture> StartAsync(Native.IAgeSignals? signals, bool storePlatform = true)
        {
            var fixture = new AgeSignalsFixture();
            var services = new ServiceCollection();
            if (signals is not null)
                services.AddSingleton(signals);
            services.AddSingleton<IWebAppMainThread>(fixture.MainThread);
            var provider = services.BuildServiceProvider();

            fixture.host = await BuiltInClientTests.HostFixture.StartAsync(
                _ => [new AgeSignalsBridge(provider, storePlatform)],
                null,
                _ => { }
            );

            fixture.Client = new AgeSignalsBridgeClient(fixture.host.Transport);
            return fixture;
        }

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    sealed class CountingMainThread : IWebAppMainThread
    {
        public int Calls { get; private set; }

        public Task<T> InvokeAsync<T>(Func<Task<T>> action)
        {
            this.Calls++;
            return action();
        }
    }

    sealed class FakeAgeSignals : Native.IAgeSignals
    {
        public Native.AgeSignalsEligibility Eligibility { get; init; } = new(true);
        public Native.AgeSignal Signal { get; init; } = new(Native.AgeSignalStatus.Shared, 18, null, Native.AgeRangeSource.Confirmed);
        public bool SignificantUpdateShown { get; init; }
        public Exception? Failure { get; init; }

        public (int, int?, int?)? Gates { get; private set; }
        public string? SignificantUpdate { get; private set; }

        public Task<Native.AgeSignalsEligibility> GetEligibility(CancellationToken cancellationToken = default)
            => Task.FromResult(this.Eligibility);

        public Task<Native.AgeSignal> RequestAgeRange(int gate1, int? gate2 = null, int? gate3 = null, CancellationToken cancellationToken = default)
        {
            if (this.Failure is not null)
                return Task.FromException<Native.AgeSignal>(this.Failure);

            this.Gates = (gate1, gate2, gate3);
            return Task.FromResult(this.Signal);
        }

        public Task<bool> ShowSignificantUpdate(string updateDescription, CancellationToken cancellationToken = default)
        {
            this.SignificantUpdate = updateDescription;
            return Task.FromResult(this.SignificantUpdateShown);
        }
    }
}
