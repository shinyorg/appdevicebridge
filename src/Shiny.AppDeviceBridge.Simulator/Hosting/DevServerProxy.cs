using System.Net;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.Simulator.Hosting;

/// <summary>
/// Serves a dev server's pages as the simulator's own, so the page and the bridges share one origin — the page's clients
/// call the bridges relative to where the page came from. The browser runs on this machine, so the dev server's hot reload
/// socket, which it reaches directly, needs no rewriting.
/// </summary>
sealed class DevServerProxy(Uri devServer)
{
    /// <summary>Headers that describe one hop, not the request.</summary>
    static readonly HashSet<string> NotForwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade",
        "Host", "Content-Length"
    };

    // Bytes and headers pass through as the dev server sent them: no decompression, no cookies, no redirects followed.
    readonly HttpClient client = new(new SocketsHttpHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public async ValueTask ForwardAsync(HttpContext context)
    {
        var request = context.Request;
        var query = String.IsNullOrEmpty(request.QueryString) ? String.Empty
            : request.QueryString.StartsWith('?') ? request.QueryString
            : "?" + request.QueryString;

        using var message = new HttpRequestMessage(new HttpMethod(request.Method), new Uri(devServer, request.Path.TrimStart('/') + query));

        if (request.HasBody)
            message.Content = new StreamContent(request.Body);

        foreach (var (name, values) in request.Headers)
        {
            if (NotForwarded.Contains(name))
                continue;

            if (!message.Headers.TryAddWithoutValidation(name, values.ToString()))
                message.Content?.Headers.TryAddWithoutValidation(name, values.ToString());
        }

        HttpResponseMessage response;
        try
        {
            response = await this.client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        }
        catch (HttpRequestException)
        {
            await WebAppBridgeResults.Error(
                context,
                StatusCodes.Status502BadGateway,
                "dev_server_unreachable",
                $"The dev server at {devServer} did not answer. Is it running?"
            );
            return;
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;

            foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
            {
                if (NotForwarded.Contains(name))
                    continue;

                // A redirect to the dev server's own address has to stay on the simulator's origin.
                if (name.Equals("Location", StringComparison.OrdinalIgnoreCase)
                    && Uri.TryCreate(values.FirstOrDefault(), UriKind.Absolute, out var location)
                    && location.Authority == devServer.Authority)
                {
                    context.Response.Headers[name] = location.PathAndQuery;
                    continue;
                }

                context.Response.Headers[name] = values.ToArray();
            }

            // Hot reload changes the bytes behind the same URLs from one moment to the next.
            context.Response.Headers["Cache-Control"] = "no-store";

            await using var body = await response.Content.ReadAsStreamAsync(context.RequestAborted);
            await body.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
    }
}
