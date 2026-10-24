using System.Runtime.CompilerServices;
using Common.Assistant.Protocol;

namespace Common.Assistant;

/// <summary>
/// A conversation's events as a bounded ring of numbered entries (AI assistant design §5.1), as a job's output is: a
/// stream resumes after the last id it saw. One that fell a ring behind resumes from the oldest event still held, and
/// the conversation read, which holds every message, is how a client settles what it missed.
/// </summary>
internal sealed class EventLog(int capacity)
{
    private readonly Lock gate = new();
    private readonly (long Id, AssistantEvent Event)[] ring = new (long, AssistantEvent)[capacity];
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Ids start at 1, so 0 means nothing seen.
    private long next = 1;
    private bool closed;

    public long LastId
    {
        get
        {
            lock (gate)
            {
                return next - 1;
            }
        }
    }

    public long Append(AssistantEvent value)
    {
        TaskCompletionSource woken;
        long id;

        lock (gate)
        {
            id = next++;
            ring[id % ring.Length] = (id, value);
            woken = changed;
            changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        woken.TrySetResult();

        return id;
    }

    /// <summary>Ends every follower; nothing more is appended.</summary>
    public void Close()
    {
        TaskCompletionSource woken;

        lock (gate)
        {
            closed = true;
            woken = changed;
        }

        woken.TrySetResult();
    }

    /// <summary>
    /// What is held after <paramref name="afterId"/>, then each new event, until the log closes or the caller cancels.
    /// </summary>
    public async IAsyncEnumerable<(long Id, AssistantEvent Event)> Follow(
        long afterId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long last = afterId;

        while (true)
        {
            List<(long Id, AssistantEvent Event)> batch;
            Task wake;
            bool ended;

            lock (gate)
            {
                batch = SinceUnlocked(last);
                wake = changed.Task;
                ended = closed;
            }

            foreach ((long Id, AssistantEvent Event) entry in batch)
            {
                last = entry.Id;

                yield return entry;
            }

            if (ended)
            {
                yield break;
            }

            if (batch.Count == 0)
            {
                await wake.WaitAsync(cancellationToken);
            }
        }
    }

    private List<(long Id, AssistantEvent Event)> SinceUnlocked(long afterId)
    {
        List<(long Id, AssistantEvent Event)> entries = [];

        if (afterId >= next - 1)
        {
            return entries;
        }

        long first = Math.Max(Math.Max(afterId + 1, next - ring.Length), 1);

        for (long id = first; id < next; id++)
        {
            entries.Add(ring[id % ring.Length]);
        }

        return entries;
    }
}
