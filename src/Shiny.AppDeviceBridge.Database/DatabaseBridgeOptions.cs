namespace Shiny.AppDeviceBridge.Database;

public sealed class DatabaseBridgeOptions
{
    /// <summary>
    /// How long one SQLite statement may run before it is interrupted — 30 seconds by default. A runaway statement on a
    /// phone is a warm device and a flat battery, and a write lock on the file the whole time.
    /// </summary>
    public TimeSpan StatementTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How many queries the history keeps per database: 500 by default. The oldest go as new ones are recorded.</summary>
    public int MaxHistory { get; set; } = 500;

    internal void Validate()
    {
        if (this.StatementTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("DatabaseBridgeOptions.StatementTimeout must be positive.");

        if (this.MaxHistory < 1)
            throw new InvalidOperationException("DatabaseBridgeOptions.MaxHistory must be at least 1.");
    }
}
