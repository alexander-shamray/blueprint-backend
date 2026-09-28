using System.Globalization;
using Catalog.Pricing.V1;
using FluentValidation;

namespace Web.Bff.Endpoints;

/// <summary>
/// The BFF's one screen, and the only thing in this host that spends §9.7's
/// hop budget.
/// </summary>
/// <remarks>
/// <c>/v1/checkout</c> rather than a resource path, because the gateway routes
/// <c>/bff</c> and <c>/api</c> as two namespaces and a client picks one or the
/// other (§10.2): aggregated responses shaped for a screen, or the service APIs
/// shaped for a resource. A caller reaches this at
/// <c>/bff/v1/checkout/quote</c>; the gateway strips <c>/bff</c> and this host
/// sees the version, exactly as a service sees one under <c>/api</c>.
/// </remarks>
public static class CheckoutEndpoints
{
    public static void MapCheckoutEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app
            .MapGroup("/v1/checkout")
            .WithTags("Checkout")
            // Fail closed at the group, the shape §11.4 uses everywhere. The
            // gateway's web-bff route already carries the "authenticated"
            // policy, and this is not redundant with it: §11.2 requires every
            // host to validate its own tokens, because anything reaching this
            // one by another path — a port-forward, a network policy somebody
            // widened — arrives with no edge in front of it.
            .RequireAuthorization();

