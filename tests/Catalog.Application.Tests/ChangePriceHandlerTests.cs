using System.Text.Json;
using Catalog.Application.Products;
using Catalog.Application.Products.ChangePrice;
using Catalog.Application.Products.PublishProduct;
using Catalog.TestSupport;
using Common.Application;
using Common.Contracts.Catalog.V1;
using Common.Infrastructure.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Catalog.Application.Tests;

/// <summary>§12.1's application level: the price change end to end, through the real container and database.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ChangePriceHandlerTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<Result> SendAsync<TCommand>(TCommand command)
        where TCommand : ICommand<Result>
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider
            .GetRequiredService<IDispatcher>()
            .SendAsync(command, TestContext.Current.CancellationToken);
    }

    /// <summary>A published product, with its <c>ProductPublished</c> row cleared so each test sees its own.</summary>
    private async Task<Guid> PublishedAsync(decimal amount = 19.99m)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        Result<Guid> published = await scope.ServiceProvider
            .GetRequiredService<IDispatcher>()
            .SendAsync(
                new PublishProductCommand(Guid.CreateVersion7(), "Walnut desk", null, amount, "EUR"),
                TestContext.Current.CancellationToken);

        await fixture.ExecuteAsync("DELETE FROM catalog.OutboxMessages");

        return published.Value;
    }

    [Fact]
    public async Task The_price_and_its_PriceChanged_row_commit_together()
    {
        Guid productId = await PublishedAsync();

        Result result = await SendAsync(new ChangePriceCommand(Guid.CreateVersion7(), productId, 24.50m, "eur"));

        result.IsSuccess.ShouldBeTrue();
        (await fixture.ScalarAsync<decimal>(
                "SELECT Value = PriceAmount FROM catalog.Products WHERE Id = {0}",
                productId))
            .ShouldBe(24.50m);

        // The Broker lane carries the contract, and nothing reaches the Local lane (§9.3, §9.4).
        OutboxMessage row = (await fixture.OutboxAsync()).ShouldHaveSingleItem();
        row.Lane.ShouldBe(OutboxLane.Broker);
        row.MessageType.ShouldBe(typeof(PriceChanged).FullName);
        row.CorrelationId.ShouldBe(productId);

        PriceChanged changed = JsonSerializer.Deserialize<PriceChanged>(row.Payload, fixture.OutboxJson.Options)!;
        changed.MessageId.ShouldBe(row.MessageId, "one identity, not two (§9.1)");
        changed.ProductId.ShouldBe(productId);
        changed.Amount.ShouldBe(24.50m);
        changed.Currency.ShouldBe("EUR");
    }

    [Fact]
    public async Task The_same_price_again_stages_nothing()
    {
        Guid productId = await PublishedAsync(19.99m);

        (await SendAsync(new ChangePriceCommand(Guid.CreateVersion7(), productId, 19.99m, "EUR"))).IsSuccess.ShouldBeTrue();

        (await fixture.OutboxAsync()).ShouldBeEmpty("the price did not change, so there is nothing to publish");
    }

    [Fact]
    public async Task Another_currency_is_a_rule_failure_that_changes_nothing()
    {
        Guid productId = await PublishedAsync();

        Result result = await SendAsync(new ChangePriceCommand(Guid.CreateVersion7(), productId, 24.50m, "USD"));

        result.Error.ShouldBe(ProductErrors.CurrencyFixed);
        (await fixture.ScalarAsync<decimal>(
                "SELECT Value = PriceAmount FROM catalog.Products WHERE Id = {0}",
                productId))
            .ShouldBe(19.99m);
        (await fixture.OutboxAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_product_is_not_found()
    {
        Result result = await SendAsync(new ChangePriceCommand(Guid.CreateVersion7(), Guid.CreateVersion7(), 24.50m, "EUR"));

        result.Error.ShouldBe(ProductErrors.NotFound);
    }
}
