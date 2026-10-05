using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Common.Application;
using Shouldly;
using Web.Bff.Orders;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>§10.7's routes through the real host: the subject from the principal, the 404, the wire's names.</summary>
[Collection(nameof(BffIntegrationCollection))]
public sealed class OrderEndpointTests(BffServiceFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private readonly Guid _buyer = Guid.CreateVersion7();

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private HttpClient As(Guid subject)
    {
        HttpClient client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, subject.ToString());

        return client;
    }

    [Fact]
    public async Task An_anonymous_caller_is_challenged_on_both_routes()
    {
        using HttpClient anonymous = fixture.Factory.CreateClient();

        (await anonymous.GetAsync("/v1/orders", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync($"/v1/orders/{Guid.CreateVersion7()}", Ct)).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_owner_reads_the_order_on_both_routes()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Placed(order, _buyer, At));
        using HttpClient client = As(_buyer);

        CursorPage<OrderSummary>? page =
            await client.GetFromJsonAsync<CursorPage<OrderSummary>>("/v1/orders", Ct);
        OrderDetail? detail = await client.GetFromJsonAsync<OrderDetail>($"/v1/orders/{order}", Ct);

        page.ShouldNotBeNull().Items.ShouldHaveSingleItem().OrderId.ShouldBe(order);
        detail.ShouldNotBeNull().Status.ShouldBe(BuyerStatuses.Placed);
        detail.Cancellable.ShouldBeTrue();
    }

    [Fact]
    public async Task Another_buyer_s_order_answers_exactly_as_an_unknown_one_does()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Placed(order, _buyer, At));
        using HttpClient stranger = As(Guid.CreateVersion7());

        HttpResponseMessage theirs = await stranger.GetAsync($"/v1/orders/{order}", Ct);
        HttpResponseMessage unknown = await stranger.GetAsync($"/v1/orders/{Guid.CreateVersion7()}", Ct);

        theirs.StatusCode.ShouldBe(HttpStatusCode.NotFound, "403 would confirm the order exists (§10.7)");
        theirs.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        (await Problem(theirs)).ShouldBe(await Problem(unknown));
        (await stranger.GetFromJsonAsync<CursorPage<OrderSummary>>("/v1/orders", Ct))!.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_wire_carries_section_10_7_s_names_and_the_list_carries_no_detail_members()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Placed(order, _buyer, At));
        using HttpClient client = As(_buyer);

        using JsonDocument page = JsonDocument.Parse(await client.GetStringAsync("/v1/orders", Ct));
        using JsonDocument detail = JsonDocument.Parse(await client.GetStringAsync($"/v1/orders/{order}", Ct));

        Names(page.RootElement).ShouldBe(["items", "nextCursor"]);
        JsonElement listed = page.RootElement.GetProperty("items")[0];
        Names(listed).ShouldBe(
            ["asOf", "cancellable", "lines", "orderId", "refunded", "refundedAt", "status", "timeline", "total"]);
        Names(listed.GetProperty("timeline")).ShouldBe(["cancelled", "confirmed", "delivered", "dispatched", "placed"]);
        Names(listed.GetProperty("lines")[0]).ShouldBe(["lineTotal", "productId", "productName"]);
        Names(listed.GetProperty("total")).ShouldBe(["amount", "currency"]);

        Names(detail.RootElement).ShouldContain("payment");
        Names(detail.RootElement).ShouldContain("shipment");
        Names(detail.RootElement.GetProperty("lines")[0]).ShouldBe(
            ["lineTotal", "productId", "productName", "quantity", "unitPrice"]);
        detail.RootElement.GetProperty("status").GetString().ShouldBe("placed");
    }

    [Fact]
    public async Task A_limit_past_the_ceiling_is_clamped_rather_than_refused()
    {
        for (int i = 0; i <= OrderPage.MaxLimit; i++)
            await fixture.DeliverAsync(OrderEvents.Placed(Guid.CreateVersion7(), _buyer, At.AddSeconds(i)));

        using HttpClient client = As(_buyer);
        CursorPage<OrderSummary>? page =
            await client.GetFromJsonAsync<CursorPage<OrderSummary>>("/v1/orders?limit=1000", Ct);

        page.ShouldNotBeNull().Items.Count.ShouldBe(OrderPage.MaxLimit);
        page.NextCursor.ShouldNotBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task A_limit_of_zero_or_less_is_clamped_to_one_at_the_host(int limit)
    {
        await fixture.DeliverAsync(OrderEvents.Placed(Guid.CreateVersion7(), _buyer, At));
        await fixture.DeliverAsync(OrderEvents.Placed(Guid.CreateVersion7(), _buyer, At.AddSeconds(1)));

        using HttpClient client = As(_buyer);
        CursorPage<OrderSummary>? page =
            await client.GetFromJsonAsync<CursorPage<OrderSummary>>($"/v1/orders?limit={limit}", Ct);

        page.ShouldNotBeNull().Items.ShouldHaveSingleItem();
        page.NextCursor.ShouldNotBeNull("the second order is the next page");
    }

    [Fact]
    public async Task Both_routes_carry_the_row_s_own_as_of_instant()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Placed(order, _buyer, At));
        DateTimeOffset written = (await fixture.OrderAsync(order)).ShouldNotBeNull().AsOf;
        using HttpClient client = As(_buyer);

        CursorPage<OrderSummary>? page = await client.GetFromJsonAsync<CursorPage<OrderSummary>>("/v1/orders", Ct);
        OrderDetail? detail = await client.GetFromJsonAsync<OrderDetail>($"/v1/orders/{order}", Ct);

        page.ShouldNotBeNull().Items.ShouldHaveSingleItem().AsOf.ShouldBe(written);
        detail.ShouldNotBeNull().AsOf.ShouldBe(written, "the BFF's clock at the row's last write, not the read's");
    }

    [Fact]
    public async Task An_edited_cursor_gets_the_first_page_as_every_list_s_does()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Placed(order, _buyer, At));
        using HttpClient client = As(_buyer);

        CursorPage<OrderSummary>? page =
            await client.GetFromJsonAsync<CursorPage<OrderSummary>>("/v1/orders?cursor=not-a-cursor", Ct);

        page.ShouldNotBeNull().Items.ShouldHaveSingleItem().OrderId.ShouldBe(order);
    }

    [Fact]
    public async Task Neither_route_spends_the_hop_budget()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Placed(order, _buyer, At));
        using HttpClient client = As(_buyer);
        int before = fixture.Factory.Tokens.Issued;

        await client.GetStringAsync("/v1/orders", Ct);
        await client.GetStringAsync($"/v1/orders/{order}", Ct);

        // The pricing client's credential handler asks for a token on every attempt, so none asked is no call (§9.7).
        fixture.Factory.Tokens.Issued.ShouldBe(before, "the read makes no synchronous call (ADR-051)");
    }

    [Fact]
    public async Task A_malformed_id_is_refused_as_not_found_and_never_as_a_fault()
    {
        using HttpClient client = As(_buyer);

        HttpResponseMessage response = await client.GetAsync("/v1/orders/not-a-guid", Ct);

        // The {id:guid} constraint leaves no route to match, so the status-code pages answer, not the handler.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    private static async Task<(int Status, string? Code, string? Detail)> Problem(HttpResponseMessage response)
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        JsonElement root = body.RootElement;

        return (root.GetProperty("status").GetInt32(), root.GetProperty("code").GetString(),
            root.GetProperty("detail").GetString());
    }

    private static string[] Names(JsonElement element) =>
        [.. element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
