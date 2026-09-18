using System.Text;

namespace Shiny.AppDeviceBridge;

/// <summary>
/// Recorded traffic in words: sizes, durations, status lines, headers and bodies, and a whole exchange as text for a bug
/// report. Shared by every traffic monitor — the MAUI pages and the simulator's terminal view — so they read the same.
/// </summary>
public static class TrafficText
{
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB"
    };

    public static string Duration(TimeSpan elapsed) => elapsed.TotalMilliseconds < 1000
        ? $"{elapsed.TotalMilliseconds:F0} ms"
        : $"{elapsed.TotalSeconds:F1} s";

    public static string Origin(TrafficOrigin origin) => origin switch
    {
        TrafficOrigin.Device => "this device",
        TrafficOrigin.Network => "network",
        TrafficOrigin.Tunnel => "tunnel",
        _ => "unknown"
    };

    public static string Reason(int status) => status switch
    {
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        206 => "Partial Content",
        301 => "Moved Permanently",
        302 => "Found",
        304 => "Not Modified",
        307 => "Temporary Redirect",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        413 => "Payload Too Large",
        421 => "Misdirected Request",
        500 => "Internal Server Error",
        501 => "Not Implemented",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        _ => ""
    };

    /// <summary><c>404 Not Found</c>.</summary>
    public static string Status(TrafficExchange exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        return $"{exchange.StatusCode} {Reason(exchange.StatusCode)}".TrimEnd();
    }

    /// <summary>Where it came from, when, how long it took and how much it moved — one fact a line.</summary>
    public static string Overview(TrafficExchange exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange);

        var at = exchange.StartedOn.ToLocalTime();
        return String.Join(
            "\n",
            $"From {Origin(exchange.Origin)} ({exchange.RemoteAddress})",
            $"At {at:HH:mm:ss.fff} on {at:d}",
            $"Took {Duration(exchange.Elapsed)}",
            $"Sent {Size(exchange.RequestBody.ByteCount)}, received {Size(exchange.ResponseBody.ByteCount)}"
        );
    }

    public static string Headers(IReadOnlyList<TrafficHeader> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (headers.Count == 0)
            return "(none)";

        var text = new StringBuilder();
        foreach (var header in headers)
            text.Append(header.Name).Append(": ").AppendLine(header.Value);

        return text.ToString().TrimEnd();
    }

    /// <summary>A body that was not kept still says why — "nothing here" and "247 KB of PNG" are different answers.</summary>
    public static string Body(TrafficBody body)
    {
        ArgumentNullException.ThrowIfNull(body);

        return body.State switch
        {
            TrafficBodyState.Empty => "(no body)",
            TrafficBodyState.Binary => $"({Size(body.ByteCount)} of {body.ContentType ?? "unknown type"}, not shown)",
            TrafficBodyState.Redacted => $"({Size(body.ByteCount)}, redacted)",
            TrafficBodyState.Truncated => $"{body.Text}\n\n(the first {Size(Encoding.UTF8.GetByteCount(body.Text ?? ""))} of {Size(body.ByteCount)})",
            _ => String.IsNullOrEmpty(body.Text) ? "(no body)" : body.Text
        };
    }

    /// <summary>The exchange as text, for a bug report.</summary>
    public static string Describe(TrafficExchange exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange);

        return new StringBuilder()
            .AppendLine($"{exchange.Method} {exchange.Target}")
            .AppendLine(Overview(exchange))
            .AppendLine()
            .AppendLine("--- request headers ---")
            .AppendLine(Headers(exchange.RequestHeaders))
            .AppendLine()
            .AppendLine("--- request body ---")
            .AppendLine(Body(exchange.RequestBody))
            .AppendLine()
            .AppendLine($"--- response {Status(exchange)} ---")
            .AppendLine(Headers(exchange.ResponseHeaders))
            .AppendLine()
            .AppendLine("--- response body ---")
            .AppendLine(Body(exchange.ResponseBody))
            .ToString();
    }

    /// <summary>The exchanges whose path contains <paramref name="text"/>, whose method is it, or whose status starts with it.</summary>
    public static IReadOnlyList<TrafficExchange> Filter(IReadOnlyList<TrafficExchange> exchanges, string? text)
    {
        ArgumentNullException.ThrowIfNull(exchanges);

        if (String.IsNullOrWhiteSpace(text))
            return exchanges;

        var term = text.Trim();
        return
        [
            .. exchanges.Where(x =>
                x.Target.Contains(term, StringComparison.OrdinalIgnoreCase)
                || x.Method.Equals(term, StringComparison.OrdinalIgnoreCase)
                || x.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture).StartsWith(term, StringComparison.Ordinal))
        ];
    }
}
