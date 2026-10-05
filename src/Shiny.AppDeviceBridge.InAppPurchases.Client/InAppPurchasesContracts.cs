using System.Text.Json.Serialization;

namespace Shiny.AppDeviceBridge.InAppPurchases.Client;

/// <summary>The store a device buys through.</summary>
public enum StorePlatform
{
    /// <summary>Apple's App Store, through StoreKit 2.</summary>
    AppStore,

    /// <summary>Google Play Billing.</summary>
    GooglePlay
}

/// <summary>What kind of product it is.</summary>
public enum ProductType
{
    /// <summary>
    /// Bought once. Whether it is consumable is decided when it is finished (<see cref="FinishPurchaseRequest.Consume"/>),
    /// because Google Play does not model consumables. Apple non-renewing subscriptions are one-time purchases.
    /// </summary>
    OneTime,

    /// <summary>An auto-renewable subscription.</summary>
    Subscription
}

/// <summary>Where a purchase stands.</summary>
public enum PurchaseState
{
    /// <summary>Paid for: grant it.</summary>
    Purchased,

    /// <summary>Waiting on Ask to Buy or a slow payment method. Do not grant it; <c>purchases.updated</c> follows when it resolves.</summary>
    Pending,

    /// <summary>Refunded or revoked: take the entitlement away.</summary>
    Revoked
}

/// <summary>How a purchase sheet ended.</summary>
public enum PurchaseResultStatus
{
    /// <summary>Paid: <see cref="PurchaseResult.Purchase"/> is set. Verify, grant, then finish it.</summary>
    Success,

    /// <summary>Waiting on Ask to Buy or a slow payment method; <c>purchases.updated</c> follows when it resolves.</summary>
    Pending,

    /// <summary>The user dismissed the sheet.</summary>
    Cancelled,

    /// <summary>Google Play: the user owns this already — an unconsumed consumable or an owned product. Offer a restore.</summary>
    AlreadyOwned
}

/// <summary>Which store environment a purchase was made in.</summary>
public enum StoreEnvironment
{
    /// <summary>Not reported — always on Google Play, where the server can tell.</summary>
    Unknown,

    /// <summary>A real purchase.</summary>
    Production,

    /// <summary>A sandbox or TestFlight purchase.</summary>
    Sandbox,

    /// <summary>Apple: a StoreKit configuration file in Xcode.</summary>
    Xcode
}

/// <summary>How a subscription pricing phase is charged.</summary>
public enum SubscriptionPaymentMode
{
    /// <summary>The regular recurring price.</summary>
    Recurring,

    /// <summary>Free for the phase.</summary>
    FreeTrial,

    /// <summary>A discounted price each period.</summary>
    PayAsYouGo,

    /// <summary>One discounted payment for the whole phase.</summary>
    PayUpFront
}

/// <summary>How a Google Play subscription change is prorated and charged.</summary>
public enum SubscriptionReplacementMode
{
    /// <summary>The base plan's configured mode.</summary>
    Default,

    /// <summary>Switch now; the remaining time is credited toward the new plan.</summary>
    WithTimeProration,

    /// <summary>Switch now and charge the prorated difference (upgrades only).</summary>
    ChargeProratedPrice,

    /// <summary>Switch now and charge the new plan's full price.</summary>
    ChargeFullPrice,

    /// <summary>Switch now; the new price starts at the next renewal.</summary>
    WithoutProration,

    /// <summary>Switch when the current plan renews.</summary>
    Deferred
}

/// <summary>The store this device buys through, and whether it can buy now.</summary>
/// <param name="Platform">App Store or Google Play.</param>
/// <param name="CanMakePayments">False when the store is unreachable or payments are blocked — parental controls, MDM, an unsupported country, no Play Store.</param>
public sealed record PurchasesStatus(StorePlatform Platform, bool CanMakePayments);

/// <summary>The products to load.</summary>
/// <param name="ProductIds">Store product ids, as configured in App Store Connect or the Play Console.</param>
public sealed record ProductsRequest(IReadOnlyList<string> ProductIds);

