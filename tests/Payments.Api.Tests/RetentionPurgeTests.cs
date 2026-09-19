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

/// <summary>
/// §9.4's, §9.5's and §8.5's retention purges, driven a pass at a time against
/// the real tables. The predicate that separates them is the whole subject: the
/// outbox deletes on <c>ProcessedAt IS NOT NULL</c> and age, the inbox on age
/// alone, and the marker on age and the claim store having let its key go
/// (ADR-039).
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class RetentionPurgeTests(ServiceFixture fixture) : IAsyncLifetime
{
    // Comfortably past the seven-day window either side, so the test is about
    // the predicate rather than about arithmetic near a boundary.
    private static DateTimeOffset LongAgo => DateTimeOffset.UtcNow.AddDays(-30);
    private static DateTimeOffset Recently => DateTimeOffset.UtcNow.AddDays(-1);

    /// <summary>
    /// A key in §8.5's shape — {subject}:{operation}:{commandId} — distinct per
    /// call, because the column is the primary key and two markers staged in
    /// one test must be two rows.
    /// </summary>
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
        // The reason `ProcessedAt IS NOT NULL` is load-bearing rather than
        // defensive (§9.4). An abandoned row — attempts at the cap, never
        // processed — is exactly what §13.6's alert exists to surface, and
        // purging on age alone would turn permanent data loss into a clean,
        // empty table. Nothing else in the system would notice.
        OutboxMessage poison = OutboxRows.Poison(fixture);
        await fixture.StageOutboxAsync(poison);
        await fixture.SetOutboxAttemptsAsync(poison.MessageId, 10);

        // OccurredAt, not the column the purge reads: `ProcessedAt` is null on
        // an abandoned row by definition, so the predicate can never match it.
        // This makes the row old by the one measure the age-alone purge §9.4
        // warns about would use, which is the mutation the assertion below
        // has to be able to fail on.
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
        // Processed rows are kept for a few days for debugging (§9.4), so the
        // window has to be read as well as the null check — a purge matching on
        // the predicate alone would delete yesterday's evidence.
        OutboxMessage row = OutboxRows.Healthy(fixture);
        await fixture.StageOutboxAsync(row);
        await fixture.SetOutboxProcessedAtAsync(row.MessageId, Recently);

        (await fixture.PurgeRetentionAsync()).Outbox.ShouldBe(0);

        (await fixture.OutboxAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Two_endpoints_differing_only_by_case_are_two_rows()
    {
        // SQL Server's default collation is case-insensitive and a queue name
        // is not, so `orders` and `Orders` would collide and the second
        // endpoint's message be dropped as a duplicate of a delivery it never
        // received; the column is `Latin1_General_BIN2` for this. Written here
        // rather than through the filter because what is under test is the
        // key's comparison semantics.
        var messageId = Guid.CreateVersion7();

        // Two calls, so two contexts: one change tracker would answer EF's
        // identity map's question rather than the database's, and the
        // database is what decides whether a second endpoint's message
        // survives.
        await fixture.StageInboxAsync(new InboxMessage(messageId, "payments-orders", Recently));
        await fixture.StageInboxAsync(new InboxMessage(messageId, "payments-Orders", Recently));

        (await fixture.InboxAsync()).Count.ShouldBe(
            2,
            "case-insensitive collation would have made the second insert a primary-key violation");
    }

    [Fact]
    public async Task Two_endpoints_differing_outside_the_code_page_are_two_rows()
    {
        // The half a binary collation cannot give: AMQP 0-9-1 allows 255 bytes
        // of UTF-8 in a queue name, so the column has to be nvarchar. Cyrillic
        // rather than accented Latin, because SQL Server best-fit folds `ő` to
        // `o` under varchar — two distinct rows, silently wrong — while `ж`
        // and `д` have no Latin form and both become `?`, so the second insert
        // is a key violation.
        var messageId = Guid.CreateVersion7();

        await fixture.StageInboxAsync(new InboxMessage(messageId, "payments-ж", Recently));
        await fixture.StageInboxAsync(new InboxMessage(messageId, "payments-д", Recently));

        IReadOnlyList<InboxMessage> rows = await fixture.InboxAsync();

        rows.Count.ShouldBe(
            2,
            "varchar folds both endpoints to 'payments-?', so the second insert is a " +
            "primary-key violation — and a message dropped by the mechanism that exists " +
            "to drop only duplicates");

        // What the column owes the filter is the endpoint it was handed,
        // unchanged. This is the assertion that catches the folding case, which
        // the count cannot see.
        rows.Select(r => r.Endpoint).ShouldBe(["payments-ж", "payments-д"], ignoreOrder: true);
    }

    [Fact]
    public async Task An_inbox_row_is_purged_on_age_alone()
    {
        // The asymmetry with the outbox, and it is deliberate: an inbox row
        // records that a message was handled, so there is no unfinished state
        // for a predicate to protect. What protects it is the window, which
        // §9.5 says must outlast the broker's longest redelivery — prune sooner
        // and a late redelivery arrives looking new.
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
        // The window half of the predicate, isolated: neither key was ever
        // claimed, so the store reports both unheld and age alone separates
        // them. Two rows rather than one, because a DELETE that ignored
        // CommittedAt would satisfy a single already-old marker; the recent
        // row is what makes this a test.
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
        // The ABA between the select and the delete: a key names a command, so
        // past §8.5's guarantee a retry can claim it again, commit, and write
        // a fresh marker under a key this pass has already selected. The
        // replacement keeps the original CommittedAt, which a delete keyed on
        // (Key, CommittedAt) cannot tell apart; the rowversion is stamped with
        // a value the SELECT never saw (ADR-041). Interposed on UnheldAsync
        // because that call is the window.
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

        // The table, not just the count — a pass that deleted the replacement
        // and miscounted is the same data loss with a better report.
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
        // The pass stops on no progress, not on an incomplete batch: TOP
        // refills the deleted slots with the next-oldest rows, so a partial
        // batch does not mean the store would release nothing more. Five rows
        // in batches of two with the middle one held: stopping on a partial
        // batch ends at three, stopping on no progress at four. The store is
        // substituted, because §2 gives Payments no way to hold a claim of
        // its own to test against.
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
        // The chunked delete: each row costs two parameters against SQL
        // Server's 2,100, so the delete is chunked at
        // `RetentionPurgeService.RowsPerDelete`, and the figure here has to
        // exceed that constant or the boundary is never crossed and the test
        // covers nothing.
        const int candidates = 1_001;

        IdempotencyMarker[] rows =
            [.. Enumerable.Range(0, candidates).Select(_ => new IdempotencyMarker(Key(), LongAgo))];

        await fixture.StageIdempotencyMarkersAsync(rows);

        // Every key here was staged directly and never claimed, so the store
        // reports all of them unheld and the pass is about the delete alone.
        (await fixture.PurgeRetentionAsync()).Idempotency.ShouldBe(
            candidates,
            "a second chunk that never ran would report a thousand and leave the remainder");

        (await fixture.IdempotencyMarkersAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_skewed_clock_purges_the_outbox_and_the_inbox_and_leaves_the_marker()
    {
        // With the host's clock and the container's in agreement, a marker
        // statement that had regressed to the application-supplied `@Before`
        // would pass unnoticed. Two clocks, one age, opposite outcomes: the
        // outbox's and inbox's cutoffs are `now - window` on the skewed clock,
        // the marker's is DATEADD over SYSDATETIMEOFFSET() on the server
        // (ADR-038). The window is read because RetentionPolicy refuses one
        // below §8.5's claim, and the skew is twice it so nothing sits near a
        // boundary.
        TimeSpan window = IdempotencyRetention.MarkerFloor;

        RetentionPolicy oneDay = new()
        {
            OutboxWindow = window,
            InboxWindow = window,
            IdempotencyWindow = window
        };

        // One instant for all three rows, so what the assertions below vary is
        // which clock a statement read and nothing else.
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

        // The table as well as the count, because a pass that deleted the row
        // and miscounted is a different defect from one that kept it.
        (await fixture.IdempotencyMarkersAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_backlog_larger_than_one_batch_drains_over_batches_and_stops_at_the_ceiling()
    {
        // Two claims a single row cannot make: the loop continues while a
        // batch comes back full, and it stops at MaxBatchesPerPass rather than
        // running until the table is empty. A policy of its own, because five
        // rows against a batch of two show both edges where the real 5,000
        // would need 10,001 rows.
        for (int row = 0; row < 5; row++)
        {
            OutboxMessage processed = OutboxRows.Healthy(fixture);
            await fixture.StageOutboxAsync(processed);
            await fixture.SetOutboxProcessedAtAsync(processed.MessageId, LongAgo);
        }

        RetentionPolicy twoAtATime = new() { BatchSize = 2, MaxBatchesPerPass = 2 };

        // Four of five: two batches of two, then the ceiling. A loop with no
        // ceiling would return five here and hold its connection until the
        // table was empty, which is the behaviour a first run against a service
        // that has never purged must not have.
        (await fixture.PurgeWithAsync(twoAtATime)).Outbox.ShouldBe(4);
        (await fixture.OutboxAsync()).Count.ShouldBe(1);

        // The next pass takes the remainder and stops short of its ceiling,
        // because a batch that comes back under BatchSize means the table is
        // drained — which is the loop's other exit, and the one that keeps an
        // idle service from running twenty statements an hour for nothing.
        (await fixture.PurgeWithAsync(twoAtATime)).Outbox.ShouldBe(1);
        (await fixture.OutboxAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_pass_purges_every_table()
    {
        // §9.5 asks for one hosted service covering all of them, and the
        // alternative is a schedule each with one being the one nobody notices
        // has stopped.
        OutboxMessage row = OutboxRows.Healthy(fixture);
        await fixture.StageOutboxAsync(row);
        await fixture.SetOutboxProcessedAtAsync(row.MessageId, LongAgo);

        await fixture.StageInboxAsync(
            new InboxMessage(Guid.CreateVersion7(), "payments-inventory-events", LongAgo));

        await fixture.StageIdempotencyMarkersAsync(
            new IdempotencyMarker(Key(), LongAgo));

        (await fixture.PurgeRetentionAsync()).ShouldBe((Outbox: 1, Inbox: 1, Idempotency: 1));
    }

    /// <summary>
    /// The registered claim store, with one side effect run at the moment the
    /// pass asks it which keys are unheld.
    /// </summary>
    /// <remarks>
    /// Every answer is the real store's; the decoration is when, not what,
    /// because substituting the verdict would make the test assert its own
    /// idea of the claim against a pass that reads the registered store.
    /// <see cref="Asked"/> is what stops a seam nothing reached leaving the
    /// test green.
    /// </remarks>
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

    /// <summary>
    /// A claim store that reports one key as still held and every other key
    /// as unheld — the shape a live claim would give the purge (ADR-039),
    /// which Payments' own registered store cannot (§2).
    /// </summary>
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
