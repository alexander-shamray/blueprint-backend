using System.Net;
using System.Net.Http.Json;
using Inventory.Application.Stock.GetStock;
using Inventory.TestSupport;
using Shouldly;
using Xunit;

namespace Inventory.Api.Tests;

[Collection(nameof(IntegrationCollection))]
public sealed class StockEndpointsTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private HttpClient Admin()
    {
        HttpClient client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.PermissionsHeader, InventoryPermissions.Admin);
        return client;
    }

    [Fact]
    public async Task Setting_stock_creates_the_row_and_stages_the_level()
    {
        using HttpClient client = Admin();
        var product = Guid.CreateVersion7();

        HttpResponseMessage put = await client.PutAsJsonAsync(
            $"/v1/inventory/stock/{product}", new { onHand = 12 }, TestContext.Current.CancellationToken);

        put.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        StockDto? read = await client.GetFromJsonAsync<StockDto>(
            $"/v1/inventory/stock/{product}", TestContext.Current.CancellationToken);
        read.ShouldNotBeNull();
        read.Available.ShouldBe(12);
        read.Reserved.ShouldBe(0);
        (await fixture.OutboxAsync())
            .Count(r => r.MessageType.Contains("StockLevelChanged", StringComparison.Ordinal))
            .ShouldBe(1, "§3.2's Publishes column, through §9.3's allow-list");
    }

    [Fact]
    public async Task A_stock_take_below_the_reserved_count_is_refused_with_422()
    {
        using HttpClient client = Admin();
        var product = Guid.CreateVersion7();
        await fixture.ExecuteAsync(
            "INSERT INTO inventory.StockItems (ProductId, Available, Reserved, UpdatedAt) " +
            "VALUES ({0}, 1, 4, SYSDATETIMEOFFSET())",
            product);

        HttpResponseMessage put = await client.PutAsJsonAsync(
            $"/v1/inventory/stock/{product}", new { onHand = 3 }, TestContext.Current.CancellationToken);

        put.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task An_empty_body_is_400_and_resets_nothing()
    {
        using HttpClient client = Admin();
        var product = Guid.CreateVersion7();
        await client.PutAsJsonAsync(
            $"/v1/inventory/stock/{product}", new { onHand = 4 }, TestContext.Current.CancellationToken);

        HttpResponseMessage put = await client.PutAsJsonAsync(
            $"/v1/inventory/stock/{product}", new { }, TestContext.Current.CancellationToken);

        put.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "an omitted count binds null and NotNull refuses it");
        (await client.GetFromJsonAsync<StockDto>(
            $"/v1/inventory/stock/{product}", TestContext.Current.CancellationToken))!.Available.ShouldBe(4);
    }

    [Fact]
    public async Task Two_first_writes_for_one_product_leave_one_row_and_no_500()
    {
        using HttpClient client = Admin();
        var product = Guid.CreateVersion7();

        HttpResponseMessage[] responses = await Task.WhenAll(
            client.PutAsJsonAsync(
                $"/v1/inventory/stock/{product}", new { onHand = 5 }, TestContext.Current.CancellationToken),
            client.PutAsJsonAsync(
                $"/v1/inventory/stock/{product}", new { onHand = 7 }, TestContext.Current.CancellationToken));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.NoContent,
            "the second waited on the first's key-range lock and loaded its committed row; neither met the key");
        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM inventory.StockItems WHERE ProductId = {0}", product))
            .ShouldBe(1);
    }

    [Fact]
    public async Task An_unknown_product_reads_404()
    {
        using HttpClient client = Admin();

        HttpResponseMessage get = await client.GetAsync(
            $"/v1/inventory/stock/{Guid.CreateVersion7()}", TestContext.Current.CancellationToken);

        get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Without_the_admin_permission_both_endpoints_answer_403()
    {
        HttpClient client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());

        (await client.GetAsync($"/v1/inventory/stock/{Guid.CreateVersion7()}", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.PutAsJsonAsync(
            $"/v1/inventory/stock/{Guid.CreateVersion7()}", new { onHand = 1 }, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
