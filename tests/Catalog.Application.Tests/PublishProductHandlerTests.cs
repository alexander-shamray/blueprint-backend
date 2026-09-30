using System.Text.Json;
using Catalog.Application.Products.PublishProduct;
using Catalog.Domain.Products;
using Catalog.TestSupport;
using Catalog.TestSupport.Outbox;
using Common.Application;
using Common.Contracts.Catalog.V1;
using Common.Infrastructure.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Catalog.Application.Tests;

/// <summary>§12.1's application level: one handler end to end, through the real container and database.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class PublishProductHandlerTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_dispatched_command_commits_the_product()
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        Result<Guid> result = await dispatcher.SendAsync(
            new PublishProductCommand(
                Guid.CreateVersion7(),
                "Walnut desk",
                "https://cdn.example/desk.jpg",
                19.99m,
                "eur"),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();

        // The handler never calls SaveChanges, so a committed row is the transaction behaviour's half (§6.3).
        string name = await fixture.ScalarAsync<string>(
            "SELECT Value = Name FROM catalog.Products WHERE Id = {0}", result.Value);
        name.ShouldBe("Walnut desk");

        string currency = await fixture.ScalarAsync<string>(
            "SELECT Value = PriceCurrency FROM catalog.Products WHERE Id = {0}", result.Value);
        currency.ShouldBe("EUR", "Money.Of normalises the code on the way in");
    }

    [Fact]
    public async Task The_product_row_and_the_outbox_row_commit_together()
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        Result<Guid> result = await dispatcher.SendAsync(
            new PublishProductCommand(Guid.CreateVersion7(), "Walnut desk", null, 19.99m, "eur"),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();

        IReadOnlyList<OutboxMessage> outbox = await fixture.OutboxAsync();

        // Unprocessed, which holds only because the factory removed the hosted dispatcher.
        outbox.ShouldAllBe(m => m.ProcessedAt == null);

        // The Broker lane carries the contract type (§9.3's allow-list) ...
        OutboxMessage row = outbox.ShouldHaveSingleItem();
        row.Lane.ShouldBe(OutboxLane.Broker);
        row.MessageType.ShouldBe(typeof(ProductPublished).FullName);

        // ... and never the domain type, which §9.3 keeps off the broker.
        row.MessageType.ShouldNotContain(nameof(ProductPublishedDomainEvent));

        // No Local row, since no IProjectionHandler takes this event (§7.5) and §9.4 throws on a row with none.
        outbox.ShouldNotContain(m => m.Lane == OutboxLane.Local);

        // The correlation is the product, not an ambient request id (§9.3).
        row.CorrelationId.ShouldBe(result.Value);

        // The payload survives the dispatcher's round trip.
        ProductPublished published = JsonSerializer
            .Deserialize<ProductPublished>(row.Payload, fixture.OutboxJson.Options)!;
        published.MessageId.ShouldBe(row.MessageId, "one identity, not two (§9.1)");
        published.Amount.ShouldBe(19.99m);
        published.Currency.ShouldBe("EUR");
    }

    [Fact]
    public async Task A_rejected_command_never_reaches_the_outbox()
    {
        // Validation runs before Transaction opens anything (§6.3), so the row is never staged, not rolled back.
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await Should.ThrowAsync<FluentValidation.ValidationException>(() =>
            dispatcher.SendAsync(
                new PublishProductCommand(Guid.CreateVersion7(), "", null, -1m, "x"),
                TestContext.Current.CancellationToken));

        (await fixture.OutboxAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_command_that_fails_after_staging_leaves_neither_row()
    {
        // Staged rows roll back with their transaction: StageThenFailCommand publishes two aggregates, so §6.3's
        // one-aggregate assertion throws after DispatchAsync has staged an event for each.
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await Should.ThrowAsync<InvariantViolationException>(() =>
            dispatcher.SendAsync(
                new StageThenFailCommand("Walnut desk"),
                TestContext.Current.CancellationToken));

        // Neither the aggregates nor the rows their events produced.
        (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM catalog.Products"))
            .ShouldBe(0);
        (await fixture.OutboxAsync()).ShouldBeEmpty(
            "the staged rows must roll back with the transaction that staged them");
    }

    [Fact]
    public async Task A_payload_longer_than_the_string_convention_survives_the_column()
    {
        // §7.2 caps every string property at 400, and a payload is unbounded (§9.1), so the column is asserted.
        string note = new('a', 1_000);

        await fixture.StageOutboxAsync(OutboxRows.Verbose(fixture, note));

        OutboxMessage row = (await fixture.OutboxAsync()).ShouldHaveSingleItem();
        row.Payload.ShouldContain(note, Case.Sensitive);
    }

    [Fact]
    public async Task An_invalid_command_is_rejected_before_the_handler_and_leaves_no_row()
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await Should.ThrowAsync<FluentValidation.ValidationException>(() =>
            dispatcher.SendAsync(
                new PublishProductCommand(Guid.CreateVersion7(), "", null, -1m, "x"),
                TestContext.Current.CancellationToken));

        int rows = await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM catalog.Products");
        rows.ShouldBe(0, "ValidationBehavior runs before Transaction opens anything (§6.3)");
    }
}
