using BffReplay;
using Common.Contracts.Catalog.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Contracts.Shipping.V1;
using Dapper;
using Microsoft.Data.SqlClient;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>ADR-051's rebuild against the real schema and broker, compared with what the consumers built.</summary>
[Collection(nameof(BffIntegrationCollection))]
public sealed class ReplayTests(BffServiceFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Processed = At.AddDays(3);

    private readonly PublisherOutboxes _outboxes = new(fixture.ConnectionString);

    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync();
        await _outboxes.ResetAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_reset_rebuilds_the_projection_the_consumers_built_from_the_same_events()
    {
        Guid kept = Guid.CreateVersion7();
        Guid cancelled = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();

        ProductPublished published = OrderEvents.Published(OrderEvents.Lamp, "Walnut desk lamp", At.AddDays(-3));
        OrderPlaced placedKept = OrderEvents.Placed(kept, customer, At);
        OrderConfirmed confirmed = OrderEvents.Confirmed(kept, customer, At.AddSeconds(7));
        PaymentAuthorised authorised = OrderEvents.Authorised(kept, At.AddSeconds(5));
        ShipmentDispatched dispatched = OrderEvents.Dispatched(kept, At.AddDays(1));
        OrderPlaced placedCancelled = OrderEvents.Placed(cancelled, customer, At.AddMinutes(1));
        OrderCancelled cancellation = OrderEvents.Cancelled(
            cancelled,
            customer,
            At.AddMinutes(2),
            CancelReasons.PaymentDeclined,
            CancelOrigins.Workflow);
        PaymentRefunded refunded = OrderEvents.Refunded(cancelled, At.AddMinutes(3));

        // One by one, so each is sent as its own contract rather than as the interface.
        await fixture.DeliverAsync(published);
        await fixture.DeliverAsync(placedKept);
        await fixture.DeliverAsync(confirmed);
        await fixture.DeliverAsync(authorised);
        await fixture.DeliverAsync(dispatched);
        await fixture.DeliverAsync(placedCancelled);
        await fixture.DeliverAsync(cancellation);
        await fixture.DeliverAsync(refunded);
        Guid[] handled =
        [
            published.MessageId, placedKept.MessageId, confirmed.MessageId, authorised.MessageId,
            dispatched.MessageId, placedCancelled.MessageId, cancellation.MessageId, refunded.MessageId
        ];

        ProjectedOrder keptBefore = (await fixture.OrderAsync(kept)).ShouldNotBeNull();
        ProjectedOrder cancelledBefore = (await fixture.OrderAsync(cancelled)).ShouldNotBeNull();
        IReadOnlyList<ProjectedLine> keptLines = await fixture.LinesAsync(kept);
        IReadOnlyList<ProjectedLine> cancelledLines = await fixture.LinesAsync(cancelled);

        // Three rows a replay must not send: two types outside the eight, and one the dispatcher still owns.
        PriceChanged repriced = new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = OrderEvents.Lamp,
            OccurredAt = At.AddDays(-2),
            ProductId = OrderEvents.Lamp,
            Amount = 24.99m,
            Currency = OrderEvents.Currency
        };
        PaymentDeclined declined = new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = cancelled,
            OccurredAt = At.AddMinutes(1).AddSeconds(30),
            OrderId = cancelled,
            Reason = "card_declined"
        };
        ShipmentDelivered undispatched = OrderEvents.Delivered(kept, At.AddDays(2));

        await _outboxes.StageAsync(Named("Catalog"), Processed, published, repriced);
        await _outboxes.StageAsync(Named("Ordering"), Processed, placedKept, confirmed, placedCancelled, cancellation);
        await _outboxes.StageAsync(Named("Payments"), Processed, authorised, refunded, declined);
        await _outboxes.StageAsync(Named("Shipping"), Processed, dispatched);
        await _outboxes.StageAsync(Named("Shipping"), null, undispatched);

        // The queue is the collection's, so a copy the inbox dropped in an earlier test is already skipped there.
        int skippedBefore = await fixture.QueueDepthAsync($"{Replay.Queue}_skipped");

        StringWriter output = new();
        ReplayReport report = await Replay.RunAsync(
            Settings(),
            reset: true,
            output,
            Replay.BrokerDeadline,
            TestContext.Current.CancellationToken);

        report.SentMessageIds.ShouldBe(handled, ignoreOrder: true);
        await BffServiceFixture.WaitUntilAsync(async () => (await fixture.InboxAsync()).Count == handled.Length);

        (await fixture.OrderAsync(kept)).ShouldNotBeNull().Facts().ShouldBe(keptBefore.Facts());
        (await fixture.OrderAsync(cancelled)).ShouldNotBeNull().Facts().ShouldBe(cancelledBefore.Facts());
        (await fixture.LinesAsync(kept)).ShouldBe(keptLines);
        (await fixture.LinesAsync(cancelled)).ShouldBe(cancelledLines);
        (await fixture.QueueDepthAsync($"{Replay.Queue}_skipped"))
            .ShouldBe(skippedBefore, "a type the queue does not bind was sent");

        report.OldestFrom(Named("Catalog")).ShouldBe(published.OccurredAt);
        output.ToString().ShouldContain("Shipping: 1 event(s) sent");
    }

    [Fact]
    public async Task A_replay_without_reset_fills_what_the_bff_never_handled_and_changes_nothing_it_had()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();
        OrderPlaced placed = OrderEvents.Placed(order, customer, At);
        PaymentAuthorised authorised = OrderEvents.Authorised(order, At.AddSeconds(5));
        ShipmentDispatched missed = OrderEvents.Dispatched(order, At.AddDays(1));

        await fixture.DeliverAsync(placed);
        await fixture.DeliverAsync(authorised);
        ProjectedOrder before = (await fixture.OrderAsync(order)).ShouldNotBeNull();

        await _outboxes.StageAsync(Named("Ordering"), Processed, placed);
        await _outboxes.StageAsync(Named("Payments"), Processed, authorised);
        await _outboxes.StageAsync(Named("Shipping"), Processed, missed);

        int skippedBefore = await fixture.QueueDepthAsync($"{Replay.Queue}_skipped");

        StringWriter output = new();
        ReplayReport report = await Replay.RunAsync(
            Settings(),
            reset: false,
            output,
            Replay.BrokerDeadline,
            TestContext.Current.CancellationToken);

        report.SentMessageIds.ShouldBe([missed.MessageId], "a repair sends only what the inbox has not handled");
        report.SkippedCount.ShouldBe(2);
        output.ToString().ShouldContain("Skipped 2 event(s)");
        await BffServiceFixture.WaitUntilAsync(async () => (await fixture.InboxAsync()).Count == 3);
        await BffServiceFixture.WaitUntilAsync(async () => await fixture.QueueDepthAsync(Replay.Queue) == 0);
        (await fixture.QueueDepthAsync($"{Replay.Queue}_skipped"))
            .ShouldBe(skippedBefore, "a handled copy was sent and dropped by the inbox");

        ProjectedOrder after = (await fixture.OrderAsync(order)).ShouldNotBeNull();
        after.DispatchedAt.ShouldBe(missed.OccurredAt);
        (after with { DispatchedAt = null, TrackingNumber = null, AsOf = before.AsOf }).ShouldBe(before);
        (await fixture.InboxAsync(placed.MessageId)).Count.ShouldBe(1, "the inbox drops a copy it has handled");
    }

    [Fact]
    public async Task A_repair_still_sends_a_row_whose_inbox_entry_was_purged()
    {
        OrderPlaced placed = OrderEvents.Placed(Guid.CreateVersion7(), Guid.CreateVersion7(), At);
        await fixture.DeliverAsync(placed);
        await using (SqlConnection connection = new(fixture.ConnectionString))
        {
            await connection.ExecuteAsync("DELETE FROM bff.InboxMessages;");
        }

        await _outboxes.StageAsync(Named("Ordering"), Processed, placed);

        ReplayReport report = await Replay.RunAsync(
            Settings(),
            reset: false,
            TextWriter.Null,
            Replay.BrokerDeadline,
            TestContext.Current.CancellationToken);

        report.SentMessageIds.ShouldBe([placed.MessageId]);
        report.SkippedCount.ShouldBe(0);
        await BffServiceFixture.WaitUntilAsync(async () => (await fixture.InboxAsync()).Count == 1);
    }

    [Fact]
    public async Task A_reset_keeps_the_product_names_no_outbox_still_holds()
    {
        Guid order = Guid.CreateVersion7();
        OrderPlaced placed = OrderEvents.Placed(order, Guid.CreateVersion7(), At);

        await fixture.DeliverAsync(OrderEvents.Published(OrderEvents.Lamp, "Walnut desk lamp", At.AddDays(-400)));
        await fixture.DeliverAsync(placed);
        await _outboxes.StageAsync(Named("Ordering"), Processed, placed);

        await Replay.RunAsync(
            Settings(),
            reset: true,
            TextWriter.Null,
            Replay.BrokerDeadline,
            TestContext.Current.CancellationToken);

        // The reset took both inbox rows, so the one back is the replayed placement's.
        await BffServiceFixture.WaitUntilAsync(async () => (await fixture.InboxAsync()).Count == 1);
        (await fixture.InboxAsync()).ShouldHaveSingleItem().MessageId.ShouldBe(placed.MessageId);
        (await fixture.ScalarAsync<string>(
            "SELECT Value = Name FROM bff.Products WHERE ProductId = {0}",
            OrderEvents.Lamp)).ShouldBe("Walnut desk lamp");
        (await fixture.OrderAsync(order)).ShouldNotBeNull().PlacedAt.ShouldBe(At);
    }

    [Fact]
    public async Task A_broker_it_cannot_reach_stops_the_run_before_anything_is_deleted()
    {
        OrderPlaced placed = OrderEvents.Placed(Guid.CreateVersion7(), Guid.CreateVersion7(), At);
        await fixture.DeliverAsync(placed);

        string secret = Guid.NewGuid().ToString("N");
        string unreachable = WithKey(BffFactory.UnreachableBroker, secret);

        InvalidOperationException refused = await Should.ThrowAsync<InvalidOperationException>(() =>
            Replay.RunAsync(
                Settings(broker: unreachable),
                reset: true,
                TextWriter.Null,
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));

        refused.Message.ShouldContain("did not answer");
        refused.Message.ShouldNotContain(secret);
        refused.InnerException.ShouldBeNull();
        (await fixture.OrderAsync(placed.OrderId)).ShouldNotBeNull();
        (await fixture.InboxAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_publisher_it_cannot_read_stops_the_run_before_anything_is_deleted()
    {
        OrderPlaced placed = OrderEvents.Placed(Guid.CreateVersion7(), Guid.CreateVersion7(), At);
        await fixture.DeliverAsync(placed);
        string absent = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = "NoSuchOutbox"
        }.ConnectionString;

        SqlException refused = await Should.ThrowAsync<SqlException>(() =>
            Replay.RunAsync(
                Settings(publishers: absent),
                reset: true,
                TextWriter.Null,
                Replay.BrokerDeadline,
                TestContext.Current.CancellationToken));

        // 4060: the login cannot open the database it names.
        refused.Number.ShouldBe(4060);
        (await fixture.OrderAsync(placed.OrderId)).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_bff_database_it_cannot_reach_stops_a_repair_before_anything_is_sent()
    {
        Guid order = Guid.CreateVersion7();
        await _outboxes.StageAsync(Named("Ordering"), Processed, OrderEvents.Placed(order, Guid.CreateVersion7(), At));
        string absent = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = "NoSuchProjection"
        }.ConnectionString;

        SqlException refused = await Should.ThrowAsync<SqlException>(() =>
            Replay.RunAsync(
                Settings(bff: absent),
                reset: false,
                TextWriter.Null,
                Replay.BrokerDeadline,
                TestContext.Current.CancellationToken));

        // 4060, from the preflight, which runs before the bus starts, so the staged row was never sent.
        refused.Number.ShouldBe(4060);
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        (await fixture.OrderAsync(order)).ShouldBeNull();
        (await fixture.InboxAsync()).ShouldBeEmpty();
        (await fixture.QueueDepthAsync(Replay.Queue)).ShouldBe(0);
    }

    [Fact]
    public async Task A_refused_login_is_reported_with_its_kind_and_never_its_password()
    {
        string secret = Guid.NewGuid().ToString("N");
        string wrong = WithKey(fixture.BrokerAddress, secret);

        InvalidOperationException refused = await Should.ThrowAsync<InvalidOperationException>(() =>
            Replay.RunAsync(
                Settings(broker: wrong),
                reset: true,
                TextWriter.Null,
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken));

        refused.Message.ShouldContain("did not answer");
        refused.Message.ShouldContain("RabbitMqConnectionException", Case.Sensitive, "the failure's kind is named");
        refused.Message.ShouldNotContain(secret);
        refused.InnerException.ShouldBeNull();
    }

    [Fact]
    public async Task A_row_that_cannot_be_decoded_stops_a_reset_before_anything_is_deleted()
    {
        Guid order = Guid.CreateVersion7();
        OrderPlaced placed = OrderEvents.Placed(order, Guid.CreateVersion7(), At);
        await fixture.DeliverAsync(placed);
        await _outboxes.StageAsync(Named("Ordering"), Processed, placed);
        await _outboxes.CorruptPayloadsAsync(Named("Ordering"));

        await Should.ThrowAsync<Exception>(() =>
            Replay.RunAsync(
                Settings(),
                reset: true,
                TextWriter.Null,
                Replay.BrokerDeadline,
                TestContext.Current.CancellationToken));

        (await fixture.OrderAsync(order)).ShouldNotBeNull();
        (await fixture.InboxAsync()).Count.ShouldBe(1);
    }

    // The account's name with a key it was never given, spelt as a URI so no connection string is written out.
    private static string WithKey(string address, string key)
    {
        Uri parsed = new(address);
        string account = parsed.UserInfo.Length == 0
            ? "bff-svc"
            : Uri.UnescapeDataString(parsed.UserInfo.Split(':')[0]);

        return $"{parsed.Scheme}://{account}:{key}@{parsed.Authority}{parsed.PathAndQuery}";
    }

    private static Publisher Named(string name) => Publisher.All.Single(p => p.Name == name);

    private ReplaySettings Settings(string? broker = null, string? publishers = null, string? bff = null) =>
        new(
            bff ?? fixture.ConnectionString,
            broker ?? fixture.BrokerAddress,
            [.. Publisher.All.Select(p => new PublisherConnection(p, publishers ?? _outboxes.ConnectionString))]);
}
