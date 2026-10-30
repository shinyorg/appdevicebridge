using System.Collections.Concurrent;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Audio.Client;
using Shiny.AppDeviceBridge.Client;
using Shiny.Net.HttpServer;
using AudioDevice = Shiny.AppDeviceBridge.Audio.Client.AudioDevice;
using AudioDeviceIo = Shiny.AppDeviceBridge.Audio.Client.AudioDeviceIo;
using AudioDeviceType = Shiny.AppDeviceBridge.Audio.Client.AudioDeviceType;
using AudioEffectPreset = Shiny.AppDeviceBridge.Audio.Client.AudioEffectPreset;
using AudioRecording = Shiny.AppDeviceBridge.Audio.Client.AudioRecording;
using AudioRecordMode = Shiny.AppDeviceBridge.Audio.Client.AudioRecordMode;
using ContractAccess = Shiny.AppDeviceBridge.Client.AccessState;
using Native = Shiny.Audio;

namespace Shiny.AppDeviceBridge.Audio;

public sealed class AudioBridgeOptions
{
    /// <summary>The file root recordings are filed into. <c>data</c> by default.</summary>
    public string Root { get; set; } = "data";

    /// <summary>The folder inside <see cref="Root"/>, created as needed. <c>audio-recordings</c> by default; empty files at the root.</summary>
    public string Folder { get; set; } = "audio-recordings";

    /// <summary>
    /// The longest a recording may run. A page asking for longer, or for no limit, gets this; the recording is stopped and
    /// filed when it runs out. One hour by default, so a page that went away cannot leave the microphone recording until
    /// the disk fills. Null lets a recording run until it is stopped.
    /// </summary>
    public TimeSpan? MaxRecordingDuration { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Registers Shiny.Audio's services for the platform — Shiny.Audio.Linux's on Linux. Turn off to register your own
    /// <see cref="Native.IAudioPlayer"/>, <see cref="Native.IAudioRecorder"/>, <see cref="Native.IAudioDevices"/> and
    /// <see cref="Native.IAudioMonitor"/> instead.
    /// </summary>
    public bool RegisterAudioServices { get; set; } = true;

    internal void Validate()
    {
        if (!WebAppFileStore.IsValidName(this.Root))
            throw new InvalidOperationException($"AudioBridgeOptions.Root '{this.Root}' is not a file root name.");

        if (WebAppFilePath.Normalize(this.Folder) is null)
            throw new InvalidOperationException($"AudioBridgeOptions.Folder '{this.Folder}' is not a relative path.");

        if (this.MaxRecordingDuration is { } max && max <= TimeSpan.Zero)
            throw new InvalidOperationException("AudioBridgeOptions.MaxRecordingDuration is positive, or null for no limit.");
    }
}

public static class AudioBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/audio</c> and registers Shiny.Audio's services for the platform.
    /// <code>
    /// bridge.AddAudioBridge();
    /// bridge.AddAudioBridge(o => o.MaxRecordingDuration = TimeSpan.FromMinutes(10));
    /// </code>
    /// <para>
    /// Platform setup: <c>RECORD_AUDIO</c> on Android, and Shiny's host started by the app. <c>NSMicrophoneUsageDescription</c>
    /// on Apple platforms, plus the <c>com.apple.security.device.audio-input</c> entitlement where the app is sandboxed.
    /// Linux needs PulseAudio, PipeWire or ALSA. The monitor is Apple, Android and Linux only; Windows answers 501 for it.
    /// </para>
    /// </summary>
    public static TBuilder AddAudioBridge<TBuilder>(this TBuilder bridge, Action<AudioBridgeOptions>? configure = null)
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

        var services = bridge.Services;
        var options = services.FirstOrDefault(x => x.ServiceType == typeof(AudioBridgeOptions))?.ImplementationInstance as AudioBridgeOptions;
        var added = options is not null;
        if (options is null)
        {
            options = new AudioBridgeOptions();
            services.AddSingleton(options);
        }

        configure?.Invoke(options);
        options.Validate();

        if (added)
            return bridge;

        if (options.RegisterAudioServices)
        {
#if AUDIO_LINUX
            // Before AddAudioServices: both TryAdd, and on plain .NET it registers no capture or playback of its own.
            services.AddLinuxAudio();
#endif
            services.AddAudioServices();
        }

        bridge.AddBridge<AudioBridge>();
        return bridge;
    }
}

