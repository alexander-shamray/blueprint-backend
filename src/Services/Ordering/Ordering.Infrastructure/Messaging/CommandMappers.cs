using System.Collections.Frozen;
using Common.Application;
using Common.Contracts.Ordering.V1;
using Common.Domain;
using Ordering.Application.Orders;
using Ordering.Application.Orders.CancelOrder;
using Ordering.Application.Orders.ConfirmOrder;
using Ordering.Application.Orders.FlagOrderForReview;
using Ordering.Application.Orders.MarkOrderShipped;
using Ordering.Domain.Orders;

namespace Ordering.Infrastructure.Messaging;

/// <summary>One mapper per command in §3.2's Accepts column: the wire-to-command boundary (§9.4).</summary>
/// <remarks>
/// A failed parse throws <see cref="ContractMappingException"/>, which <c>ordering-commands</c> does not retry
/// (§9.8). Only <see cref="CancelOrderMapper"/> declares an origin: only that command has a second way in (§11.4).
/// </remarks>
public sealed class CancelOrderMapper : ICommandMessageMapper<CancelOrder, CancelOrderCommand>
{
    public CancelOrderCommand Map(CancelOrder message)
    {
        // The same parse the endpoint uses (§11.4), failing differently: a
        // sibling service sending a code we do not know is a deployment
        // problem, and no amount of backoff resolves it.
        if (!CancellationReasons.TryParse(message.Reason, out CancellationReason reason))
        {
            throw new ContractMappingException(
                $"Unknown cancellation reason '{message.Reason}' on {nameof(CancelOrder)}.");
        }

        // CommandOrigin.System, written here and nowhere else. The message
        // carries no origin field, so nothing a peer sends can forge one —
        // arriving on this service's command queue is what earns it (§11.4).
        return new CancelOrderCommand(message.OrderId, reason, CommandOrigin.System);
    }
}

/// <inheritdoc cref="CancelOrderMapper"/>
public sealed class ConfirmOrderMapper : ICommandMessageMapper<ConfirmOrder, ConfirmOrderCommand>
{
    public ConfirmOrderCommand Map(ConfirmOrder message)
    {
        // A malformed contract, not a domain rejection: a refusal would be acked (§9.8), and this belongs in the
        // error queue on the first attempt.
        try
        {
            return new ConfirmOrderCommand(message.OrderId, PaymentReference.Of(message.PaymentReference));
        }
        catch (DomainException e)
        {
            throw new ContractMappingException($"Unusable payment reference on {nameof(ConfirmOrder)}.", e);
        }
    }
}

/// <inheritdoc cref="CancelOrderMapper"/>
public sealed class MarkOrderShippedMapper : ICommandMessageMapper<MarkOrderShipped, MarkOrderShippedCommand>
{
    public MarkOrderShippedCommand Map(MarkOrderShipped message)
    {
        try
        {
            return new MarkOrderShippedCommand(message.OrderId, TrackingNumber.Of(message.TrackingNumber));
        }
        catch (DomainException e)
        {
            throw new ContractMappingException($"Unusable tracking number on {nameof(MarkOrderShipped)}.", e);
        }
    }
}

/// <inheritdoc cref="CancelOrderMapper"/>
public sealed class FlagOrderForReviewMapper
    : ICommandMessageMapper<FlagOrderForReview, FlagOrderForReviewCommand>
{
    /// <summary>The closed vocabulary of <see cref="ReviewReasons"/>, listed so that it can refuse.</summary>
    /// <remarks>
    /// An unknown code would open a second <c>ordering.OrderReviews</c> row nobody resolves (§9.6). A reason
    /// added here reaches an older consumer's error queue on the first attempt, so it ships consumer-first (ADR-026).
    /// </remarks>
    public static readonly FrozenSet<string> Known = FrozenSet.Create(
        StringComparer.Ordinal,
        ReviewReasons.NotDespatched,
        ReviewReasons.StockNotReleased,
        ReviewReasons.PaymentAuthorisedDuringCompensation,
        ReviewReasons.CancelledAfterConfirmation,
        ReviewReasons.NotConfirmed);

    public FlagOrderForReviewCommand Map(FlagOrderForReview message)
    {
        if (!Known.Contains(message.Reason))
        {
            throw new ContractMappingException(
                $"Unknown review reason '{message.Reason}' on {nameof(FlagOrderForReview)}.");
        }

        return new FlagOrderForReviewCommand(message.OrderId, message.Reason);
    }
}
