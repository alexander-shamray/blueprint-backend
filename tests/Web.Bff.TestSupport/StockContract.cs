using Inventory.Stock.V1;

namespace Web.Bff.TestSupport;

/// <summary>What <c>Web.Bff</c> needs of Inventory's availability RPC (ADR-154, ADR-023).</summary>
/// <remarks>
/// The BFF asks about one product at a time (ADR-154), so a request ceiling is no expectation of its own: the
/// provider's refusal past <c>GetAvailabilityValidator.MaxProductIds</c> is that suite's, and no consumer reads it.
/// </remarks>
public static class StockContract
{
    public const string Consumer = "Web.Bff";

    public const string Provider = "Inventory.Api";

    private static readonly ContractStock Stocked = new("stocked", 5);
    private static readonly ContractStock Reserved = new("reserved", 0);

    /// <summary>Every expectation of the provider, each verified on both sides (ADR-045).</summary>
    public static IReadOnlyList<StockInteraction> Interactions { get; } =
    [
        new StockInteraction(
            "a product with unreserved stock is in stock",
            [Stocked],
            ["stocked"],
            0,
            [("stocked", true)]),

        // Available is net of what is reserved, so a product promised in full is not in stock.
        new StockInteraction(
            "a product whose stock is all reserved is not in stock",
            [Reserved],
            ["reserved"],
            0,
            [("reserved", false)]),

        // Unknown and sold out are different facts to tell a customer, so the first has no entry at all.
        new StockInteraction(
            "a product Inventory holds no stock for is absent rather than out of stock",
            [Stocked],
            ["stocked"],
            1,
            [("stocked", true)])
    ];

    /// <summary>The interactions' descriptions, a serialisable datum for both suites' theories.</summary>
    public static IEnumerable<string> Descriptions => Interactions.Select(i => i.Description);

    public static StockInteraction Named(string description) =>
        Interactions.SingleOrDefault(i => i.Description == description) ??
        throw new StockContractException($"No interaction is described as '{description}'.");

    /// <summary>A canonical id no product will have, as Inventory's rows are keyed by version-7 ids.</summary>
    public static Guid UnknownId(int index) => new($"00000000-0000-0000-0000-{index:D12}");

    /// <summary>The ids both sides ask about: the published products, then ids naming nothing (§12.6).</summary>
    public static IReadOnlyList<Guid> RequestedIds(
        StockInteraction interaction,
        IReadOnlyDictionary<string, Guid> published)
    {
        List<Guid> ids = new(interaction.Ask.Count + interaction.PlusUnknownIds);

        foreach (string alias in interaction.Ask)
        {
            if (!published.TryGetValue(alias, out Guid id))
            {
                throw new StockContractException(
                    $"'{interaction.Description}' asks about '{alias}', which the caller did not publish.");
            }

            ids.Add(id);
        }

        for (int index = 1; index <= interaction.PlusUnknownIds; index++)
            ids.Add(UnknownId(index));

        return ids;
    }

    /// <summary>This interaction as a <c>GetAvailabilityRequest</c>, for the provider suite alone (§12.6).</summary>
    public static GetAvailabilityRequest Request(
        StockInteraction interaction,
        IReadOnlyDictionary<string, Guid> published)
    {
        GetAvailabilityRequest request = new();
        request.ProductId.AddRange(RequestedIds(interaction, published).Select(id => id.ToString()));

        return request;
    }

    /// <summary>Throws unless <paramref name="reply"/> is within the consumer's own tolerance (§12.6).</summary>
    public static void Verify(
        StockInteraction interaction,
        IReadOnlyDictionary<string, Guid> published,
        GetAvailabilityReply reply)
    {
        HashSet<Guid> outstanding = [.. RequestedIds(interaction, published)];
        Dictionary<Guid, bool> expected = [];

        foreach ((string alias, bool inStock) in interaction.Then)
            expected.Add(published[alias], inStock);

        foreach (ProductAvailability entry in reply.Availability)
        {
            // Canonical D-form, as stock.proto states: stricter than the tool's own parse on purpose.
            if (!Guid.TryParseExact(entry.ProductId, "D", out Guid productId))
            {
                throw new StockContractException(
                    $"'{interaction.Description}': product_id '{entry.ProductId}' is not a canonical GUID.");
            }

            // Removing answers both "was this asked about" and "has it been answered already".
            if (!outstanding.Remove(productId))
            {
                throw new StockContractException(
                    $"'{interaction.Description}': {productId} was either not asked about or answered twice.");
            }

            if (!expected.TryGetValue(productId, out bool inStock))
            {
                throw new StockContractException(
                    $"'{interaction.Description}': {productId} was answered, and the contract says it is absent.");
            }

            if (entry.InStock != inStock)
            {
                throw new StockContractException(
                    $"'{interaction.Description}': {productId} came back in_stock={entry.InStock}, and the " +
                    $"contract says {inStock}.");
            }
        }

        // A product the reply left out, which the loop over the reply cannot see.
        foreach (Guid productId in expected.Keys)
        {
            if (outstanding.Contains(productId))
            {
                throw new StockContractException(
                    $"'{interaction.Description}': {productId} was not answered, and the contract says it is.");
            }
        }
    }
}

/// <summary>One expectation of the provider, whose description names the test on both sides of the hop.</summary>
/// <param name="Then">Exactly the entries the provider answers, each alias with its flag; no other product.</param>
public sealed record StockInteraction(
    string Description,
    IReadOnlyList<ContractStock> Given,
    IReadOnlyList<string> Ask,
    int PlusUnknownIds,
    IReadOnlyList<(string Alias, bool InStock)> Then);

/// <summary>A stock row the provider holds when an interaction runs, by alias, with its unreserved count.</summary>
public sealed record ContractStock(string Alias, int Available);

/// <summary>A reply or request the contract forbids, as a type both sides can throw without Shouldly.</summary>
public sealed class StockContractException : Exception
{
    public StockContractException()
    {
    }

    public StockContractException(string message)
        : base(message)
    {
    }

    public StockContractException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
