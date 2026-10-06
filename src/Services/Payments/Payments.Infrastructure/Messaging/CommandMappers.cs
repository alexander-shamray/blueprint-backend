using Common.Application;
using Common.Contracts.Payments.V1;
using Payments.Application;
using Payments.Application.Intents.AuthorisePayment;

namespace Payments.Infrastructure.Messaging;

/// <summary>§3.2's accepted command at the wire; a contract no placed order makes is refused (§9.8).</summary>
public sealed class AuthorisePaymentMapper : ICommandMessageMapper<AuthorisePayment, AuthorisePaymentCommand>
{
    public AuthorisePaymentCommand Map(AuthorisePayment message)
    {
        // No record will ever exist for it, so the handler would ride the whole redelivery ladder.
        if (message.OrderId == Guid.Empty)
            throw new ContractMappingException($"An empty order id on {nameof(AuthorisePayment)}.");

        // Zero is a valid total, and goes to the provider, whose answer decides.
        if (message.Amount < 0)
            throw new ContractMappingException($"A negative amount on {nameof(AuthorisePayment)}.");

        // Before the amount, whose minor units are the currency's (ADR-067).
        if (message.Currency is not { Length: 3 } || !message.Currency.All(char.IsAsciiLetterUpper))
        {
            throw new ContractMappingException(
                $"A currency that is not three upper-case letters on {nameof(AuthorisePayment)}.");
        }

        // Here, since the cancelled-order path records the amount without converting it to minor units.
        if (message.Amount >= PaymentAmounts.Ceiling ||
            PaymentAmounts.ToMinorUnits(message.Amount, message.Currency) is null)
        {
            throw new ContractMappingException(
                $"An amount beyond what Payments can record or send on {nameof(AuthorisePayment)}.");
        }

        return new AuthorisePaymentCommand(message.OrderId, message.Amount, message.Currency);
    }
}
