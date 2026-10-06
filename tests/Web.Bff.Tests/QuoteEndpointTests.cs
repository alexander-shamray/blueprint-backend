using System.Net;
using Common.Contracts.Ordering.V1;
using System.Net.Http.Json;
using Grpc.Core;
using Shouldly;
using Web.Bff.TestSupport;
using Web.Bff.Endpoints;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>
/// The BFF's one screen, driven end to end over a real gRPC server (§9.7).
/// </summary>
public sealed class QuoteEndpointTests : IAsyncLifetime
{
    private static readonly Guid Chair = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Desk = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Unknown = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly StubCatalog _catalog = new();

    private BffFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        await _catalog.InitializeAsync();

        _factory = new BffFactory { PricingAddress = _catalog.Address };
        _catalog.Prices[Chair] = ("Chair", 49.99m, "GBP");
        _catalog.Prices[Desk] = ("Desk", 120.50m, "GBP");
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _catalog.DisposeAsync();
    }

    private HttpClient Caller()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "customer-1");

        return client;
    }

    [Fact]
    public async Task A_quote_prices_every_product_and_totals_the_basket()
    {
        using HttpClient client = Caller();

        QuoteResponse? quote = await client.Quote("GBP", TestContext.Current.CancellationToken, (Chair, 2), (Desk, 1));

        quote.ShouldNotBeNull();
        quote.Currency.ShouldBe("GBP");
        quote.Lines.Count.ShouldBe(2);

        // Unequal quantities, since at one each a total that ignored them would match (ADR-045).
        quote.Total.ShouldBe(220.48m);
        quote.Unpriced.ShouldBeEmpty();

        // The unit price too, as a cart shows it beside the line total.
        QuoteLine chair = quote.Lines.Single(line => line.ProductId == Chair);
        chair.Amount.ShouldBe(49.99m);
        chair.Quantity.ShouldBe(2);
        chair.LineTotal.ShouldBe(99.98m);
    }

    [Fact]
    public async Task A_product_with_no_price_is_named_rather_than_dropped()
    {
        using HttpClient client = Caller();

        QuoteResponse? quote = await client.Quote(
            "GBP",
            TestContext.Current.CancellationToken,
            (Chair, 3),
            (Unknown, 2));

        // The last assertion matters, as a dropped line leaves the total right.
        quote.ShouldNotBeNull();
        quote.Total.ShouldBe(149.97m);
        quote.Unpriced.ShouldBe([Unknown]);
    }

    [Fact]
    public async Task A_currency_Catalog_does_not_price_in_leaves_everything_unpriced()
    {
        using HttpClient client = Caller();

        QuoteResponse? quote = await client.Quote("USD", TestContext.Current.CancellationToken, (Chair, 2), (Desk, 1));

        // Catalog filters rather than converts (pricing.proto), so this is an answer, not an error.
        quote.ShouldNotBeNull();
        quote.Lines.ShouldBeEmpty();
        quote.Total.ShouldBe(0m);
        quote.Unpriced.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_repeated_product_is_merged_and_asked_about_once()
    {
        using HttpClient client = Caller();

        QuoteResponse? quote = await client.Quote("GBP", TestContext.Current.CancellationToken, (Chair, 2), (Chair, 1));

        // Merged and summed, as placing the order merges a repeated product (ADR-045).
        quote.ShouldNotBeNull();

        QuoteLine line = quote.Lines.ShouldHaveSingleItem();
        line.Quantity.ShouldBe(3);
        line.LineTotal.ShouldBe(149.97m);
        quote.Total.ShouldBe(149.97m);

        // At the wire too, so one repeated product cannot spend Catalog's id ceiling.
        _catalog.Calls.Single().ProductIds.ShouldBe([Chair.ToString()]);
    }

    [Fact]
    public async Task A_merged_quantity_past_the_ceiling_is_refused_without_a_hop()
    {
        using HttpClient client = Caller();

        HttpResponseMessage response = await client.PostQuote(
            "GBP",
            TestContext.Current.CancellationToken,
            (Chair, OrderLimits.MaxQuantity),
            (Chair, 1));

        // Each line is within the bound and the basket is not, which a per-line rule cannot see.
        await ShouldBeRefusedWithoutAHop(response);
    }

    [Fact]
    public async Task A_quote_with_no_lines_is_refused_without_a_hop()
    {
        using HttpClient client = Caller();

        HttpResponseMessage response = await client.PostQuote("GBP", TestContext.Current.CancellationToken);

        await ShouldBeRefusedWithoutAHop(response);
    }

    [Fact]
    public async Task A_null_line_list_is_a_400_and_not_a_500()
    {
        using HttpClient client = Caller();

        HttpResponseMessage response = await client.PostQuote(
            new QuoteRequest("GBP", null!),
            TestContext.Current.CancellationToken);

        await ShouldBeRefusedWithoutAHop(response);
    }

    [Fact]
    public async Task A_null_line_is_a_400_and_not_a_500()
    {
        using HttpClient client = Caller();

        // Apart from the null list, as the two fail in different rules.
        HttpResponseMessage response = await client.PostQuote(
            new QuoteRequest("GBP", [null!]),
            TestContext.Current.CancellationToken);

        await ShouldBeRefusedWithoutAHop(response);
    }

    [Theory]
    [InlineData("")]
    [InlineData("GB")]
    [InlineData("GBPP")]
    [InlineData("GB1")]
    [InlineData("GBP\n")]
    public async Task A_currency_that_is_not_three_letters_is_refused_without_a_hop(string currency)
    {
        using HttpClient client = Caller();

        HttpResponseMessage response = await client.PostQuote(
            currency,
            TestContext.Current.CancellationToken,
            (Chair, 1));

        await ShouldBeRefusedWithoutAHop(response);
    }


    [Fact]
    public async Task A_quantity_of_none_is_refused_without_a_hop()
    {
        using HttpClient client = Caller();

        HttpResponseMessage response = await client.PostQuote("GBP", TestContext.Current.CancellationToken, (Chair, 0));

        await ShouldBeRefusedWithoutAHop(response);
    }

    [Fact]
    public async Task A_quantity_past_the_ceiling_is_refused_without_a_hop()
    {
        using HttpClient client = Caller();

        HttpResponseMessage response = await client.PostQuote(
            "GBP",
            TestContext.Current.CancellationToken,
            (Chair, OrderLimits.MaxQuantity + 1));

        // Ordering's bound, so the cart never prices an order PlaceOrderValidator refuses.
        await ShouldBeRefusedWithoutAHop(response);
    }

    [Fact]
    public async Task A_quantity_at_the_ceiling_is_priced()
    {
        using HttpClient client = Caller();

        // The boundary from below, where an off-by-one the rejection test cannot see would show.
        QuoteResponse? quote = await client.Quote(
            "GBP",
            TestContext.Current.CancellationToken,
            (Chair, OrderLimits.MaxQuantity));

        quote.ShouldNotBeNull();
        quote.Total.ShouldBe(49.99m * OrderLimits.MaxQuantity);
    }

    [Fact]
    public async Task A_quantity_that_overflows_the_merge_is_refused_without_a_hop()
    {
        using HttpClient client = Caller();

        HttpResponseMessage response = await client.PostQuote(
            "GBP",
            TestContext.Current.CancellationToken,
            (Chair, int.MaxValue),
            (Chair, int.MaxValue));

        await ShouldBeRefusedWithoutAHop(response);
    }

    [Fact]
    public async Task Two_products_at_the_quantity_ceiling_are_both_priced()
    {
        using HttpClient client = Caller();

        // Per product, which a basket-wide sum would refuse while every one-product case passed.
        QuoteResponse? quote = await client.Quote(
            "GBP",
            TestContext.Current.CancellationToken,
            (Chair, OrderLimits.MaxQuantity),
            (Desk, OrderLimits.MaxQuantity));

        quote.ShouldNotBeNull();
        quote.Lines.Count.ShouldBe(2);
        quote.Total.ShouldBe(170319.51m);
    }

    [Fact]
    public async Task A_quote_past_the_line_ceiling_is_refused_without_a_hop()
    {
        using HttpClient client = Caller();

        (Guid ProductId, int Quantity)[] lines =
        [
            .. Enumerable
                .Range(0, OrderLimits.MaxLines + 1)
                .Select(_ => (Guid.CreateVersion7(), 1))
        ];

        HttpResponseMessage response = await client.PostQuote("GBP", TestContext.Current.CancellationToken, lines);

        // The order's own bound, before the hop; GetPricesValidator's is a separate one (ADR-045).
        await ShouldBeRefusedWithoutAHop(response);
    }

    [Fact]
    public async Task A_quote_at_the_line_ceiling_is_priced_in_one_hop()
    {
        (Guid ProductId, int Quantity)[] lines =
        [
            .. Enumerable
                .Range(0, OrderLimits.MaxLines)
                .Select(i =>
                {
                    Guid id = Guid.CreateVersion7();
                    _catalog.Prices[id] = ($"Product {i}", 1.00m, "GBP");

                    return (id, 1);
                })
        ];

        using HttpClient client = Caller();

        QuoteResponse? quote = await client.Quote("GBP", TestContext.Current.CancellationToken, lines);

        // One call, as §9.7 budgets one synchronous hop rather than one per batch.
        quote.ShouldNotBeNull();
        quote.Lines.Count.ShouldBe(OrderLimits.MaxLines);
        quote.Total.ShouldBe(OrderLimits.MaxLines * 1.00m);
        _catalog.Calls.Count.ShouldBe(1);
    }

    /// <summary>A 400 that never spent the pricing hop (§9.7).</summary>
    private async Task ShouldBeRefusedWithoutAHop(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        // Field-keyed errors, which is why the endpoint throws rather than returning a problem (ADR-045).
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("errors");

        _catalog.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_anonymous_caller_is_challenged_and_never_reaches_Catalog()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.PostQuote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        // This host validates its own tokens, whatever the gateway did (§11.2).
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // A body too, which §10.5's promise reaches through UseStatusCodePages.
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        _catalog.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_upstream_refusal_is_the_callers_400_rather_than_the_hosts_500()
    {
        _catalog.FailNextWith.Enqueue(StatusCode.InvalidArgument);

        using HttpClient client = Caller();

        HttpResponseMessage response = await client.PostQuote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        // Catalog refused what the BFF built from the caller's basket, so the caller must change something.
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task An_upstream_outage_is_503()
    {
        _catalog.FailNextWith.Enqueue(StatusCode.Unavailable);

        using HttpClient client = Caller();

        HttpResponseMessage response = await client.PostQuote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        // One call, as an HTTP pipeline cannot retry a gRPC status riding an HTTP 200 (§9.7).
        _catalog.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_reply_priced_in_another_currency_stays_a_500()
    {
        _catalog.RawCurrency = "USD";

        using HttpClient client = Caller();

        HttpResponseMessage response = await client.PostQuote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        // pricing.proto echoes the currency so each amount describes itself.
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task A_price_for_a_product_nobody_asked_about_stays_a_500()
    {
        _catalog.Prices[Desk] = ("Desk", 120.50m, "GBP");
        _catalog.AlsoAnswerWith.Add(Desk);

        using HttpClient client = Caller();

        HttpResponseMessage response = await client.PostQuote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        // An unasked price would join the total unseen by Unpriced, which is computed from the reply.
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task A_product_priced_twice_stays_a_500()
    {
        _catalog.DuplicateEveryPrice = true;

        using HttpClient client = Caller();

        HttpResponseMessage response = await client.PostQuote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        // A second copy would double the total with every id one the caller asked for.
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task A_malformed_upstream_amount_stays_a_500()
    {
        _catalog.RawAmount = "12,50";

        using HttpClient client = Caller();

        HttpResponseMessage response = await client.PostQuote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        // A contract violation is not the caller's fault, and "12,50" parses under a comma-decimal culture.
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task A_negative_upstream_amount_stays_a_500()
    {
        _catalog.RawAmount = "-12.50";

        using HttpClient client = Caller();

        HttpResponseMessage response = await client.PostQuote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        // A valid decimal with an invalid value; Catalog refusing negatives is no guarantee to this consumer.
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }
}
