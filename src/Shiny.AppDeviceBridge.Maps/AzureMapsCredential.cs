using System.Net.Http.Headers;

namespace Shiny.AppDeviceBridge.Maps;

/// <summary>
/// How every Azure Maps provider — <see cref="AzureMapsBasemapProvider"/>, <see cref="AzureMapsTrafficProvider"/>,
/// <see cref="AzureMapsRouteProvider"/>, <see cref="AzureMapsGeocoder"/> — authenticates: the account's shared key, or Microsoft
/// Entra ID. Either stays in the app; the page never sees it. One credential can be shared by all of them.
/// </summary>
public sealed class AzureMapsCredential
{
    readonly string? subscriptionKey;

    /// <summary>Authenticates with the Azure Maps account's shared key.</summary>
    public AzureMapsCredential(string subscriptionKey)
    {
        if (String.IsNullOrWhiteSpace(subscriptionKey))
            throw new ArgumentException("An Azure Maps key is required.", nameof(subscriptionKey));

        this.subscriptionKey = subscriptionKey;
    }

    /// <summary>
    /// Authenticates with Microsoft Entra ID: <paramref name="clientId"/> is the Azure Maps account's client id, and
    /// <paramref name="getAccessToken"/> returns a token for <c>https://atlas.microsoft.com/.default</c>, cached as the app sees fit.
    /// </summary>
    public AzureMapsCredential(string clientId, Func<CancellationToken, Task<string>> getAccessToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(getAccessToken);

        this.ClientId = clientId;
        this.GetAccessToken = getAccessToken;
    }

    /// <summary>The Azure Maps account's client id, for Entra ID authentication. Null with a shared key.</summary>
    public string? ClientId { get; }

    /// <summary>Returns an Entra ID access token for each request. Null with a shared key.</summary>
    public Func<CancellationToken, Task<string>>? GetAccessToken { get; }

    /// <summary>What to add to a request: the key in a header, or a bearer token and the client id. A token is fetched once per call.</summary>
    internal async Task<Action<HttpRequestMessage>> AuthorizeAsync(CancellationToken cancellationToken)
    {
        if (this.GetAccessToken is not { } getToken)
            return request => request.Headers.Add("subscription-key", this.subscriptionKey);

        var token = await getToken(cancellationToken).ConfigureAwait(false);
        return request =>
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("x-ms-client-id", this.ClientId);
        };
    }
}
