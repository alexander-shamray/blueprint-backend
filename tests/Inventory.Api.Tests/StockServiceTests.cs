using Grpc.Core;
using Grpc.Net.Client;
using Inventory.Application.Stock.GetAvailability;
using Inventory.Stock.V1;
using Inventory.TestSupport;
using Shouldly;
using Xunit;
using StockGrpc = Inventory.Stock.V1.Stock;

namespace Inventory.Api.Tests;

/// <summary>ADR-154's server half over the real pipeline, from authentication to Dapper on a real database.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class StockServiceTests(ServiceFixture fixture) : IAsyncLifetime
{
    private GrpcChannel _channel = null!;

    public async ValueTask InitializeAsync()
    {
        _channel = GrpcChannel.ForAddress(
            fixture.Factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = fixture.Factory.Server.CreateHandler() });

        await fixture.ResetAsync();
    }

    public ValueTask DisposeAsync()
    {
        _channel.Dispose();

        return ValueTask.CompletedTask;
    }

    /// <summary>A client-credentials principal (§11.3) carrying no permission, per call so a call can arrive without.</summary>
    private static Metadata Authenticated() =>
        [new Metadata.Entry(TestAuthHandler.UserHeader, "service-account-web-bff")];

    private StockGrpc.StockClient Stock => new(_channel);

    [Fact]
    public async Task A_product_with_unreserved_stock_is_in_stock()
    {
        Guid product = Guid.CreateVersion7();
        await ReservationTestSupport.SeedStock(fixture, product, 3);

        GetAvailabilityReply reply = await Ask(product);

        reply.Availability.ShouldHaveSingleItem().InStock.ShouldBeTrue();
    }

    [Fact]
    public async Task A_product_with_nothing_unreserved_is_not_in_stock_though_it_has_a_row()
    {
        Guid product = Guid.CreateVersion7();
        await fixture.ExecuteAsync(
            "INSERT INTO inventory.StockItems (ProductId, Available, Reserved, UpdatedAt) " +
            "VALUES ({0}, 0, 4, SYSDATETIMEOFFSET())",
            product);

        GetAvailabilityReply reply = await Ask(product);

        ProductAvailability entry = reply.Availability.ShouldHaveSingleItem();
        entry.ProductId.ShouldBe(product.ToString());
        entry.InStock.ShouldBeFalse();
    }

    [Fact]
    public async Task A_product_with_no_row_is_absent_and_not_out_of_stock()
    {
        GetAvailabilityReply reply = await Ask(Guid.CreateVersion7());

        reply.Availability.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_empty_request_is_answered_with_an_empty_reply()
    {
        GetAvailabilityReply reply = await Ask();

        reply.Availability.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        // No principal, which is what makes §11.5's client credentials load-bearing rather than ceremonial.
        RpcException thrown = await Should.ThrowAsync<RpcException>(
            () => Stock
                .GetAvailabilityAsync(new GetAvailabilityRequest(), cancellationToken: TestContext.Current.CancellationToken)
                .ResponseAsync);

        thrown.StatusCode.ShouldBe(StatusCode.Unauthenticated);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("11111111111111111111111111111111")]
    [InlineData("{11111111-1111-1111-1111-111111111111}")]
    public async Task A_product_id_that_is_not_canonical_is_InvalidArgument_without_the_value(string productId)
    {
        GetAvailabilityRequest request = new();
        request.ProductId.Add(productId);

        RpcException thrown = await Should.ThrowAsync<RpcException>(
            () => Stock
                .GetAvailabilityAsync(request, Authenticated(), cancellationToken: TestContext.Current.CancellationToken)
                .ResponseAsync);

        thrown.StatusCode.ShouldBe(StatusCode.InvalidArgument);

        // The index, never the value: §13.4's redactor cannot see a value interpolated into a message.
        thrown.Status.Detail.ShouldContain("product_id[0]");
        thrown.Status.Detail.ShouldNotContain(productId);
    }

    [Fact]
    public async Task Too_many_products_is_InvalidArgument_rather_than_Unknown()
    {
        GetAvailabilityRequest request = new();

        for (int i = 0; i <= GetAvailabilityValidator.MaxProductIds; i++)
            request.ProductId.Add(Guid.CreateVersion7().ToString());

        RpcException thrown = await Should.ThrowAsync<RpcException>(
            () => Stock
                .GetAvailabilityAsync(request, Authenticated(), cancellationToken: TestContext.Current.CancellationToken)
                .ResponseAsync);

        // ValidationInterceptor's job: untranslated this is Unknown, which the BFF maps to a 500.
        thrown.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        thrown.Status.Detail.ShouldContain("ProductIds");
    }

    private async Task<GetAvailabilityReply> Ask(params Guid[] products)
    {
        GetAvailabilityRequest request = new();
        request.ProductId.AddRange(products.Select(p => p.ToString()));

        return await Stock.GetAvailabilityAsync(
            request,
            Authenticated(),
            cancellationToken: TestContext.Current.CancellationToken);
    }
}
