using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.InAppPurchases;
using Shiny.AppDeviceBridge.InAppPurchases.Client;
using Shiny.Net.HttpServer;
using Native = Shiny.InAppPurchases;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The in-app purchases bridge over a fake <see cref="Native.IInAppPurchaseManager"/>: the routes, the contract-to-native
/// mapping both ways, the store's error codes, finishing by transaction id, and the delegate that turns store updates
/// into page events. StoreKit and Play Billing themselves are Shiny.Mobile.InAppPurchases' and need a device and a store.
/// </summary>
public class InAppPurchasesBridgeTests
{
    static readonly Guid Account = Guid.Parse("6f1d7c1e-3b0a-4c55-9a43-9d2b1f6f0a11");
    static readonly DateTimeOffset Bought = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Answers_501_without_a_manager()
    {
        await using var fixture = await PurchasesFixture.StartAsync(manager: null);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetStatusAsync());
        Assert.True(refused.IsNotSupported);
        Assert.False(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "purchases").IsSupported);
    }

    [Fact]
    public async Task Reports_the_store_and_whether_it_can_pay()
    {
        var manager = new FakePurchaseManager { Platform = Native.StorePlatform.GooglePlay, CanPay = false };
        await using var fixture = await PurchasesFixture.StartAsync(manager);

        Assert.Equal(new PurchasesStatus(StorePlatform.GooglePlay, false), await fixture.Client.GetStatusAsync());
        Assert.True(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "purchases").IsSupported);
    }

    [Fact]
    public async Task Loads_products_with_their_offers()
    {
        var manager = new FakePurchaseManager();
        manager.Products.Add(new Native.StoreProduct(
            "pro_monthly",
            Native.ProductType.Subscription,
            "Pro",
            "Everything",
            "$4.99",
            4.99m,
            "USD",
            [
                new Native.SubscriptionOffer(
                    "intro",
                    "monthly",
                    "offer-token",
                    true,
                    [
                        new Native.PricingPhase("Free", 0m, "USD", "P1W", 1, Native.SubscriptionPaymentMode.FreeTrial),
                        new Native.PricingPhase("$4.99", 4.99m, "USD", "P1M", 0, Native.SubscriptionPaymentMode.Recurring)
                    ],
                    ["trial"]
                )
            ],
            "group-1"
        ));
        await using var fixture = await PurchasesFixture.StartAsync(manager);

        var product = Assert.Single(await fixture.Client.GetProductsAsync(new ProductsRequest(["pro_monthly", "unknown"])));

        Assert.Equal(["pro_monthly", "unknown"], manager.ProductQueries.Single());
        Assert.Equal("pro_monthly", product.Id);
        Assert.Equal(ProductType.Subscription, product.Type);
        Assert.Equal("Pro", product.Title);
        Assert.Equal("Everything", product.Description);
        Assert.Equal("$4.99", product.DisplayPrice);
        Assert.Equal(4.99m, product.Price);
        Assert.Equal("USD", product.CurrencyCode);
        Assert.Equal("group-1", product.SubscriptionGroupId);

        var offer = Assert.Single(product.SubscriptionOffers);
        Assert.Equal("intro", offer.OfferId);
        Assert.Equal("monthly", offer.BasePlanId);
        Assert.Equal("offer-token", offer.OfferToken);
        Assert.True(offer.IsIntroductory);
        Assert.Equal(["trial"], offer.Tags);
        Assert.Equal(
            [
                new PricingPhase("Free", 0m, "USD", "P1W", 1, SubscriptionPaymentMode.FreeTrial),
                new PricingPhase("$4.99", 4.99m, "USD", "P1M", 0, SubscriptionPaymentMode.Recurring)
            ],
            offer.PricingPhases
        );
    }

    [Theory]
    [InlineData("""{ "productIds": [] }""")]
    [InlineData("""{ "productIds": [ " " ] }""")]
    [InlineData("""{}""")]
    [InlineData("not json")]
    public async Task Refuses_product_requests_it_cannot_use(string body)
    {
        var manager = new FakePurchaseManager();
        await using var fixture = await PurchasesFixture.StartAsync(manager);

        var response = await fixture.WebView.PostAsync("/_bridge/purchases/products", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(manager.ProductQueries);
    }

    [Fact]
    public async Task Purchases_with_everything_the_page_sent_on_the_main_thread()
    {
        var manager = new FakePurchaseManager { Result = new Native.PurchaseResult(Native.PurchaseResultStatus.Success, NativePurchase("t1")) };
        await using var fixture = await PurchasesFixture.StartAsync(manager);

        var result = await fixture.Client.PurchaseAsync(new PurchaseRequest(
            "pro_yearly",
            Account,
            "offer-token",
            2,
            new SubscriptionReplacement("pro_monthly", "old-token", SubscriptionReplacementMode.ChargeProratedPrice)
        ));

        Assert.Equal(PurchaseResultStatus.Success, result.Status);
        Assert.Equal(ContractPurchase("t1"), result.Purchase);

        var (productId, options) = Assert.Single(manager.Purchases);
        Assert.Equal("pro_yearly", productId);
        Assert.Equal(Account, options.AccountToken);
        Assert.Equal("offer-token", options.OfferToken);
        Assert.Equal(2, options.Quantity);
        Assert.Equal(new Native.SubscriptionReplacement("pro_monthly", "old-token", Native.SubscriptionReplacementMode.ChargeProratedPrice), options.Replacement);
        Assert.Equal(1, fixture.MainThread.Calls);
    }

    [Theory]
    [InlineData(Native.PurchaseResultStatus.Cancelled, PurchaseResultStatus.Cancelled)]
    [InlineData(Native.PurchaseResultStatus.Pending, PurchaseResultStatus.Pending)]
    [InlineData(Native.PurchaseResultStatus.AlreadyOwned, PurchaseResultStatus.AlreadyOwned)]
    public async Task A_sheet_that_did_not_pay_is_a_result_not_an_error(Native.PurchaseResultStatus native, PurchaseResultStatus expected)
    {
        var manager = new FakePurchaseManager { Result = new Native.PurchaseResult(native) };
        await using var fixture = await PurchasesFixture.StartAsync(manager);

        var response = await fixture.WebView.PostAsync("/_bridge/purchases/purchase", Json("""{ "productId": "coins_100" }"""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await fixture.Client.PurchaseAsync(new PurchaseRequest("coins_100"));
        Assert.Equal(new PurchaseResult(expected), result);

        // Defaults the page did not send.
        var options = manager.Purchases[0].Options;
        Assert.Null(options.AccountToken);
        Assert.Null(options.OfferToken);
        Assert.Equal(1, options.Quantity);
        Assert.Null(options.Replacement);
    }

    [Theory]
    [InlineData("""{ "accountToken": "6f1d7c1e-3b0a-4c55-9a43-9d2b1f6f0a11" }""")]
    [InlineData("""{ "productId": "" }""")]
    [InlineData("""{ "productId": "coins", "quantity": 0 }""")]
    [InlineData("""{ "productId": "pro", "replacement": { "oldProductId": "basic" } }""")]
    [InlineData("not json")]
    public async Task Refuses_purchase_requests_it_cannot_use(string body)
    {
        var manager = new FakePurchaseManager();
        await using var fixture = await PurchasesFixture.StartAsync(manager);

        var response = await fixture.WebView.PostAsync("/_bridge/purchases/purchase", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(manager.Purchases);
    }

    [Fact]
    public async Task Lists_entitlements_unfinished_purchases_and_restores()
    {
        var manager = new FakePurchaseManager();
        manager.Entitlements.Add(NativePurchase("e1"));
        manager.Unfinished.Add(NativePurchase("u1", finished: false));
        await using var fixture = await PurchasesFixture.StartAsync(manager);

        Assert.Equal([ContractPurchase("e1")], await fixture.Client.GetEntitlementsAsync());
        Assert.Equal([ContractPurchase("u1", finished: false)], await fixture.Client.GetUnfinishedAsync());

        Assert.Equal([ContractPurchase("e1")], await fixture.Client.RestoreAsync());
        Assert.Equal(1, manager.Restores);
        Assert.Equal(1, fixture.MainThread.Calls);
    }

    [Fact]
    public async Task Finishes_the_stores_own_record_of_the_purchase()
    {
        var manager = new FakePurchaseManager();
        var unfinished = NativePurchase("u1", finished: false);
        var acknowledged = NativePurchase("e1");
        manager.Unfinished.Add(unfinished);
        manager.Entitlements.Add(acknowledged);
        await using var fixture = await PurchasesFixture.StartAsync(manager);

        await fixture.Client.FinishAsync("u1", new FinishPurchaseRequest(Consume: false));
        // An acknowledged Google consumable is an entitlement until it is consumed.
        await fixture.Client.FinishAsync("e1", new FinishPurchaseRequest(Consume: true));

        Assert.Equal([(unfinished, false), (acknowledged, true)], manager.Finished);
    }

    [Fact]
    public async Task Finishing_a_purchase_the_store_does_not_have_is_a_404()
    {
        var manager = new FakePurchaseManager();
        manager.Entitlements.Add(NativePurchase("e1"));
        await using var fixture = await PurchasesFixture.StartAsync(manager);

        var missing = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.FinishAsync("nope", new FinishPurchaseRequest(true)));

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("purchase_not_found", missing.Code);
        Assert.Empty(manager.Finished);
    }

    [Fact]
    public async Task Finishing_needs_to_be_told_whether_to_consume()
    {
        var manager = new FakePurchaseManager();
        manager.Unfinished.Add(NativePurchase("u1", finished: false));
        await using var fixture = await PurchasesFixture.StartAsync(manager);

        var response = await fixture.WebView.PostAsync("/_bridge/purchases/transactions/u1/finish", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(manager.Finished);
    }

    [Fact]
    public async Task Opens_subscription_management()
    {
        var manager = new FakePurchaseManager();
        await using var fixture = await PurchasesFixture.StartAsync(manager);

        await fixture.Client.ShowManageSubscriptionsAsync(new ManageSubscriptionsRequest("pro_monthly"));
        var withoutBody = await fixture.WebView.PostAsync("/_bridge/purchases/manage", null);

        Assert.Equal(HttpStatusCode.NoContent, withoutBody.StatusCode);
        Assert.Equal(["pro_monthly", null], manager.Managed);
        Assert.Equal(2, fixture.MainThread.Calls);
    }

    [Theory]
    [InlineData(Native.InAppPurchaseErrorCode.StoreUnavailable, HttpStatusCode.ServiceUnavailable, "store_unavailable")]
    [InlineData(Native.InAppPurchaseErrorCode.Network, HttpStatusCode.ServiceUnavailable, "network")]
    [InlineData(Native.InAppPurchaseErrorCode.NotAllowed, HttpStatusCode.Forbidden, "not_allowed")]
    [InlineData(Native.InAppPurchaseErrorCode.ProductNotFound, HttpStatusCode.NotFound, "product_not_found")]
    [InlineData(Native.InAppPurchaseErrorCode.ProductUnavailable, HttpStatusCode.Conflict, "product_unavailable")]
    [InlineData(Native.InAppPurchaseErrorCode.NoUserInterface, HttpStatusCode.Conflict, "no_user_interface")]
    [InlineData(Native.InAppPurchaseErrorCode.InvalidState, HttpStatusCode.Conflict, "invalid_state")]
    [InlineData(Native.InAppPurchaseErrorCode.DeveloperError, HttpStatusCode.BadRequest, "developer_error")]
    [InlineData(Native.InAppPurchaseErrorCode.VerificationFailed, HttpStatusCode.BadGateway, "verification_failed")]
    [InlineData(Native.InAppPurchaseErrorCode.Unknown, HttpStatusCode.BadGateway, "purchase_failed")]
    public async Task Store_failures_are_the_errors_the_page_switches_on(Native.InAppPurchaseErrorCode error, HttpStatusCode status, string code)
    {
        var manager = new FakePurchaseManager { Throw = new Native.InAppPurchaseException(error, "Store said no") { NativeErrorCode = "7" } };
        await using var fixture = await PurchasesFixture.StartAsync(manager);

        var failed = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.PurchaseAsync(new PurchaseRequest("coins_100")));

        Assert.Equal(status, failed.StatusCode);
        Assert.Equal(code, failed.Code);
        Assert.Contains("Store said no", failed.Message);
        Assert.Contains("store code 7", failed.Message);
    }

    [Fact]
    public async Task The_delegate_publishes_store_updates()
    {
        await using var app = new TestApp();
        var events = new WebAppEventHub();
        var d = new WebAppPurchaseDelegate(events, new WebAppInvoker(app.BridgeOptions()));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var updated = Listen(events.Source("purchases.updated", InAppPurchasesJsonContext.Default.Purchase), cancel.Token);

        // No page and no background.js: the handler call finds nobody, and the event still goes out.
        await d.OnPurchaseUpdated(NativePurchase("t9", state: Native.PurchaseState.Revoked));

        Assert.Equal(ContractPurchase("t9", state: PurchaseState.Revoked), await updated);
    }

    [Fact]
    public void Registers_once_and_keeps_an_apps_own_delegate()
    {
        var services = new ServiceCollection();
        services.AddShinyHttpServer(http => http.AddAppDeviceBridge(bridge =>
        {
            bridge.AddInAppPurchasesBridge();
            bridge.AddInAppPurchasesBridge();
        }), autoStart: false);

        Assert.Single(services, x => x.ImplementationType == typeof(WebAppPurchaseDelegate));
        Assert.Single(services, x => x.ImplementationType == typeof(InAppPurchasesBridge));

        var own = new ServiceCollection();
        own.AddInAppPurchases<AppDelegate>();
        own.AddShinyHttpServer(http => http.AddAppDeviceBridge(bridge => bridge.AddInAppPurchasesBridge()), autoStart: false);

        Assert.Single(own, x => x.ServiceType == typeof(Native.IPurchaseDelegate) && x.ImplementationType == typeof(AppDelegate));
        Assert.Single(own, x => x.ServiceType == typeof(Native.IPurchaseDelegate) && x.ImplementationType == typeof(WebAppPurchaseDelegate));
    }

    static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    static Native.Purchase NativePurchase(string transactionId, bool finished = true, Native.PurchaseState state = Native.PurchaseState.Purchased) => new(
        Native.StorePlatform.AppStore,
        "pro_monthly",
        transactionId,
        "orig-1",
        state,
        Bought,
        Bought.AddMonths(1),
        finished,
        true,
        1,
        Account,
        "jws",
        "com.shiny.sample",
        Native.StoreEnvironment.Sandbox,
        "{}",
        null,
        state == Native.PurchaseState.Revoked ? Bought.AddDays(1) : null
    );

    static Purchase ContractPurchase(string transactionId, bool finished = true, PurchaseState state = PurchaseState.Purchased) => new(
        StorePlatform.AppStore,
        "pro_monthly",
        transactionId,
        "orig-1",
        state,
        Bought,
        Bought.AddMonths(1),
        finished,
        true,
        1,
        Account,
        "jws",
        "com.shiny.sample",
        StoreEnvironment.Sandbox,
        "{}",
        null,
        state == PurchaseState.Revoked ? Bought.AddDays(1) : null
    );

    /// <summary>The first value published to <paramref name="source"/> from now on.</summary>
    static Task<T> Listen<T>(WebAppEventSource<T> source, CancellationToken cancellationToken)
    {
        var enumerator = source.ListenAsync(cancellationToken: cancellationToken).GetAsyncEnumerator(cancellationToken);
        var next = enumerator.MoveNextAsync();
        return Read();

        async Task<T> Read()
        {
            Assert.True(await next);
            var value = enumerator.Current;
            await enumerator.DisposeAsync();
            return value;
        }
    }

    sealed class PurchasesFixture : IAsyncDisposable
    {
        BuiltInClientTests.HostFixture host = null!;

        public HttpClient WebView { get; private set; } = null!;
        public IBridgeTransport Transport => this.host.Transport;
        public InAppPurchasesBridgeClient Client { get; private set; } = null!;
        public CountingMainThread MainThread { get; } = new();

        public static async Task<PurchasesFixture> StartAsync(Native.IInAppPurchaseManager? manager)
        {
            var fixture = new PurchasesFixture();
            var services = new ServiceCollection();
            if (manager is not null)
                services.AddSingleton(manager);
            services.AddSingleton<IWebAppMainThread>(fixture.MainThread);
            var provider = services.BuildServiceProvider();

            fixture.host = await BuiltInClientTests.HostFixture.StartAsync(
                _ => [new InAppPurchasesBridge(provider)],
                null,
                client => fixture.WebView = client
            );

            fixture.Client = new InAppPurchasesBridgeClient(fixture.host.Transport);
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

    sealed class AppDelegate : Native.IPurchaseDelegate
    {
        public Task OnPurchaseUpdated(Native.Purchase purchase) => Task.CompletedTask;
    }

    sealed class FakePurchaseManager : Native.IInAppPurchaseManager
    {
        public Native.StorePlatform Platform { get; init; } = Native.StorePlatform.AppStore;
        public bool CanPay { get; init; } = true;
        public Native.PurchaseResult Result { get; init; } = new(Native.PurchaseResultStatus.Cancelled);
        public Exception? Throw { get; init; }
        public int Restores { get; private set; }

        public List<Native.StoreProduct> Products { get; } = [];
        public List<Native.Purchase> Entitlements { get; } = [];
        public List<Native.Purchase> Unfinished { get; } = [];
        public List<string[]> ProductQueries { get; } = [];
        public List<(string ProductId, Native.PurchaseOptions Options)> Purchases { get; } = [];
        public List<(Native.Purchase Purchase, bool Consume)> Finished { get; } = [];
        public List<string?> Managed { get; } = [];

        public event EventHandler<Native.Purchase>? PurchaseUpdated { add { } remove { } }

        public Task<bool> CanMakePaymentsAsync(CancellationToken cancelToken = default) => Task.FromResult(this.CanPay);

        public Task<IReadOnlyList<Native.StoreProduct>> GetProductsAsync(IEnumerable<string> productIds, CancellationToken cancelToken = default)
        {
            var ids = productIds.ToArray();
            this.ProductQueries.Add(ids);
            return Task.FromResult<IReadOnlyList<Native.StoreProduct>>([.. this.Products.Where(x => ids.Contains(x.Id))]);
        }

        public Task<Native.PurchaseResult> PurchaseAsync(string productId, Native.PurchaseOptions? options = null, CancellationToken cancelToken = default)
        {
            if (this.Throw is not null)
                throw this.Throw;

            this.Purchases.Add((productId, options ?? new()));
            return Task.FromResult(this.Result);
        }

        public Task<IReadOnlyList<Native.Purchase>> GetEntitlementsAsync(CancellationToken cancelToken = default)
            => Task.FromResult<IReadOnlyList<Native.Purchase>>(this.Entitlements);

        public Task<IReadOnlyList<Native.Purchase>> GetUnfinishedPurchasesAsync(CancellationToken cancelToken = default)
            => Task.FromResult<IReadOnlyList<Native.Purchase>>(this.Unfinished);

        public Task FinishPurchaseAsync(Native.Purchase purchase, bool consume, CancellationToken cancelToken = default)
        {
            this.Finished.Add((purchase, consume));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Native.Purchase>> RestorePurchasesAsync(CancellationToken cancelToken = default)
        {
            this.Restores++;
            return this.GetEntitlementsAsync(cancelToken);
        }

        public Task ShowManageSubscriptionsAsync(string? productId = null, CancellationToken cancelToken = default)
        {
            this.Managed.Add(productId);
            return Task.CompletedTask;
        }
    }
}
