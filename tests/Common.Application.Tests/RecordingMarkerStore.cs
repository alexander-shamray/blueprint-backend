namespace Common.Application.Tests;

/// <summary>
/// A recording <see cref="IIdempotencyMarkerStore"/> on the shared
/// <see cref="PipelineLog"/>, so its two calls are placed in §6.3's sequence
/// rather than merely counted.
/// </summary>
/// <remarks>
/// A read after the handler comes too late to stop the work, and a write before
/// the aggregate-count guard leaves a marker for a command §6.3 is about to
/// refuse, refusing every retry of work that never committed.
/// </remarks>
public sealed class RecordingMarkerStore(PipelineLog log) : IIdempotencyMarkerStore
{
    /// <summary>Keys a previous attempt is to be reported as having committed.</summary>
    public HashSet<string> Committed { get; } = [];

    /// <summary>Keys this run wrote, in order.</summary>
    public List<string> Written { get; } = [];

    public Task<bool> ExistsAsync(string key, CancellationToken ct)
    {
        log.Add("marker-read");
        return Task.FromResult(Committed.Contains(key));
    }

    public Task MarkAsync(string key, CancellationToken ct)
    {
        log.Add("marker-write");
        Written.Add(key);
        return Task.CompletedTask;
    }
}
