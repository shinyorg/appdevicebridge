using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.HttpTransfers.Client;

public enum TransferType
{
    /// <summary>A multipart form upload of the file, with an optional extra part.</summary>
    UploadMultipart,

    /// <summary>The file as the request body.</summary>
    UploadRaw,

    Download
}

public enum TransferState
{
    Unknown,
    Pending,
    Paused,
    PausedByNoNetwork,
    PausedByCostedNetwork,
    InProgress,
    Error,
    Canceled,
    Completed
}

/// <summary>An extra form part sent with a multipart upload.</summary>
/// <param name="Content">At most 1 MB.</param>
/// <param name="ContentType"><c>text/plain</c> when null.</param>
public sealed record TransferBody(string Content, string? ContentType = null, string? FormDataName = null);

/// <summary>A transfer to queue.</summary>
/// <param name="Url">An absolute http or https URL. Loopback is refused unless the app allows it.</param>
/// <param name="Root">The file root a download goes into or an upload comes from.</param>
/// <param name="Path">The file inside the root.</param>
/// <param name="Method">GET, POST, PUT or PATCH; the transfer type's default when null.</param>
/// <param name="Headers">At most 32. Hop-by-hop, content and proxy headers are refused.</param>
/// <param name="UseMeteredConnection">Whether the transfer may run on a metered network.</param>
/// <param name="FormDataName">The file's form field name for a multipart upload; <c>file</c> when null.</param>
/// <param name="Body">An extra form part, for a multipart upload only.</param>
/// <param name="Overwrite">Whether a download may replace an existing file.</param>
public sealed record TransferRequest(
    TransferType Type,
    string Url,
    string Root,
    string Path,
    string? Method = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    bool UseMeteredConnection = true,
    string? FormDataName = null,
    TransferBody? Body = null,
    bool Overwrite = true
);

/// <summary>A transfer.</summary>
/// <param name="Root">The file root, while the file is inside one.</param>
/// <param name="PercentComplete">0–1, when the size is known.</param>
/// <param name="StatusCode">The server's status, for a failure that had one.</param>
/// <param name="Error">Why it failed.</param>
public sealed record TransferInfo(
    string Id,
    TransferType Type,
    string Url,
    string? Root,
    string? Path,
    TransferState Status,
    long BytesTransferred,
    long? BytesToTransfer,
    long? BytesPerSecond,
    double? PercentComplete,
    DateTimeOffset? CreatedAt,
    int? StatusCode = null,
    string? Error = null
);

public sealed record TransferList(IReadOnlyList<TransferInfo> Transfers);

/// <summary>Serialization for every transfer contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(TransferRequest))]
[JsonSerializable(typeof(TransferInfo))]
[JsonSerializable(typeof(TransferList))]
public partial class TransfersJsonContext : JsonSerializerContext;
