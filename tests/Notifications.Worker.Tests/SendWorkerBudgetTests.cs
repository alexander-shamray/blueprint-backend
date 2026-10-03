using Microsoft.Extensions.Hosting;
using Notifications.Infrastructure.Contacts;
using Notifications.Infrastructure.Delivery;
using Notifications.Infrastructure.Mail;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The send pass's arithmetic: the lease and the drain against the two hops' totals (§9.7, §15.3).</summary>
public sealed class SendWorkerBudgetTests
{
    /// <summary>A row's two calls, made in turn; a pass makes every row's at once, so this bounds a pass too.</summary>
    private static readonly TimeSpan OneRow = MailHop.TotalTimeout + ContactHop.TotalRequestTimeout;

    [Fact]
    public void The_lease_outlives_both_calls_a_row_makes()
    {
        TimeSpan.FromSeconds(SendWorker.LeaseSeconds).ShouldBeGreaterThan(
            OneRow, "a lease shorter than a row's calls lets a second replica claim a row still being sent");
    }

    [Fact]
    public void The_drain_budget_and_its_last_commit_fit_the_host_s_drain()
    {
        // The default the solution never overrides, measured rather than written down (§15.3).
        (SendWorker.DrainBudget + SendWorker.CommitRoom).ShouldBeLessThanOrEqualTo(
            new HostOptions().ShutdownTimeout, "the host abandons a pass still committing when its drain runs out");
    }

    [Fact]
    public void A_pass_fits_the_drain_budget()
    {
        OneRow.ShouldBeLessThanOrEqualTo(
            SendWorker.DrainBudget, "a stop cancels a pass whose calls outrun the drain budget, mid-send");
    }

    [Fact]
    public void A_pass_has_rows_to_run_at_once()
    {
        SendWorker.ClaimBatchSize.ShouldBeGreaterThan(1, "one row a tick is twelve notices a minute a replica");
    }
}
