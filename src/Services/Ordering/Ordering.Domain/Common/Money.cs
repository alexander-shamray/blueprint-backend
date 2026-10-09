using Common.Domain;

namespace Ordering.Domain.Common;

/// <summary>§5.3's always-valid value object: <see cref="Of"/> is the only way in.</summary>
/// <remarks>Ordering's own, not Catalog's: a value object belongs to its context's model (§3, §4.3).</remarks>
public readonly record struct Money
{
    public decimal Amount { get; }
    public string Currency { get; }

    private Money(decimal amount, string currency)
    {
        // At the exponent on the way out of a column as well: a decimal(19,4) hands back four places for a currency
        // of two, and the wire carries the scale it is given (ADR-067).
        Amount = decimal.Round(amount, CurrencyMinorUnits.Of(currency), MidpointRounding.ToEven);
        Currency = currency;
    }

    public static Money Of(decimal amount, string currency)
    {
        if (amount < 0)
            throw new DomainException("Money cannot be negative.");

        // Letters as well as length: "1$?" is three characters and no currency.
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
        // Otherwise a back door past Of: a negative quantity would construct negative Money.
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
