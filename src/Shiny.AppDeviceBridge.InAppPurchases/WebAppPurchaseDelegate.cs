using Shiny.AppDeviceBridge.InAppPurchases.Client;
using Native = Shiny.InAppPurchases;

namespace Shiny.AppDeviceBridge.InAppPurchases;

/// <summary>
/// Turns purchase updates from StoreKit and Google Play — Ask to Buy approvals, completed pending payments, renewals,
/// refunds, purchases on another device — into the <c>purchases.updated</c> event and a call to the web app's
/// <c>purchases.updated</c> handler: the page when it is listening, background.js otherwise. Shiny starts listening at
/// launch, so background.js can verify, grant and finish a purchase that arrives with no page open.
/// <para>
/// Subclass to keep some of it to the native app: override <see cref="OnPurchaseUpdated"/> and call the base only for
/// what the web app should see.
/// </para>
/// </summary>
public class WebAppPurchaseDelegate(WebAppEventHub events, WebAppInvoker invoker) : Native.IPurchaseDelegate
{
    internal const string UpdatedEvent = "purchases.updated";

    readonly WebAppEventSource<Purchase> updated = events.Source(UpdatedEvent, InAppPurchasesJsonContext.Default.Purchase);


    public virtual async Task OnPurchaseUpdated(Native.Purchase purchase)
    {
        var payload = InAppPurchasesBridge.ToContract(purchase);
        this.updated.Publish(payload);
        await invoker.InvokeAsync(UpdatedEvent, payload, InAppPurchasesJsonContext.Default.Purchase).ConfigureAwait(false);
    }
}
