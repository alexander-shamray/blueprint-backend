using Payments.TestSupport;
using Payments.TestSupport.Outbox;
using Common.Application;
using Common.Contracts;
using Common.Infrastructure.Outbox;
using Shouldly;
using Xunit;

namespace Payments.Api.Tests;

/// <summary>
/// §9.4's dispatcher, driven explicitly rather than by waiting on a timer.
/// These cover the behaviour §13.6 alerts on — per-row isolation and attempt
/// accounting — and neither is observable from a test that lets the background
/// service run, which is why <see cref="PaymentsApiFactory"/> removes it.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class OutboxDispatcherTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_failing_row_does_not_block_healthy_rows()
    {
        await fixture.StageOutboxAsync(
            OutboxRows.Poison(fixture),          // its handler always throws
            OutboxRows.Healthy(fixture),
            OutboxRows.Healthy(fixture));

        (await fixture.ProcessOutboxBatchAsync()).ShouldBe(2);

        IReadOnlyList<OutboxMessage> rows = await fixture.OutboxAsync();

        rows.Count(r => r.ProcessedAt is not null).ShouldBe(2);

        OutboxMessage poison = rows.Single(r => r.ProcessedAt is null);
        poison.Attempts.ShouldBe(1);
        poison.LastError.ShouldNotBeNullOrEmpty();
        poison.LockedUntil.ShouldNotBeNull();     // backed off, not abandoned
    }

    [Fact]
    public async Task A_row_stops_being_claimed_at_the_attempt_cap()
    {
        OutboxMessage poison = OutboxRows.Poison(fixture);
        await fixture.StageOutboxAsync(poison);
        await fixture.SetOutboxAttemptsAsync(poison.MessageId, 9);

        (await fixture.ProcessOutboxBatchAsync()).ShouldBe(0);   // 9 → 10

        // Clear the backoff lease, so the second pass is blocked by the
        // attempt cap and nothing else. Without this the test would pass even
        // if the cap were removed entirely.
        await fixture.ExpireOutboxLeasesAsync();

        (await fixture.ProcessOutboxBatchAsync()).ShouldBe(0);

        OutboxMessage row = (await fixture.OutboxAsync()).ShouldHaveSingleItem();
        row.Attempts.ShouldBe(10);                // not 11 — never re-claimed
        row.ProcessedAt.ShouldBeNull();           // visible to the §13.6 alert
    }

    [Fact]
    public async Task A_domain_event_on_the_broker_lane_is_never_published()
    {
        // §5.5's rule, enforced at the last place able to enforce it. Stage
        // refuses this pairing, so the row is built the only way the failure
        // can actually occur: written correctly, then repointed — a rename
        // that aliased an old Broker name onto a domain event, or a row
        // edited during an incident.
        OutboxMessage row = OutboxRows.Healthy(fixture);
        await fixture.StageOutboxAsync(row);
        await fixture.SetOutboxLaneAsync(row.MessageId, OutboxLane.Broker);

        await fixture.ProcessOutboxBatchAsync();

        OutboxMessage failed = (await fixture.OutboxAsync()).ShouldHaveSingleItem();
        failed.ProcessedAt.ShouldBeNull();
        failed.LastError.ShouldNotBeNull().ShouldContain(nameof(IIntegrationEvent));
    }

    [Fact]
    public async Task A_local_row_with_no_registered_handler_fails_loudly()
    {
        // The one worth keeping forever. It asserts the failure mode that
        // would otherwise be invisible: a projection that never runs while
        // every dashboard stays green.
        await fixture.StageOutboxAsync(OutboxRows.Unhandled(fixture));

        await fixture.ProcessOutboxBatchAsync();

        OutboxMessage row = (await fixture.OutboxAsync()).ShouldHaveSingleItem();
        row.ProcessedAt.ShouldBeNull();           // NOT silently completed
        row.LastError.ShouldNotBeNull().ShouldContain("IProjectionHandler");
    }

    [Fact]
    public async Task A_row_still_being_delivered_is_not_claimed_by_a_second_pass()
    {
        // The lease, observed while it is held — which takes two overlapping
        // passes and cannot be done with sequential ones. An earlier version
        // of this test staged a poison row and ran two passes back to back,
        // and proved nothing about the lease at all: the first pass fails the
        // row, `_failSql` immediately replaces the 60-second lease with the
        // 5-second retry backoff, and the second pass is then blocked by the
        // backoff. It would have passed with the lease removed entirely.
        //
        // So: a handler that blocks, a first pass left in flight, and a second
        // pass run while the first still holds the claim. This is what
        // UPDLOCK, READPAST and LockedUntil exist for — without them two
        // replicas deliver the same row at the same time.
        DeliveryGate.Close();
        try
        {
            await fixture.StageOutboxAsync(OutboxRows.Blocking(fixture));

            Task<int> inFlight = fixture.ProcessOutboxBatchAsync();
            await DeliveryGate.Entered.Task.WaitAsync(
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);

            // The row is claimed and its delivery has not finished.
            (await fixture.ProcessOutboxBatchAsync()).ShouldBe(
                0,
                "a leased row must be invisible to a concurrent pass");

            DeliveryGate.Open();
            (await inFlight).ShouldBe(1);
        }
        finally
        {
            // Opened whatever happened, so a failure here cannot hang the
            // rest of the collection on a gate nobody closes.
            DeliveryGate.Open();
        }

        (await fixture.OutboxAsync()).ShouldHaveSingleItem().ProcessedAt.ShouldNotBeNull();
    }

    // Three tests return with this service's first contract, beside the
    // OutboxRows.Broker builder they all need — this one, the Local
    // lane's guard below, and OutboxTransportIdentityTests, which pins
    // §9.1's single identity onto the transport. Until then the
    // allow-list is empty and nothing can build a contract instance, so
    // each would assert against a row no code here can produce.

    [Fact]
    public async Task A_processed_row_is_never_claimed_again()
    {
        await fixture.StageOutboxAsync(OutboxRows.Healthy(fixture));

        (await fixture.ProcessOutboxBatchAsync()).ShouldBe(1);

        // At-least-once is the outbox's promise, but re-delivering a row the
        // dispatcher has already marked processed would be at-least-once
        // forever: nothing else in the design ever stops it.
        (await fixture.ProcessOutboxBatchAsync()).ShouldBe(0);
    }
}
