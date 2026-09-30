using Ordering.TestSupport;
using Ordering.TestSupport.Outbox;
using Common.Application;
using Common.Contracts;
using Common.Domain;
using Common.Infrastructure.Outbox;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>§9.4's dispatcher, a pass at a time, since <see cref="OrderingApiFactory"/> removes its timer.</summary>
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

        // Clears the backoff lease, so the second pass is blocked by the attempt cap alone.
        await fixture.ExpireOutboxLeasesAsync();

        (await fixture.ProcessOutboxBatchAsync()).ShouldBe(0);

        OutboxMessage row = (await fixture.OutboxAsync()).ShouldHaveSingleItem();
        row.Attempts.ShouldBe(10);                // not 11 — never re-claimed
        row.ProcessedAt.ShouldBeNull();           // visible to the §13.6 alert
    }

    [Fact]
    public async Task A_domain_event_on_the_broker_lane_is_never_published()
    {
        // §5.5's rule at the last place able to enforce it; Stage refuses this pairing, so the row is repointed.
        OutboxMessage row = OutboxRows.Healthy(fixture);
        await fixture.StageOutboxAsync(row);
        await fixture.SetOutboxLaneAsync(row.MessageId, OutboxLane.Broker);

        await fixture.ProcessOutboxBatchAsync();

        OutboxMessage failed = (await fixture.OutboxAsync()).ShouldHaveSingleItem();
        failed.ProcessedAt.ShouldBeNull();
        failed.LastError.ShouldNotBeNull().ShouldContain(nameof(IIntegrationEvent));
    }

    [Fact]
    public async Task An_integration_event_on_the_local_lane_never_reaches_a_projection()
    {
        // ProjectionInvoker is unconstrained, so without the guard the row would complete with no trace.
        OutboxMessage row = OutboxRows.Broker(fixture, Guid.CreateVersion7());
        await fixture.StageOutboxAsync(row);
        await fixture.SetOutboxLaneAsync(row.MessageId, OutboxLane.Local);

        await fixture.ProcessOutboxBatchAsync();

        OutboxMessage failed = (await fixture.OutboxAsync()).ShouldHaveSingleItem();
        failed.ProcessedAt.ShouldBeNull();
        failed.LastError.ShouldNotBeNull().ShouldContain(nameof(IDomainEvent));
    }

    [Fact]
    public async Task A_local_row_with_no_registered_handler_fails_loudly()
    {
        // A projection that never runs would otherwise leave every dashboard green.
        await fixture.StageOutboxAsync(OutboxRows.Unhandled(fixture));

        await fixture.ProcessOutboxBatchAsync();

        OutboxMessage row = (await fixture.OutboxAsync()).ShouldHaveSingleItem();
        row.ProcessedAt.ShouldBeNull();           // not silently completed
        row.LastError.ShouldNotBeNull().ShouldContain("IProjectionHandler");
    }

    [Fact]
    public async Task A_row_still_being_delivered_is_not_claimed_by_a_second_pass()
    {
        // The lease, observed while held, which takes two overlapping passes: UPDLOCK, READPAST and LockedUntil
        // are what stop two replicas delivering one row.
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

    [Fact]
    public async Task A_broker_row_is_published_and_completed()
    {
        // The Broker half of DeliverAsync against the real broker; the row completing, not the wire (§12.4).
        await fixture.StageOutboxAsync(OutboxRows.Broker(fixture, Guid.CreateVersion7()));

        (await fixture.ProcessOutboxBatchAsync()).ShouldBe(1);

        OutboxMessage row = (await fixture.OutboxAsync()).ShouldHaveSingleItem();
        row.Lane.ShouldBe(OutboxLane.Broker);
        row.ProcessedAt.ShouldNotBeNull();
        row.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task A_processed_row_is_never_claimed_again()
    {
        await fixture.StageOutboxAsync(OutboxRows.Healthy(fixture));

        (await fixture.ProcessOutboxBatchAsync()).ShouldBe(1);

        // A processed row is never delivered again, since nothing else would ever stop it.
        (await fixture.ProcessOutboxBatchAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_payload_longer_than_the_string_convention_survives_the_column()
    {
        // §7.2 caps every string at 400 characters; this asserts the Payload column rather than the setting.
        string note = new('a', 1_000);

        await fixture.StageOutboxAsync(OutboxRows.Verbose(fixture, note));

        OutboxMessage row = (await fixture.OutboxAsync()).ShouldHaveSingleItem();
        row.Payload.ShouldContain(note, Case.Sensitive);
    }
}
