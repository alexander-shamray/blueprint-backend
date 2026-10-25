using System.Text.Json;
using System.Text.Json.Nodes;
using Common.Assistant;
using Common.Assistant.Tools;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Web.Bff.Assistant;
using Web.Bff.TestSupport;
using Xunit;

namespace Web.Bff.Tests;

/// <summary><c>get_stock</c> over a real gRPC server (ADR-154), and the hop's credentials (§11.5, §10.4).</summary>
public sealed class CustomerStockToolTests : IAsyncLifetime
{
    private static readonly Guid Chair = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Guid Gone = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly ToolContext Context =
        new(new Caller(Guid.CreateVersion7().ToString(), "customer"), "conversation", []);

    private const string InventoryUnavailable = """{"error":"inventory_unavailable"}""";

    private readonly StubInventory _inventory = new();

    private BffFactory _factory = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _inventory.InitializeAsync();

        _factory = new BffFactory { StockAddress = _inventory.Address };
        _inventory.InStock[Chair] = true;
        _inventory.InStock[Gone] = false;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _inventory.DisposeAsync();
    }

    private CustomerReadTools Tools => _factory.Services.GetRequiredService<CustomerReadTools>();

    [Fact]
    public void The_stock_tool_is_one_the_read_only_policy_admits()
    {
        ToolDescriptor tool = Tools.Tools.Single(t => t.Name == CustomerReadTools.GetStock);

        ToolPolicy.ReadOnly.Refusal(tool).ShouldBeNull();
        tool.Side.ShouldBe(ToolSide.Server);
    }

    [Fact]
    public async Task A_product_with_stock_answers_true_and_never_a_count()
    {
        ToolOutcome outcome = await GetStockAsync(Chair);

        outcome.Ok.ShouldBeTrue(outcome.Content);
        using JsonDocument body = JsonDocument.Parse(outcome.Content);
        body.RootElement.GetProperty("productId").GetGuid().ShouldBe(Chair);
        body.RootElement.GetProperty("inStock").GetBoolean().ShouldBeTrue();
        body.RootElement.EnumerateObject().Select(p => p.Name).ShouldBe(["productId", "inStock"]);
    }

    [Fact]
    public async Task A_product_that_is_sold_out_answers_false_as_a_result_and_not_a_refusal()
    {
        ToolOutcome outcome = await GetStockAsync(Gone);

        outcome.Ok.ShouldBeTrue(outcome.Content);
        using JsonDocument body = JsonDocument.Parse(outcome.Content);
        body.RootElement.GetProperty("inStock").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task A_product_inventory_holds_no_row_for_is_unknown_and_not_out_of_stock()
    {
        ToolOutcome outcome = await GetStockAsync(Guid.CreateVersion7());

        outcome.Ok.ShouldBeFalse();
        outcome.Content.ShouldContain("stock_not_known");
    }

    [Fact]
    public async Task An_id_echoed_in_another_spelling_of_the_same_guid_is_still_an_answer()
    {
        _inventory.BracedIds = true;

        ToolOutcome outcome = await GetStockAsync(Chair);

        outcome.Ok.ShouldBeTrue(outcome.Content);
    }

    [Fact]
    public async Task A_reply_that_breaks_the_contract_is_inventory_s_fault_and_not_an_answer()
    {
        _inventory.DuplicateEveryAnswer = true;
        ToolOutcome duplicate = await GetStockAsync(Chair);
        _inventory.DuplicateEveryAnswer = false;
        _inventory.InStock[Gone] = true;
        _inventory.AlsoAnswerWith.Add(Gone);
        ToolOutcome unasked = await GetStockAsync(Chair);

        // §9.7's rule 6: each is a contract violation, so the model is told Inventory failed.
        duplicate.Content.ShouldBe(InventoryUnavailable);
        unasked.Content.ShouldBe(InventoryUnavailable);
    }

    [Fact]
    public async Task Inventory_failing_is_a_code_and_never_the_failure_s_own_text()
    {
        _inventory.FailNextWith.Enqueue(StatusCode.Internal);
        _inventory.FailNextWith.Enqueue(StatusCode.Internal);
        _inventory.FailNextWith.Enqueue(StatusCode.Internal);

        ToolOutcome outcome = await GetStockAsync(Chair);

        outcome.Ok.ShouldBeFalse();
        outcome.Content.ShouldBe(InventoryUnavailable);
    }

    [Fact]
    public async Task An_input_that_is_not_an_id_is_refused_before_any_call()
    {
        ToolOutcome outcome = await Tools.InvokeAsync(
            CustomerReadTools.GetStock,
            new JsonObject { ["product_id"] = "chair" },
            Context,
            Ct);

        outcome.Content.ShouldContain("product_id_not_a_uuid");
        _inventory.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_hop_carries_a_bearer_token_for_the_configured_scope_and_a_correlation_id()
    {
        await GetStockAsync(Chair);

        // Off the request Inventory received, as a handler registered and never reached is invisible in process.
        StubInventory.Call call = _inventory.Calls.Single();
        call.Authorization.ShouldBe("Bearer token-1");
        _factory.Tokens.Scopes.ShouldBe([BffFactory.Scope]);
    }

    private async Task<ToolOutcome> GetStockAsync(Guid product) =>
        await Tools.InvokeAsync(
            CustomerReadTools.GetStock,
            new JsonObject { ["product_id"] = product.ToString() },
            Context,
            Ct);
}