/// <summary>
/// <c>/_bridge/audio</c> over Shiny.Audio's <see cref="Native.IAudioPlayer"/>, <see cref="Native.IAudioRecorder"/>,
/// <see cref="Native.IAudioDevices"/> and <see cref="Native.IAudioMonitor"/>.
/// <code>
/// GET    /_bridge/audio/status
/// POST   /_bridge/audio/access             requests microphone permission
/// GET    /_bridge/audio/playback           the clips playing
/// POST   /_bridge/audio/playback           { "url": "https://…" } or { "file": { "root": "data", "path": "…" } }
/// DELETE /_bridge/audio/playback/{id}
/// DELETE /_bridge/audio/playback           stops every clip
/// PUT    /_bridge/audio/volume             { "volume": 0.5 }
/// POST   /_bridge/audio/recording          { "mode": "Wet", "effect": "Robot", "maxDurationSeconds": 60 }
/// DELETE /_bridge/audio/recording          stops and files it  →  { "file": { "root": "data", "path": "…" }, … }
/// POST   /_bridge/audio/recording/cancel   keeps nothing
/// GET    /_bridge/audio/devices
/// POST   /_bridge/audio/devices/picker
/// POST   /_bridge/audio/monitor            { "gain": 0.8, "processing": { "echoCancellation": true } }
/// PUT    /_bridge/audio/monitor            { "gain": 0.5 }
/// DELETE /_bridge/audio/monitor
///
/// events: audio.playback.ended, audio.playback.level, audio.volume, audio.recording.level, audio.recording.ended,
///         audio.devices, audio.monitor.level, audio.monitor.ended
/// </code>
/// <para>
/// There is one microphone. A recording and the monitor exclude each other, and whichever comes second answers 409
/// <c>microphone_busy</c>. The monitor belongs to whoever listens for <c>audio.monitor.level</c>: it needs one to start,
/// and stops once the last is gone, so a page that went away cannot leave the microphone on the speaker. A recording has
/// the app's time limit instead.
/// </para>
/// <para>
/// A URL is fetched by the device rather than the page, so it is not subject to the page's CORS — but nothing it returns
/// reaches the page, only the sound. A file is one of the page's own, named by file root.
/// </para>
/// </summary>
public sealed class AudioBridge : IWebAppBridge, IDisposable
{
    const int MaxClips = 32;
    const int MaxUrlLength = 2048;

    readonly IServiceProvider services;
    readonly WebAppFileRoots roots;
    readonly AudioBridgeOptions options;
    readonly IWebAppMainThread mainThread;
    readonly TimeProvider time;
    readonly Native.IAudioPlayer? player;
    readonly Native.IAudioDevices? devices;
    readonly Native.IAudioMonitor? monitor;
    readonly bool recordingSupported;

    readonly ConcurrentDictionary<string, Clip> clips = new(StringComparer.Ordinal);
    readonly WebAppEventSource<AudioPlaybackEnded> playbackEnded = new();
    readonly WebAppEventSource<AudioLevelChanged> recordingLevels = new();
    readonly WebAppEventSource<AudioRecordingEnded> recordingEnded = new();
    readonly WebAppEventSource<AudioLevelChanged> monitorLevels = new();
    readonly WebAppEventSource<AudioMonitorEnded> monitorEnded = new();
    readonly Lock gate = new();

    volatile MicrophoneUse use;
    Native.IAudioRecorder? recorder;
    ITimer? recordingLimit;

    public AudioBridge(IServiceProvider services, WebAppFileRoots roots)
    {
        this.services = services;
        this.roots = roots;
        this.options = services.GetOptionalService<AudioBridgeOptions>() ?? new AudioBridgeOptions();
        this.mainThread = services.GetRequiredService<IWebAppMainThread>();
        this.time = services.GetOptionalService<TimeProvider>() ?? TimeProvider.System;
        this.player = services.GetOptionalService<Native.IAudioPlayer>();
        this.devices = services.GetOptionalService<Native.IAudioDevices>();
        this.monitor = services.GetOptionalService<Native.IAudioMonitor>();

        // The recorder is transient and builds on the capture source, which plain .NET has none of: the recorder is
        // registered there but cannot be built. Building one is the only honest test.
        if (this.TryCreateRecorder() is { } probe)
        {
            this.recordingSupported = true;
            _ = DisposeQuietlyAsync(probe);
        }

        if (this.monitor is not null)
            this.monitor.InputLevelChanged += this.OnMonitorLevel;
    }

    public string Name => "audio";

