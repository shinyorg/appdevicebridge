using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Nfc.Client;

/// <summary>Something this platform's NFC can do.</summary>
public enum NfcCapability
{
    ReadNdef,
    WriteNdef,

    /// <summary>Tags can be locked permanently.</summary>
    MakeReadOnly,

    /// <summary>Tags report their UID. Not on iOS.</summary>
    TagIdentifier
}

/// <param name="Capabilities">What this platform can do; empty when there is no NFC hardware.</param>
/// <param name="Scanning">Whether a scan session is running.</param>
public sealed record NfcStatus(IReadOnlyList<NfcCapability> Capabilities, bool Scanning);

/// <param name="Access">
/// <see cref="AccessState.Available"/>, <see cref="AccessState.Disabled"/> (Android: NFC switched off in Settings) or
/// <see cref="AccessState.NotSetup"/> (a desktop with no PC/SC reader plugged in yet).
/// </param>
public sealed record NfcAccessResult(AccessState Access);

/// <summary>How an NDEF record's <see cref="NfcRecord.Type"/> is read (its TNF).</summary>
public enum NfcTypeNameFormat
{
    Empty,

    /// <summary>An NFC Forum well-known type: <c>U</c> (URI), <c>T</c> (text), <c>Sp</c> (smart poster).</summary>
    WellKnown,

    /// <summary>A MIME type, such as <c>application/json</c>.</summary>
    Media,

    AbsoluteUri,

    /// <summary>An external type, such as <c>android.com:pkg</c>.</summary>
    External,

    Unknown
}

/// <summary>An NDEF record read from a tag: its raw parts, plus the decoded value for the common types.</summary>
/// <param name="Format">The record's type name format.</param>
/// <param name="Type">The record type: <c>U</c>, <c>T</c>, <c>Sp</c>, a MIME type or an external type.</param>
/// <param name="Payload">The raw payload, base64 in JSON.</param>
/// <param name="Id">The record ID, when it has one.</param>
/// <param name="Uri">A URI record's URI, or a smart poster's.</param>
/// <param name="Text">A text record's text, or a smart poster's first title.</param>
/// <param name="Language">A text record's language code.</param>
/// <param name="MimeType">A MIME record's type.</param>
public sealed record NfcRecord(
    NfcTypeNameFormat Format,
    string Type,
    byte[] Payload,
    string? Id = null,
    string? Uri = null,
    string? Text = null,
    string? Language = null,
    string? MimeType = null
);

/// <summary>
/// One record to write. Set exactly one of: <see cref="Uri"/>; <see cref="Text"/> (with <see cref="Language"/>);
/// <see cref="MimeType"/> with <see cref="Data"/>; <see cref="AndroidPackage"/>; or the raw <see cref="Format"/>,
/// <see cref="Type"/> and <see cref="Data"/>.
/// </summary>
/// <param name="Uri">A URI record, with the standard prefix compression.</param>
/// <param name="Text">A UTF-8 text record.</param>
/// <param name="Language">The text record's language code; <c>en</c> when omitted.</param>
/// <param name="MimeType">A MIME record of this type, carrying <paramref name="Data"/>.</param>
/// <param name="Data">The payload for a MIME or raw record, base64 in JSON.</param>
/// <param name="AndroidPackage">An Android Application Record: opens, or offers to install, this app.</param>
/// <param name="Format">A raw record's type name format.</param>
/// <param name="Type">A raw record's type.</param>
/// <param name="Id">The record ID, for any kind.</param>
public sealed record NfcWriteRecord(
    string? Uri = null,
    string? Text = null,
    string? Language = null,
    string? MimeType = null,
    byte[]? Data = null,
    string? AndroidPackage = null,
    NfcTypeNameFormat? Format = null,
    string? Type = null,
    string? Id = null
);

/// <param name="AlertMessage">iOS: the prompt on the scan sheet, such as "Hold your iPhone near the tag".</param>
public sealed record NfcReadRequest(string? AlertMessage = null);

/// <param name="Identifier">The tag's UID as hex, or null where the platform does not expose it (iOS).</param>
/// <param name="Records">The tag's NDEF records, or null when it holds no message.</param>
public sealed record NfcReadResult(string? Identifier, IReadOnlyList<NfcRecord>? Records);

/// <param name="Records">The message to write, one or more records.</param>
/// <param name="AlertMessage">iOS: the prompt on the scan sheet.</param>
public sealed record NfcWriteRequest(IReadOnlyList<NfcWriteRecord> Records, string? AlertMessage = null);

/// <param name="Written">True once written; false when the scan ended before a tag was tapped (iOS: the user dismissed the sheet).</param>
public sealed record NfcWriteResult(bool Written);

/// <param name="AlertMessage">iOS: the prompt on the scan sheet.</param>
/// <param name="HoldSeconds">
/// How long, up to 60, each tag stays available to <see cref="INfcBridge.WriteTagAsync"/> and
/// <see cref="INfcBridge.LockTagAsync"/> after <c>nfc.tag</c> fires, unless released sooner. The next tag waits until
/// then. 0 (the default) releases every tag straight away, for read-only scanning.
/// </param>
public sealed record NfcScanRequest(string? AlertMessage = null, int HoldSeconds = 0);

/// <param name="Records">The message to write to the held tag.</param>
public sealed record NfcTagWriteRequest(IReadOnlyList<NfcWriteRecord> Records);

/// <summary>A tag came into range during a scan.</summary>
/// <param name="TagId">The handle for the tag routes while it is held. Not the UID.</param>
/// <param name="Identifier">The tag's UID as hex, or null where the platform does not expose it (iOS).</param>
/// <param name="IsWritable">The tag is NDEF formatted (or formattable) and not locked.</param>
/// <param name="MaxSize">The largest message, in encoded bytes, the tag holds; null when unknown.</param>
/// <param name="Records">The tag's NDEF records, already read; null when it holds no message.</param>
/// <param name="ReadError">Why the message could not be read, when it could not.</param>
public sealed record NfcTag(
    string TagId,
    string? Identifier,
    bool IsWritable,
    int? MaxSize,
    IReadOnlyList<NfcRecord>? Records,
    string? ReadError = null
);

/// <summary>Why a scan session ended.</summary>
public enum NfcScanEndReason
{
    /// <summary>The page stopped it, or stopped listening for <c>nfc.tag</c>.</summary>
    Stopped,

    /// <summary>The user dismissed the platform's scan UI, or it timed out (iOS ends a session after 60 seconds).</summary>
    Dismissed,

    /// <summary>The platform ended it with an error; see <see cref="NfcScanEnded.Message"/>.</summary>
    Failed
}

/// <param name="Reason">Why the scan ended.</param>
/// <param name="Message">The platform's reason, for <see cref="NfcScanEndReason.Failed"/>.</param>
public sealed record NfcScanEnded(NfcScanEndReason Reason, string? Message = null);

/// <summary>Serialization for every NFC contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(NfcStatus))]
[JsonSerializable(typeof(NfcAccessResult))]
[JsonSerializable(typeof(NfcReadRequest))]
[JsonSerializable(typeof(NfcReadResult))]
[JsonSerializable(typeof(NfcWriteRequest))]
[JsonSerializable(typeof(NfcWriteResult))]
[JsonSerializable(typeof(NfcScanRequest))]
[JsonSerializable(typeof(NfcTagWriteRequest))]
[JsonSerializable(typeof(NfcTag))]
[JsonSerializable(typeof(NfcScanEnded))]
public partial class NfcJsonContext : JsonSerializerContext;
