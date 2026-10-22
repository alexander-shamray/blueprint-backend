using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Common.Infrastructure.Switching;

/// <summary>Stops a receive endpoint while its name is switched off, and starts it again when not (ADR-113).</summary>
/// <remarks>
/// Stopping is <c>ProviderKillSwitch</c>'s mechanism, and the inbox absorbs any redelivery (§9.5). The bus check reads
/// a stopped endpoint Degraded, and one off since configuration not at all, so operator-switches reports it (ADR-144).
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
            "Receive endpoint {Queue} started again; its name is no longer switched off.");

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

    private static readonly Action<ILogger, string, Exception?> Connected =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(7, nameof(Connected)),
            "Receive endpoint {Queue} connected; it was switched off when the bus was configured.");

    private readonly OperatorSwitches _switches;
    private readonly ILogger<ReceiveEndpointSwitch> _log;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _disposed = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, IReceiveEndpoint> _endpoints = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stopped = new(StringComparer.Ordinal);
    private readonly HashSet<string> _retrying = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unsettled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action<IReceiveEndpointConfigurator>> _switchable = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deferred = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unregistered = new(StringComparer.Ordinal);
    private readonly IDisposable? _subscription;
    private IServiceProvider? _services;

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

    /// <summary>The endpoints the bus configured without <c>SwitchedReceiveEndpoint</c>.</summary>
    public IReadOnlyCollection<string> Unregistered
    {
        get
        {
            lock (_endpoints)
                return [.. _unregistered.Order(StringComparer.Ordinal)];
        }
    }

    /// <summary>The container the bus was configured from, holding the connector a deferred endpoint needs.</summary>
    public void Attach(IServiceProvider services) => _services = services;

    /// <summary>
    /// Records how a switchable endpoint is configured and whether the bus should configure it now: false when its
    /// switch is off, so the bus never consumes from it until the switch connects it (ADR-144).
    /// </summary>
    public bool Register(string queue, Action<IReceiveEndpointConfigurator> configure)
    {
        _switches.DeclareEndpoint(queue);
        bool off = _switches.IsOff(queue);
        lock (_endpoints)
        {
            if (!_switchable.TryAdd(queue, configure))
                throw new InvalidOperationException($"Receive endpoint {queue} is registered with the switch twice.");

            if (off)
            {
                _deferred.Add(queue);
                _stopped.Add(queue);
            }
        }

        return !off;
    }

    /// <summary>Called as the bus configures each endpoint, so a host test sees them without a broker.</summary>
    public void Configured(IReceiveEndpointConfigurator configurator)
    {
        // The bus's own reply endpoint is temporary and named per instance; stopping it breaks the bus, not a duty.
        if (configurator.InputAddress.Query.Contains("temporary=true", StringComparison.Ordinal))
            return;

        // An endpoint the switch did not register starts before a switch that is off can stop it, so the host tests
        // read this list (ADR-144); it is still stopped from Ready, as every endpoint was before.
        string queue = QueueOf(configurator.InputAddress);
        lock (_endpoints)
        {
            if (!_switchable.ContainsKey(queue))
                _unregistered.Add(queue);
        }

        _switches.DeclareEndpoint(queue);
        configurator.ConnectReceiveEndpointObserver(this);
    }

    public Task Ready(ReceiveEndpointReady ready)
    {
        string queue = QueueOf(ready.InputAddress);
        lock (_endpoints)
            _endpoints[queue] = ready.ReceiveEndpoint;

        // Thrown after the bus was configured and before this endpoint was ready, so what the broker delivered before
        // the stop lands still runs; one off when the bus was configured never got here (ADR-144). Off the callback,
        // since the bus waits on it and a stop waits on the bus.
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
            queues = [.. _endpoints.Keys.Union(_deferred, StringComparer.Ordinal)];

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
            IReceiveEndpoint? endpoint;
            bool stopped;
            bool unsettled;
            Action<IReceiveEndpointConfigurator>? deferred;
            lock (_endpoints)
            {
                _endpoints.TryGetValue(queue, out endpoint);
                stopped = _stopped.Contains(queue);
                unsettled = _unsettled.Contains(queue);
                deferred = _deferred.Contains(queue) ? _switchable[queue] : null;
            }

            // Never configured, since its switch was off when the bus was: connected now it is on, and stopped and
            // started like any other endpoint from then on.
            if (deferred is not null)
            {
                if (!_switches.IsOff(queue))
                    await ConnectAsync(queue, deferred);

                return;
            }

            if (endpoint is null)
                return;

            // Unsettled is a start whose undoing stop failed, so the endpoint may be running whatever the set says;
            // it is stopped first either way, and stays unsettled until a stop succeeds.
            bool off = _switches.IsOff(queue);
            if (off && (!stopped || unsettled))
            {
                await endpoint.Stop();
                lock (_endpoints)
                {
                    _stopped.Add(queue);
                    _unsettled.Remove(queue);
                }

                Stopped(_log, queue, null);
            }
            else if (!off && stopped)
            {
                if (unsettled)
                {
                    await endpoint.Stop();
                    lock (_endpoints)
                        _unsettled.Remove(queue);
                }

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

    // Called under the gate. The endpoint is kept from its handle, as the bus's own are from Ready, so a later switch
    // stops and starts it like them; one that cannot be connected throws, and ApplyAsync retries it.
    private async Task ConnectAsync(string queue, Action<IReceiveEndpointConfigurator> configure)
    {
        IServiceProvider services = _services ??
            throw new InvalidOperationException("The switch was never attached to the bus's container.");

        HostReceiveEndpointHandle handle = services
            .GetRequiredService<IReceiveEndpointConnector>()
            .ConnectReceiveEndpoint(queue, (_, e) => configure(e));
        await handle.Ready;

        lock (_endpoints)
        {
            _endpoints[queue] = handle.ReceiveEndpoint;
            _deferred.Remove(queue);
            _stopped.Remove(queue);
        }

        Connected(_log, queue, null);
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