/// <summary>A localized product from the store.</summary>
/// <param name="Id">The store product id.</param>
/// <param name="Type">One-time purchase or subscription.</param>
/// <param name="Title">The localized name.</param>
/// <param name="Description">The localized description.</param>
/// <param name="DisplayPrice">The localized, formatted price — for a subscription, the recurring price of its default plan.</param>
/// <param name="Price">The price as a number, in <paramref name="CurrencyCode"/>.</param>
/// <param name="CurrencyCode">ISO 4217 currency code.</param>
/// <param name="SubscriptionOffers">
/// Google Play: one per base plan and per offer the user is eligible for; buy one with its
/// <see cref="SubscriptionOffer.OfferToken"/>. Apple: the introductory offer when the user is eligible, then promotional offers.
/// </param>
/// <param name="SubscriptionGroupId">Apple's subscription group; null on Google Play and for one-time products.</param>
public sealed record StoreProduct(
    string Id,
    ProductType Type,
    string Title,
    string Description,
    string DisplayPrice,
    decimal Price,
    string CurrencyCode,
    IReadOnlyList<SubscriptionOffer> SubscriptionOffers,
    string? SubscriptionGroupId = null
);

/// <summary>A subscription base plan or offer.</summary>
/// <param name="OfferId">Apple's offer id or Google's; null for an Apple introductory offer and a Google base plan.</param>
/// <param name="BasePlanId">Google's base plan id; null on Apple.</param>
/// <param name="OfferToken">Google Play: pass it as <see cref="PurchaseRequest.OfferToken"/> to buy this offer. Null on Apple.</param>
/// <param name="IsIntroductory">An Apple introductory offer, or a Google free trial or introductory price.</param>
/// <param name="PricingPhases">The price schedule in order — a free trial, then the recurring price.</param>
/// <param name="Tags">Google offer tags; empty on Apple.</param>
public sealed record SubscriptionOffer(
    string? OfferId,
    string? BasePlanId,
    string? OfferToken,
    bool IsIntroductory,
    IReadOnlyList<PricingPhase> PricingPhases,
    IReadOnlyList<string> Tags
);

/// <summary>One phase of a subscription's price schedule.</summary>
/// <param name="DisplayPrice">The localized, formatted price of one billing period.</param>
/// <param name="Price">The price of one billing period, as a number.</param>
/// <param name="CurrencyCode">ISO 4217 currency code.</param>
/// <param name="BillingPeriod">An ISO 8601 duration: <c>P1W</c>, <c>P1M</c>, <c>P1Y</c>.</param>
/// <param name="BillingCycleCount">How many periods the phase lasts; 0 recurs until cancelled.</param>
/// <param name="PaymentMode">How the phase is charged.</param>
public sealed record PricingPhase(
    string DisplayPrice,
    decimal Price,
    string CurrencyCode,
    string BillingPeriod,
    int BillingCycleCount,
    SubscriptionPaymentMode PaymentMode
);

/// <summary>Replaces a Google Play subscription with another — an upgrade or downgrade. Apple changes plans within a subscription group itself.</summary>
/// <param name="OldProductId">The subscription being replaced.</param>
/// <param name="OldPurchaseToken">Its purchase token: the old purchase's <see cref="Purchase.OriginalTransactionId"/>.</param>
/// <param name="Mode">How the change is prorated and charged.</param>
public sealed record SubscriptionReplacement(
    string OldProductId,
    string OldPurchaseToken,
    SubscriptionReplacementMode Mode = SubscriptionReplacementMode.Default
);

/// <summary>A product to buy.</summary>
/// <param name="ProductId">The store product id.</param>
/// <param name="AccountToken">
/// Your user's id. It comes back on every transaction and in the stores' server notifications (Apple
/// <c>appAccountToken</c>, Google <c>obfuscatedAccountId</c>), so your server can tell whose purchase it is. Always set it.
/// </param>
/// <param name="OfferToken">Google Play: the <see cref="SubscriptionOffer.OfferToken"/> to buy; the first base plan when omitted. Ignored on Apple.</param>
/// <param name="Quantity">Apple: how many units of a consumable, 1 to 10. Google lets the user choose in the sheet when the product allows it.</param>
/// <param name="Replacement">Google Play: the subscription this one replaces. Ignored on Apple.</param>
public sealed record PurchaseRequest(
    string ProductId,
    Guid? AccountToken = null,
    string? OfferToken = null,
    int Quantity = 1,
    SubscriptionReplacement? Replacement = null
);

