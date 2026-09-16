using System.Text.Json.Serialization;

namespace Shiny.AppDeviceBridge;

/// <summary>What the server told a host to do about the version it is running.</summary>
public enum WebAppUpdateKind
{
    /// <summary>The host is current, or nothing newer is compatible with it.</summary>
    None,

    /// <summary>A newer release exists. Download it in the background and apply it on the next launch.</summary>
    Optional,

    /// <summary>
    /// The installed version is below the minimum the server will accept. The host must install the
    /// release before showing the app.
    /// </summary>
    Required
}

/// <summary>
/// One published web app build.
/// <para>
/// <see cref="AppId"/>, <see cref="Version"/>, <see cref="Sha256"/>, <see cref="Size"/> and
/// <see cref="MinimumHostVersion"/> are covered by the release signature — see
/// <see cref="WebAppReleaseSignature"/>. Everything else is informational and can be changed
/// without re-signing, which is why nothing a host acts on lives outside that set.
/// </para>
/// </summary>
public sealed record WebAppRelease
{
    /// <summary>Which app this build belongs to. Letters, digits, <c>.</c>, <c>-</c> and <c>_</c>.</summary>
    public required string AppId { get; init; }

    /// <summary>The build's version, in the form <see cref="WebAppVersion"/> parses.</summary>
    public required string Version { get; init; }

    /// <summary>SHA-256 of the zip, as 64 lowercase hex characters.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Byte length of the zip. Checked before hashing, so a truncated download fails fast.</summary>
    public required long Size { get; init; }

    /// <summary>
    /// The oldest native host able to run this build, or null for any.
    /// <para>
    /// A web build that calls a bridge endpoint the installed app does not have is broken in a way
    /// no amount of retrying fixes. The server skips releases a host is too old for, and the host
    /// refuses them anyway if one arrives.
    /// </para>
    /// </summary>
    public string? MinimumHostVersion { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    public string? ReleaseNotes { get; init; }
}

/// <summary>The answer to an update check.</summary>
public sealed record WebAppUpdateResponse
{
    public WebAppUpdateKind Kind { get; init; }

    /// <summary>The release to install. Null when <see cref="Kind"/> is <see cref="WebAppUpdateKind.None"/>.</summary>
    public WebAppRelease? Release { get; init; }

    /// <summary>Base64 signature over <see cref="Release"/>. See <see cref="WebAppReleaseSignature"/>.</summary>
    public string? Signature { get; init; }

    /// <summary>Where to fetch the zip. May be relative, in which case it resolves against the check URL.</summary>
    public string? DownloadUrl { get; init; }

    /// <summary>The lowest version the server still accepts, for display. The decision is already in <see cref="Kind"/>.</summary>
    public string? MinimumVersion { get; init; }
}

/// <summary>The wire names both sides use, so neither has to guess the other's spelling.</summary>
public static class WebAppProtocol
{
    public const string VersionQuery = "version";
    public const string PlatformQuery = "platform";
    public const string HostVersionQuery = "host";
    public const string ChannelQuery = "channel";

    /// <summary>
    /// The check URL for an app: <c>{baseUri}/{appId}/check?version=…&amp;platform=…&amp;host=…&amp;channel=…</c>.
    /// </summary>
    public static Uri BuildCheckUri(
        Uri baseUri,
        string appId,
        string? currentVersion,
        string platform,
        string hostVersion,
        string? channel
    )
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);

        var query = new List<string>(4);
        if (!String.IsNullOrWhiteSpace(currentVersion))
            query.Add($"{VersionQuery}={Uri.EscapeDataString(currentVersion)}");

        query.Add($"{PlatformQuery}={Uri.EscapeDataString(platform)}");
        query.Add($"{HostVersionQuery}={Uri.EscapeDataString(hostVersion)}");

        if (!String.IsNullOrWhiteSpace(channel))
            query.Add($"{ChannelQuery}={Uri.EscapeDataString(channel)}");

        var root = baseUri.AbsoluteUri.TrimEnd('/');
        return new Uri($"{root}/{Uri.EscapeDataString(appId)}/check?{String.Join('&', query)}");
    }

    /// <summary>True when an app id is safe to use as a URL segment and a directory name.</summary>
    public static bool IsValidAppId(string? appId)
        => !String.IsNullOrEmpty(appId)
           && appId.Length <= 100
           && appId.All(c => Char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
           && appId is not "." and not "..";
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(WebAppRelease))]
[JsonSerializable(typeof(List<WebAppRelease>))]
[JsonSerializable(typeof(WebAppUpdateResponse))]
public partial class WebAppJsonContext : JsonSerializerContext;
