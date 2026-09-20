using Common.Application;
using Payments.Application.Provider;
using Payments.Domain.Intents;
using Payments.Domain.Orders;
using Payments.Domain.Refunds;

namespace Payments.Application.Orders.RecordOrderCancelled;

/// <summary>
/// Records the cancellation and voids an authorisation it finds (§3.2, §9.6).
/// </summary>
/// <remarks>
/// The stamp goes first, and its lock is held to commit: an
/// <c>AuthorisePayment</c> for the same order locks the same row, so whichever
/// commits second sees the first's work — a decline with nothing to void, or
/// an authorisation this voids (spec, section 6; ADR-047).
/// </remarks>
public sealed class RecordOrderCancelledHandler(
    IPaymentOrderStore orders,
    IPaymentIntentRepository intents,
    IRefundRepository refunds,
    IPaymentProvider provider,
    TimeProvider clock)
    : ICommandHandler<RecordOrderCancelledCommand, Result>
{
    public async Task<Result> HandleAsync(RecordOrderCancelledCommand command, CancellationToken ct)
    {
        OrderId order = new(command.OrderId);

        await orders.RecordCancelledAsync(order, command.CancelledAt, ct);

        PaymentIntent? intent = await intents.GetAsync(order, ct);
        if (intent is not { Status: PaymentIntentStatus.Authorised } || await refunds.ExistsAsync(order, ct))
            return Result.Success();

        // The void key makes a retry of this whole unit a replay at the
        // provider rather than a second void (spec, section 4).
        await provider.VoidAsync(new VoidRequest(order, intent.Reference!), ct);
        refunds.Add(Refund.Voided(intent, clock.GetUtcNow()));

        return Result.Success();
    }
}
