using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.InAppPurchases.Client;

/// <summary>
/// In-app purchases — consumables, non-consumables and auto-renewable subscriptions — through the App Store (StoreKit 2)
/// on iOS and Google Play Billing on Android. The store's own sheet takes the payment; the page never sees card details.
/// <para>
/// Verify, grant, then finish: send <see cref="Purchase.VerificationData"/> to your server, store the entitlement, and
/// only then call <see cref="FinishAsync"/>. Never grant a <see cref="PurchaseState.Pending"/> purchase. Google Play
/// refunds a purchase that is not finished within three days.
/// </para>
/// <para>
/// Store failures answer with the error code the page switches on: 503 <c>store_unavailable</c> or <c>network</c>,
/// 403 <c>not_allowed</c>, 404 <c>product_not_found</c>, 409 <c>product_unavailable</c>, <c>no_user_interface</c> or
/// <c>invalid_state</c>, 400 <c>developer_error</c>, and 502 <c>verification_failed</c> or <c>purchase_failed</c>.
/// </para>
/// </summary>
[BridgeClient("purchases", typeof(InAppPurchasesJsonContext))]
public interface IInAppPurchasesBridge
{
    /// <summary>Which store this device buys through, and whether this user may make payments right now.</summary>
    [BridgeGet]
    Task<PurchasesStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Localized products and prices. Ids the store does not know are left out rather than failing.</summary>
    [BridgePost("products")]
    Task<IReadOnlyList<StoreProduct>> GetProductsAsync(ProductsRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the store's purchase sheet. A cancelled sheet, a pending payment (Ask to Buy, a slow payment method) and a
    /// product already owned are results, not errors. A <see cref="PurchaseResultStatus.Success"/> is not finished:
    /// verify and grant it, then call <see cref="FinishAsync"/>.
    /// </summary>
    [BridgePost("purchase")]
    Task<PurchaseResult> PurchaseAsync(PurchaseRequest request, CancellationToken cancellationToken = default);

    /// <summary>What the user owns now: non-consumables, active subscriptions, and consumables not yet finished.</summary>
    [BridgeGet("entitlements")]
    Task<IReadOnlyList<Purchase>> GetEntitlementsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Purchases that were paid for but never finished — check at startup, and run each through the same verify, grant,
    /// finish path, so a crash between payment and finishing never loses a purchase.
    /// </summary>
    [BridgeGet("unfinished")]
    Task<IReadOnlyList<Purchase>> GetUnfinishedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Finishes a purchase once its entitlement is stored. <see cref="FinishPurchaseRequest.Consume"/> makes a
    /// consumable buyable again. The bridge finds the purchase in the store by its transaction id, and fails with 404
    /// (<c>purchase_not_found</c>) when the store has no unfinished purchase or entitlement by it — typically because it
    /// was finished already.
    /// </summary>
    [BridgePost("transactions/{transactionId}/finish")]
    Task FinishAsync(string transactionId, FinishPurchaseRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores purchases and returns the entitlements. On iOS this syncs with the App Store and may ask the user to sign
    /// in, so call it only from a "Restore Purchases" button.
    /// </summary>
    [BridgePost("restore")]
    Task<IReadOnlyList<Purchase>> RestoreAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens the store's subscription management screen — on Android, focused on one product when it is named.</summary>
    [BridgePost("manage")]
    Task ShowManageSubscriptionsAsync(ManageSubscriptionsRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// A purchase changed outside a <see cref="PurchaseAsync"/> call: an Ask to Buy approval, a pending payment that
    /// completed, a renewal, a refund, a purchase on another device, an offer code. The same purchase can arrive more
    /// than once, so handle it idempotently by <see cref="Purchase.TransactionId"/>. Also handed to a
    /// <c>purchases.updated</c> handler, so background.js can verify, grant and finish it when no page is open.
    /// </summary>
    [BridgeEvent("purchases.updated")]
    Task<IAsyncDisposable> OnUpdatedAsync(Func<Purchase, Task> handler);
}
