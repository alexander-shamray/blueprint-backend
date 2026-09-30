using Payments.TestSupport;
using Payments.TestSupport.Outbox;
using Common.Application;
using Common.Infrastructure.Idempotency;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using Common.Infrastructure.Outbox;
using Shouldly;
using Xunit;

namespace Payments.Api.Tests;

/// <summary>§9.4's, §9.5's and §8.5's retention purges, driven a pass at a time against the real tables.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class RetentionPurgeTests(ServiceFixture fixture) : IAsyncLifetime
{
    // Far from the window on either side, so each test is about the predicate, not a boundary.
    private static DateTimeOffset LongAgo => DateTimeOffset.UtcNow.AddDays(-30);
    private static DateTimeOffset Recently => DateTimeOffset.UtcNow.AddDays(-1);

    /// <summary>A key in §8.5's shape, distinct per call, because the column is the primary key.</summary>
    private static string Key() =>
        $"{Guid.CreateVersion7()}:tests.purge:{Guid.CreateVersion7()}";

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_processed_outbox_row_past_the_window_is_deleted()
    {
        OutboxMessage row = OutboxRows.Healthy(fixture);
        await fixture.StageOutboxAsync(row);
        await fixture.SetOutboxProcessedAtAsync(row.MessageId, LongAgo);

        (await fixture.PurgeRetentionAsync()).Outbox.ShouldBe(1);

        (await fixture.OutboxAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_abandoned_outbox_row_survives_however_old_it_is()
    {
        // ProcessedAt IS NOT NULL is load-bearing (§9.4): an abandoned row is what §13.6's alert surfaces.
        OutboxMessage poison = OutboxRows.Poison(fixture);
        await fixture.StageOutboxAsync(poison);
        await fixture.SetOutboxAttemptsAsync(poison.MessageId, 10);

        // Old by OccurredAt, the one measure an age-alone purge could use, since ProcessedAt is null here.
        await fixture.ExecuteAsync(
            "UPDATE payments.OutboxMessages SET OccurredAt = {0} WHERE MessageId = {1};",
            LongAgo,
            poison.MessageId);

        (await fixture.PurgeRetentionAsync()).Outbox.ShouldBe(0);

        OutboxMessage survivor = (await fixture.OutboxAsync()).ShouldHaveSingleItem();
        survivor.MessageId.ShouldBe(poison.MessageId);
        survivor.ProcessedAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_processed_outbox_row_inside_the_window_survives()
    {
        // Processed rows are kept for a few days (§9.4), so the window is read as well as the null check.
        OutboxMessage row = OutboxRows.Healthy(fixture);
        await fixture.StageOutboxAsync(row);
        await fixture.SetOutboxProcessedAtAsync(row.MessageId, Recently);

        (await fixture.PurgeRetentionAsync()).Outbox.ShouldBe(0);

        (await fixture.OutboxAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Two_endpoints_differing_only_by_case_are_two_rows()
    {
        // SQL Server's default collation is case-insensitive and a queue name is not, so the column is
        // Latin1_General_BIN2.
        var messageId = Guid.CreateVersion7();

        // Two calls, so two contexts, and the database rather than one change tracker decides.
        await fixture.StageInboxAsync(new InboxMessage(messageId, "payments-orders", Recently));
        await fixture.StageInboxAsync(new InboxMessage(messageId, "payments-Orders", Recently));

        (await fixture.InboxAsync()).Count.ShouldBe(
            2,
            "case-insensitive collation would have made the second insert a primary-key violation");
    }

    [Fact]
    public async Task Two_endpoints_differing_outside_the_code_page_are_two_rows()
    {
        // A queue name is UTF-8, so the column is nvarchar; under varchar `ж` and `д` both become `?`.
        var messageId = Guid.CreateVersion7();

        await fixture.StageInboxAsync(new InboxMessage(messageId, "payments-ж", Recently));
        await fixture.StageInboxAsync(new InboxMessage(messageId, "payments-д", Recently));

        IReadOnlyList<InboxMessage> rows = await fixture.InboxAsync();

        rows.Count.ShouldBe(
            2,
            "varchar folds both endpoints to 'payments-?', so the second insert is a " +
            "primary-key violation — and a message dropped by the mechanism that exists " +
            "to drop only duplicates");

        // The endpoint unchanged, which catches a folding the count cannot see.
        rows.Select(r => r.Endpoint).ShouldBe(["payments-ж", "payments-д"], ignoreOrder: true);
    }

    [Fact]
    public async Task An_inbox_row_is_purged_on_age_alone()
    {
        // Unlike the outbox, an inbox row has no unfinished state; its window outlasts the longest redelivery (§9.5).
        await fixture.StageInboxAsync(
            new InboxMessage(Guid.CreateVersion7(), "payments-inventory-events", LongAgo),
            new InboxMessage(Guid.CreateVersion7(), "payments-inventory-events", Recently));

        (await fixture.PurgeRetentionAsync()).Inbox.ShouldBe(1);

        InboxMessage survivor = (await fixture.InboxAsync()).ShouldHaveSingleItem();
        survivor.HandledAt.ShouldBeGreaterThan(LongAgo);
    }

    [Fact]
    public async Task An_unclaimed_marker_past_its_window_is_purged_and_a_recent_one_is_not()
    {
        // Neither key was claimed, so age alone separates them; the recent row fails a DELETE ignoring CommittedAt.
        await fixture.StageIdempotencyMarkersAsync(
            new IdempotencyMarker(Key(), LongAgo),
            new IdempotencyMarker(Key(), Recently));

        (await fixture.PurgeRetentionAsync()).Idempotency.ShouldBe(1);

        IdempotencyMarker survivor = (await fixture.IdempotencyMarkersAsync()).ShouldHaveSingleItem();
        survivor.CommittedAt.ShouldBeGreaterThan(LongAgo);
    }

    [Fact]
    public async Task A_marker_replaced_between_the_select_and_the_delete_is_not_the_row_that_goes()
    {
        // The ABA between the select and the delete: the replacement keeps CommittedAt, and only the rowversion
        // differs (ADR-041). Interposed on UnheldAsync because that call is the window.
        string key = Key();

        await fixture.StageIdempotencyMarkersAsync(new IdempotencyMarker(key, LongAgo));

        byte[]? selected = await fixture.IdempotencyMarkerVersionAsync(key);
        selected.ShouldNotBeNull("the staged row carries the version the pass is about to select");

        ReplacingClaims claims = new(
            fixture.IdempotencyClaims,
            () => fixture.ReplaceIdempotencyMarkerAsync(key));

        (int _, int _, int idempotency) = await fixture.PurgeWithAsync(new RetentionPolicy(), claims);

        claims.Asked.ShouldBeTrue("the pass must reach the seam, or this test proves nothing");

        idempotency.ShouldBe(
            0,
            "the row the pass selected is gone and the row now under that key is a different write");

        // The table, not just the count, since deleting the replacement and miscounting loses the same data.
        (await fixture.IdempotencyMarkerCountAsync(key)).ShouldBe(1);

        byte[]? survivor = await fixture.IdempotencyMarkerVersionAsync(key);

        survivor.ShouldNotBeNull();
        survivor.ShouldNotBe(
            selected,
            "the replacement is a different write, and the version is what says so");
    }

    [Fact]
    public async Task A_held_key_costs_its_own_batch_and_not_the_rest_of_the_pass()
    {
        // The pass stops on no progress, not on a partial batch, because TOP refills deleted slots. Five rows in
        // batches of two with the middle one held: a partial-batch stop ends at three, a no-progress stop at four.
        string held = Key();

        await fixture.StageIdempotencyMarkersAsync(
            new IdempotencyMarker(Key(), LongAgo.AddMinutes(-5)),
            new IdempotencyMarker(Key(), LongAgo.AddMinutes(-4)),
            new IdempotencyMarker(held, LongAgo.AddMinutes(-3)),
            new IdempotencyMarker(Key(), LongAgo.AddMinutes(-2)),
            new IdempotencyMarker(Key(), LongAgo.AddMinutes(-1)));

        RetentionPolicy twoAtATime = new()
        {
            BatchSize = 2,
            MaxBatchesPerPass = 5,
            IdempotencyWindow = IdempotencyRetention.MarkerFloor
        };

        (await fixture.PurgeWithAsync(twoAtATime, new WithOneKeyHeld(held))).Idempotency.ShouldBe(
            4,
            "stopping on a partial batch would have ended the pass at three");

        IdempotencyMarker survivor = (await fixture.IdempotencyMarkersAsync()).ShouldHaveSingleItem();
        survivor.Key.ShouldBe(held, "the row the store still reports held is the one that stays");
    }

    [Fact]
    public async Task A_batch_spanning_more_than_one_delete_chunk_is_deleted_whole()
    {
        // Each row costs two parameters against SQL Server's 2,100, so the delete is chunked at
        // RetentionPurgeService.RowsPerDelete, and this figure has to exceed it.
        const int candidates = 1_001;

        IdempotencyMarker[] rows =
            [.. Enumerable.Range(0, candidates).Select(_ => new IdempotencyMarker(Key(), LongAgo))];

        await fixture.StageIdempotencyMarkersAsync(rows);

        // Staged directly and never claimed, so the store reports every key unheld.
        (await fixture.PurgeRetentionAsync()).Idempotency.ShouldBe(
            candidates,
            "a second chunk that never ran would report a thousand and leave the remainder");

        (await fixture.IdempotencyMarkersAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_skewed_clock_purges_the_outbox_and_the_inbox_and_leaves_the_marker()
    {
        // Two clocks, one age, opposite outcomes: the outbox's and inbox's cutoffs read the skewed clock, the
        // marker's reads SYSDATETIMEOFFSET() on the server (ADR-038). Twice the window, so nothing is near a boundary.
        TimeSpan window = IdempotencyRetention.MarkerFloor;

        RetentionPolicy oneDay = new()
        {
            OutboxWindow = window,
            InboxWindow = window,
            IdempotencyWindow = window
        };

        // One instant for all three rows, so only the clock a statement read varies.
        DateTimeOffset justNow = DateTimeOffset.UtcNow;

        OutboxMessage row = OutboxRows.Healthy(fixture);
        await fixture.StageOutboxAsync(row);
        await fixture.SetOutboxProcessedAtAsync(row.MessageId, justNow);

        await fixture.StageInboxAsync(
            new InboxMessage(Guid.CreateVersion7(), "payments-inventory-events", justNow));

        await fixture.StageIdempotencyMarkersAsync(new IdempotencyMarker(Key(), justNow));

        (int outbox, int inbox, int idempotency) =
            await fixture.PurgeWithSkewedClockAsync(oneDay, window * 2);

        outbox.ShouldBe(1, "the outbox cutoff is subtracted from the registered clock, which is two days ahead");
        inbox.ShouldBe(1, "§9.5 keeps the inbox on that same application-computed cutoff, deliberately");
        idempotency.ShouldBe(
            0,
            "the marker's cutoff is DATEADD over SYSDATETIMEOFFSET(), so skewing this process's clock " +
            "cannot move it — a regression to @Before deletes this row, and this line is what says so " +
            "(ADR-038)");

        // The table as well as the count, since deleting and miscounting is a different defect.
        (await fixture.IdempotencyMarkersAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_backlog_larger_than_one_batch_drains_over_batches_and_stops_at_the_ceiling()
    {
        // A policy of its own: five rows in batches of two show both edges, where the real batch needs 10,001.
        for (int row = 0; row < 5; row++)
        {
            OutboxMessage processed = OutboxRows.Healthy(fixture);
            await fixture.StageOutboxAsync(processed);
            await fixture.SetOutboxProcessedAtAsync(processed.MessageId, LongAgo);
        }

        RetentionPolicy twoAtATime = new() { BatchSize = 2, MaxBatchesPerPass = 2 };

        // Four of five: two batches of two, then the ceiling.
        (await fixture.PurgeWithAsync(twoAtATime)).Outbox.ShouldBe(4);
        (await fixture.OutboxAsync()).Count.ShouldBe(1);

        // The next pass takes the remainder and stops short of its ceiling, on the partial batch.
        (await fixture.PurgeWithAsync(twoAtATime)).Outbox.ShouldBe(1);
        (await fixture.OutboxAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_pass_purges_every_table()
    {
        // §9.5 asks for one hosted service covering every table.
        OutboxMessage row = OutboxRows.Healthy(fixture);
        await fixture.StageOutboxAsync(row);
        await fixture.SetOutboxProcessedAtAsync(row.MessageId, LongAgo);

        await fixture.StageInboxAsync(
            new InboxMessage(Guid.CreateVersion7(), "payments-inventory-events", LongAgo));

        await fixture.StageIdempotencyMarkersAsync(
            new IdempotencyMarker(Key(), LongAgo));

        (await fixture.PurgeRetentionAsync()).ShouldBe((Outbox: 1, Inbox: 1, Idempotency: 1));
    }

    /// <summary>The registered claim store, with a side effect run when the pass asks which keys are unheld.</summary>
    private sealed class ReplacingClaims(IIdempotencyStore inner, Func<Task> onAsked) : IIdempotencyStore
    {
        public bool Asked { get; private set; }

        public Task<string?> TryClaimAsync(string key, TimeSpan retention, CancellationToken ct) =>
            inner.TryClaimAsync(key, retention, ct);

        public Task<IdempotencyEntry?> GetAsync(string key, CancellationToken ct) =>
            inner.GetAsync(key, ct);

        public Task CompleteAsync(string key, string claim, string payload, CancellationToken ct) =>
            inner.CompleteAsync(key, claim, payload, ct);

        public Task ReleaseAsync(string key, string claim, CancellationToken ct) =>
            inner.ReleaseAsync(key, claim, ct);

        public async Task<IReadOnlyCollection<string>> UnheldAsync(
            IReadOnlyCollection<string> keys,
            CancellationToken ct)
        {
            Asked = true;
            await onAsked();

            return await inner.UnheldAsync(keys, ct);
        }
    }

    /// <summary>Reports one key held and every other unheld, as a live claim would (ADR-039).</summary>
    private sealed class WithOneKeyHeld(string held) : IIdempotencyStore
    {
        public Task<string?> TryClaimAsync(string key, TimeSpan retention, CancellationToken ct) =>
            throw new NotSupportedException("This double answers only UnheldAsync.");

        public Task<IdempotencyEntry?> GetAsync(string key, CancellationToken ct) =>
            throw new NotSupportedException("This double answers only UnheldAsync.");

        public Task CompleteAsync(string key, string claim, string payload, CancellationToken ct) =>
            throw new NotSupportedException("This double answers only UnheldAsync.");

        public Task ReleaseAsync(string key, string claim, CancellationToken ct) =>
            throw new NotSupportedException("This double answers only UnheldAsync.");

        public Task<IReadOnlyCollection<string>> UnheldAsync(IReadOnlyCollection<string> keys, CancellationToken ct) =>
            Task.FromResult<IReadOnlyCollection<string>>([.. keys.Where(k => k != held)]);
    }
}
