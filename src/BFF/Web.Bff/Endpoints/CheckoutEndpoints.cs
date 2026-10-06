using System.Globalization;
using Catalog.Pricing.V1;
using Common.Web;
using FluentValidation;

namespace Web.Bff.Endpoints;

/// <summary>The checkout screen, and the only thing in this host that spends §9.7's hop budget.</summary>
public static class CheckoutEndpoints
{
    public static void MapCheckoutEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app
            .MapGroup("/v1/checkout")
            .WithTags("Checkout")
            // Fail closed at the group (§11.4); §11.2 has every host validate, whatever the gateway checked.
            .RequireAuthorization();

        group
            // POST, and v1 changed in place rather than growing a v2 (ADR-045).
            .MapPost(
                "/quote",
                async (
                    QuoteRequest request,
                    IValidator<QuoteRequest> validator,
                    Pricing.PricingClient pricing,
                    CancellationToken ct) =>
                {
                    // Before the hop, so a request that cannot succeed spends none of §9.7's budget.
                    await validator.ValidateAndThrowAsync(request, ct);

                    // Merged by product, as the order merges the same basket (ADR-045); the reply shows the sum.
                    Dictionary<Guid, int> quantities = [];
                    foreach (QuoteRequestLine line in request.Lines)
                        quantities[line.ProductId] = quantities.GetValueOrDefault(line.ProductId) + line.Quantity;

                    Guid[] requested = [.. quantities.Keys];

                    GetPricesRequest pricesRequest = new() { Currency = request.Currency };
                    pricesRequest.ProductId.AddRange(requested.Select(id => id.ToString()));

                    // No product-count ceiling here: Catalog's GetPricesValidator owns that number (§4.3).
                    GetPricesReply reply = await pricing.GetPricesAsync(pricesRequest, cancellationToken: ct);

                    List<QuoteLine> lines = new(reply.Price.Count);

                    // The reply is checked against the question rather than trusted to have answered it (§9.7).
                    HashSet<Guid> outstanding = [.. requested];

                    foreach (ProductPrice price in reply.Price)
                    {
                        // Invariant culture and no AllowThousands: a wire format has no group separators.
                        if (!decimal.TryParse(
                                price.Amount,
                                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                                CultureInfo.InvariantCulture,
                                out decimal amount))
                        {
                            throw new InvalidOperationException(
                                $"Catalog returned '{price.Amount}' as the price of product " +
                                $"{price.ProductId}, which is not a decimal in the invariant form " +
                                "pricing.proto specifies.");
                        }

                        // Parsed, then refused (§9.7): refusing the sign in the parse would call "-1.00" no decimal.
                        if (amount < 0)
                        {
                            throw new InvalidOperationException(
                                $"Catalog priced product {price.ProductId} at '{price.Amount}', which is " +
                                "negative. pricing.proto states the amount is never negative and Catalog's " +
                                "own Money refuses one, so this is a contract violation rather than a " +
                                "price.");
                        }

                        if (!string.Equals(price.Currency, request.Currency, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException(
                                $"Catalog priced product {price.ProductId} in '{price.Currency}' for a " +
                                $"'{request.Currency}' request. A reply's currency is the amount's own " +
                                "label (pricing.proto), so the two disagreeing is a contract violation " +
                                "rather than a quote.");
                        }

                        Guid pricedProduct = Guid.Parse(price.ProductId);

                        if (!outstanding.Remove(pricedProduct))
                        {
                            throw new InvalidOperationException(
                                $"Catalog priced product {pricedProduct}, which this request either did " +
                                "not ask about or has already been answered. pricing.proto promises one " +
                                "price per product, and a quote that totalled the extra would be wrong " +
                                "in a way only the arithmetic shows.");
                        }

                        // Keyed by the id the reply named, never by position: Catalog may answer in any order.
                        int quantity = quantities[pricedProduct];

                        lines.Add(
                            new QuoteLine(pricedProduct, price.Name, amount, quantity, amount * quantity));
                    }

                    HashSet<Guid> priced = [.. lines.Select(line => line.ProductId)];

                    return Results.Ok(
                        new QuoteResponse(
                            request.Currency,
                            lines,
                            lines.Sum(line => line.LineTotal),
                            [.. requested.Where(id => !priced.Contains(id))]));
                })
            // Prices a basket and writes nothing (§9.7).
            .RetrySafe(RetrySafety.ReadOnly)
            .WithName("Quote");
    }
}
