using Common.Application;
using Common.Contracts.Payments.V1;
using Payments.Application;
using Payments.Application.Intents.AuthorisePayment;

namespace Payments.Infrastructure.Messaging;

/// <summary>
/// The wire-to-command boundary for §3.2's one accepted command. A contract no
/// placed order could have produced is a defect in the sender, refused before
/// the handler and excluded from retry (§9.8).
/// </summary>
public sealed class AuthorisePaymentMapper : ICommandMessageMapper<AuthorisePayment, AuthorisePaymentCommand>
{
    public AuthorisePaymentCommand Map(AuthorisePayment message)
    {
        // Refused here, not left to the handler: no record will ever exist for
        // it, so it would ride the whole redelivery ladder to the error queue.
        if (message.OrderId == Guid.Empty)
            throw new ContractMappingException($"An empty order id on {nameof(AuthorisePayment)}.");

        // Zero is an order's total like any other — Money.Zero is valid in
        // Catalog and Ordering — and goes to the provider, whose answer decides.
        if (message.Amount < 0)
            throw new ContractMappingException($"A negative amount on {nameof(AuthorisePayment)}.");

        // Refused here, before any branch writes it: the cancelled-order path
        // records the amount without ever converting it to minor units, so a
        // bound checked only in the adapter would be a bound one branch skips.
        if (message.Amount >= PaymentAmounts.Ceiling
            || decimal.Round(message.Amount, PaymentAmounts.MinorUnitPlaces) != message.Amount)
        {
            throw new ContractMappingException(
                $"An amount beyond what Payments can record or send on {nameof(AuthorisePayment)}.");
        }

        if (message.Currency is not { Length: 3 } || !message.Currency.All(char.IsAsciiLetterUpper))
        {
            throw new ContractMappingException(
                $"A currency that is not three upper-case letters on {nameof(AuthorisePayment)}.");
        }

        return new AuthorisePaymentCommand(message.OrderId, message.Amount, message.Currency);
    }
}
