using Common.Domain;

namespace Catalog.Domain.Common;

/// <summary>§5.3's always-valid value object, local to Catalog since §4.1 rejects a shared kernel.</summary>
/// <remarks><c>default(Money)</c> has a null <see cref="Currency"/>, so aggregates guard against it.</remarks>
public readonly record struct Money
{
    public decimal Amount { get; }

    public string Currency { get; }

    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public static Money Of(decimal amount, string currency)
    {
        if (amount < 0)
            throw new DomainException("Money cannot be negative.");

        // Letters as well as length, or the exception message claims more than the type keeps.
        if (currency is not { Length: 3 } || !currency.All(char.IsAsciiLetter))
            throw new DomainException("Currency must be a 3-letter currency code.");

        // The currency's own exponent (ADR-067), half to even.
        return new Money(
            decimal.Round(amount, CurrencyMinorUnits.Of(currency), MidpointRounding.ToEven),
            currency.ToUpperInvariant());
    }

    public static Money Zero(string currency) => Of(0m, currency);

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator *(Money money, int quantity)
    {
        // Without this guard the operator is a back door past Of's refusal of a negative amount.
        if (quantity < 0)
            throw new DomainException("Money cannot be multiplied by a negative quantity.");

        return new Money(money.Amount * quantity, money.Currency);
    }

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (left.Currency != right.Currency)
        {
            throw new DomainException(
                $"Cannot combine {left.Currency} with {right.Currency}.");
        }
    }
}
