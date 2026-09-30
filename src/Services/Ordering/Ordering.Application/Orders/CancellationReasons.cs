using System.Collections.Frozen;
using Common.Contracts.Ordering.V1;
using Ordering.Domain.Orders;

namespace Ordering.Application.Orders;

/// <summary>The wire vocabulary for <see cref="CancellationReason"/>, read from <see cref="CancelReasons"/>.</summary>
/// <remarks>An unknown code is refused, not defaulted: from a sibling it is a deployment problem (§11.4).</remarks>
public static class CancellationReasons
{
    private static readonly FrozenDictionary<string, CancellationReason> ByCode =
        new Dictionary<string, CancellationReason>(StringComparer.Ordinal)
        {
            [CancelReasons.OutOfStock] = CancellationReason.OutOfStock,
            [CancelReasons.StockTimeout] = CancellationReason.StockTimeout,
            [CancelReasons.PaymentDeclined] = CancellationReason.PaymentDeclined,
            [CancelReasons.PaymentTimeout] = CancellationReason.PaymentTimeout,
            [CancelReasons.CustomerRequest] = CancellationReason.CustomerRequest
        }.ToFrozenDictionary();

    public static bool TryParse(string? code, out CancellationReason reason) =>
        ByCode.TryGetValue(code ?? "", out reason);

    // The reverse, for the wire's Reason code, inverted from the map above rather than written twice.
    private static readonly FrozenDictionary<CancellationReason, string> ToCodeMap =
        ByCode.ToFrozenDictionary(p => p.Value, p => p.Key);

    public static string ToCode(CancellationReason reason) => ToCodeMap[reason];
}

/// <summary>The wire vocabulary for <see cref="CancellationOrigin"/>, one way: no ingress accepts one.</summary>
public static class CancellationOrigins
{
    private static readonly FrozenDictionary<CancellationOrigin, string> ToCodeMap =
        new Dictionary<CancellationOrigin, string>
        {
            [CancellationOrigin.User] = CancelOrigins.User,
            [CancellationOrigin.Workflow] = CancelOrigins.Workflow
        }.ToFrozenDictionary();

    public static string ToCode(CancellationOrigin origin) => ToCodeMap[origin];
}
