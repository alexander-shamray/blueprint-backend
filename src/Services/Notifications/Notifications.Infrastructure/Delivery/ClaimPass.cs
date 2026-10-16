using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Notifications.Infrastructure.Delivery;

/// <summary>The claim pass both senders run: lease the due rows, then end each alone or back it off.</summary>
/// <remarks>
/// <see cref="SendWorker"/> and <see cref="PushSendWorker"/> supply the parked read, claim, row steps and fault lines;
/// the lease, the fan-out and the back-off catch are written here once, and the sender nests its half (internal claims).
/// </remarks>
internal abstract class ClaimPass<TWork, TClaims, TState>(IServiceScopeFactory scopes)
    where TClaims : notnull
{
    // A stop before the claim leases nothing; the claim itself runs on the drain's token, because a read cancelled
    // after its lease commits strands the rows until the lease lapses (§15.3).
    public async Task<SendPass> RunAsync(CancellationToken claim, CancellationToken rows)
    {
        await using AsyncServiceScope claimScope = scopes.CreateAsyncScope();

        TState state = Begin(claimScope.ServiceProvider);
        TClaims claims = claimScope.ServiceProvider.GetRequiredService<TClaims>();
        claim.ThrowIfCancellationRequested();
        IReadOnlyList<TWork> claimed = await ClaimAsync(claims, state, rows);

        // Every row at once, so a pass lasts one row's calls and the lease bounds it; WhenAll, so one row's fault
        // leaves the others to finish.
        bool[] finished = await Task.WhenAll(claimed.Select(work => SendOrBackOffAsync(claims, state, work, rows)));

        return new SendPass(claimed.Count, finished.Count(f => f));
    }

    /// <summary>Reads what parks rows, logging it, before the claim; the claim and every row are handed the result.</summary>
    protected abstract TState Begin(IServiceProvider claimScope);

    protected abstract Task<IReadOnlyList<TWork>> ClaimAsync(TClaims claims, TState state, CancellationToken rows);

    /// <summary>The row's span, a child of the intake that wrote it so the calls join the order's trace (§9.4).</summary>
    protected abstract Activity? StartSpan(TWork work);

    /// <summary>One leased row, step by step, in a scope of its own; true when it reached an outcome.</summary>
    protected abstract Task<bool> SendAsync(
        IServiceProvider row,
        TClaims claims,
        TState state,
        TWork work,
        CancellationToken ct);

    /// <summary>Backs the row off if it is still under this pass's lease; true when it was.</summary>
    protected abstract Task<bool> BackOffAsync(TClaims claims, TWork work, CancellationToken ct);

    /// <summary>The line for a row that threw; the type picks it, never whether the row backs off.</summary>
    protected abstract void LogFault(TWork work, Exception exception);

    protected abstract void LogOutgrown(TWork work);

    protected abstract void LogBackOffFailed(TWork work, Exception exception);

    private async Task<bool> SendOrBackOffAsync(TClaims claims, TState state, TWork work, CancellationToken ct)
    {
        using Activity? span = StartSpan(work);

        try
        {
            // A scope per row, so a row that throws mid-write hands the next none of its tracked state.
            await using AsyncServiceScope row = scopes.CreateAsyncScope();

            return await SendAsync(row.ServiceProvider, claims, state, work, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            span?.SetStatus(ActivityStatusCode.Error);

            // Logged before the backoff is written, so a database fault there cannot hide this one.
            LogFault(work, ex);

            try
            {
                // False when another pass took the row once this one's lease lapsed: the line above is then not true.
                if (!await BackOffAsync(claims, work, ct))
                    LogOutgrown(work);
            }
            catch (Exception backOff) when (!ct.IsCancellationRequested)
            {
                // The claim held, so the loop's line would be false; the lease lapses and a later pass reclaims it.
                LogBackOffFailed(work, backOff);
            }

            return false;
        }
    }
}
