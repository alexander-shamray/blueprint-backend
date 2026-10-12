using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace Platform.IntegrationTests.Journey;

/// <summary>Every request Payments' client puts on the wire to the provider, held in memory (#824).</summary>
/// <remarks>
/// A line written to disk per call changes the timing the flake depends on, so a send is one append to a
/// collection and the failure message reads it back. It sits inside the resilience pipeline, so a retry is a
/// second entry, with the thread-pool state the send was made under.
/// </remarks>
internal sealed class ProviderSendLog
{
    private readonly ConcurrentQueue<Send> _sends = new();

    internal int Count => _sends.Count;

    internal void Record(HttpRequestMessage request)
    {
        _sends.Enqueue(new Send(
            DateTime.UtcNow,
            Stopwatch.GetTimestamp(),
            Header(request, "Idempotency-Key"),
            Header(request, "traceparent"),
            Environment.CurrentManagedThreadId,
            ThreadPool.PendingWorkItemCount,
            ThreadPool.ThreadCount));
    }

    /// <summary>Each send's wall time and its gap to the one before, in microseconds.</summary>
    internal string Describe()
    {
        Send[] sends = [.. _sends];
        List<string> lines = [];

        for (int i = 0; i < sends.Length; i++)
        {
            Send s = sends[i];
            string gap = i == 0
                ? "first"
                : "+" + (Stopwatch.GetElapsedTime(sends[i - 1].Ticks, s.Ticks).TotalMicroseconds)
                    .ToString("F0", CultureInfo.InvariantCulture) + "us";

            lines.Add($"{s.At:O} {gap} key {s.Key} traceparent {s.Traceparent} thread {s.Thread} " +
                      $"pool pending {s.Pending} threads {s.Threads}");
        }

        return $"{sends.Length} send(s) by the client [{string.Join("; ", lines)}]";
    }

    private static string Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join(",", values) : "-";

    private sealed record Send(
        DateTime At,
        long Ticks,
        string Key,
        string Traceparent,
        int Thread,
        long Pending,
        int Threads);
}

/// <summary>Innermost on the provider client, after the pipeline's retry, so it sees each attempt once.</summary>
internal sealed class ProviderSendRecorder(ProviderSendLog log) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        log.Record(request);
        return base.SendAsync(request, ct);
    }
}
