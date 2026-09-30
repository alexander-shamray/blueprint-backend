using System.Net;
using System.Net.Http.Json;
using Shouldly;
using Web.Bff.Endpoints;
using Web.Bff.TestSupport;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>ADR-023's consumer half: each <see cref="PricingContract"/> entry driven through the screen.</summary>
/// <remarks>The ceiling refusal no longer reaches the stub, and stays as Catalog still owes it (ADR-045).</remarks>
public sealed class PricingContractTests : IAsyncLifetime
{
    private readonly StubCatalog _catalog = new();

    private BffFactory _factory = null!;

    public static TheoryData<string> Answered => [.. PricingContract.Answered];

    public static TheoryData<string> Refused => [.. PricingContract.Refusals];

    public async ValueTask InitializeAsync()
    {
        await _catalog.InitializeAsync();

        _factory = new BffFactory { PricingAddress = _catalog.Address };
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _catalog.DisposeAsync();
    }

    [Theory]
    [MemberData(nameof(Answered))]
    public async Task The_stub_answers_what_the_contract_promises(string description)
    {
        PricingInteraction interaction = PricingContract.Named(description);
        IReadOnlyDictionary<string, Guid> published = _catalog.Publish(interaction);

        using HttpClient client = Caller();
        await client.PostQuote(interaction.Currency, TestContext.Current.CancellationToken, Basket(interaction, published));

        // The provider run's own verification, so the stub is a Catalog the real one could be (ADR-023).
        PricingContract.Verify(interaction, published, _catalog.Replies.ShouldHaveSingleItem());
    }

    [Theory]
    [MemberData(nameof(Answered))]
    public async Task The_quote_is_the_one_the_contract_implies(string description)
    {
        PricingInteraction interaction = PricingContract.Named(description);
        IReadOnlyDictionary<string, Guid> published = _catalog.Publish(interaction);
        PricingOutcome.Priced priced = (PricingOutcome.Priced)interaction.Then;

        using HttpClient client = Caller();
        QuoteResponse? quote = await client.Quote(
            interaction.Currency, TestContext.Current.CancellationToken, Basket(interaction, published));

        Guid[] expected = [.. priced.Aliases.Select(alias => published[alias])];

        quote.ShouldNotBeNull();
        quote.Currency.ShouldBe(interaction.Currency);
        quote.Lines.Select(line => line.ProductId).ShouldBe(expected, ignoreOrder: true);
        quote.Total.ShouldBe(priced.Aliases.Sum(alias => PricingContract.Product(interaction, alias).Amount));

        // Asked about and not priced is named rather than dropped, as QuoteResponse.Unpriced promises.
        quote.Unpriced.ShouldBe(
            [.. PricingContract.RequestedIds(interaction, published).Where(id => !expected.Contains(id))],
            ignoreOrder: true);
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_refusal_the_contract_promises_reaches_the_caller_as_a_bad_request(string description)
    {
        PricingInteraction interaction = PricingContract.Named(description);
        IReadOnlyDictionary<string, Guid> published = _catalog.Publish(interaction);

        using HttpClient client = Caller();
        using HttpResponseMessage response = await client.PostQuote(
            interaction.Currency,
            TestContext.Current.CancellationToken,
            Basket(interaction, published));

        // QuoteRequestValidator refuses it before the hop since ADR-045, so Catalog is never asked.
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _catalog.Calls.ShouldBeEmpty();
    }

    private HttpClient Caller()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "customer-1");

        return client;
    }

    /// <summary>The interaction as a basket of one each, so the endpoint builds the request (§12.6).</summary>
    private static (Guid ProductId, int Quantity)[] Basket(
        PricingInteraction interaction,
        IReadOnlyDictionary<string, Guid> published) =>
        [.. PricingContract
            .RequestedIds(interaction, published)
            .Select(id => (id, 1))];
}
