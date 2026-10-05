using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.InAppPurchases.Client;
using Shiny.Net.HttpServer;
using Native = Shiny.InAppPurchases;

namespace Shiny.AppDeviceBridge.InAppPurchases;

public static class InAppPurchasesBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/purchases</c> and registers Shiny's in-app purchase manager — there is nothing else to call.
    /// <code>
    /// bridge.AddInAppPurchasesBridge();
    /// </code>
    /// <para>
    /// Purchase updates that happen outside a purchase call — Ask to Buy approvals, renewals, refunds, purchases on
    /// another device — go to the web app's <c>purchases.updated</c> handler: the page if it is listening, background.js
    /// otherwise. An app that already called <c>AddInAppPurchases</c> keeps its registration; the bridge only adds its
    /// delegate, and Shiny runs every delegate. iOS and Android only; every other platform answers 501.
    /// </para>
    /// </summary>
    public static TBuilder AddInAppPurchasesBridge<TBuilder>(this TBuilder bridge)
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

        var services = bridge.Services;
        if (services.Any(x => x.ImplementationType == typeof(WebAppPurchaseDelegate)))
            return bridge;

        // AddInAppPurchases registers the delegate with TryAddEnumerable, so it never duplicates an app's own registration.
        services.AddInAppPurchases<WebAppPurchaseDelegate>();
        bridge.AddBridge<InAppPurchasesBridge>();
        return bridge;
    }
}

/// <summary>
/// <c>/_bridge/purchases</c> over <see cref="Native.IInAppPurchaseManager"/>.
/// <code>
/// GET  /_bridge/purchases                                    { "platform": "AppStore", "canMakePayments": true }
/// POST /_bridge/purchases/products                           { "productIds": [ "coins_100", "pro_monthly" ] }  →  [ { "id", "type", "displayPrice", … } ]
/// POST /_bridge/purchases/purchase                           { "productId": "pro_monthly", "accountToken": "…" }  →  { "status": "Success", "purchase": {…} }
/// GET  /_bridge/purchases/entitlements                       [ { "productId", "transactionId", "state", "verificationData", … } ]
/// GET  /_bridge/purchases/unfinished                         [ … ]
/// POST /_bridge/purchases/transactions/{transactionId}/finish  { "consume": true }
/// POST /_bridge/purchases/restore                            [ … ]
/// POST /_bridge/purchases/manage                             { "productId": "pro_monthly" }
///
/// event and handler: purchases.updated
/// </code>
/// </summary>
public sealed class InAppPurchasesBridge(IServiceProvider services) : IWebAppBridge
{
    // Shiny.Mobile.InAppPurchases registers no manager off iOS and Android.
    readonly Native.IInAppPurchaseManager? manager = services.GetOptionalService<Native.IInAppPurchaseManager>();

    public string Name => "purchases";

    public bool IsSupported => this.manager is not null;

    public void Map(WebAppBridgeRoutes routes)
    {
        // The delegate raises it; mapping it here means the topic exists as soon as the server is composed.
        routes.Events.Source(WebAppPurchaseDelegate.UpdatedEvent, InAppPurchasesJsonContext.Default.Purchase);

        routes
            .MapGet("", this.StatusAsync)
            .MapPost("/products", this.ProductsAsync)
            .MapPost("/purchase", this.PurchaseAsync)
            .MapGet("/entitlements", this.EntitlementsAsync)
            .MapGet("/unfinished", this.UnfinishedAsync)
            .MapPost("/transactions/{transactionId}/finish", this.FinishAsync)
            .MapPost("/restore", this.RestoreAsync)
            .MapPost("/manage", this.ManageAsync);
    }

