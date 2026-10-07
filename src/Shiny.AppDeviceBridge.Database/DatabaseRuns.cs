namespace Shiny.AppDeviceBridge.Database;

/// <summary>
/// The queries running right now, by the id the page gave each, so a cancel can reach one.
/// </summary>
/// <remarks>
/// Linked with the request's own token rather than replacing it: a page that goes away still stops a
/// query, and a cancel stops it whether or not an aborted fetch ever made it through whatever is between
/// the page and this device.
/// </remarks>
sealed class DatabaseRuns
{
    readonly Dictionary<Guid, CancellationTokenSource> running = [];
    readonly Lock gate = new();

    public Run Start(Guid? id, CancellationToken requestToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(requestToken);

        if (id is { } named)
        {
            lock (this.gate)
            {
                // a second run under the same id is a client bug; the older one is the one to stop
                if (this.running.Remove(named, out var older))
                    older.Cancel();

                this.running[named] = source;
            }
        }

        return new Run(this, id, source);
    }

    public bool Cancel(Guid id)
    {
        CancellationTokenSource? source;

        lock (this.gate)
            this.running.TryGetValue(id, out source);

        if (source is null)
            return false;

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // finished between the lookup and the cancel, which is what the cancel wanted anyway
        }

        return true;
    }

    void End(Guid? id, CancellationTokenSource source)
    {
        if (id is { } named)
        {
            lock (this.gate)
            {
                if (this.running.TryGetValue(named, out var current) && ReferenceEquals(current, source))
                    this.running.Remove(named);
            }
        }

        source.Dispose();
    }

    public sealed class Run(DatabaseRuns owner, Guid? id, CancellationTokenSource source) : IDisposable
    {
        public CancellationToken Token => source.Token;

                public void Dispose() => owner.End(id, source);
    }
}
