using System.Globalization;
using Catalog.Pricing.V1;
using Grpc.Core;

namespace Web.Bff.TestSupport;

/// <summary>What <c>Web.Bff</c> needs of Catalog's pricing RPC (§9.7, ADR-023).</summary>
public static class PricingContract
{
    public const string Consumer = "Web.Bff";

    public const string Provider = "Catalog.Api";

    /// <summary>The consumer's ceiling, which <c>CheckoutEndpoints</c> holds no copy of (§12.6).</summary>
    public const int MaxProductIds = 100;

    private static readonly ContractProduct Chair = new("chair", "Chair", 49.99m, "GBP");
    private static readonly ContractProduct Desk = new("desk", "Desk", 120.50m, "GBP");
    private static readonly ContractProduct Lamp = new("lamp", "Lamp", 18.00m, "EUR");

    /// <summary>Every expectation of the provider, each verified on both sides but the last (ADR-045).</summary>
    public static IReadOnlyList<PricingInteraction> Interactions { get; } =
    [
        new PricingInteraction(
            "every product the basket names is priced",
            [Chair, Desk],
            ["chair", "desk"],
            0,
            "GBP",
            PricingOutcome.Prices("chair", "desk")),

        // Unpriced is computed from what came back, so an entry of zero would be totalled as free.
        new PricingInteraction(
            "a product priced in another currency is absent rather than zero",
            [Chair, Lamp],
            ["chair", "lamp"],
            0,
            "GBP",
            PricingOutcome.Prices("chair")),

        // A basket from a stale page names withdrawn products, and the rest must still be priced.
        new PricingInteraction(
            "a product Catalog has never heard of is absent rather than an error",
            [Chair],
            ["chair"],
            1,
            "GBP",
            PricingOutcome.Prices("chair")),

        // The currency comes from the caller's own body, so the consumer cannot promise its case.
        new PricingInteraction(
            "a currency spelled in another case prices the same products",
            [Chair],
            ["chair"],
            0,
            "gbp",
            PricingOutcome.Prices("chair")),

        // This and the next bracket the ceiling, so a move in either direction fails verification (ADR-023).
        new PricingInteraction(
            "a basket at the ceiling is served rather than refused",
            [],
            [],
            MaxProductIds,
            "GBP",
            PricingOutcome.Prices()),

        // Catalog still owes it (ADR-045).
        new PricingInteraction(
            "a basket past the ceiling is refused rather than served in part",
            [],
            [],
            MaxProductIds + 1,
            "GBP",
            PricingOutcome.Refused(StatusCode.InvalidArgument))
    ];

    /// <summary>The answered interactions' descriptions, a serialisable datum for both suites' theories.</summary>
    public static IEnumerable<string> Answered =>
        Interactions
            .Where(interaction => interaction.Then is PricingOutcome.Priced)
            .Select(interaction => interaction.Description);

    /// <summary>The refused interactions' descriptions.</summary>
    public static IEnumerable<string> Refusals =>
        Interactions
            .Where(interaction => interaction.Then is PricingOutcome.Refusal)
            .Select(interaction => interaction.Description);

    /// <summary>The interaction with this description, which keeps each theory's name readable (§12.8).</summary>
    public static PricingInteraction Named(string description) =>
        Interactions.SingleOrDefault(i => i.Description == description) ??
        throw new PricingContractException(
            $"No interaction is described as '{description}'.");

    /// <summary>A canonical id no product will have, as Catalog mints only version-7 ids.</summary>
    public static Guid UnknownId(int index) =>
        new($"00000000-0000-0000-0000-{index:D12}");

    /// <summary>The ids both sides ask about: the published products, then ids naming nothing (§12.6).</summary>
    public static IReadOnlyList<Guid> RequestedIds(
        PricingInteraction interaction,
        IReadOnlyDictionary<string, Guid> published)
    {
        List<Guid> ids = new(interaction.Ask.Count + interaction.PlusUnknownIds);

        foreach (string alias in interaction.Ask)
        {
            if (!published.TryGetValue(alias, out Guid id))
            {
                throw new PricingContractException(
                    $"'{interaction.Description}' asks about '{alias}', which the caller did not publish.");
            }

            ids.Add(id);
        }

        for (int index = 1; index <= interaction.PlusUnknownIds; index++)
            ids.Add(UnknownId(index));

        return ids;
    }

    /// <summary>This interaction as a <c>GetPricesRequest</c>, for the provider suite alone (§12.6).</summary>
    public static GetPricesRequest Request(
        PricingInteraction interaction,
        IReadOnlyDictionary<string, Guid> published)
    {
        GetPricesRequest request = new() { Currency = interaction.Currency };
        request.ProductId.AddRange(RequestedIds(interaction, published).Select(id => id.ToString()));

        return request;
    }

