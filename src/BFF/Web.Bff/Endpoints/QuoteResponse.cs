namespace Web.Bff.Endpoints;

/// <summary>What the order form needs to render, in one response (§10.1): the total is computed here because every
/// client would otherwise compute it, and two clients computing a total is two places to get rounding wrong.</summary>
/// <param name="Total">The sum of every <see cref="QuoteLine.LineTotal"/>, quantities and all (ADR-045).</param>
/// <param name="Unpriced">The products Catalog returned no price for, named rather than silently dropped.</param>
public sealed record QuoteResponse(
    string Currency,
    IReadOnlyList<QuoteLine> Lines,
    decimal Total,
    IReadOnlyList<Guid> Unpriced);

/// <summary>One priced product on the order form, echoing the requested quantity so the reply stands alone.</summary>
/// <param name="LineTotal">Computed here, so the client computes no money.</param>
public sealed record QuoteLine(
    Guid ProductId,
    string Name,
    decimal Amount,
    int Quantity,
    decimal LineTotal);
