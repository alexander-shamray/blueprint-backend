using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Common.Infrastructure.Switching;

/// <summary>Stops a receive endpoint while its name is switched off and restarts it when it is not (ADR-113).</summary>
/// <remarks>
/// Stopping is the mechanism <c>ProviderKillSwitch</c> uses: the queue keeps its messages and the inbox absorbs any
/// redelivery (§9.5). A stopped endpoint leaves the bus Degraded, which readiness answers 200 (ADR-113).
/// </remarks>
public sealed class ReceiveEndpointSwitch : IReceiveEndpointObserver, IDisposable
{
    private static readonly Action<ILogger, string, Exception?> Stopped =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1, nameof(Stopped)),
            "Receive endpoint {Queue} stopped by an operator switch; its messages wait in the queue.");

    private static readonly Action<ILogger, string, Exception?> Started =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(2, nameof(Started)),
            "Receive endpoint {Queue} started again; its operator switch is off.");

    private static readonly Action<ILogger, string, TimeSpan, Exception?> ApplyFailed =
        LoggerMessage.Define<string, TimeSpan>(
            LogLevel.Error,
            new EventId(3, nameof(ApplyFailed)),
            "The operator switch could not change receive endpoint {Queue}; it retries in {Delay}.");

    private static readonly Action<ILogger, string, Exception?> Undeclared =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(4, nameof(Undeclared)),
            "The operator switch names {Name}, which no receive endpoint or worker declares; nothing is switched.");

    private static readonly Action<ILogger, string, Exception?> Changed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(5, nameof(Changed)),
            "The operator switches now off: [{Names}]. A loop named here skips its pass on each tick.");

    private static readonly Action<ILogger, Exception?> ReportFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(6, nameof(ReportFailed)),
            "The operator switch could not log the names now off.");

    private readonly OperatorSwitches _switches;
    private readonly ILogger<ReceiveEndpointSwitch> _log;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _disposed = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, IReceiveEndpoint> _endpoints = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stopped = new(StringComparer.Ordinal);
    private readonly HashSet<string> _retrying = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unsettled = new(StringComparer.Ordinal);
    private readonly IDisposable? _subscription;

    /// <summary>The wait before a failed stop or start is tried again, doubling up to
    /// <see cref="LastRetry"/>.</summary>
    public static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan LastRetry = TimeSpan.FromMinutes(1);

    // The logger is optional, since a suite builds a bus without logging and the switch is not the reason to add it.
    public ReceiveEndpointSwitch(
        OperatorSwitches switches,
        ILogger<ReceiveEndpointSwitch>? log = null,
        TimeProvider? clock = null)
    {
        _switches = switches;
        _log = log ?? NullLogger<ReceiveEndpointSwitch>.Instance;
        _clock = clock ?? TimeProvider.System;
        _subscription = switches.OnChange(() => _ = ApplyAllAsync());
    }

    public bool IsStopped(string queue)
    {
        lock (_endpoints)
            return _stopped.Contains(queue);
    }

    /// <summary>Called as the bus configures each endpoint, so a host test sees them without a broker.</summary>
    public void Configured(IReceiveEndpointConfigurator configurator)
    {
        // The bus's own reply endpoint is temporary and named per instance; stopping it breaks the bus, not a duty.
        if (configurator.InputAddress.Query.Contains("temporary=true", StringComparison.Ordinal))
            return;

        _switches.DeclareEndpoint(QueueOf(configurator.InputAddress));
        configurator.ConnectReceiveEndpointObserver(this);
    }

    public Task Ready(ReceiveEndpointReady ready)
    {
        string queue = QueueOf(ready.InputAddress);
        lock (_endpoints)
            _endpoints[queue] = ready.ReceiveEndpoint;

        // Off at start, so a pod that restarts while the switch is thrown stops once its endpoint is ready; what the
        // broker delivered before the stop lands still runs. Off the callback, since the bus waits on it and a stop
        // waits on the bus.
        if (_switches.IsOff(queue))
            _ = Task.Run(() => ApplyAsync(queue));

        return Task.CompletedTask;
    }

    public Task Stopping(ReceiveEndpointStopping stopping) => Task.CompletedTask;

    public Task Completed(ReceiveEndpointCompleted completed) => Task.CompletedTask;

    public Task Faulted(ReceiveEndpointFaulted faulted) => Task.CompletedTask;

    public void Dispose()
    {
        // The gate is left undisposed: it holds no wait handle, and a callback still in flight at shutdown would
        // otherwise fault on a released semaphore. The token source likewise, since a retry may still be waiting on it.
        _subscription?.Dispose();
        _disposed.Cancel();
    }

    /// <summary>Brings every known endpoint to the state the configuration now asks for.</summary>
    public async Task ApplyAllAsync()
    {
        string[] queues;
        lock (_endpoints)
            queues = [.. _endpoints.Keys];

        foreach (string queue in queues)
            await ApplyAsync(queue);

        // Fire-and-forget from the change callback, so nothing here may fault unobserved.
        try
        {
            Changed(_log, string.Join(", ", _switches.Off), null);

            foreach (string name in _switches.Undeclared)
                Undeclared(_log, name, null);
        }
        catch (Exception ex)
        {
            ReportFailed(_log, ex);
        }
    }

    private async Task ApplyAsync(string queue, int attempt = 0)
    {
        TimeSpan? retry = null;

        await _gate.WaitAsync();
        try
        {
            IReceiveEndpoint endpoint;
            bool stopped;
            lock (_endpoints)
            {
                endpoint = _endpoints[queue];
                stopped = _stopped.Contains(queue);
            }

            bool off = _switches.IsOff(queue);
            if (off && !stopped)
            {
                await endpoint.Stop();
                lock (_endpoints)
                    _stopped.Add(queue);
                Stopped(_log, queue, null);
            }
            else if (!off && stopped)
            {
                // A start whose stop also failed may have left the endpoint running, so it is stopped first.
                bool unsettled;
                lock (_endpoints)
                    unsettled = _unsettled.Remove(queue);
                if (unsettled)
                    await endpoint.Stop();

                ReceiveEndpointHandle handle = endpoint.Start();
                try
                {
                    await handle.Ready;
                }
                catch (Exception readyFault)
                {
                    // Stopped again, so the retry starts a stopped endpoint and not one that is already started.
                    try
                    {
                        await endpoint.Stop();
                    }
                    catch (Exception stopFault)
                    {
                        lock (_endpoints)
                            _unsettled.Add(queue);
                        throw new AggregateException(readyFault, stopFault);
                    }

                    throw;
                }

                lock (_endpoints)
                    _stopped.Remove(queue);
                Started(_log, queue, null);
            }
        }
        catch (Exception ex)
        {
            // Retried on its own, since nothing else may come: a stop that failed leaves the queue consuming.
            retry = TimeSpan.FromTicks(Math.Min(FirstRetry.Ticks << Math.Min(attempt, 16), LastRetry.Ticks));
            ApplyFailed(_log, queue, retry.Value, ex);
        }
        finally
        {
            _gate.Release();
        }

        if (retry is not TimeSpan delay)
            return;

        // One pending retry per queue: a reload during a long broker fault would otherwise add a chain each time.
        lock (_endpoints)
        {
            if (!_retrying.Add(queue))
                return;
        }

        _ = RetryAsync(queue, delay, attempt + 1);
    }

    private async Task RetryAsync(string queue, TimeSpan delay, int attempt)
    {
        try
        {
            await Task.Delay(delay, _clock, _disposed.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_endpoints)
            _retrying.Remove(queue);

        // Asks the switch again, so a retry after the operator changed their mind converges on the new state.
        await ApplyAsync(queue, attempt);
    }

    private static string QueueOf(Uri address) =>
        address.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];
}