    public bool IsSupported => this.player is not null || this.recordingSupported || this.devices is not null || this.monitor is not null;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapEvent("audio.playback.ended", this.playbackEnded.ListenAsync, AudioJsonContext.Default.AudioPlaybackEnded)
        .MapEvent("audio.playback.level", this.PlaybackLevelsAsync, AudioJsonContext.Default.AudioLevelChanged)
        .MapEvent("audio.volume", this.VolumeChangesAsync, AudioJsonContext.Default.AudioVolume)
        .MapEvent("audio.recording.level", this.recordingLevels.ListenAsync, AudioJsonContext.Default.AudioLevelChanged)
        .MapEvent("audio.recording.ended", this.recordingEnded.ListenAsync, AudioJsonContext.Default.AudioRecordingEnded)
        .MapEvent("audio.devices", this.DeviceChangesAsync, AudioJsonContext.Default.AudioDevices)
        .MapEvent("audio.monitor.level", ct => this.monitorLevels.ListenAsync(this.OnMonitorListenerStopped, ct), AudioJsonContext.Default.AudioLevelChanged)
        .MapEvent("audio.monitor.ended", this.monitorEnded.ListenAsync, AudioJsonContext.Default.AudioMonitorEnded)
        .MapGet("/status", this.StatusAsync)
        .MapPost("/access", this.RequestAccessAsync)
        .MapGet("/playback", this.PlaybackAsync)
        .MapPost("/playback", this.PlayAsync)
        .MapDelete("/playback/{id}", this.StopPlaybackAsync)
        .MapDelete("/playback", this.StopAllPlaybackAsync)
        .MapPut("/volume", this.SetVolumeAsync)
        .MapPost("/recording", this.StartRecordingAsync)
        .MapDelete("/recording", this.StopRecordingAsync)
        .MapPost("/recording/cancel", this.CancelRecordingAsync)
        .MapGet("/devices", this.DevicesAsync)
        .MapPost("/devices/picker", this.ShowOutputPickerAsync)
        .MapPost("/monitor", this.StartMonitorAsync)
        .MapPut("/monitor", this.SetMonitorGainAsync)
        .MapDelete("/monitor", this.StopMonitorAsync);

    ValueTask StatusAsync(HttpContext context) => WebAppBridgeResults.Json(context, this.CreateStatus(), AudioJsonContext.Default.AudioStatus);

    AudioStatus CreateStatus()
    {
        Native.IAudioRecorder? current;
        lock (this.gate)
            current = this.recorder;

        return new AudioStatus(
            this.player is not null,
            this.recordingSupported,
            this.devices is not null,
            this.monitor is not null,
            this.player?.IsVolumeControlSupported == true,
            this.player?.IsPlayerAnalysisSupported == true,
            this.player?.Volume,
            this.clips.Count,
            this.use == MicrophoneUse.Recording,
            current?.Elapsed.TotalSeconds,
            this.use == MicrophoneUse.Monitoring,
            this.options.MaxRecordingDuration?.TotalSeconds
        );
    }

    async ValueTask RequestAccessAsync(HttpContext context)
    {
        Native.IAudioRecorder? probe = null;
        try
        {
            probe = this.recordingSupported ? this.TryCreateRecorder() : null;

            Func<Task<AccessState>>? request = probe is not null
                ? probe.RequestAccess
                : this.monitor is { } m ? m.RequestAccess : null;

            if (request is null)
            {
                await NotSupported(context, "The microphone");
                return;
            }

            var access = await this.mainThread.InvokeAsync(request);
            await WebAppBridgeResults.Json(context, new AudioAccessResult(BridgeEnum.Convert<AccessState, ContractAccess>(access)), AudioJsonContext.Default.AudioAccessResult);
        }
        finally
        {
            if (probe is not null)
                await DisposeQuietlyAsync(probe);
        }
    }

    // ---- playback

    ValueTask PlaybackAsync(HttpContext context)
    {
        if (this.player is null)
            return NotSupported(context, "Audio playback");

        // The player's order is the oldest first; clips it no longer lists are already on their way out.
        IReadOnlyList<AudioPlayback> playing =
        [
            .. this.player.Active
                .Select(x => this.clips.TryGetValue(Key(x.Id), out var clip) ? clip.Contract : null)
                .OfType<AudioPlayback>()
        ];

        return WebAppBridgeResults.Json(context, playing, AudioJsonContext.Default.IReadOnlyListAudioPlayback);
    }

