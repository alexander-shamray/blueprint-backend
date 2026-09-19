using System.Net;
using System.Net.Http.Json;
using Common.Contracts.Inventory.V1;
using Inventory.Application.Reservations.GetReservation;
using Inventory.TestSupport;
using Shouldly;
using Xunit;

namespace Inventory.Api.Tests;

/// <summary>
/// The routes the runbook promises over <c>Reservation</c>: a read, a
/// release an operator can trigger by hand, and a reinstate that only the
/// runbook calls. The release the queue drives is a separate path through
/// the command mappers; this suite is the admin surface.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ReservationEndpointsTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_runbook_can_read_a_reservation()
    {
        var product = Guid.CreateVersion7();
        await SeedStock(product, 3);
        var order = Guid.CreateVersion7();
        await SendAsync(new ReserveStock(order, [new StockLine(product, 2)]));
        await EventuallyStatus(order, "Reserved");

        using HttpClient client = Admin();
        ReservationDto? dto = await client.GetFromJsonAsync<ReservationDto>(
            $"/v1/inventory/reservations/{order}", TestContext.Current.CancellationToken);

        dto.ShouldNotBeNull();
        dto.Status.ShouldBe("Reserved");
        dto.Lines.ShouldHaveSingleItem().Quantity.ShouldBe(2);
    }

    [Fact]
    public async Task The_runbook_can_release_by_hand_and_the_release_answers_like_the_command()
    {
        var product = Guid.CreateVersion7();
        await SeedStock(product, 3);
        var order = Guid.CreateVersion7();
        await SendAsync(new ReserveStock(order, [new StockLine(product, 2)]));
        await EventuallyStatus(order, "Reserved");

        using HttpClient client = Admin();
        HttpResponseMessage response = await client.PostAsync(
            $"/v1/inventory/reservations/{order}/release", null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Available(product)).ShouldBe(3);
        (await fixture.OutboxAsync())
            .Count(r => r.MessageType.Contains("StockReleased", StringComparison.Ordinal))
            .ShouldBe(1);
    }

    [Fact]
    public async Task Releasing_the_empty_order_id_is_400_and_writes_nothing()
    {
        using HttpClient client = Admin();
        HttpResponseMessage response = await client.PostAsync(
            $"/v1/inventory/reservations/{Guid.Empty}/release", null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await fixture.ScalarAsync<int>(
                "SELECT Value = COUNT(*) FROM inventory.Reservations WHERE OrderId = {0}", Guid.Empty))
            .ShouldBe(0, "no tombstone for an identity no order can have");
    }

    [Fact]
    public async Task Releasing_an_unknown_order_is_204_and_writes_the_tombstone()
    {
        var order = Guid.CreateVersion7();

        using HttpClient client = Admin();
        HttpResponseMessage response = await client.PostAsync(
            $"/v1/inventory/reservations/{order}/release", null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await StatusAsync(order)).ShouldBe("Released");
    }

    [Fact]
    public async Task Reinstating_a_released_reservation_takes_the_stock_again_and_publishes_only_levels()
    {
        var product = Guid.CreateVersion7();
        await SeedStock(product, 3);
        var order = Guid.CreateVersion7();
        await SendAsync(new ReserveStock(order, [new StockLine(product, 2)]));
        await EventuallyStatus(order, "Reserved");
        await SendAsync(new ReleaseStock(order));
        await EventuallyStatus(order, "Released");
        int before = (await fixture.OutboxAsync())
            .Count(r => r.MessageType.Contains("StockReserved", StringComparison.Ordinal));
        int levelsBefore = (await fixture.OutboxAsync())
            .Count(r => r.MessageType.Contains("StockLevelChanged", StringComparison.Ordinal));

        using HttpClient client = Admin();
        HttpResponseMessage response = await client.PostAsync(
            $"/v1/inventory/reservations/{order}/reinstate", null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await StatusAsync(order)).ShouldBe("Reserved");
        (await Available(product)).ShouldBe(1);
        (await fixture.OutboxAsync())
            .Count(r => r.MessageType.Contains("StockReserved", StringComparison.Ordinal))
            .ShouldBe(before, "an operator's act answers no saga");
        (await fixture.OutboxAsync())
            .Count(r => r.MessageType.Contains("StockLevelChanged", StringComparison.Ordinal))
            .ShouldBe(levelsBefore + 1, "the reinstate moved the level again");
    }

    [Fact]
    public async Task Reinstating_a_tombstone_and_reinstating_into_a_shortage_are_both_422_under_their_own_codes()
    {
        using HttpClient client = Admin();
        var order = Guid.CreateVersion7();
        await client.PostAsync(
            $"/v1/inventory/reservations/{order}/release", null, TestContext.Current.CancellationToken);
        HttpResponseMessage tombstone = await client.PostAsync(
            $"/v1/inventory/reservations/{order}/reinstate", null, TestContext.Current.CancellationToken);
        tombstone.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await tombstone.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain("reservation.not_reinstatable");

        var product = Guid.CreateVersion7();
        await SeedStock(product, 2);
        var held = Guid.CreateVersion7();
        await SendAsync(new ReserveStock(held, [new StockLine(product, 2)]));
        await EventuallyStatus(held, "Reserved");
        await SendAsync(new ReleaseStock(held));
        await EventuallyStatus(held, "Released");
        await fixture.ExecuteAsync("UPDATE inventory.StockItems SET Available = 1 WHERE ProductId = {0}", product);

        HttpResponseMessage shortage = await client.PostAsync(
            $"/v1/inventory/reservations/{held}/reinstate", null, TestContext.Current.CancellationToken);
        shortage.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await shortage.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain(product.ToString(), customMessage: "the operator needs to know which product is short");
        (await StatusAsync(held)).ShouldBe("Released");
    }

    [Fact]
    public async Task Two_releases_for_one_unknown_order_at_once_leave_one_tombstone_and_no_500()
    {
        using HttpClient client = Admin();
        var order = Guid.CreateVersion7();

        HttpResponseMessage[] responses = await Task.WhenAll(
            client.PostAsync(
                $"/v1/inventory/reservations/{order}/release", null, TestContext.Current.CancellationToken),
            client.PostAsync(
                $"/v1/inventory/reservations/{order}/release", null, TestContext.Current.CancellationToken));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.NoContent,
            "the second creator waited on the first's key-range lock and found the tombstone");
        (await fixture.ScalarAsync<int>(
                "SELECT Value = COUNT(*) FROM inventory.Reservations WHERE OrderId = {0}", order))
            .ShouldBe(1);
        (await fixture.OutboxAsync())
            .Count(r => r.MessageType.Contains("StockReleased", StringComparison.Ordinal))
            .ShouldBe(2, "ADR-024: both releases answered");
    }

    [Fact]
    public async Task A_release_and_a_reinstate_at_once_end_in_exactly_one_state()
    {
        var product = Guid.CreateVersion7();
        await SeedStock(product, 3);
        var order = Guid.CreateVersion7();
        await SendAsync(new ReserveStock(order, [new StockLine(product, 2)]));
        await EventuallyStatus(order, "Reserved");
        await SendAsync(new ReleaseStock(order));
        await EventuallyStatus(order, "Released");

        using HttpClient client = Admin();
        HttpResponseMessage[] responses = await Task.WhenAll(
            client.PostAsync(
                $"/v1/inventory/reservations/{order}/reinstate", null, TestContext.Current.CancellationToken),
            client.PostAsync(
                $"/v1/inventory/reservations/{order}/release", null, TestContext.Current.CancellationToken));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.NoContent);
        string status = await StatusAsync(order);
        int available = await Available(product);
        (status, available).ShouldBeOneOf(
            ("Reserved", 1),   // the reinstate ran second and re-took the lines
            ("Released", 3));  // the release ran second and gave them back
    }

    [Fact]
    public async Task Every_reservation_endpoint_requires_the_admin_permission()
    {
        HttpClient client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        var order = Guid.CreateVersion7();

        (await client.GetAsync($"/v1/inventory/reservations/{order}", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.PostAsync(
                $"/v1/inventory/reservations/{order}/release", null, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.PostAsync(
                $"/v1/inventory/reservations/{order}/reinstate", null, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private HttpClient Admin()
    {
        HttpClient client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.PermissionsHeader, InventoryPermissions.Admin);
        return client;
    }

    // Thin forwarders that bind this class's fixture once: the private,
    // same-named members ReservationTestSupport expects of a caller.
    private Task SeedStock(Guid product, int available) =>
        ReservationTestSupport.SeedStock(fixture, product, available);

    private Task<int> Available(Guid product) =>
        ReservationTestSupport.Available(fixture, product);

    private Task<string> StatusAsync(Guid orderId) =>
        ReservationTestSupport.StatusAsync(fixture, orderId);

    private Task EventuallyStatus(Guid orderId, string expected) =>
        ReservationTestSupport.EventuallyStatus(fixture, orderId, expected);

    private Task SendAsync<T>(T command, bool drain = true)
        where T : class =>
        ReservationTestSupport.SendAsync(fixture, command, drain);
}
