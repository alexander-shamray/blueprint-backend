using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace Platform.IntegrationTests.Journey;

/// <summary>Every request Payments' client puts on the wire to the provider, held in memory.</summary>
/// <remarks>
/// A disk write per call changes the timing a double send depends on, so a send is one queue append (§9.7).
/// Inside the retry, a resend is a second entry beside the thread-pool state it was made under.
/// </remarks>
internal sealed class ProviderSendLog : IDisposable
{
    private const int StallFloorMs = 100;
    private const int MaxStalls = 200;

    private readonly ConcurrentQueue<Event> _events = new();
    private readonly ConcurrentQueue<Stall> _stalls = new();
    private readonly CancellationTokenSource _stop = new();

    public ProviderSendLog() =>
        new Thread(Probe) { IsBackground = true, Name = "provider-send-log-probe" }.Start();

    internal int Count => _events.Count(e => e.Kind == "send");

    internal void Sent(HttpRequestMessage request) => Add("send", request, "");

    internal void Answered(HttpRequestMessage request, string outcome) => Add("done", request, outcome);

    /// <summary>Each send's wall time and gap to the one before, each answer, then the last stalls the probe
    /// saw.</summary>
    internal string Describe()
    {
        Event[] events = [.. _events];
        List<string> lines = [];
        long? lastSend = null;

        foreach (Event e in events)
        {
            string gap = e.Kind == "send" && lastSend is { } before
                ? " +" + Stopwatch.GetElapsedTime(before, e.Ticks).TotalMicroseconds
                    .ToString("F0", CultureInfo.InvariantCulture) + "us"
                : "";
            if (e.Kind == "send")
                lastSend = e.Ticks;

            lines.Add($"{e.At:O} {e.Kind}{gap} {e.Detail} key {e.Key} traceparent {e.Traceparent} " +
                      $"thread {e.Thread} pool pending {e.Pending} threads {e.Threads}");
        }

        Stall[] stalls = [.. _stalls];
        string late = stalls.Length == 0
            ? "none over " + StallFloorMs + " ms"
            : string.Join("; ", stalls.Select(s =>
                $"{s.At:O} late {s.LateMs} ms, pool pending {s.Pending}, gen2 collections {s.Gen2}"));

        return $"{events.Count(e => e.Kind == "send")} send(s) by the client [{string.Join("; ", lines)}]; " +
               $"probe stalls [{late}]";
    }

    public void Dispose() => _stop.Cancel();

    private void Add(string kind, HttpRequestMessage request, string detail) =>
        _events.Enqueue(new Event(
            kind,
            DateTime.UtcNow,
            Stopwatch.GetTimestamp(),
            Header(request, "Idempotency-Key"),
            Header(request, "traceparent"),
            Environment.CurrentManagedThreadId,
            ThreadPool.PendingWorkItemCount,
            ThreadPool.ThreadCount,
            detail));

    /// <summary>A thread of its own, so a starved pool cannot hold it back; it notes every tick that came
    /// late.</summary>
    private void Probe()
    {
        const int periodMs = 20;
        long last = Stopwatch.GetTimestamp();

        while (!_stop.IsCancellationRequested)
        {
            Thread.Sleep(periodMs);
            long now = Stopwatch.GetTimestamp();
            long late = (long)Stopwatch.GetElapsedTime(last, now).TotalMilliseconds - periodMs;
            last = now;

            if (late < StallFloorMs)
                continue;

            _stalls.Enqueue(new Stall(DateTime.UtcNow, late, ThreadPool.PendingWorkItemCount, GC.CollectionCount(2)));
            while (_stalls.Count > MaxStalls)
                _stalls.TryDequeue(out _);
        }
    }

    private static string Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join(",", values) : "-";

    private sealed record Event(
        string Kind,
        DateTime At,
        long Ticks,
        string Key,
        string Traceparent,
        int Thread,
        long Pending,
        int Threads,
        string Detail);

    private sealed record Stall(DateTime At, long LateMs, long Pending, int Gen2);
}

/// <summary>Innermost on the provider client, after the pipeline's retry, so it sees each attempt once.</summary>
internal sealed class ProviderSendRecorder(ProviderSendLog log) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        log.Sent(request);
        long started = Stopwatch.GetTimestamp();

        try
        {
            HttpResponseMessage response = await base.SendAsync(request, ct);
            log.Answered(request, $"{(int)response.StatusCode} in {Elapsed(started)} ms");
            return response;
        }
        catch (Exception e)
        {
            log.Answered(request, $"{e.GetType().Name} after {Elapsed(started)} ms");
            throw;
        }
    }

    private static long Elapsed(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