        group
            // POST, and v1 changed in place rather than growing a v2
            // (ADR-045). QuoteRequest argues the verb; the version is the
            // narrower call: the response change is additive, the request
            // change is breaking, and the one known consumer lands with it. A
            // /v2 whose /v1 has no remaining callers is a second code path
            // maintained to serve nobody.
            .MapPost(
                "/quote",
                async (
                    QuoteRequest request,
                    IValidator<QuoteRequest> validator,
                    Pricing.PricingClient pricing,
                    CancellationToken ct) =>
                {
                    // Before the hop, always. A request that cannot produce
                    // anything must not spend a synchronous hop finding that
                    // out (§9.7) — and the throw is how the 400 gets its field
                    // keys, because Common.Web's ValidationExceptionHandler
                    // turns a ValidationException into §10.5's
                    // ValidationProblemDetails.
                    await validator.ValidateAndThrowAsync(request, ct);

                    // Merged by product, first appearance ordered. This is
                    // the replacement for the Distinct() that used to stand
                    // here, and it is a different operation: Distinct() on a
                    // set of ids discarded nothing, where DistinctBy on a list
                    // of QUANTIFIED lines would discard part of the customer's
                    // basket. Summing is what Order.AddLine does with the same
                    // basket one service over, and a quote that answered a
                    // different question from the order it quotes for is the
                    // defect ADR-045 exists to close.
                    //
                    // It is not silent: the reply echoes Quantity per line, so
                    // a caller that sent two lines for one product gets back
                    // one line carrying their sum and can see the merge.
                    Dictionary<Guid, int> quantities = [];
                    foreach (QuoteRequestLine line in request.Lines)
                        quantities[line.ProductId] = quantities.GetValueOrDefault(line.ProductId) + line.Quantity;

                    // The merged ids, which is what Catalog is asked about —
                    // so a repeated product still costs one id against its
                    // ceiling rather than one per line.
                    Guid[] requested = [.. quantities.Keys];

                    GetPricesRequest pricesRequest = new() { Currency = request.Currency };
                    pricesRequest.ProductId.AddRange(requested.Select(id => id.ToString()));

                    // No PRODUCT-COUNT ceiling checked here, deliberately, and
                    // the line ceiling the validator holds is not a second copy
                    // of it. Catalog's GetPricesValidator owns how many ids one
                    // price query may carry and answers InvalidArgument past
                    // it, which UpstreamExceptionHandler turns into the 400 the
                    // caller deserves; a second copy of THAT limit in this host
                    // would drift from the one actually enforced. What the
                    // validator bounds is this request's own size, before the
                    // hop, at the number the order will later insist on.
                    GetPricesReply reply = await pricing.GetPricesAsync(pricesRequest, cancellationToken: ct);

                    List<QuoteLine> lines = new(reply.Price.Count);

                    // The reply is checked against the question rather than
                    // trusted to have answered it. pricing.proto promises one
                    // price per product and QuoteResponse repeats the promise;
                    // until this set existed the endpoint took the reply's word
                    // for both, so an id nobody asked about was priced and
                    // totalled, and an id answered twice was totalled twice.
                    // Neither appears in Unpriced — that is computed from what
                    // came back — so the only symptom was a wrong Total.
                    HashSet<Guid> outstanding = [.. requested];

                    foreach (ProductPrice price in reply.Price)
                    {
                        // InvariantCulture on both sides of the wire, matching
                        // what PricingService formats with. Left to the ambient
                        // culture, a host running under a comma-decimal locale
                        // parses "12.50" as 1250 — a hundredfold error that
                        // appears in production and never on the machine that
                        // wrote the code.
                        //
                        // And NOT NumberStyles.Number, which is the obvious
                        // choice and was the first one written here. It
                        // includes AllowThousands, so "12,50" parses under the
                        // INVARIANT culture as twelve hundred and fifty — the
                        // exact hundredfold error the invariant culture was
                        // supposed to rule out, arriving through the styles
                        // argument instead. A wire format has no group
                        // separators, so neither does this parse. Caught by a
                        // test that expected a 500 and got a 200.
                        if (!decimal.TryParse(
                                price.Amount,
                                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                                CultureInfo.InvariantCulture,
                                out decimal amount))
                        {
                            // A contract violation between two services, not a
                            // caller's mistake, so it stays a 500. Naming the
                            // product is what makes it findable; the value is
                            // Catalog's own price and not a secret.
                            throw new InvalidOperationException(
                                $"Catalog returned '{price.Amount}' as the price of product " +
                                $"{price.ProductId}, which is not a decimal in the invariant form " +
                                "pricing.proto specifies.");
                        }

                        // AllowLeadingSign is kept and the sign is refused
                        // HERE, one step later, on purpose. Dropping the style
                        // would make "-1.00" fail the parse above and answer
                        // "which is not a decimal in the invariant form" — a
                        // false statement about a string that plainly is one,
                        // and it would refuse a legal "+12.50" as well.
                        //
                        // Catalog's Money.Of already refuses a negative, so
                        // nothing well-behaved sends one. That is exactly the
                        // argument the currency check below declines to make:
                        // a producer's invariant is not a consumer's guarantee,
                        // and this is the boundary where the difference is
                        // paid. Unchecked, a negative line reaches the quote's
                        // total and subtracts from it.
                        if (amount < 0)
                        {
                            throw new InvalidOperationException(
                                $"Catalog priced product {price.ProductId} at '{price.Amount}', which is " +
                                "negative. pricing.proto states the amount is never negative and Catalog's " +
                                "own Money refuses one, so this is a contract violation rather than a " +
                                "price.");
                        }

                        // The reply says which currency each amount is in, and
                        // until this check nothing read it — so a Catalog that
                        // answered USD for a GBP request would have had the
                        // amount totalled and labelled GBP by this endpoint.
                        // pricing.proto gives the field precisely so a price is
                        // self-describing; ignoring it made the response a
                        // claim about the REQUEST rather than about the money.
                        //
                        // A contract violation between two services, like the
                        // malformed amount above, so it stays a 500 rather than
                        // blaming the caller.
                        if (!string.Equals(price.Currency, request.Currency, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException(
                                $"Catalog priced product {price.ProductId} in '{price.Currency}' for a " +
                                $"'{request.Currency}' request. A reply's currency is the amount's own " +
                                "label (pricing.proto), so the two disagreeing is a contract violation " +
                                "rather than a quote.");
                        }

                        Guid pricedProduct = Guid.Parse(price.ProductId);

                        // Removing rather than testing membership: one
                        // operation answers both "was this asked for" and "has
                        // it been answered already", which are the two faults
                        // that end as the same wrong total.
                        if (!outstanding.Remove(pricedProduct))
                        {
                            throw new InvalidOperationException(
                                $"Catalog priced product {pricedProduct}, which this request either did " +
                                "not ask about or has already been answered. pricing.proto promises one " +
                                "price per product, and a quote that totalled the extra would be wrong " +
                                "in a way only the arithmetic shows.");
                        }

                        // The quantity comes from the REQUEST, keyed by the id
                        // the reply named — never from the reply's position in
                        // the list. Catalog is free to answer in any order and
                        // to leave products out, so pairing by index would put
                        // one product's quantity on another's price, which is
                        // the failure the request record refuses two parallel
                        // arrays to avoid. The lookup cannot miss: outstanding
                        // was built from the same ids and has just accepted
                        // this one.
                        int quantity = quantities[pricedProduct];

                        lines.Add(new QuoteLine(
                            pricedProduct,
                            price.Name,
                            amount,
                            quantity,
                            amount * quantity));
                    }

                    // Set-based, so the answer does not depend on the reply's
                    // order and a product Catalog echoed twice cannot make a
                    // requested one look priced.
                    HashSet<Guid> priced = [.. lines.Select(line => line.ProductId)];

                    return Results.Ok(new QuoteResponse(
                        request.Currency,
                        lines,
                        lines.Sum(line => line.LineTotal),
                        [.. requested.Where(id => !priced.Contains(id))]));
                })
            .WithName("Quote");
    }
}
