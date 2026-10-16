using Microsoft.Extensions.Hosting;

namespace Common.Infrastructure.Switching;

/// <summary>The timer loop every <see cref="ISwitchedWorker"/> shares: tick, skip while off, run a pass (ADR-113).</summary>
/// <remarks>
/// A worker supplies its interval, its pass and what a failed pass logs; it does not write the loop, so a change to
/// what a switched-off tick or a failed pass does is made here once.
/// </remarks>
public abstract class SwitchedPollingWorker(OperatorSwitches? switches) : BackgroundService, ISwitchedWorker
{
    /// <inheritdoc />
    public abstract string SwitchName { get; }

    /// <summary>How often a pass is attempted.</summary>
    protected abstract TimeSpan PassInterval { get; }

    /// <summary>Whether a pass runs before the first tick, so a host restarted often still passes.</summary>
    protected virtual bool PassAtStart => false;

    /// <summary>How long a pass under way at a stop runs before <see cref="Drain"/> fires; null cancels it at once.</summary>
    protected virtual TimeSpan? StopDrain => null;

    /// <summary>The token a pass gives its rows: the stop's own, or one that fires <see cref="StopDrain"/> after it.</summary>
    protected CancellationToken Drain { get; private set; }

    /// <summary>One pass. A stop ends the loop and starts no pass, so a pass under way is the only one it meets.</summary>
    protected abstract Task RunPassAsync(CancellationToken stoppingToken);

    /// <summary>Logs a pass that threw while the host was running; the loop swallows it and ticks again.</summary>
    protected abstract void OnPassFailed(Exception exception);

    // stoppingToken, not ct: CA1725 keeps the base's name, an error under ADR-019.
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(PassInterval);
        using CancellationTokenSource drain = new();
        using CancellationTokenRegistration stopping = stoppingToken.Register(() =>
        {
            if (StopDrain is TimeSpan budget)
                drain.CancelAfter(budget);
        });

        Drain = StopDrain is null ? stoppingToken : drain.Token;

        bool first = PassAtStart;

        while (first || await TickAsync(timer, stoppingToken))
        {
            first = false;

            if (switches?.IsOff(SwitchName) == true)
                continue;

            try
            {
                await RunPassAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // The token, not the type: a call's own deadline throws the same type, and an escape stops the host.
                OnPassFailed(ex);
            }
        }
    }

    private static async Task<bool> TickAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return !stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
