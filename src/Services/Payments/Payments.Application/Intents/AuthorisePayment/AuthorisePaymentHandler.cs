using Common.Application;
using Microsoft.Extensions.Logging;
using Payments.Application.Orders;
using Payments.Application.Provider;
using Payments.Domain.Intents;
using Payments.Domain.Orders;

namespace Payments.Application.Intents.AuthorisePayment;

public sealed class AuthorisePaymentHandler(
    IPaymentOrderStore orders,
    IPaymentIntentRepository intents,
    IPaymentProvider provider,
    TimeProvider clock,
    ILogger<AuthorisePaymentHandler> log)
    : ICommandHandler<AuthorisePaymentCommand, Result>
{
    // §13.4's own example of an Information line: a business event worth an
    // audit trail. Compiled once (CA1848, ADR-019). The order and the
    // provider's reference, never the payer: the subject is not the audit's.
    private static readonly Action<ILogger, Guid, string, Exception?> Authorised =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Information,
            new EventId(1, nameof(Authorised)),
            "Payment authorised for order {OrderId} as {Reference}.");

    public async Task<Result> HandleAsync(AuthorisePaymentCommand command, CancellationToken ct)
    {
        OrderId order = new(command.OrderId);

        // First, and held to commit: the cancellation's stamp waits behind this
        // lock, so it cannot land between the check below and the charge
        // (spec, section 6; ADR-049).
        PaymentOrderRecord? record = await orders.LockAsync(order, ct);

        PaymentIntent? existing = await intents.GetAsync(order, ct);
        if (existing is not null)
        {
            // A resend is answered only when it asks for what was decided: a
            // fresh command with other money must not inherit an authorisation.
            if (command.Amount != existing.Amount ||
                !string.Equals(command.Currency, existing.Currency, StringComparison.Ordinal))
            {
                throw new PaymentMismatchException(
                    Mismatch(order, command, existing.Amount, existing.Currency, "the recorded payment"));
            }

            // Acknowledged, not answered again: the verdict was staged with the
            // intent and reaches the saga regardless, and a second
            // PaymentAuthorised is not idempotent there (spec, section 6).
            return Result.Success();
        }

        // Money first, whenever there are figures to compare: a cancelled order
        // that was placed still holds its total, and a command disagreeing
        // with it is a fault, not a customer-facing decline. A tombstone has no
        // figures, so it has nothing to disagree with.
        if (record is { IsPlaced: true } &&
            (command.Amount != record.TotalAmount ||
                !string.Equals(command.Currency, record.Currency, StringComparison.Ordinal)))
        {
            throw new PaymentMismatchException(
                Mismatch(order, command, record.TotalAmount, record.Currency, "the placed order"));
        }

        if (record is { IsCancelled: true })
        {
            intents.Add(PaymentIntent.Decline(
                order, command.Amount, command.Currency, DeclineReasons.OrderCancelled, clock.GetUtcNow()));
            return Result.Success();
        }

        // §3.2: a missing record is a wait, not a decline. Thrown, so the
        // endpoint's delayed redelivery takes it (spec, section 8).
        if (record is not { IsPlaced: true })
            throw new PaymentOrderNotYetKnownException($"No OrderPlaced has reached Payments for {order}.");

        AuthorisationResult verdict = await provider.AuthoriseAsync(
            new AuthorisationRequest(order, record.CustomerId!.Value, command.Amount, command.Currency), ct);

        if (verdict is AuthorisationResult.Authorised authorised)
            Authorised(log, order.Value, authorised.Reference, null);

        DateTimeOffset now = clock.GetUtcNow();
        intents.Add(verdict switch
        {
            AuthorisationResult.Authorised a =>
                PaymentIntent.Authorise(order, command.Amount, command.Currency, a.Reference, now),
            AuthorisationResult.Declined d =>
                PaymentIntent.Decline(order, command.Amount, command.Currency, d.Reason, now),
            _ => throw new InvalidOperationException($"Unknown verdict {verdict.GetType().Name}.")
        });

        return Result.Success();
    }

    // Both fields, both sides: the error queue is read by a person deciding
    // whether the sender or the record is wrong.
    private static string Mismatch(
        OrderId order, AuthorisePaymentCommand command, decimal? amount, string? currency, string against) =>
        $"AuthorisePayment for {order} asks for {command.Amount} {command.Currency}; " +
        $"{against} holds {amount} {currency}.";
}
