using Common.Infrastructure.Outbox;
using Microsoft.Extensions.Hosting;

namespace Platform.IntegrationTests.Journey;

/// <summary>A hold on one service's outbox, which parks a saga in the state a scenario needs (§12.1).</summary>
/// <remarks>
/// A held outbox stages and commits as it always does and publishes nothing, which is §9.6's "an outbox stuck" as an
/// operator meets it. The hold stops a pass starting and waits for one already running, so a row claimed before the
/// hold is published and every row staged after it is not.
/// </remarks>
public sealed class OutboxGate : IDisposable
{
    private readonly SemaphoreSlim _pass = new(1, 1);

    private volatile bool _held;

    public bool Held => _held;

    public void Dispose() => _pass.Dispose();

    /// <summary>Holds the outbox until the returned scope is disposed.</summary>
    public async Task<IDisposable> HoldAsync(CancellationToken ct)
    {
        _held = true;

        // A pass in flight finishes first; the next sees the hold before it claims.
        await _pass.WaitAsync(ct);
        _pass.Release();

        return new Release(this);
    }

    /// <summary>Runs one pass unless the outbox is held, under the semaphore the hold waits on.</summary>
    internal async Task PassAsync(Func<Task> pass, CancellationToken ct)
    {
        await _pass.WaitAsync(ct);
        try
        {
            if (!_held)
                await pass();
        }
        finally
        {
            _pass.Release();
        }
    }

    private sealed class Release(OutboxGate gate) : IDisposable
    {
        public void Dispose() => gate._held = false;
    }
}

/// <summary>
/// The shipped dispatcher's own loop, behind <see cref="OutboxGate"/>: a <see cref="PeriodicTimer"/> at the shipped
/// interval and one <see cref="OutboxDispatcher.ProcessBatchAsync"/> per tick.
/// </summary>
internal sealed class GatedOutboxRunner(OutboxDispatcher dispatcher, OutboxGate gate) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(OutboxDispatcher.PollInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await gate.PassAsync(async () => await dispatcher.ProcessBatchAsync(stoppingToken), stoppingToken);
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                // As the shipped loop: the next tick claims again.
            }
        }
    }
}