    /// <summary>Throws unless <paramref name="reply"/> is within the consumer's own tolerance (§12.6).</summary>
    public static void Verify(
        PricingInteraction interaction,
        IReadOnlyDictionary<string, Guid> published,
        GetPricesReply reply)
    {
        if (interaction.Then is not PricingOutcome.Priced priced)
        {
            throw new PricingContractException(
                $"'{interaction.Description}' expects a refusal, so it has no reply to verify.");
        }

        HashSet<Guid> outstanding = [.. RequestedIds(interaction, published)];
        Dictionary<Guid, ContractProduct> expected = [];

        foreach (string alias in priced.Aliases)
            expected.Add(published[alias], Product(interaction, alias));

        foreach (ProductPrice price in reply.Price)
        {
            // Canonical D-form, as pricing.proto states: stricter than CheckoutEndpoints' Guid.Parse on purpose.
            if (!Guid.TryParseExact(price.ProductId, "D", out Guid productId))
            {
                throw new PricingContractException(
                    $"'{interaction.Description}': product_id '{price.ProductId}' is not a canonical GUID.");
            }

            // Removing answers both "was this asked about" and "has it been answered already".
            if (!outstanding.Remove(productId))
            {
                throw new PricingContractException(
                    $"'{interaction.Description}': {productId} was either not asked about or priced twice.");
            }

            if (!expected.TryGetValue(productId, out ContractProduct? product))
            {
                throw new PricingContractException(
                    $"'{interaction.Description}': {productId} was priced, and the contract says it is absent.");
            }

            // Not NumberStyles.Number, whose AllowThousands reads "12,50" as twelve hundred and fifty.
            if (!decimal.TryParse(
                    price.Amount,
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out decimal amount))
            {
                throw new PricingContractException(
                    $"'{interaction.Description}': '{price.Amount}' is not a decimal in the invariant form " +
                    "pricing.proto specifies.");
            }

            if (amount < 0)
            {
                throw new PricingContractException(
                    $"'{interaction.Description}': {productId} was priced at '{price.Amount}', and " +
                    "pricing.proto states the amount is never negative.");
            }

            if (amount != product.Amount)
            {
                throw new PricingContractException(
                    $"'{interaction.Description}': {productId} was priced at {amount} and was published " +
                    $"at {product.Amount}.");
            }

            if (!string.Equals(price.Currency, interaction.Currency, StringComparison.OrdinalIgnoreCase))
            {
                throw new PricingContractException(
                    $"'{interaction.Description}': {productId} was priced in '{price.Currency}' for a " +
                    $"'{interaction.Currency}' request.");
            }

            if (!string.Equals(price.Name, product.Name, StringComparison.Ordinal))
            {
                throw new PricingContractException(
                    $"'{interaction.Description}': {productId} came back named '{price.Name}' and was " +
                    $"published as '{product.Name}'.");
            }
        }

        // A product the reply left out, which the loop over the reply cannot see.
        foreach ((Guid productId, ContractProduct product) in expected)
        {
            if (outstanding.Contains(productId))
            {
                throw new PricingContractException(
                    $"'{interaction.Description}': '{product.Alias}' ({productId}) was not priced, and " +
                    "the contract says it is.");
            }
        }
    }

    /// <summary>The product this interaction publishes under <paramref name="alias"/>.</summary>
    public static ContractProduct Product(PricingInteraction interaction, string alias) =>
        interaction.Given.SingleOrDefault(p => p.Alias == alias) ??
        throw new PricingContractException(
            $"'{interaction.Description}' names no product '{alias}'.");
}

/// <summary>One expectation of the provider, whose description names the test on both sides of the hop.</summary>
public sealed record PricingInteraction(
    string Description,
    IReadOnlyList<ContractProduct> Given,
    IReadOnlyList<string> Ask,
    int PlusUnknownIds,
    string Currency,
    PricingOutcome Then);

/// <summary>A product the provider holds when an interaction runs, named by alias as each side mints ids.</summary>
public sealed record ContractProduct(string Alias, string Name, decimal Amount, string Currency);

/// <summary>What the consumer needs the provider to do: answer or refuse, and nothing between.</summary>
public abstract record PricingOutcome
{
    private PricingOutcome()
    {
    }

    /// <summary>The provider answers, pricing exactly these aliases and no others.</summary>
    public static PricingOutcome Prices(params string[] aliases) => new Priced([.. aliases]);

    /// <summary>The provider refuses, with this status.</summary>
    public static PricingOutcome Refused(StatusCode status) => new Refusal(status);

    public sealed record Priced(IReadOnlyList<string> Aliases) : PricingOutcome;

    public sealed record Refusal(StatusCode Status) : PricingOutcome;
}

/// <summary>A reply or request the contract forbids, as a type both sides can throw without Shouldly.</summary>
public sealed class PricingContractException : Exception
{
    public PricingContractException()
    {
    }

    public PricingContractException(string message)
        : base(message)
    {
    }

    public PricingContractException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
