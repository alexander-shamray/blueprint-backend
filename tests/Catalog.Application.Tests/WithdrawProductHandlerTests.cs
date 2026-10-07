using System.Text.Json;
using Catalog.Application.Products;
using Catalog.Application.Products.PublishProduct;
using Catalog.Application.Products.WithdrawProduct;
using Catalog.TestSupport;
using Common.Application;
using Common.Contracts.Catalog.V1;
using Common.Infrastructure.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Catalog.Application.Tests;

/// <summary>§12.1's application level: the withdrawal end to end, through the real container and database.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class WithdrawProductHandlerTests(ServiceFixture fixture) : IAsyncLifetime
{
    /// <summary>The seller every product here is published by, and the default caller.</summary>
    private static readonly Guid Seller = Guid.CreateVersion7();

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<TResult> SendAsync<TResult>(ICommand<TResult> command, Guid? caller = null)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        return await Caller.SendAsync(scope.ServiceProvider, command, caller ?? Seller);
    }

    /// <summary>A published product, with its <c>ProductPublished</c> row cleared so each test sees its own.</summary>
    private async Task<Guid> PublishedAsync()
    {
        Result<Guid> published =
            await SendAsync(new PublishProductCommand(Guid.CreateVersion7(), "Walnut desk", null, 19.99m, "EUR"));

        await fixture.ExecuteAsync("DELETE FROM catalog.OutboxMessages");

        return published.Value;
    }

    [Fact]
    public async Task Publishing_records_the_caller_as_the_seller()
    {
        Guid productId = await PublishedAsync();

        (await fixture.ScalarAsync<Guid>("SELECT Value = SellerId FROM catalog.Products WHERE Id = {0}", productId))
            .ShouldBe(Seller);
    }

    [Fact]
    public async Task The_withdrawal_and_its_ProductDiscontinued_row_commit_together()
    {
        Guid productId = await PublishedAsync();

        Result result = await SendAsync(new WithdrawProductCommand(Guid.CreateVersion7(), productId));

        result.IsSuccess.ShouldBeTrue();
        DateTimeOffset withdrawnAt = await fixture.ScalarAsync<DateTimeOffset>(
            "SELECT Value = WithdrawnAt FROM catalog.Products WHERE Id = {0}",
            productId);

        // The Broker lane carries the contract, and nothing reaches the Local lane (§9.3, §9.4).
        OutboxMessage row = (await fixture.OutboxAsync()).ShouldHaveSingleItem();
        row.Lane.ShouldBe(OutboxLane.Broker);
        row.MessageType.ShouldBe(typeof(ProductDiscontinued).FullName);
        row.CorrelationId.ShouldBe(productId);

        ProductDiscontinued discontinued =
            JsonSerializer.Deserialize<ProductDiscontinued>(row.Payload, fixture.OutboxJson.Options)!;
        discontinued.MessageId.ShouldBe(row.MessageId, "one identity, not two (§9.1)");
        discontinued.ProductId.ShouldBe(productId);
        discontinued.OccurredAt.ShouldBe(withdrawnAt, "Ordering's watermark compares this instant (§6.6)");
    }

    [Fact]
    public async Task A_second_withdrawal_is_a_rule_failure_that_stages_nothing()
    {
        Guid productId = await PublishedAsync();
        (await SendAsync(new WithdrawProductCommand(Guid.CreateVersion7(), productId))).IsSuccess.ShouldBeTrue();
        await fixture.ExecuteAsync("DELETE FROM catalog.OutboxMessages");

        Result result = await SendAsync(new WithdrawProductCommand(Guid.CreateVersion7(), productId));

        result.Error.ShouldBe(ProductErrors.Withdrawn);
        (await fixture.OutboxAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Another_sellers_product_is_not_found_and_stays_on_sale()
    {
        Guid productId = await PublishedAsync();

        Result result = await SendAsync(
            new WithdrawProductCommand(Guid.CreateVersion7(), productId),
            caller: Guid.CreateVersion7());

        // A 404, not a 403, which would confirm the product exists (ADR-074, §11.4).
        result.Error.ShouldBe(ProductErrors.NotFound);
        (await fixture.ScalarAsync<int>(
                "SELECT Value = COUNT(*) FROM catalog.Products WHERE Id = {0} AND WithdrawnAt IS NULL",
                productId))
            .ShouldBe(1);
        (await fixture.OutboxAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_product_with_no_seller_belongs_to_no_caller()
    {
        Guid productId = await PublishedAsync();
        await fixture.ExecuteAsync("UPDATE catalog.Products SET SellerId = NULL WHERE Id = {0}", productId);

        Result result = await SendAsync(new WithdrawProductCommand(Guid.CreateVersion7(), productId));

        // The seeder's rows and every row older than the column (ADR-074).
        result.Error.ShouldBe(ProductErrors.NotFound);
    }

    [Fact]
    public async Task An_unknown_product_is_not_found()
    {
        Result result = await SendAsync(new WithdrawProductCommand(Guid.CreateVersion7(), Guid.CreateVersion7()));

        result.Error.ShouldBe(ProductErrors.NotFound);
    }
}
