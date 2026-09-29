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

        QuoteResponse? quote = await client.Quote(
            "GBP", TestContext.Current.CancellationToken, (Chair, 2), (Desk, 1));

        quote.ShouldNotBeNull();
        quote.Currency.ShouldBe("GBP");
        quote.Lines.Count.ShouldBe(2);

        // 2A + B, not A + B, and the difference is the whole of ADR-045. The
        // quantities are deliberately unequal: at one each, a total that
        // multiplies and one that does not produce identical bytes, so this
        // assertion would pass against the endpoint it replaced.
        quote.Total.ShouldBe(220.48m);
        quote.Unpriced.ShouldBeEmpty();

        // The unit price keeps its meaning and its name — a cart renders
        // "£49.99 each" beside the line total — so both are asserted rather
        // than only the one the total is computed from.
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
            "GBP", TestContext.Current.CancellationToken, (Chair, 3), (Unknown, 2));

        // The assertion that matters is the second: a form that silently drops
        // a line the customer chose is worse than one that says it cannot
        // price it, and "the total is right" is true of both.
        quote.ShouldNotBeNull();
        quote.Total.ShouldBe(149.97m);
        quote.Unpriced.ShouldBe([Unknown]);
    }

    [Fact]
    public async Task A_currency_Catalog_does_not_price_in_leaves_everything_unpriced()
    {
        using HttpClient client = Caller();

        QuoteResponse? quote = await client.Quote(
            "USD", TestContext.Current.CancellationToken, (Chair, 2), (Desk, 1));

        // Catalog stores one price per product and filters rather than
        // converts (pricing.proto), so this is the honest answer and not an
        // error — the BFF must not invent a conversion.
        quote.ShouldNotBeNull();
        quote.Lines.ShouldBeEmpty();
        quote.Total.ShouldBe(0m);
        quote.Unpriced.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_repeated_product_is_merged_and_asked_about_once()
    {
        using HttpClient client = Caller();

        QuoteResponse? quote = await client.Quote(
            "GBP", TestContext.Current.CancellationToken, (Chair, 2), (Chair, 1));

        // Merged, not refused and not deduplicated, because that is what
        // placing the order does with the same basket: PlaceOrderHandler calls
        // a repeated product legitimate in as many words and Order.AddLine
        // merges the lines. A quote that refused what the order accepts is the
        // defect ADR-045 exists to close, pointing the other way — found by
        // Copilot after the first version of this test asserted the refusal.
        //
        // Dropping the duplicate is still wrong and is not what happens: a
        // repeated LINE carries a quantity, so DistinctBy would discard part
        // of the customer's basket. The sum is the whole of it.
        quote.ShouldNotBeNull();

        QuoteLine line = quote.Lines.ShouldHaveSingleItem();
        line.Quantity.ShouldBe(3);
        line.LineTotal.ShouldBe(149.97m);
        quote.Total.ShouldBe(149.97m);

        // Asserted at the wire as well, because the merge's second job is
        // upstream and invisible from the response: it is what keeps a caller
        // from spending Catalog's id ceiling on one product repeated as many
        // times as OrderLimits.MaxLines allows.
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

        // Every LINE here is within the bound and the basket is not, which is
        // the case a per-line rule cannot see. Without the merged check this
        // quoted a basket the order refuses — so OrderLimits.MaxQuantity was
        // not the bound ADR-045 claims it is, on either side.
        await ShouldBeRefusedWithoutAHop(response);
    }

    [Fact]
    public async Task A_quote_with_no_lines_is_refused_without_a_hop()
    {
        using HttpClient client = Caller();

        HttpResponseMessage response = await client.PostQuote(
            "GBP", TestContext.Current.CancellationToken);

        await ShouldBeRefusedWithoutAHop(response);
    }

    [Fact]
    public async Task A_null_line_list_is_a_400_and_not_a_500()
    {
        using HttpClient client = Caller();

        // An explicit JSON "lines": null binds as null. Without Cascade(Stop)
        // in the validator, NotEmpty records its failure and the count
        // predicate then dereferences null — turning a malformed request into
        // a 500. The same guard PlaceOrderValidator carries, for the same
        // reason, asserted through the status because the throw is not
        // observable from out here.
        HttpResponseMessage response = await client.PostQuote(
            new QuoteRequest("GBP", null!), TestContext.Current.CancellationToken);

        await ShouldBeRefusedWithoutAHop(response);
    }

    [Fact]
    public async Task A_null_line_is_a_400_and_not_a_500()
    {
        using HttpClient client = Caller();

        // A JSON "lines": [null] binds as a list holding a null: the compiler's
        // non-nullable element type is not a deserialisation constraint, and
        // nothing in System.Text.Json enforces one. The null then reaches the
        // duplicate-product predicate, which projects ProductId off every
        // element — so a malformed request became a 500 before RuleForEach ever
        // saw it. Found by Copilot on this pull request.
        //
        // Separate from the null-LIST case above, because the two fail in
        // different rules and a guard on one does nothing for the other.
        HttpResponseMessage response = await client.PostQuote(
            new QuoteRequest("GBP", [null!]), TestContext.Current.CancellationToken);

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
            currency, TestContext.Current.CancellationToken, (Chair, 1));

        // The trailing-newline case is the one with a reason beyond
        // completeness, and it is why the rule is anchored with \z rather than
        // $: .NET's $ matches before a trailing newline, so "GBP\n" satisfies a
        // $-anchored pattern and would reach Catalog as a currency label no row
        // carries. PlaceOrderValidator makes the same choice for the same
        // reason and has had cases for it since it was written; this rule had
        // none until Copilot pointed out that every quote test sends GBP or
        // USD.
        await ShouldBeRefusedWithoutAHop(response);
    }


    [Fact]
    public async Task A_quantity_of_none_is_refused_without_a_hop()
    {
        using HttpClient client = Caller();

        HttpResponseMessage response = await client.PostQuote(
            "GBP", TestContext.Current.CancellationToken, (Chair, 0));

        await ShouldBeRefusedWithoutAHop(response);
    }

    [Fact]
    public async Task A_quantity_past_the_ceiling_is_refused_without_a_hop()
    {
        using HttpClient client = Caller();

        HttpResponseMessage response = await client.PostQuote(
            "GBP", TestContext.Current.CancellationToken, (Chair, OrderLimits.MaxQuantity + 1));

        // The bound is Ordering's, and that is the point rather than a
        // convenience: a quote that priced this basket would hand the customer
        // a number for an order PlaceOrderValidator refuses, and move the
        // refusal from the cart screen to the checkout screen.
        await ShouldBeRefusedWithoutAHop(response);
    }

    [Fact]
    public async Task A_quantity_at_the_ceiling_is_priced()
    {
        using HttpClient client = Caller();

        // The boundary from below. Without it the rule could be off by one in
        // the strict direction and only the rejection test would notice —
        // which it would not, because it asserts a failure either way.
        QuoteResponse? quote = await client.Quote(
            "GBP", TestContext.Current.CancellationToken, (Chair, OrderLimits.MaxQuantity));

        quote.ShouldNotBeNull();
        quote.Total.ShouldBe(49.99m * OrderLimits.MaxQuantity);
    }

    [Fact]
    public async Task A_quantity_that_overflows_the_merge_is_refused_without_a_hop()
    {
        using HttpClient client = Caller();

        // int.MaxValue twice. Enumerable.Sum over int is CHECKED, so the
        // merged-quantity rule threw OverflowException before the per-line
        // rule could report either quantity as invalid — turning a malformed
        // body into a 500, which is the same shape of defect as the null
        // element two tests up and arrived in the fix for it. Found by
        // Copilot.
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

        // The rule is PER PRODUCT, and nothing else here pins that half of it.
        // Every other quantity case uses one product, so a mistaken
        // basket-wide sum — the same predicate without its GroupBy — passes
        // all of them while refusing this, which is a legitimate basket.
        // Found by Copilot, which reached it by mutating the rule rather than
        // by reading the tests.
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
            [.. Enumerable
                .Range(0, OrderLimits.MaxLines + 1)
                .Select(_ => (Guid.CreateVersion7(), 1))];

        HttpResponseMessage response = await client.PostQuote(
            "GBP", TestContext.Current.CancellationToken, lines);

        // Refused here rather than by Catalog, and the two are different
        // bounds that happen to agree today: this one is on the request's own
        // size and is checked before the hop, where GetPricesValidator's is on
        // how many ids one price query may carry. Were Catalog's raised, this
        // would still refuse — because the ORDER would.
        await ShouldBeRefusedWithoutAHop(response);
    }

    [Fact]
    public async Task A_quote_at_the_line_ceiling_is_priced_in_one_hop()
    {
        (Guid ProductId, int Quantity)[] lines =
            [.. Enumerable
                .Range(0, OrderLimits.MaxLines)
                .Select(i =>
                {
                    Guid id = Guid.CreateVersion7();
                    _catalog.Prices[id] = ($"Product {i}", 1.00m, "GBP");

                    return (id, 1);
                })];

        using HttpClient client = Caller();

        QuoteResponse? quote = await client.Quote("GBP", TestContext.Current.CancellationToken, lines);

        // The boundary from below again, and the last assertion is the one
        // that would catch a ceiling enforced by batching rather than by
        // refusing: §9.7 budgets ONE synchronous hop, not one per hundred.
        quote.ShouldNotBeNull();
        quote.Lines.Count.ShouldBe(OrderLimits.MaxLines);
        quote.Total.ShouldBe(OrderLimits.MaxLines * 1.00m);
        _catalog.Calls.Count.ShouldBe(1);
    }

    /// <summary>
    /// Both halves of every refusal above. The second is the one worth
    /// stating: a request that cannot produce anything must not spend the
    /// pricing hop finding that out (§9.7).
    /// </summary>
    private async Task ShouldBeRefusedWithoutAHop(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        // Field-keyed, which is what makes this a ValidationProblemDetails
        // rather than a bare problem response: the client can put the message
        // beside the input that caused it. Results.Problem could not, and that
        // is the reason the endpoint throws rather than returning (ADR-045).
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("errors");

        _catalog.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_anonymous_caller_is_challenged_and_never_reaches_Catalog()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.PostQuote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        // §11.2: this host validates its own tokens, whatever the gateway in
        // front of it did. The group fails closed, so the refusal comes from
        // RequireAuthorization rather than from anything the endpoint does.
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // And problem+json, not an empty body — §10.5's promise covers the two
        // statuses a client meets first, which is what UseStatusCodePages is
        // in the pipeline for. Asserting the status alone would pass just as
        // happily against no body at all.
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        _catalog.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_upstream_refusal_is_the_callers_400_rather_than_the_hosts_500()
    {
        _catalog.FailNextWith.Enqueue(StatusCode.InvalidArgument);

        using HttpClient client = Caller();

        HttpResponseMessage response = await client.PostQuote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        // Catalog refused a request the BFF built out of the caller's own
        // basket, so the caller is who has to change something. Without
        // UpstreamExceptionHandler this is a 500, which sends them to read
        // another service's logs for a mistake in what they sent.
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

        // ONE call, and this test was written expecting three. The count is
        // the finding: an HTTP resilience pipeline cannot retry a gRPC status,
        // because a gRPC status travels as an HTTP 200 with grpc-status in the
        // trailers. UpstreamRetryTests is where both halves of that are
        // measured and argued.
        _catalog.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_reply_priced_in_another_currency_stays_a_500()
    {
        _catalog.RawCurrency = "USD";

        using HttpClient client = Caller();

        HttpResponseMessage response = await client.PostQuote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        // Without this check the endpoint totalled a USD amount and labelled
        // the quote GBP, because the response's currency came from the REQUEST
        // rather than from the price. pricing.proto echoes the currency so each
        // amount is self-describing, and nothing was reading it.
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task A_price_for_a_product_nobody_asked_about_stays_a_500()
    {
        _catalog.Prices[Desk] = ("Desk", 120.50m, "GBP");
        _catalog.AlsoAnswerWith.Add(Desk);

        using HttpClient client = Caller();

        HttpResponseMessage response = await client.PostQuote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        // Untrusted, the Desk would have been priced and added to a total the
        // caller never asked for — and Unpriced would not show it, because that
        // is computed from what came back rather than from what was requested.
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task A_product_priced_twice_stays_a_500()
    {
        _catalog.DuplicateEveryPrice = true;

        using HttpClient client = Caller();

        HttpResponseMessage response = await client.PostQuote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        // The second copy would have been added and totalled, doubling the
        // quote while every id in it was one the caller asked for — the
        // failure mode with no visible symptom but the arithmetic.
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task A_malformed_upstream_amount_stays_a_500()
    {
        _catalog.RawAmount = "12,50";

        using HttpClient client = Caller();

        HttpResponseMessage response = await client.PostQuote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        // A contract violation between two services is nobody's caller's
        // fault, and answering 400 would tell the client to fix a request that
        // was correct. This is also the assertion that would catch a
        // culture-sensitive parse: "12,50" is a valid decimal under a
        // comma-decimal locale, so a host that dropped InvariantCulture would
        // answer 200 with a hundredfold price.
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task A_negative_upstream_amount_stays_a_500()
    {
        _catalog.RawAmount = "-12.50";

        using HttpClient client = Caller();

        HttpResponseMessage response = await client.PostQuote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        // "-12.50" parses perfectly well, which is why this needs its own
        // assertion rather than riding on the one above: the failure is a
        // VALID decimal carrying an invalid value, and it reaches the total
        // and subtracts from it. Catalog's Money.Of refuses a negative, so
        // nothing well-behaved sends one — and a producer's invariant is not
        // a consumer's guarantee, which is the whole reason this boundary
        // reads the currency too.
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }
}
