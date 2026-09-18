using Shiny.AppDeviceBridge.Simulator.Simulation;

namespace Shiny.AppDeviceBridge.Simulator.Trails;

public enum TrailPlayback
{
    Stopped,
    Playing,
    Paused,
    Finished
}

/// <summary>
/// Plays one <see cref="Trail"/> against the simulator: each step after its delay, divided by <see cref="Speed"/>. Several
/// players can run at once — a GPS walk while a BLE device comes and goes.
/// </summary>
public sealed class TrailPlayer(SimulatorState state, Trail trail) : IDisposable
{
    readonly Lock gate = new();
    CancellationTokenSource? cancel;
    TaskCompletionSource? resume;
    double speed = 1;

    public Trail Trail => trail;

    public TrailPlayback Playback { get; private set; }

    /// <summary>The step playing, or about to; -1 before the first.</summary>
    public int Position { get; private set; } = -1;

    /// <summary>Passes completed, for a looping trail.</summary>
    public int Passes { get; private set; }

    /// <summary>Delays are divided by this: 2 plays twice as fast. Takes effect from the next step.</summary>
    public double Speed
    {
        get => this.speed;
        set => this.speed = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), "Speed must be positive.");
    }

    /// <summary>Loops even if the trail does not ask to.</summary>
    public bool Loop { get; set; } = trail.Loop;

    /// <summary>Finishes when playback ends — the last step, or <see cref="Stop"/>.</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    /// <summary>A step played, the player paused, stopped or finished. From the player's thread.</summary>
    public event Action<TrailPlayer>? Changed;

    /// <summary>Starts from the first step. Restarts one already playing.</summary>
    public Task Play()
    {
        this.Stop();

        lock (this.gate)
        {
            this.cancel = new CancellationTokenSource();
            this.resume = null;
            this.Position = -1;
            this.Passes = 0;
            this.Playback = TrailPlayback.Playing;
            this.Completion = this.RunAsync(this.cancel.Token);
        }

        state.Log($"trail '{trail.Name}' playing ({trail.Steps.Count} steps, {trail.Duration.TotalSeconds:0.#}s at 1×)");
        this.Changed?.Invoke(this);
        return this.Completion;
    }

    public void Pause()
    {
        lock (this.gate)
        {
            if (this.Playback != TrailPlayback.Playing)
                return;

            this.resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            this.Playback = TrailPlayback.Paused;
        }

        state.Log($"trail '{trail.Name}' paused");
        this.Changed?.Invoke(this);
    }

    public void Resume()
    {
        TaskCompletionSource? waiting;
        lock (this.gate)
        {
            if (this.Playback != TrailPlayback.Paused)
                return;

            waiting = this.resume;
            this.resume = null;
            this.Playback = TrailPlayback.Playing;
        }

        waiting?.TrySetResult();
        state.Log($"trail '{trail.Name}' resumed");
        this.Changed?.Invoke(this);
    }

    public void Stop()
    {
        CancellationTokenSource? running;
        lock (this.gate)
        {
            running = this.cancel;
            this.cancel = null;
            this.resume?.TrySetCanceled();
            this.resume = null;
        }

        if (running is null)
            return;

        running.Cancel();
        running.Dispose();
    }

    async Task RunAsync(CancellationToken cancellationToken)
    {
        // Off the caller's thread: the first step's delay may be zero, and the caller may be the UI.
        await Task.Yield();

        try
        {
            do
            {
                for (var i = 0; i < trail.Steps.Count; i++)
                {
                    var step = trail.Steps[i];
                    this.Position = i;

                    if (step.DelayMs > 0)
                        await Task.Delay(TimeSpan.FromMilliseconds(step.DelayMs / this.speed), state.Time, cancellationToken);

                    await this.WhilePausedAsync(cancellationToken);

                    try
                    {
                        step.Apply(state);
                    }
                    catch (ArgumentException ex)
                    {
                        // A bad step is reported and skipped; the rest of the trail still plays.
                        state.Log($"trail '{trail.Name}' step {i + 1}: {ex.Message}");
                    }

                    this.Changed?.Invoke(this);
                }

                this.Passes++;
            }
            while (this.Loop && trail.Steps.Count > 0 && !cancellationToken.IsCancellationRequested);

            this.Playback = TrailPlayback.Finished;
            state.Log($"trail '{trail.Name}' finished");
        }
        catch (OperationCanceledException)
        {
            this.Playback = TrailPlayback.Stopped;
            state.Log($"trail '{trail.Name}' stopped");
        }

        this.Changed?.Invoke(this);
    }

    Task WhilePausedAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource? waiting;
        lock (this.gate)
            waiting = this.resume;

        return waiting?.Task.WaitAsync(cancellationToken) ?? Task.CompletedTask;
    }

    public void Dispose() => this.Stop();
}