/// <summary>A purchase as the device's store reports it.</summary>
/// <param name="Platform">The store it was made through.</param>
/// <param name="ProductId">The store product id.</param>
/// <param name="TransactionId">
/// Unique per charge — finish it by this. Apple: the transaction id, new on each renewal. Google: the order id, or the
/// purchase token while the purchase is pending.
/// </param>
/// <param name="OriginalTransactionId">Stable across renewals. Apple: the original transaction id. Google: the purchase token.</param>
/// <param name="State">Purchased, pending or revoked.</param>
/// <param name="PurchaseDate">When it — or this renewal — was bought.</param>
/// <param name="ExpirationDate">Apple subscriptions only; Google does not report expiry on the device, so ask your server.</param>
/// <param name="IsFinished">Apple: finished. Google: acknowledged or consumed.</param>
/// <param name="IsAutoRenewing">A subscription set to renew.</param>
/// <param name="Quantity">Units bought.</param>
/// <param name="AccountToken">The <see cref="PurchaseRequest.AccountToken"/> it was bought with.</param>
/// <param name="VerificationData">
/// Send this to your server to verify the purchase. Apple: the signed JWS transaction. Google: the purchase token.
/// Shiny.Mobile.InAppPurchases.Server's <c>IPurchaseVerifier</c> takes it as it is.
/// </param>
/// <param name="AppId">The bundle id (Apple) or package name (Google).</param>
/// <param name="Environment">The store environment; always Unknown on Google Play.</param>
/// <param name="OriginalJson">The store's raw JSON for the transaction.</param>
/// <param name="Signature">Google Play: the RSA signature of <paramref name="OriginalJson"/>. Null on Apple.</param>
/// <param name="RevocationDate">Apple: when it was refunded or revoked.</param>
public sealed record Purchase(
    StorePlatform Platform,
    string ProductId,
    string TransactionId,
    string OriginalTransactionId,
    PurchaseState State,
    DateTimeOffset PurchaseDate,
    DateTimeOffset? ExpirationDate,
    bool IsFinished,
    bool IsAutoRenewing,
    int Quantity,
    Guid? AccountToken,
    string VerificationData,
    string AppId,
    StoreEnvironment Environment,
    string? OriginalJson = null,
    string? Signature = null,
    DateTimeOffset? RevocationDate = null
);

/// <summary>How a purchase sheet ended.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Purchase">Set on <see cref="PurchaseResultStatus.Success"/>, and on <see cref="PurchaseResultStatus.Pending"/> when the store reports one.</param>
public sealed record PurchaseResult(PurchaseResultStatus Status, Purchase? Purchase = null);

/// <summary>How to finish a purchase.</summary>
/// <param name="Consume">
/// True for a consumable, so it can be bought again; false acknowledges a non-consumable or subscription. Apple has no
/// separate consume step, so this only matters on Google Play — but always say what the product is.
/// </param>
public sealed record FinishPurchaseRequest(bool Consume);

/// <summary>Which subscription management screen to open.</summary>
/// <param name="ProductId">Android: focus on this subscription. Apple always opens the full list.</param>
public sealed record ManageSubscriptionsRequest(string? ProductId = null);

/// <summary>Serialization for every in-app purchase contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PurchasesStatus))]
[JsonSerializable(typeof(ProductsRequest))]
[JsonSerializable(typeof(StoreProduct))]
[JsonSerializable(typeof(IReadOnlyList<StoreProduct>))]
[JsonSerializable(typeof(PurchaseRequest))]
[JsonSerializable(typeof(PurchaseResult))]
[JsonSerializable(typeof(Purchase))]
[JsonSerializable(typeof(IReadOnlyList<Purchase>))]
[JsonSerializable(typeof(FinishPurchaseRequest))]
[JsonSerializable(typeof(ManageSubscriptionsRequest))]
public partial class InAppPurchasesJsonContext : JsonSerializerContext;
