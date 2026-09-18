using Shiny.AppDeviceBridge.Simulator.Simulation;

namespace Shiny.AppDeviceBridge.Simulator.Trails;

/// <summary>
/// Records what is done to the simulator — values set, events fired, bridges switched — with the time between each, as a
/// trail that plays it back. Steps a trail plays while recording are recorded too.
/// </summary>
public sealed class TrailRecording : IDisposable
{
    readonly SimulatorState state;
    readonly Lock gate = new();
    readonly List<TrailStep> steps = [];
    long last;
    bool stopped;

    public TrailRecording(SimulatorState state, string name)
    {
        this.state = state;
        this.Name = name;
        this.last = state.Time.GetTimestamp();
        state.Applied += this.OnApplied;
        state.Log($"recording trail '{name}'");
    }

    public string Name { get; }

    public int Count
    {
        get
        {
            lock (this.gate)
                return this.steps.Count;
        }
    }

    /// <summary>Stops recording and returns the trail.</summary>
    public Trail Stop()
    {
        this.Dispose();

        lock (this.gate)
        {
            this.state.Log($"recorded trail '{this.Name}': {this.steps.Count} steps");
            return new Trail { Name = this.Name, Steps = [.. this.steps] };
        }
    }

    void OnApplied(TrailStep step)
    {
        lock (this.gate)
        {
            if (this.stopped)
                return;

            var now = this.state.Time.GetTimestamp();
            step.DelayMs = (int)Math.Round(this.state.Time.GetElapsedTime(this.last, now).TotalMilliseconds);
            this.last = now;
            this.steps.Add(step);
        }
    }

    public void Dispose()
    {
        lock (this.gate)
            this.stopped = true;

        this.state.Applied -= this.OnApplied;
    }
}
