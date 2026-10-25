using System.Text.Json;
using System.Text.Json.Nodes;
using Common.Assistant;
using Common.Assistant.Tools;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Web.Bff.Assistant;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>ADR-151's order tools over ADR-051's projection: the caller's own orders, and nobody else's.</summary>
[Collection(nameof(BffIntegrationCollection))]
public sealed class CustomerOrderToolTests(BffServiceFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private readonly Guid _buyer = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CustomerReadTools Tools => fixture.Factory.Services.GetRequiredService<CustomerReadTools>();

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_owner_reads_the_order_through_both_tools()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Placed(order, _buyer, At));

        ToolOutcome list = await InvokeAsync(_buyer, CustomerReadTools.ListOrders, []);
        ToolOutcome detail = await InvokeAsync(_buyer, CustomerReadTools.GetOrder, OrderInput(order));

        list.Ok.ShouldBeTrue();
        using JsonDocument page = JsonDocument.Parse(list.Content);
        page.RootElement.GetProperty("items").EnumerateArray().ShouldHaveSingleItem()
            .GetProperty("orderId").GetGuid().ShouldBe(order);
        detail.Ok.ShouldBeTrue();
        using JsonDocument body = JsonDocument.Parse(detail.Content);
        body.RootElement.GetProperty("orderId").GetGuid().ShouldBe(order);
    }

    [Fact]
    public async Task Another_customer_s_order_answers_exactly_as_an_unknown_one_does()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Placed(order, _buyer, At));
        Guid stranger = Guid.CreateVersion7();

        ToolOutcome theirs = await InvokeAsync(stranger, CustomerReadTools.GetOrder, OrderInput(order));
        ToolOutcome unknown =
            await InvokeAsync(stranger, CustomerReadTools.GetOrder, OrderInput(Guid.CreateVersion7()));
        ToolOutcome list = await InvokeAsync(stranger, CustomerReadTools.ListOrders, []);

        theirs.ShouldBe(unknown);
        theirs.Ok.ShouldBeFalse();
        using JsonDocument page = JsonDocument.Parse(list.Content);
        page.RootElement.GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    private static JsonObject OrderInput(Guid order) => new() { ["order_id"] = order.ToString() };

    private async Task<ToolOutcome> InvokeAsync(Guid customer, string tool, JsonObject input) =>
        await Tools.InvokeAsync(
            tool,
            input,
            new ToolContext(new Caller(customer.ToString(), "customer"), "conversation", []),
            Ct);
}