    ValueTask StatusAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        var canPay = await m.CanMakePaymentsAsync(context.RequestAborted);
        var status = new PurchasesStatus(BridgeEnum.Convert<Native.StorePlatform, StorePlatform>(m.Platform), canPay);
        await WebAppBridgeResults.Json(context, status, InAppPurchasesJsonContext.Default.PurchasesStatus);
    });

    ValueTask ProductsAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        if (await WebAppBridgeResults.ReadBodyAsync(context, InAppPurchasesJsonContext.Default.ProductsRequest) is not { ProductIds: { Count: > 0 } ids }
            || ids.Any(String.IsNullOrWhiteSpace))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"productIds\": [ \"…\" ] }.");
            return;
        }

        var products = await m.GetProductsAsync(ids, context.RequestAborted);
        IReadOnlyList<StoreProduct> list = [.. products.Select(ToContract)];
        await WebAppBridgeResults.Json(context, list, InAppPurchasesJsonContext.Default.IReadOnlyListStoreProduct);
    });

    ValueTask PurchaseAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        if (await WebAppBridgeResults.ReadBodyAsync(context, InAppPurchasesJsonContext.Default.PurchaseRequest) is not { } body
            || String.IsNullOrWhiteSpace(body.ProductId))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"productId\": \"…\" }.");
            return;
        }
        if (body.Quantity < 1)
        {
            await WebAppBridgeResults.BadRequest(context, "quantity is at least 1.");
            return;
        }
        if (body.Replacement is { } r && (String.IsNullOrWhiteSpace(r.OldProductId) || String.IsNullOrWhiteSpace(r.OldPurchaseToken)))
        {
            await WebAppBridgeResults.BadRequest(context, "replacement needs oldProductId and oldPurchaseToken.");
            return;
        }

        var options = new Native.PurchaseOptions
        {
            AccountToken = body.AccountToken,
            OfferToken = body.OfferToken,
            Quantity = body.Quantity,
            Replacement = body.Replacement is { } replace
                ? new Native.SubscriptionReplacement(
                    replace.OldProductId,
                    replace.OldPurchaseToken,
                    BridgeEnum.Convert<SubscriptionReplacementMode, Native.SubscriptionReplacementMode>(replace.Mode)
                )
                : null
        };

        // The purchase sheet is UI.
        var result = await services
            .GetRequiredService<IWebAppMainThread>()
            .InvokeAsync(() => m.PurchaseAsync(body.ProductId, options, context.RequestAborted));

        var contract = new PurchaseResult(
            BridgeEnum.Convert<Native.PurchaseResultStatus, PurchaseResultStatus>(result.Status),
            result.Purchase is { } p ? ToContract(p) : null
        );
        await WebAppBridgeResults.Json(context, contract, InAppPurchasesJsonContext.Default.PurchaseResult);
    });

    ValueTask EntitlementsAsync(HttpContext context) => this.RunAsync(context, async m =>
        await WritePurchases(context, await m.GetEntitlementsAsync(context.RequestAborted))
    );

    ValueTask UnfinishedAsync(HttpContext context) => this.RunAsync(context, async m =>
        await WritePurchases(context, await m.GetUnfinishedPurchasesAsync(context.RequestAborted))
    );

    ValueTask FinishAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        var transactionId = context.Request.RouteValues["transactionId"] as string;
        if (String.IsNullOrWhiteSpace(transactionId))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected a transaction id.");
            return;
        }

        if (await WebAppBridgeResults.ReadBodyAsync(context, InAppPurchasesJsonContext.Default.FinishPurchaseRequest) is not { } body)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"consume\": true | false }.");
            return;
        }

        // The page names the purchase; the store supplies it. A record the page sent could carry any purchase token.
        // An unfinished purchase is the usual case; an acknowledged Google consumable is still an entitlement until it
        // is consumed.
        var purchase = (await m.GetUnfinishedPurchasesAsync(context.RequestAborted)).FirstOrDefault(x => x.TransactionId == transactionId)
            ?? (await m.GetEntitlementsAsync(context.RequestAborted)).FirstOrDefault(x => x.TransactionId == transactionId);

        if (purchase is null)
        {
            await WebAppBridgeResults.Error(
                context,
                StatusCodes.Status404NotFound,
                "purchase_not_found",
                $"No unfinished purchase or entitlement '{transactionId}' — it may be finished already."
            );
            return;
        }

        await m.FinishPurchaseAsync(purchase, body.Consume, context.RequestAborted);
        await WebAppBridgeResults.NoContent(context);
    });

    ValueTask RestoreAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        // Apple may ask the user to sign in.
        var restored = await services
            .GetRequiredService<IWebAppMainThread>()
            .InvokeAsync(() => m.RestorePurchasesAsync(context.RequestAborted));

        await WritePurchases(context, restored);
    });

    ValueTask ManageAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        // The body is optional: without one, the whole list opens.
        var body = await WebAppBridgeResults.ReadBodyAsync(context, InAppPurchasesJsonContext.Default.ManageSubscriptionsRequest);
        await services
            .GetRequiredService<IWebAppMainThread>()
            .InvokeAsync(() => m.ShowManageSubscriptionsAsync(body?.ProductId, context.RequestAborted));

        await WebAppBridgeResults.NoContent(context);
    });

    static ValueTask WritePurchases(HttpContext context, IReadOnlyList<Native.Purchase> purchases)
    {
        IReadOnlyList<Purchase> list = [.. purchases.Select(ToContract)];
        return WebAppBridgeResults.Json(context, list, InAppPurchasesJsonContext.Default.IReadOnlyListPurchase);
    }

    /// <summary>501 off iOS and Android; the store's failures as the errors the page switches on.</summary>
    async ValueTask RunAsync(HttpContext context, Func<Native.IInAppPurchaseManager, Task> action)
    {
        if (this.manager is not { } m)
        {
            await WebAppBridgeResults.NotSupported(context, "In-app purchases");
            return;
        }

        try
        {
            await action(m);
        }
        catch (Native.InAppPurchaseException ex)
        {
            var (status, code) = ToError(ex.ErrorCode);
            var message = ex.NativeErrorCode is { } native ? $"{ex.Message} (store code {native})" : ex.Message;
            await WebAppBridgeResults.Error(context, status, code, message);
        }
        catch (ArgumentException ex)
        {
            await WebAppBridgeResults.BadRequest(context, ex.Message);
        }
    }

    internal static (int Status, string Code) ToError(Native.InAppPurchaseErrorCode code) => code switch
    {
        Native.InAppPurchaseErrorCode.StoreUnavailable => (StatusCodes.Status503ServiceUnavailable, "store_unavailable"),
        Native.InAppPurchaseErrorCode.Network => (StatusCodes.Status503ServiceUnavailable, "network"),
        Native.InAppPurchaseErrorCode.NotAllowed => (StatusCodes.Status403Forbidden, "not_allowed"),
        Native.InAppPurchaseErrorCode.ProductNotFound => (StatusCodes.Status404NotFound, "product_not_found"),
        Native.InAppPurchaseErrorCode.ProductUnavailable => (StatusCodes.Status409Conflict, "product_unavailable"),
        Native.InAppPurchaseErrorCode.NoUserInterface => (StatusCodes.Status409Conflict, "no_user_interface"),
        Native.InAppPurchaseErrorCode.InvalidState => (StatusCodes.Status409Conflict, "invalid_state"),
        Native.InAppPurchaseErrorCode.DeveloperError => (StatusCodes.Status400BadRequest, "developer_error"),
        Native.InAppPurchaseErrorCode.VerificationFailed => (StatusCodes.Status502BadGateway, "verification_failed"),
        _ => (StatusCodes.Status502BadGateway, "purchase_failed")
    };

    internal static StoreProduct ToContract(Native.StoreProduct product) => new(
        product.Id,
        BridgeEnum.Convert<Native.ProductType, ProductType>(product.Type),
        product.Title,
        product.Description,
        product.DisplayPrice,
        product.Price,
        product.CurrencyCode,
        [.. product.SubscriptionOffers.Select(o => new SubscriptionOffer(
            o.OfferId,
            o.BasePlanId,
            o.OfferToken,
            o.IsIntroductory,
            [.. o.PricingPhases.Select(p => new PricingPhase(
                p.DisplayPrice,
                p.Price,
                p.CurrencyCode,
                p.BillingPeriod,
                p.BillingCycleCount,
                BridgeEnum.Convert<Native.SubscriptionPaymentMode, SubscriptionPaymentMode>(p.PaymentMode)
            ))],
            o.Tags
        ))],
        product.SubscriptionGroupId
    );

    internal static Purchase ToContract(Native.Purchase purchase) => new(
        BridgeEnum.Convert<Native.StorePlatform, StorePlatform>(purchase.Platform),
        purchase.ProductId,
        purchase.TransactionId,
        purchase.OriginalTransactionId,
        BridgeEnum.Convert<Native.PurchaseState, PurchaseState>(purchase.State),
        purchase.PurchaseDate,
        purchase.ExpirationDate,
        purchase.IsFinished,
        purchase.IsAutoRenewing,
        purchase.Quantity,
        purchase.AccountToken,
        purchase.VerificationData,
        purchase.AppId,
        BridgeEnum.Convert<Native.StoreEnvironment, StoreEnvironment>(purchase.Environment),
        purchase.OriginalJson,
        purchase.Signature,
        purchase.RevocationDate
    );
}
