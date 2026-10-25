using System.Text.Json;
using System.Text.Json.Nodes;
using Common.Assistant;
using Common.Assistant.Tools;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Web.Bff.Assistant;
using Web.Bff.TestSupport;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>ADR-023's consumer half: each <see cref="StockContract"/> entry driven through <c>get_stock</c>.</summary>
/// <remarks>
/// The tool asks about one product per call (ADR-154), so the stub's replies are gathered into the one reply the
/// provider suite sees; the BFF's own message types are Web.Bff's alone, and no test names them (CS0433).
/// </remarks>
public sealed class StockContractTests : IAsyncLifetime
{
    private static readonly ToolContext Context =
        new(new Caller(Guid.CreateVersion7().ToString(), "customer"), "conversation", []);

    private readonly StubInventory _inventory = new();

    private BffFactory _factory = null!;

    public static TheoryData<string> Interactions => [.. StockContract.Descriptions];

    public async ValueTask InitializeAsync()
    {
        await _inventory.InitializeAsync();

        _factory = new BffFactory { StockAddress = _inventory.Address };
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _inventory.DisposeAsync();
    }

    [Theory]
    [MemberData(nameof(Interactions))]
    public async Task The_stub_answers_what_the_contract_promises(string description)
    {
        StockInteraction interaction = StockContract.Named(description);
        IReadOnlyDictionary<string, Guid> published = _inventory.Publish(interaction);

        await AskAboutEachAsync(interaction, published);

        // The provider run's own verification, so the stub is an Inventory the real one could be (ADR-023).
        StockContract.Verify(interaction, published, _inventory.GatheredReply());
    }

    [Theory]
    [MemberData(nameof(Interactions))]
    public async Task The_tool_tells_the_model_what_the_contract_implies(string description)
    {
        StockInteraction interaction = StockContract.Named(description);
        IReadOnlyDictionary<string, Guid> published = _inventory.Publish(interaction);

        IReadOnlyList<(Guid Product, ToolOutcome Outcome)> outcomes = await AskAboutEachAsync(interaction, published);

        foreach ((Guid product, ToolOutcome outcome) in outcomes)
        {
            (string Alias, bool InStock)[] answered =
                [.. interaction.Then.Where(entry => published[entry.Alias] == product)];

            if (answered.Length == 0)
            {
                // Unknown is not sold out: a refusal the model reads, never a false it would tell a customer.
                outcome.Ok.ShouldBeFalse();
                outcome.Content.ShouldContain("stock_not_known");
                continue;
            }

            outcome.Ok.ShouldBeTrue(outcome.Content);
            using JsonDocument body = JsonDocument.Parse(outcome.Content);
            body.RootElement.GetProperty("inStock").GetBoolean().ShouldBe(answered.Single().InStock);
        }
    }

    private async Task<IReadOnlyList<(Guid Product, ToolOutcome Outcome)>> AskAboutEachAsync(
        StockInteraction interaction,
        IReadOnlyDictionary<string, Guid> published)
    {
        CustomerReadTools tools = _factory.Services.GetRequiredService<CustomerReadTools>();
        List<(Guid, ToolOutcome)> outcomes = [];

        foreach (Guid product in StockContract.RequestedIds(interaction, published))
        {
            ToolOutcome outcome = await tools.InvokeAsync(
                CustomerReadTools.GetStock,
                new JsonObject { ["product_id"] = product.ToString() },
                Context,
                TestContext.Current.CancellationToken);

            outcomes.Add((product, outcome));
        }

        return outcomes;
    }
}
