namespace Shiny.AppDeviceBridge.Camera;

public sealed class CameraBridgeOptions
{
    /// <summary>The file root captures are filed into. <c>data</c> by default.</summary>
    public string Root { get; set; } = "data";

    /// <summary>The folder inside <see cref="Root"/>, created as needed. <c>camera</c> by default; empty files at the root.</summary>
    public string Folder { get; set; } = "camera";

    /// <summary>Record sound with video. On by default: a clip somebody watches later is one they expect to hear.</summary>
    public bool IncludeAudio { get; set; } = true;

    /// <summary>
    /// Show the bridge's own camera screen when a page asks the device to open its camera, and close it when asked. On by
    /// default. Turn it off when the app shows a camera screen of its own — handle
    /// <see cref="CameraBridgeSession.OpenRequested"/> to navigate there.
    /// </summary>
    public bool PresentWhenOpened { get; set; } = true;

    /// <summary>
    /// Frames a second in the viewfinder stream. Enough to frame a shot and follow a subject, and low enough that encoding
    /// does not compete with a recording for the same silicon.
    /// </summary>
    public int PreviewFramesPerSecond { get; set; } = 12;

    /// <summary>
    /// The longest edge of a viewfinder frame, in pixels. Small on purpose: the frame is for framing on another screen, and
    /// the stream's cost grows with the square of this. The captures are full resolution either way.
    /// </summary>
    public int PreviewMaxEdge { get; set; } = 720;

    /// <summary>JPEG quality of the viewfinder, 1–100. At 55 a 720p frame is roughly 40–60 KB.</summary>
    public int PreviewQuality { get; set; } = 55;

    internal void Validate()
    {
        if (!WebAppFileStore.IsValidName(this.Root))
            throw new InvalidOperationException($"CameraBridgeOptions.Root '{this.Root}' is not a file root name.");

        if (WebAppFilePath.Normalize(this.Folder) is null)
            throw new InvalidOperationException($"CameraBridgeOptions.Folder '{this.Folder}' is not a relative path.");

        if (this.PreviewFramesPerSecond is < 1 or > 60)
            throw new InvalidOperationException("CameraBridgeOptions.PreviewFramesPerSecond is 1 to 60.");

        if (this.PreviewMaxEdge is < 160 or > 3840)
            throw new InvalidOperationException("CameraBridgeOptions.PreviewMaxEdge is 160 to 3840.");

        if (this.PreviewQuality is < 1 or > 100)
            throw new InvalidOperationException("CameraBridgeOptions.PreviewQuality is 1 to 100.");
    }
}
