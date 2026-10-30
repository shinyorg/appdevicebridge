using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Nfc.Client;

/// <summary>
/// Reads and writes NDEF NFC tags: URLs, text, MIME payloads, smart posters and Android app records. Android, iOS, and
/// a PC/SC reader on Windows, macOS and Linux; Mac Catalyst answers 501, as does a device with no NFC hardware. For one
/// tap, use <see cref="ReadAsync"/> or <see cref="WriteAsync"/>. To keep scanning, listen for <c>nfc.tag</c>, then
/// <see cref="StartScanAsync"/>. Only one scan or one-tap call runs at a time; another answers <c>409 nfc_busy</c>.
/// </summary>
[BridgeClient("nfc", typeof(NfcJsonContext))]
public interface INfcBridge
{
    /// <summary>What this platform can do, and whether a scan is running.</summary>
    [BridgeGet]
    Task<NfcStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether NFC is present and switched on. Nothing prompts: NFC needs no runtime permission.</summary>
    [BridgePost("access")]
    Task<NfcAccessResult> RequestAccessAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits for one tag and reads it. Null when the scan ended first (iOS: the user dismissed the sheet) or the request
    /// was aborted. Fails with <c>422 invalid_ndef</c> when the tag holds bytes that are not NDEF.
    /// </summary>
    [BridgePost("read")]
    Task<NfcReadResult?> ReadAsync(NfcReadRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits for one tag and writes a message to it. Fails with 400 for a malformed record, <c>409 tag_read_only</c>,
    /// <c>413 message_too_large</c> (nothing written) and <c>410 tag_lost</c> (the tag left mid-write; its previous
    /// message may be gone).
    /// </summary>
    [BridgePost("write")]
    Task<NfcWriteResult> WriteAsync(NfcWriteRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a scan session. Each tag fires <c>nfc.tag</c> with its message already read; the session runs until
    /// <see cref="StopScanAsync"/>, the page stops listening, or the platform ends it (<c>nfc.scan</c>). Listen for
    /// <c>nfc.tag</c> first, or it answers <c>409 not_listening</c>.
    /// </summary>
    [BridgePost("scan")]
    Task StartScanAsync(NfcScanRequest request, CancellationToken cancellationToken = default);

    /// <summary>Ends the scan session. Does nothing when none is running.</summary>
    [BridgeDelete("scan")]
    Task StopScanAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes to the tag a scan is holding (see <see cref="NfcScanRequest.HoldSeconds"/>). <c>404 tag_not_held</c> once
    /// it has been released; otherwise the same errors as <see cref="WriteAsync"/>.
    /// </summary>
    [BridgePost("tags/{tagId}/write")]
    Task WriteTagAsync(string tagId, NfcTagWriteRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Locks the held tag so it can never be written again. <b>This is permanent.</b> <c>409 tag_not_supported</c> when the
    /// tag cannot be locked.
    /// </summary>
    [BridgePost("tags/{tagId}/lock")]
    Task LockTagAsync(string tagId, CancellationToken cancellationToken = default);

    /// <summary>Lets the scan move on to the next tag before the hold runs out. Does nothing for a tag already released.</summary>
    [BridgePost("tags/{tagId}/release")]
    Task ReleaseTagAsync(string tagId, CancellationToken cancellationToken = default);

    /// <summary>A tag came into range during a scan.</summary>
    [BridgeEvent("nfc.tag")]
    Task<IAsyncDisposable> OnTagAsync(Func<NfcTag, Task> handler);

    /// <summary>The scan session ended, and why.</summary>
    [BridgeEvent("nfc.scan")]
    Task<IAsyncDisposable> OnScanEndedAsync(Func<NfcScanEnded, Task> handler);
}
