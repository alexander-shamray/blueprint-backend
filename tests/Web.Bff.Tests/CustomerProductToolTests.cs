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

/// <summary>ADR-151's tools as the core admits them, and <c>get_product</c> over a real gRPC server (§9.7).</summary>
public sealed class CustomerProductToolTests : IAsyncLifetime
{
    private static readonly Guid Chair = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly ToolContext Context =
        new(new Caller(Guid.CreateVersion7().ToString(), "customer"), "conversation", []);

    private const string CatalogUnavailable = """{"error":"catalog_unavailable"}""";

    private readonly StubCatalog _catalog = new();

    private BffFactory _factory = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _catalog.InitializeAsync();

        _factory = new BffFactory { PricingAddress = _catalog.Address };
        _catalog.Prices[Chair] = ("Chair", 49.99m, "GBP");
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _catalog.DisposeAsync();
    }

    private CustomerReadTools Tools => _factory.Services.GetRequiredService<CustomerReadTools>();

    [Fact]
    public void Every_tool_is_one_the_read_only_policy_admits()
    {
        Tools.Tools.ShouldNotBeEmpty();
        Tools.Tools.ShouldAllBe(tool => ToolPolicy.ReadOnly.Refusal(tool) == null && tool.Side == ToolSide.Server);
    }

    [Fact]
    public async Task A_product_sold_in_the_currency_answers_its_name_and_price()
    {
        ToolOutcome outcome = await GetProductAsync(Chair, "gbp");

        outcome.Ok.ShouldBeTrue();
        using JsonDocument body = JsonDocument.Parse(outcome.Content);
        body.RootElement.GetProperty("name").GetString().ShouldBe("Chair");
        body.RootElement.GetProperty("price").GetProperty("amount").GetDecimal().ShouldBe(49.99m);
        body.RootElement.GetProperty("price").GetProperty("currency").GetString().ShouldBe("GBP");
    }

    [Fact]
    public async Task A_product_not_sold_in_the_currency_is_a_refusal_the_model_reads()
    {
        ToolOutcome outcome = await GetProductAsync(Chair, "EUR");

        outcome.Ok.ShouldBeFalse();
        outcome.Content.ShouldContain("product_not_offered");
    }

    [Fact]
    public async Task A_reply_that_breaks_the_contract_is_catalog_s_fault_and_not_a_price()
    {
        _catalog.RawAmount = "-1.00";
        ToolOutcome negative = await GetProductAsync(Chair, "GBP");
        _catalog.RawAmount = null;
        _catalog.RawCurrency = "EUR";
        ToolOutcome otherCurrency = await GetProductAsync(Chair, "GBP");
        _catalog.RawCurrency = null;
        _catalog.DuplicateEveryPrice = true;
        ToolOutcome duplicate = await GetProductAsync(Chair, "GBP");

        // §9.7's rule 6: each is a contract violation, so the model is told Catalog failed, never the value.
        negative.Content.ShouldBe(CatalogUnavailable);
        otherCurrency.Content.ShouldBe(CatalogUnavailable);
        duplicate.Content.ShouldBe(CatalogUnavailable);
    }

    [Fact]
    public async Task Catalog_failing_is_a_code_and_never_the_failure_s_own_text()
    {
        _catalog.FailNextWith.Enqueue(StatusCode.Internal);
        _catalog.FailNextWith.Enqueue(StatusCode.Internal);
        _catalog.FailNextWith.Enqueue(StatusCode.Internal);

        ToolOutcome outcome = await GetProductAsync(Chair, "GBP");

        outcome.Ok.ShouldBeFalse();
        outcome.Content.ShouldBe(CatalogUnavailable);
    }

    [Fact]
    public async Task An_input_that_is_not_an_id_and_a_code_is_refused_before_any_call()
    {
        ToolOutcome noId = await Tools.InvokeAsync(
            CustomerReadTools.GetProduct,
            new JsonObject { ["product_id"] = "chair", ["currency"] = "GBP" },
            Context,
            Ct);
        ToolOutcome noCode = await GetProductAsync(Chair, "pounds");

        noId.Content.ShouldContain("product_id_not_a_uuid");
        noCode.Content.ShouldContain("currency_not_an_iso_code");
        _catalog.Calls.ShouldBeEmpty();
    }

    private async Task<ToolOutcome> GetProductAsync(Guid product, string currency) =>
        await Tools.InvokeAsync(
            CustomerReadTools.GetProduct,
            new JsonObject { ["product_id"] = product.ToString(), ["currency"] = currency },
            Context,
            Ct);
}
