using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Maui;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The passkeys bridge over a fake authenticator: the routes, the relying party allow-list, the pass-through of the
/// server's JSON and how a ceremony's failures reach the page. The native sheets themselves are MAUI Essentials' and need
/// a device with an associated domain.
/// </summary>
public class PasskeysBridgeTests
{
    const string CreationOptions = """{"rp":{"id":"example.com","name":"Example"},"user":{"id":"dXNlcg","name":"ada","displayName":"Ada"},"challenge":"Y2hhbGxlbmdl","pubKeyCredParams":[{"type":"public-key","alg":-7}]}""";
    const string RequestOptions = """{"rpId":"example.com","challenge":"Y2hhbGxlbmdl","userVerification":"required"}""";

    [Fact]
    public async Task Answers_501_where_the_platform_has_no_passkeys()
    {
        await using var fixture = await PasskeysFixture.StartAsync(new FakeAuthenticator { IsSupported = false });

        Assert.False((await fixture.Client.GetStatusAsync()).Supported);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.CreateAsync(new PasskeyCreateRequest(Json(CreationOptions))))).IsNotSupported);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.AssertAsync(new PasskeyAssertRequest(Json(RequestOptions))))).IsNotSupported);
        Assert.False(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "passkeys").IsSupported);
    }

    [Fact]
    public async Task Reports_support_and_the_registered_relying_parties()
    {
        await using var fixture = await PasskeysFixture.StartAsync(new FakeAuthenticator(), "example.com", "login.example.org");

        var status = await fixture.Client.GetStatusAsync();

        Assert.True(status.Supported);
        Assert.Equal(["example.com", "login.example.org"], status.RelyingPartyIds);
        Assert.True(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "passkeys").IsSupported);
    }

    [Fact]
    public async Task Creates_on_the_main_thread_passing_the_servers_json_through_both_ways()
    {
        var authenticator = new FakeAuthenticator();
        await using var fixture = await PasskeysFixture.StartAsync(authenticator);

        var result = await fixture.Client.CreateAsync(new PasskeyCreateRequest(Json(CreationOptions), PreferImmediatelyAvailable: true));

        Assert.Equal(1, fixture.MainThread.Calls);
        Assert.True(JsonElement.DeepEquals(Json(CreationOptions), Json(authenticator.OptionsJson!)));
        Assert.True(authenticator.PreferImmediatelyAvailable);
        Assert.Equal("Y3JlZA", result.Id);
        Assert.Equal("attestation", result.Response.GetProperty("response").GetProperty("attestationObject").GetString());
    }

    [Fact]
    public async Task Asserts_with_the_user_handle()
    {
        var authenticator = new FakeAuthenticator();
        await using var fixture = await PasskeysFixture.StartAsync(authenticator);

        var result = await fixture.Client.AssertAsync(new PasskeyAssertRequest(Json(RequestOptions)));

        Assert.True(JsonElement.DeepEquals(Json(RequestOptions), Json(authenticator.OptionsJson!)));
        Assert.False(authenticator.PreferImmediatelyAvailable);
        Assert.Equal("Y3JlZA", result.Id);
        Assert.Equal("dXNlcg", result.UserHandle);
        Assert.Equal("signature", result.Response.GetProperty("response").GetProperty("signature").GetString());
    }

    [Fact]
    public async Task Matches_the_relying_party_id_without_regard_to_case()
    {
        var authenticator = new FakeAuthenticator();
        await using var fixture = await PasskeysFixture.StartAsync(authenticator, "Example.COM");

        await fixture.Client.AssertAsync(new PasskeyAssertRequest(Json(RequestOptions)));

        Assert.NotNull(authenticator.OptionsJson);
    }

    [Theory]
    [InlineData("""{"rp":{"id":"evil.example"},"challenge":"Y2hhbGxlbmdl"}""")]
    [InlineData("""{"rp":{"id":"sub.example.com"},"challenge":"Y2hhbGxlbmdl"}""")]
    public async Task Refuses_to_create_for_a_relying_party_the_app_did_not_register(string options)
    {
        var authenticator = new FakeAuthenticator();
        await using var fixture = await PasskeysFixture.StartAsync(authenticator);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.CreateAsync(new PasskeyCreateRequest(Json(options))));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("rp_not_allowed", refused.Code);
        Assert.Null(authenticator.OptionsJson);
    }

    [Fact]
    public async Task Refuses_to_assert_for_a_relying_party_the_app_did_not_register()
    {
        var authenticator = new FakeAuthenticator();
        await using var fixture = await PasskeysFixture.StartAsync(authenticator);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.AssertAsync(new PasskeyAssertRequest(Json("""{"rpId":"evil.example","challenge":"Y2hhbGxlbmdl"}"""))));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Null(authenticator.OptionsJson);
    }

    [Theory]
    [InlineData("""{"challenge":"Y2hhbGxlbmdl"}""")]
    [InlineData("""{"rp":"example.com","challenge":"Y2hhbGxlbmdl"}""")]
    [InlineData("""{"rp":{"id":7}}""")]
    [InlineData("""{"rpId":"example.com"}""")]
    [InlineData("[]")]
    [InlineData("\"example.com\"")]
    public async Task Creation_options_without_an_rp_id_are_a_bad_request(string options)
    {
        var authenticator = new FakeAuthenticator();
        await using var fixture = await PasskeysFixture.StartAsync(authenticator);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.CreateAsync(new PasskeyCreateRequest(Json(options))));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Null(authenticator.OptionsJson);
    }

    [Theory]
    [InlineData("""{"challenge":"Y2hhbGxlbmdl"}""")]
    [InlineData("""{"rp":{"id":"example.com"}}""")]
    [InlineData("""{"rpId":""}""")]
    public async Task Request_options_without_an_rp_id_are_a_bad_request(string options)
    {
        var authenticator = new FakeAuthenticator();
        await using var fixture = await PasskeysFixture.StartAsync(authenticator);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.AssertAsync(new PasskeyAssertRequest(Json(options))));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Null(authenticator.OptionsJson);
    }

    [Fact]
    public async Task A_dismissed_sheet_is_a_cancelled_conflict()
    {
        await using var fixture = await PasskeysFixture.StartAsync(new FakeAuthenticator { Failure = new TaskCanceledException() });

        var failed = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.CreateAsync(new PasskeyCreateRequest(Json(CreationOptions))));

        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        Assert.Equal("cancelled", failed.Code);
    }

    [Fact]
    public async Task A_failed_ceremony_is_a_conflict_with_the_platforms_message()
    {
        await using var fixture = await PasskeysFixture.StartAsync(new FakeAuthenticator { Failure = new InvalidOperationException("The app is not associated with example.com") });

        var failed = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.AssertAsync(new PasskeyAssertRequest(Json(RequestOptions))));

        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        Assert.Equal("failed", failed.Code);
        Assert.Contains("not associated", failed.Message);
    }

    [Fact]
    public async Task Options_the_platform_rejects_are_a_bad_request()
    {
        await using var fixture = await PasskeysFixture.StartAsync(new FakeAuthenticator { Failure = new ArgumentException("challenge is missing") });

        var failed = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.CreateAsync(new PasskeyCreateRequest(Json(CreationOptions))));

        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        Assert.Contains("challenge", failed.Message);
    }

    [Fact]
    public async Task An_os_version_without_passkeys_is_501_mid_ceremony()
    {
        await using var fixture = await PasskeysFixture.StartAsync(new FakeAuthenticator { Failure = new Microsoft.Maui.ApplicationModel.FeatureNotSupportedException() });

        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.CreateAsync(new PasskeyCreateRequest(Json(CreationOptions))))).IsNotSupported);
    }

    [Fact]
    public void Essentials_reference_build_reports_unsupported_rather_than_throwing()
        // This test host runs Essentials' net11.0 build, the one the macOS AppKit and GTK4 heads get.
        => Assert.False(new PasskeysBridge(new ServiceCollection().BuildServiceProvider(), Options("example.com")).IsSupported);

    [Fact]
    public void Registration_accumulates_relying_parties_and_adds_the_bridge_once()
    {
        var services = new ServiceCollection();
        var bridge = new AppDeviceBridgeBuilder(new ShinyHttpServerBuilder(services));

        bridge.AddPasskeysBridge("example.com").AddPasskeysBridge("login.example.org", "EXAMPLE.com");

        var options = Assert.Single(services, x => x.ServiceType == typeof(PasskeysBridgeOptions)).ImplementationInstance as PasskeysBridgeOptions;
        Assert.Equal(["example.com", "login.example.org"], options!.RelyingPartyIds);
        Assert.Single(services, x => x.ServiceType == typeof(IWebAppBridge) && x.ImplementationType == typeof(PasskeysBridge));
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://example.com")]
    [InlineData("example.com/login")]
    [InlineData("127.0.0.1")]
    public void Registration_refuses_what_is_not_a_relying_party_id(string id)
    {
        var bridge = new AppDeviceBridgeBuilder(new ShinyHttpServerBuilder(new ServiceCollection()));
        Assert.Throws<InvalidOperationException>(() => bridge.AddPasskeysBridge(id));
    }

    [Fact]
    public void Registration_needs_a_relying_party()
    {
        var bridge = new AppDeviceBridgeBuilder(new ShinyHttpServerBuilder(new ServiceCollection()));
        Assert.Throws<InvalidOperationException>(() => bridge.AddPasskeysBridge());
    }

    static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    static PasskeysBridgeOptions Options(params string[] ids)
    {
        var services = new ServiceCollection();
        new AppDeviceBridgeBuilder(new ShinyHttpServerBuilder(services)).AddPasskeysBridge(ids);
        return (PasskeysBridgeOptions)services.Single(x => x.ServiceType == typeof(PasskeysBridgeOptions)).ImplementationInstance!;
    }

    sealed class PasskeysFixture : IAsyncDisposable
    {
        BuiltInClientTests.HostFixture host = null!;

        public IBridgeTransport Transport => this.host.Transport;
        public PasskeysBridgeClient Client { get; private set; } = null!;
        public CountingMainThread MainThread { get; } = new();

        public static async Task<PasskeysFixture> StartAsync(FakeAuthenticator authenticator, params string[] relyingPartyIds)
        {
            var fixture = new PasskeysFixture();
            var services = new ServiceCollection();
            services.AddSingleton<IWebAppMainThread>(fixture.MainThread);
            var provider = services.BuildServiceProvider();
            var options = Options(relyingPartyIds.Length == 0 ? ["example.com"] : relyingPartyIds);

            fixture.host = await BuiltInClientTests.HostFixture.StartAsync(
                _ => [new PasskeysBridge(provider, options, authenticator)],
                null,
                _ => { }
            );

            fixture.Client = new PasskeysBridgeClient(fixture.host.Transport);
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

    sealed class FakeAuthenticator : IPasskeyAuthenticator
    {
        public bool IsSupported { get; init; } = true;
        public Exception? Failure { get; init; }

        public string? OptionsJson { get; private set; }
        public bool? PreferImmediatelyAvailable { get; private set; }

        public Task<PasskeyCredential> CreateAsync(string optionsJson, bool preferImmediatelyAvailable, CancellationToken cancellationToken)
            => this.Ceremony(optionsJson, preferImmediatelyAvailable, new PasskeyCredential(
                "Y3JlZA",
                null,
                """{"id":"Y3JlZA","rawId":"Y3JlZA","type":"public-key","response":{"clientDataJSON":"e30","attestationObject":"attestation"}}"""
            ));

        public Task<PasskeyCredential> AssertAsync(string optionsJson, bool preferImmediatelyAvailable, CancellationToken cancellationToken)
            => this.Ceremony(optionsJson, preferImmediatelyAvailable, new PasskeyCredential(
                "Y3JlZA",
                "dXNlcg",
                """{"id":"Y3JlZA","rawId":"Y3JlZA","type":"public-key","response":{"clientDataJSON":"e30","authenticatorData":"YXV0aA","signature":"signature","userHandle":"dXNlcg"}}"""
            ));

        Task<PasskeyCredential> Ceremony(string optionsJson, bool preferImmediatelyAvailable, PasskeyCredential credential)
        {
            if (this.Failure is not null)
                return Task.FromException<PasskeyCredential>(this.Failure);

            this.OptionsJson = optionsJson;
            this.PreferImmediatelyAvailable = preferImmediatelyAvailable;
            return Task.FromResult(credential);
        }
    }
}
