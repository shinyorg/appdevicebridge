using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Biometrics;
using Shiny.AppDeviceBridge.Biometrics.Client;
using Shiny.AppDeviceBridge.Client;
using Native = Shiny.Biometrics;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The biometrics bridge over a fake <see cref="Native.IBiometrics"/>: the routes, request validation and the mapping.
/// LocalAuthentication, BiometricPrompt and Windows Hello themselves are Shiny.Biometrics' and need a device.
/// </summary>
public class BiometricsBridgeTests
{
    [Fact]
    public async Task Answers_501_off_the_biometric_platforms()
    {
        await using var fixture = await BiometricsFixture.StartAsync(new FakeBiometrics(), biometricPlatform: false);

        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetAvailabilityAsync())).IsNotSupported);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.AuthenticateAsync(new BiometricRequest("Unlock")))).IsNotSupported);
        Assert.False(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "biometrics").IsSupported);
    }

    [Fact]
    public async Task Answers_501_without_biometrics_registered()
    {
        await using var fixture = await BiometricsFixture.StartAsync(null);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetAvailabilityAsync())).IsNotSupported);
    }

    [Fact]
    public async Task Reports_availability_with_each_sensor_as_a_list()
    {
        var biometrics = new FakeBiometrics
        {
            Availability = new Native.BiometricAvailability(
                Native.BiometricStatus.Available,
                Native.BiometricType.Fingerprint | Native.BiometricType.Face,
                true
            )
        };
        await using var fixture = await BiometricsFixture.StartAsync(biometrics);

        var availability = await fixture.Client.GetAvailabilityAsync();

        Assert.Equal(BiometricStatus.Available, availability.Status);
        Assert.Equal([BiometricType.Fingerprint, BiometricType.Face], availability.Types);
        Assert.True(availability.IsDeviceCredentialSet);
        Assert.Equal(Native.BiometricStrength.Strong, biometrics.Strength);
        Assert.True(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "biometrics").IsSupported);
    }

    [Fact]
    public async Task Passes_the_pages_strength()
    {
        var biometrics = new FakeBiometrics();
        await using var fixture = await BiometricsFixture.StartAsync(biometrics);

        await fixture.Client.GetAvailabilityAsync(BiometricStrength.Weak);

        Assert.Equal(Native.BiometricStrength.Weak, biometrics.Strength);
    }

    [Theory]
    [InlineData(Native.BiometricStatus.NotEnrolled, BiometricStatus.NotEnrolled)]
    [InlineData(Native.BiometricStatus.LockedOut, BiometricStatus.LockedOut)]
    [InlineData(Native.BiometricStatus.PasscodeNotSet, BiometricStatus.PasscodeNotSet)]
    [InlineData(Native.BiometricStatus.NotSupported, BiometricStatus.NotSupported)]
    [InlineData(Native.BiometricStatus.Unavailable, BiometricStatus.Unavailable)]
    [InlineData(Native.BiometricStatus.SecurityUpdateRequired, BiometricStatus.SecurityUpdateRequired)]
    [InlineData((Native.BiometricStatus)99, BiometricStatus.Unavailable)]
    public void Maps_every_status_and_never_turns_an_unknown_one_into_available(Native.BiometricStatus native, BiometricStatus expected)
        => Assert.Equal(expected, BiometricsBridge.ToContract(native));

    [Fact]
    public async Task Authenticates_on_the_main_thread_with_the_pages_request()
    {
        var biometrics = new FakeBiometrics();
        await using var fixture = await BiometricsFixture.StartAsync(biometrics);

        var result = await fixture.Client.AuthenticateAsync(new BiometricRequest(
            "Unlock your vault",
            Title: "Unlock",
            Subtitle: "Vault",
            CancelText: "Not now",
            FallbackText: "",
            AllowDeviceCredential: true,
            Strength: BiometricStrength.Weak,
            ConfirmationRequired: false
        ));

        Assert.Equal(BiometricResultStatus.Success, result.Status);
        Assert.Equal(1, fixture.MainThread.Calls);

        var request = biometrics.Request!;
        Assert.Equal("Unlock your vault", request.Reason);
        Assert.Equal("Unlock", request.Title);
        Assert.Equal("Vault", request.Subtitle);
        Assert.Equal("Not now", request.CancelText);
        Assert.Equal("", request.FallbackText);
        Assert.True(request.AllowDeviceCredential);
        Assert.Equal(Native.BiometricStrength.Weak, request.Strength);
        Assert.False(request.ConfirmationRequired);
    }

    [Theory]
    [InlineData(Native.BiometricResultStatus.Cancelled, BiometricResultStatus.Cancelled)]
    [InlineData(Native.BiometricResultStatus.Fallback, BiometricResultStatus.Fallback)]
    [InlineData(Native.BiometricResultStatus.Failed, BiometricResultStatus.Failed)]
    [InlineData(Native.BiometricResultStatus.LockedOut, BiometricResultStatus.LockedOut)]
    [InlineData(Native.BiometricResultStatus.NotEnrolled, BiometricResultStatus.NotEnrolled)]
    [InlineData(Native.BiometricResultStatus.PasscodeNotSet, BiometricResultStatus.PasscodeNotSet)]
    [InlineData(Native.BiometricResultStatus.Timeout, BiometricResultStatus.Timeout)]
    [InlineData(Native.BiometricResultStatus.NotAvailable, BiometricResultStatus.NotAvailable)]
    [InlineData((Native.BiometricResultStatus)99, BiometricResultStatus.NotAvailable)]
    public async Task A_failed_prompt_is_a_result_not_an_error(Native.BiometricResultStatus native, BiometricResultStatus expected)
    {
        var biometrics = new FakeBiometrics { Result = new Native.BiometricResult(native, "Too many attempts") };
        await using var fixture = await BiometricsFixture.StartAsync(biometrics);

        var result = await fixture.Client.AuthenticateAsync(new BiometricRequest("Unlock"));

        Assert.Equal(expected, result.Status);
        Assert.Equal("Too many attempts", result.ErrorMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Refuses_an_empty_reason(string reason)
    {
        var biometrics = new FakeBiometrics();
        await using var fixture = await BiometricsFixture.StartAsync(biometrics);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.AuthenticateAsync(new BiometricRequest(reason)));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Null(biometrics.Request);
    }

    [Fact]
    public async Task Refuses_text_longer_than_the_prompt_takes()
    {
        var biometrics = new FakeBiometrics();
        await using var fixture = await BiometricsFixture.StartAsync(biometrics);
        var tooLong = new string('x', BiometricsBridge.MaxTextLength + 1);

        Assert.Equal(HttpStatusCode.BadRequest, (await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.AuthenticateAsync(new BiometricRequest(tooLong)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.AuthenticateAsync(new BiometricRequest("Unlock", Title: tooLong)))).StatusCode);
        Assert.Null(biometrics.Request);
    }

    [Fact]
    public async Task Nothing_to_prompt_from_is_a_conflict()
    {
        var biometrics = new FakeBiometrics { Failure = new InvalidOperationException("Info.plist is missing NSFaceIDUsageDescription") };
        await using var fixture = await BiometricsFixture.StartAsync(biometrics);

        var failed = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.AuthenticateAsync(new BiometricRequest("Unlock")));

        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        Assert.Equal("cannot_prompt", failed.Code);
        Assert.Contains("NSFaceIDUsageDescription", failed.Message);
    }

    sealed class BiometricsFixture : IAsyncDisposable
    {
        BuiltInClientTests.HostFixture host = null!;

        public IBridgeTransport Transport => this.host.Transport;
        public BiometricsBridgeClient Client { get; private set; } = null!;
        public CountingMainThread MainThread { get; } = new();

        public static async Task<BiometricsFixture> StartAsync(Native.IBiometrics? biometrics, bool biometricPlatform = true)
        {
            var fixture = new BiometricsFixture();
            var services = new ServiceCollection();
            if (biometrics is not null)
                services.AddSingleton(biometrics);
            services.AddSingleton<IWebAppMainThread>(fixture.MainThread);
            var provider = services.BuildServiceProvider();

            fixture.host = await BuiltInClientTests.HostFixture.StartAsync(
                _ => [new BiometricsBridge(provider, biometricPlatform)],
                null,
                _ => { }
            );

            fixture.Client = new BiometricsBridgeClient(fixture.host.Transport);
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

    sealed class FakeBiometrics : Native.IBiometrics
    {
        public Native.BiometricAvailability Availability { get; init; } = new(Native.BiometricStatus.Available, Native.BiometricType.Face, true);
        public Native.BiometricResult Result { get; init; } = Native.BiometricResult.Succeeded;
        public Exception? Failure { get; init; }

        public Native.BiometricStrength? Strength { get; private set; }
        public Native.BiometricRequest? Request { get; private set; }

        public Task<Native.BiometricAvailability> GetAvailability(Native.BiometricStrength strength = Native.BiometricStrength.Strong, CancellationToken cancellationToken = default)
        {
            this.Strength = strength;
            return Task.FromResult(this.Availability);
        }

        public Task<Native.BiometricResult> Authenticate(Native.BiometricRequest request, CancellationToken cancellationToken = default)
        {
            if (this.Failure is not null)
                return Task.FromException<Native.BiometricResult>(this.Failure);

            this.Request = request;
            return Task.FromResult(this.Result);
        }
    }
}
