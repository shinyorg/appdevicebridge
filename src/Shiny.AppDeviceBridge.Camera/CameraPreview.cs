using Shiny.Controls.Camera;

namespace Shiny.AppDeviceBridge.Camera;

/// <summary>
/// Fans the viewfinder's JPEG frames out to everyone watching, and tells the camera to stop producing them when nobody is.
/// <para>
/// <b>Latest frame wins, and a slow viewer is dropped behind rather than waited for.</b> A viewfinder is the one stream
/// where falling behind is worse than losing frames: a viewer on a poor connection that received every frame in order
/// would be watching an ever-growing delay, which is useless for framing a shot and looks like a frozen picture. Each
/// viewer holds one slot, and a new frame overwrites whatever is still in it.
/// </para>
/// <para>
/// The reference count is the other half. The camera only delivers frames while a viewer is subscribed, so the first
/// subscriber turns delivery on and the last one turns it off — a camera screen nobody is watching from elsewhere costs
/// nothing beyond its own preview. Encoding JPEGs on a phone that is also recording is the difference between a warm
/// device and a throttled one.
/// </para>
/// </summary>
public sealed class CameraPreview
{
    readonly Lock gate = new();
    readonly List<Viewer> viewers = [];

    /// <summary>Raised with true when the first viewer arrives and false when the last one leaves.</summary>
    public event Action<bool>? StreamingChanged;

    public bool HasViewers
    {
        get
        {
            lock (this.gate)
                return this.viewers.Count > 0;
        }
    }

    /// <summary>Hands a frame to everyone watching. Called from the capture thread, so it only hands the array over.</summary>
    public void Publish(byte[] jpeg)
    {
        ArgumentNullException.ThrowIfNull(jpeg);

        lock (this.gate)
        {
            foreach (var viewer in this.viewers)
                viewer.Offer(jpeg);
        }
    }

    /// <summary>Starts watching. Dispose the viewer when the response ends.</summary>
    public Viewer Watch()
    {
        var viewer = new Viewer(this);
        bool first;

        lock (this.gate)
        {
            first = this.viewers.Count == 0;
            this.viewers.Add(viewer);
        }

        if (first)
            this.StreamingChanged?.Invoke(true);

        return viewer;
    }

    void Leave(Viewer viewer)
    {
        bool last;

        lock (this.gate)
        {
            if (!this.viewers.Remove(viewer))
                return;

            last = this.viewers.Count == 0;
        }

        if (last)
            this.StreamingChanged?.Invoke(false);
    }

    /// <summary>One viewer's slot: the newest frame, and a signal that there is one.</summary>
    public sealed class Viewer : IDisposable
    {
        readonly CameraPreview owner;
        readonly SemaphoreSlim ready = new(0, 1);
        byte[]? pending;
        int disposed;

        internal Viewer(CameraPreview owner) => this.owner = owner;

        internal void Offer(byte[] jpeg)
        {
            // Overwriting a frame this viewer has not collected yet is the point — see the class remarks.
            Interlocked.Exchange(ref this.pending, jpeg);

            try
            {
                if (this.ready.CurrentCount == 0)
                    this.ready.Release();
            }
            catch (SemaphoreFullException)
            {
                // Raced another publish; the frame is in the slot either way.
            }
            catch (ObjectDisposedException)
            {
                // The viewer left mid-publish.
            }
        }

        /// <summary>Waits for the next frame.</summary>
        public async Task<byte[]> NextAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                await this.ready.WaitAsync(cancellationToken).ConfigureAwait(false);

                if (Interlocked.Exchange(ref this.pending, null) is { } frame)
                    return frame;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) != 0)
                return;

            this.owner.Leave(this);
            this.ready.Dispose();
        }
    }
}

/// <summary>
/// Turns camera frames into viewfinder JPEGs. A frame analyzer because that is the one per-frame hook the camera control
/// has, and attaching one is what makes it deliver frames at all; it detects nothing and draws nothing.
/// <para>
/// <see cref="WantsFrame"/> is the throttle, asked on the capture thread before the platform materializes anything, so a
/// frame that would be dropped costs nothing. The frame itself is borrowed — a pooled native buffer released when
/// <see cref="AnalyzeAsync"/> returns — so the encode happens inside the call.
/// </para>
/// </summary>
sealed class CameraPreviewAnalyzer(CameraPreview preview, CameraBridgeOptions options) : IFrameAnalyzer
{
    readonly TimeSpan interval = TimeSpan.FromSeconds(1d / options.PreviewFramesPerSecond);
    readonly Lock gate = new();
    DateTime nextFrameDue = DateTime.MinValue;

    public string Id => "appdevicebridge.camera.preview";

    public bool WantsFrame()
    {
        if (!preview.HasViewers)
            return false;

        var now = DateTime.UtcNow;
        lock (this.gate)
        {
            if (now < this.nextFrameDue)
                return false;

            this.nextFrameDue = now + this.interval;
            return true;
        }
    }

    public async ValueTask<IReadOnlyList<OverlayBox>?> AnalyzeAsync(CameraFrame frame, CancellationToken ct)
    {
        try
        {
            if (await CameraPreviewJpeg.EncodeAsync(frame, options.PreviewMaxEdge, options.PreviewQuality).ConfigureAwait(false) is { } jpeg)
                preview.Publish(jpeg);
        }
        catch (Exception)
        {
            // One dropped frame. The next is a fraction of a second away, and taking the capture pipeline down over it
            // would stop the preview and any recording with it.
        }

        return null;
    }
}