    async ValueTask PlayAsync(HttpContext context)
    {
        if (this.player is not { } p)
        {
            await NotSupported(context, "Audio playback");
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, AudioJsonContext.Default.AudioPlayRequest);
        if (body is null || (body.Url is null) == (body.File is null))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"url\": \"https://…\" } or { \"file\": { \"root\": \"data\", \"path\": \"…\" } }.");
            return;
        }

        string source;
        if (body.Url is { } url)
        {
            if (url.Length > MaxUrlLength
                || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                await WebAppBridgeResults.BadRequest(context, $"Expected an absolute http or https URL of at most {MaxUrlLength} characters.");
                return;
            }

            source = uri.AbsoluteUri;
        }
        else
        {
            var file = body.File!;
            if (!this.roots.TryGet(file.Root, out var root))
            {
                await WebAppBridgeResults.NotFound(context, $"No file root '{file.Root}'.");
                return;
            }

            if (WebAppFilePath.Normalize(file.Path) is not { Length: > 0 } path)
            {
                await WebAppBridgeResults.BadRequest(context, WebAppFileException.InvalidPath().Message);
                return;
            }

            if (root.GetLocalPath(path) is not { } local || !File.Exists(local))
            {
                await WebAppBridgeResults.NotFound(context, "No such file.");
                return;
            }

            source = local;
        }

        if (this.clips.Count >= MaxClips)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "too_many_clips", $"At most {MaxClips} clips play at once. Stop one first.");
            return;
        }

        Native.IAudioPlayback playback;
        try
        {
            // Not the request's token: Shiny.Audio stops a clip when the token it started with fires, and the request's
            // fires as soon as this answers — the clip would end the moment the page heard it had started.
            playback = await this.mainThread.InvokeAsync(() => p.StartAsync(source, CancellationToken.None));
        }
        catch (Exception ex)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status500InternalServerError, "playback_failed", ex.Message);
            return;
        }

        var clip = new Clip(playback, new AudioPlayback(Key(playback.Id), body.Url is null ? null : source, body.File));
        this.clips[clip.Contract.Id] = clip;
        _ = this.WatchAsync(clip);

        await WebAppBridgeResults.Json(context, clip.Contract, AudioJsonContext.Default.AudioPlayback);
    }

    async Task WatchAsync(Clip clip)
    {
        string? error = null;
        try
        {
            await clip.Playback.Completion;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            this.clips.TryRemove(clip.Contract.Id, out _);
            this.playbackEnded.Publish(new AudioPlaybackEnded(clip.Contract.Id, clip.Stopped, error));
            await DisposeQuietlyAsync(clip.Playback);
        }
    }

    async ValueTask StopPlaybackAsync(HttpContext context)
    {
        if (this.player is null)
        {
            await NotSupported(context, "Audio playback");
            return;
        }

        var id = context.Request.RouteValues["id"]?.ToString();
        if (id is null || !this.clips.TryGetValue(id, out var clip))
        {
            await WebAppBridgeResults.NotFound(context, "That clip is not playing.");
            return;
        }

        clip.Stopped = true;
        await clip.Playback.StopAsync();
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask StopAllPlaybackAsync(HttpContext context)
    {
        if (this.player is not { } p)
        {
            await NotSupported(context, "Audio playback");
            return;
        }

        foreach (var clip in this.clips.Values)
            clip.Stopped = true;

        await p.StopAsync();
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask SetVolumeAsync(HttpContext context)
    {
        if (this.player is not { IsVolumeControlSupported: true } p)
        {
            await NotSupported(context, "Setting the volume");
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, AudioJsonContext.Default.AudioVolumeRequest);
        if (body is null || body.Volume is < 0f or > 1f || Single.IsNaN(body.Volume))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"volume\": 0.5 } from 0 to 1.");
            return;
        }

        var volume = await this.mainThread.InvokeAsync(() =>
        {
            p.Volume = body.Volume;
            return Task.FromResult(p.Volume);
        });

        await WebAppBridgeResults.Json(context, new AudioVolume(volume), AudioJsonContext.Default.AudioVolume);
    }

    IAsyncEnumerable<AudioLevelChanged> PlaybackLevelsAsync(CancellationToken cancellationToken)
        => WebAppEventStream.FromEvent<AudioLevelChanged>(emit =>
        {
            if (this.player is not { } p)
                return () => { };

            EventHandler<double> handler = (_, level) => emit(new AudioLevelChanged(level));
            p.AudioLevelChanged += handler;
            return () => p.AudioLevelChanged -= handler;
        }, cancellationToken);

    IAsyncEnumerable<AudioVolume> VolumeChangesAsync(CancellationToken cancellationToken)
        => WebAppEventStream.FromEvent<AudioVolume>(emit =>
        {
            if (this.player is not { } p)
                return () => { };

            EventHandler<float> handler = (_, volume) => emit(new AudioVolume(volume));
            p.VolumeChanged += handler;
            return () => p.VolumeChanged -= handler;
        }, cancellationToken);

    // ---- recording

    async ValueTask StartRecordingAsync(HttpContext context)
    {
        if (!this.recordingSupported)
        {
            await NotSupported(context, "Audio recording");
            return;
        }

        var (valid, body) = await ReadOptionalBodyAsync(context, AudioJsonContext.Default.AudioRecordingRequest);
        if (!valid)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"mode\": \"Wet\", \"effect\": \"Robot\", \"maxDurationSeconds\": 60 }, or no body.");
            return;
        }

        body ??= new AudioRecordingRequest();

        if (body.MaxDurationSeconds is <= 0 || (body.MaxDurationSeconds is { } seconds && Double.IsNaN(seconds)))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected a positive maxDurationSeconds.");
            return;
        }

        var input = this.FindDevice(body.InputDeviceId, Native.AudioDeviceIo.Input);
        if (body.InputDeviceId is not null && input is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Unknown input device. GET /_bridge/audio/devices lists them.");
            return;
        }

        var duration = body.MaxDurationSeconds is { } requested ? TimeSpan.FromSeconds(Math.Min(requested, TimeSpan.MaxValue.TotalSeconds / 2)) : (TimeSpan?)null;
        if (this.options.MaxRecordingDuration is { } max && (duration is null || duration > max))
            duration = max;

        if (this.TryCreateRecorder() is not { } r)
        {
            await NotSupported(context, "Audio recording");
            return;
        }

        if (!await this.EnsureAccessAsync(context, r.RequestAccess))
        {
            await DisposeQuietlyAsync(r);
            return;
        }

        if (!this.TryClaim(MicrophoneUse.Recording))
        {
            await DisposeQuietlyAsync(r);
            await MicrophoneBusy(context);
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "shiny-appdevicebridge", "audio");
        var recording = new Native.AudioRecordingOptions
        {
            Path = Path.Combine(directory, Guid.NewGuid().ToString("n") + ".wav"),
            Mode = BridgeEnum.Convert<AudioRecordMode, Native.AudioRecordMode>(body.Mode),
            Effects = body.Effect is { } effect ? Native.AudioEffectPresets.Create(BridgeEnum.Convert<AudioEffectPreset, Native.AudioEffectPreset>(effect)) : null,
            InputDevice = input,
            Processing = ToNative(body.Processing)
        };

        r.InputLevelChanged += this.OnRecordingLevel;
        try
        {
            await this.mainThread.InvokeAsync(() => r.StartAsync(recording));
        }
        catch (Exception ex)
        {
            r.InputLevelChanged -= this.OnRecordingLevel;
            await DisposeQuietlyAsync(r);
            this.Release(MicrophoneUse.Recording);

            if (ex is InvalidOperationException)
                await MicrophoneBusy(context);
            else
                await WebAppBridgeResults.Error(context, StatusCodes.Status500InternalServerError, "recording_failed", ex.Message);

            return;
        }

        lock (this.gate)
        {
            this.recorder = r;
            if (duration is { } limit)
                this.recordingLimit = this.time.CreateTimer(_ => _ = this.EndRecordingAsync(AudioRecordingEndReason.MaxDurationReached), null, limit, Timeout.InfiniteTimeSpan);
        }

        await WebAppBridgeResults.Json(context, this.CreateStatus(), AudioJsonContext.Default.AudioStatus);
    }

    async ValueTask StopRecordingAsync(HttpContext context)
    {
        if (!this.recordingSupported)
        {
            await NotSupported(context, "Audio recording");
            return;
        }

        var ended = await this.EndRecordingAsync(AudioRecordingEndReason.Stopped);
        if (ended is null)
            await NotRecording(context);
        else if (ended.Recording is { } filed)
            await WebAppBridgeResults.Json(context, filed, AudioJsonContext.Default.AudioRecording);
        else
            await WebAppBridgeResults.Error(context, StatusCodes.Status500InternalServerError, "recording_failed", ended.Message ?? "Nothing was recorded.");
    }

    async ValueTask CancelRecordingAsync(HttpContext context)
    {
        if (!this.recordingSupported)
        {
            await NotSupported(context, "Audio recording");
            return;
        }

        if (await this.EndRecordingAsync(AudioRecordingEndReason.Cancelled) is null)
            await NotRecording(context);
        else
            await WebAppBridgeResults.NoContent(context);
    }

    /// <summary>Ends the recording, files it unless cancelled, and announces it. Null when nothing was recording.</summary>
    async Task<AudioRecordingEnded?> EndRecordingAsync(AudioRecordingEndReason reason)
    {
        Native.IAudioRecorder r;
        lock (this.gate)
        {
            if (this.use != MicrophoneUse.Recording || this.recorder is null)
                return null;

            // Held until the file is written, so a new recording cannot start into a closing one.
            this.use = MicrophoneUse.Stopping;
            r = this.recorder;
            this.recorder = null;
            this.recordingLimit?.Dispose();
            this.recordingLimit = null;
        }

        AudioRecordingEnded ended;
        try
        {
            r.InputLevelChanged -= this.OnRecordingLevel;
            var result = await r.StopAsync();

            if (reason == AudioRecordingEndReason.Cancelled)
            {
                DeleteQuietly(result?.Path);
                DeleteQuietly(result?.DryPath);
                ended = new AudioRecordingEnded(reason);
            }
            else if (result is null)
            {
                ended = new AudioRecordingEnded(AudioRecordingEndReason.Failed, null, "Nothing was recorded.");
            }
            else
            {
                // Not the request's token: a page that stops waiting must not leave a recording half filed.
                ended = new AudioRecordingEnded(reason, await this.FileAsync(result, CancellationToken.None));
            }
        }
        catch (Exception ex)
        {
            // Anything else — the disk filled while filing, say — still ends the recording, or the bridge would stay busy.
            ended = new AudioRecordingEnded(AudioRecordingEndReason.Failed, null, ex.Message);
        }
        finally
        {
            await DisposeQuietlyAsync(r);
            this.Release(MicrophoneUse.Stopping);
        }

        this.recordingEnded.Publish(ended);
        return ended;
    }

    void OnRecordingLevel(object? sender, double level) => this.recordingLevels.Publish(new AudioLevelChanged(level));

    /// <summary>
    /// Copies the finished recording into the file root, named for when it was filed — <c>REC_20260924-201502.wav</c>, its
    /// dry take beside it as <c>REC_20260924-201502-dry.wav</c> — never over an existing file, then deletes the originals.
    /// </summary>
    async Task<AudioRecording> FileAsync(Native.AudioRecording result, CancellationToken cancellationToken)
    {
        try
        {
            if (!this.roots.TryGet(this.options.Root, out var root))
                throw WebAppFileException.NotFound($"No file root '{this.options.Root}' to file the recording in.");

            var folder = WebAppFilePath.Normalize(this.options.Folder) ?? String.Empty;
            var path = await FreePathAsync(root, folder, $"REC_{this.time.GetLocalNow():yyyyMMdd-HHmmss}", cancellationToken);

            FileWriteResult written;
            await using (var content = File.OpenRead(result.Path))
                written = await root.WriteAsync(path, content, FileWriteMode.CreateNew, Int64.MaxValue, cancellationToken);

            BridgeFile? dry = null;
            if (result.DryPath is { } dryPath && File.Exists(dryPath))
            {
                var dryFiled = path[..^".wav".Length] + "-dry.wav";
                await using (var content = File.OpenRead(dryPath))
                    await root.WriteAsync(dryFiled, content, FileWriteMode.CreateNew, Int64.MaxValue, cancellationToken);

                dry = new BridgeFile(root.Name, dryFiled);
            }

            return new AudioRecording(
                new BridgeFile(root.Name, path),
                dry,
                result.Duration.TotalSeconds,
                result.SampleRate,
                result.Channels,
                written.Entry.Size ?? result.SizeInBytes,
                "audio/wav"
            );
        }
        finally
        {
            DeleteQuietly(result.Path);
            DeleteQuietly(result.DryPath);
        }
    }

    /// <summary>The stamp, or the stamp with a counter when two recordings land in the same second.</summary>
    static async Task<string> FreePathAsync(WebAppFileStore root, string folder, string stamp, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 1000; attempt++)
        {
            var name = attempt == 1 ? stamp : $"{stamp}-{attempt}";
            var path = folder.Length == 0 ? name : $"{folder}/{name}";

            if (await root.GetEntryAsync(path + ".wav", cancellationToken) is null
                && await root.GetEntryAsync(path + "-dry.wav", cancellationToken) is null)
                return path + ".wav";
        }

        throw WebAppFileException.Exists("Too many recordings in the same second.");
    }

    // ---- routes

    ValueTask DevicesAsync(HttpContext context) => this.devices is null
        ? NotSupported(context, "Audio routes")
        : WebAppBridgeResults.Json(context, this.CreateDevices(), AudioJsonContext.Default.AudioDevices);

    async ValueTask ShowOutputPickerAsync(HttpContext context)
    {
        if (this.devices is not { } d)
        {
            await NotSupported(context, "Audio routes");
            return;
        }

        await this.mainThread.InvokeAsync(d.ShowOutputPicker);
        await WebAppBridgeResults.NoContent(context);
    }

    IAsyncEnumerable<AudioDevices> DeviceChangesAsync(CancellationToken cancellationToken)
        => WebAppEventStream.FromEvent<AudioDevices>(emit =>
        {
            if (this.devices is not { } d)
                return () => { };

            EventHandler handler = (_, _) => emit(this.CreateDevices());
            d.Changed += handler;
            return () => d.Changed -= handler;
        }, cancellationToken);

    AudioDevices CreateDevices()
    {
        var d = this.devices!;
        return new AudioDevices(
            [.. d.GetInputs().Select(ToContract)],
            [.. d.GetOutputs().Select(ToContract)],
            d.CurrentInput is { } input ? ToContract(input) : null,
            d.CurrentOutput is { } output ? ToContract(output) : null
        );
    }

    Native.AudioDevice? FindDevice(string? id, Native.AudioDeviceIo io)
    {
        if (id is null || this.devices is not { } d)
            return null;

        var list = io == Native.AudioDeviceIo.Input ? d.GetInputs() : d.GetOutputs();
        return list.FirstOrDefault(x => String.Equals(x.Id, id, StringComparison.Ordinal));
    }

    static AudioDevice ToContract(Native.AudioDevice device) => new(
        device.Id,
        device.Name,
        BridgeEnum.Convert<Native.AudioDeviceIo, AudioDeviceIo>(device.Io),
        BridgeEnum.Convert<Native.AudioDeviceType, AudioDeviceType>(device.Type),
        device.IsCurrent
    );

    // ---- monitor

    async ValueTask StartMonitorAsync(HttpContext context)
    {
        if (this.monitor is not { } m)
        {
            await NotSupported(context, "The audio monitor");
            return;
        }

        if (!this.monitorLevels.HasListeners)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "not_listening", "Listen for audio.monitor.level first: the monitor runs only while someone is listening.");
            return;
        }

        var (valid, body) = await ReadOptionalBodyAsync(context, AudioJsonContext.Default.AudioMonitorRequest);
        if (!valid)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"gain\": 0.8, \"duckOtherAudio\": true }, or no body.");
            return;
        }

        body ??= new AudioMonitorRequest();

        if (body.Gain is < 0 or > 1 || (body.Gain is { } g && Double.IsNaN(g)))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected a gain from 0 to 1.");
            return;
        }

        var input = this.FindDevice(body.InputDeviceId, Native.AudioDeviceIo.Input);
        var output = this.FindDevice(body.OutputDeviceId, Native.AudioDeviceIo.Output);
        if ((body.InputDeviceId is not null && input is null) || (body.OutputDeviceId is not null && output is null))
        {
            await WebAppBridgeResults.BadRequest(context, "Unknown device. GET /_bridge/audio/devices lists them.");
            return;
        }

        if (!await this.EnsureAccessAsync(context, m.RequestAccess))
            return;

        // The native app may be monitoring already; the bridge does not take it over.
        if (m.IsMonitoring || !this.TryClaim(MicrophoneUse.Monitoring))
        {
            await MicrophoneBusy(context);
            return;
        }

        var start = new Native.AudioMonitorOptions
        {
            Gain = body.Gain ?? 1.0,
            DuckOtherAudio = body.DuckOtherAudio,
            InputDevice = input,
            OutputDevice = output,
            Processing = ToNative(body.Processing)
        };

        try
        {
            await this.mainThread.InvokeAsync(() => m.Start(start));
        }
        catch (Exception ex)
        {
            this.Release(MicrophoneUse.Monitoring);

            if (ex is InvalidOperationException)
                await MicrophoneBusy(context);
            else
                await WebAppBridgeResults.Error(context, StatusCodes.Status500InternalServerError, "monitor_failed", ex.Message);

            return;
        }

        // The last listener may have gone between the check above and now, and then nothing would ever turn it off.
        if (!this.monitorLevels.HasListeners)
            await this.EndMonitorAsync(AudioMonitorEndReason.PageClosed);

        await WebAppBridgeResults.Json(context, new AudioMonitor(m.Gain), AudioJsonContext.Default.AudioMonitor);
    }

    async ValueTask SetMonitorGainAsync(HttpContext context)
    {
        if (this.monitor is not { } m)
        {
            await NotSupported(context, "The audio monitor");
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, AudioJsonContext.Default.AudioMonitorGainRequest);
        if (body is null || body.Gain is < 0 or > 1 || Double.IsNaN(body.Gain))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"gain\": 0.5 } from 0 to 1.");
            return;
        }

        if (this.use != MicrophoneUse.Monitoring)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "not_monitoring", "The monitor is not running.");
            return;
        }

        m.Gain = body.Gain;
        await WebAppBridgeResults.Json(context, new AudioMonitor(m.Gain), AudioJsonContext.Default.AudioMonitor);
    }

    async ValueTask StopMonitorAsync(HttpContext context)
    {
        if (this.monitor is null)
        {
            await NotSupported(context, "The audio monitor");
            return;
        }

        await this.EndMonitorAsync(AudioMonitorEndReason.Stopped);
        await WebAppBridgeResults.NoContent(context);
    }

    async Task EndMonitorAsync(AudioMonitorEndReason reason)
    {
        lock (this.gate)
        {
            if (this.use != MicrophoneUse.Monitoring)
                return;

            this.use = MicrophoneUse.Stopping;
        }

        string? message = null;
        try
        {
            await this.mainThread.InvokeAsync(this.monitor!.Stop);
        }
        catch (Exception ex)
        {
            reason = AudioMonitorEndReason.Error;
            message = ex.Message;
        }
        finally
        {
            this.Release(MicrophoneUse.Stopping);
        }

        this.monitorEnded.Publish(new AudioMonitorEnded(reason, message));
    }

    void OnMonitorLevel(object? sender, double level)
    {
        if (this.use == MicrophoneUse.Monitoring)
            this.monitorLevels.Publish(new AudioLevelChanged(level));
    }

    /// <summary>The monitor is for whoever hears its level; with the last of them gone, the microphone closes.</summary>
    void OnMonitorListenerStopped(int remaining)
    {
        if (remaining == 0 && this.use == MicrophoneUse.Monitoring)
            _ = this.EndMonitorAsync(AudioMonitorEndReason.PageClosed);
    }

    // ---- helpers

    Native.IAudioRecorder? TryCreateRecorder()
    {
        try
        {
            return this.services.GetService<Native.IAudioRecorder>();
        }
        catch (InvalidOperationException)
        {
            // Registered, but with no capture source to build on.
            return null;
        }
    }

    bool TryClaim(MicrophoneUse claim)
    {
        lock (this.gate)
        {
            if (this.use != MicrophoneUse.None)
                return false;

            this.use = claim;
            return true;
        }
    }

    void Release(MicrophoneUse claim)
    {
        lock (this.gate)
        {
            if (this.use == claim)
                this.use = MicrophoneUse.None;
        }
    }

    async Task<bool> EnsureAccessAsync(HttpContext context, Func<Task<AccessState>> request)
    {
        var access = await this.mainThread.InvokeAsync(request);

        if (access == AccessState.Available)
            return true;

        if (access == AccessState.NotSupported)
            await NotSupported(context, "The microphone");
        else
            await WebAppBridgeResults.Error(context, StatusCodes.Status403Forbidden, "permission_denied", $"Microphone access is {access}.");

        return false;
    }

    static Native.AudioProcessingOptions? ToNative(AudioProcessing? processing) => processing is null
        ? null
        : new Native.AudioProcessingOptions
        {
            EchoCancellation = processing.EchoCancellation,
            NoiseSuppression = processing.NoiseSuppression,
            AutomaticGainControl = processing.AutomaticGainControl
        };

    static string Key(Guid id) => id.ToString("n");

    static ValueTask NotSupported(HttpContext context, string feature) => WebAppBridgeResults.NotSupported(context, feature);

    static ValueTask MicrophoneBusy(HttpContext context) => WebAppBridgeResults.Error(
        context,
        StatusCodes.Status409Conflict,
        "microphone_busy",
        "The microphone is already in use. Stop the recording or the monitor first."
    );

    static ValueTask NotRecording(HttpContext context) => WebAppBridgeResults.Error(
        context,
        StatusCodes.Status409Conflict,
        "not_recording",
        "Nothing is recording."
    );

    /// <summary>An absent body is valid and reads as null; a present one must parse.</summary>
    static async ValueTask<(bool Valid, T? Body)> ReadOptionalBodyAsync<T>(HttpContext context, JsonTypeInfo<T> typeInfo) where T : class
    {
        if (context.Request.ContentLength is 0)
            return (true, null);

        var body = await WebAppBridgeResults.ReadBodyAsync(context, typeInfo);
        return (body is not null || context.Request.ContentLength is null, body);
    }

    static void DeleteQuietly(string? path)
    {
        if (path is null)
            return;

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The temp folder; the OS reclaims it in time.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    static async Task DisposeQuietlyAsync(IAsyncDisposable disposable)
    {
        try
        {
            await disposable.DisposeAsync();
        }
        catch (Exception)
        {
            // A finished session has nothing left to release that a failure here would keep.
        }
    }

    public void Dispose()
    {
        if (this.monitor is not null)
        {
            this.monitor.InputLevelChanged -= this.OnMonitorLevel;

            if (this.use == MicrophoneUse.Monitoring)
                _ = this.monitor.Stop();
        }

        if (this.use == MicrophoneUse.Recording)
            _ = this.EndRecordingAsync(AudioRecordingEndReason.Cancelled);
    }

    sealed class Clip(Native.IAudioPlayback playback, AudioPlayback contract)
    {
        public Native.IAudioPlayback Playback { get; } = playback;

        public AudioPlayback Contract { get; } = contract;

        public volatile bool Stopped;
    }

    enum MicrophoneUse
    {
        None,
        Recording,
        Monitoring,
        Stopping
    }
}
