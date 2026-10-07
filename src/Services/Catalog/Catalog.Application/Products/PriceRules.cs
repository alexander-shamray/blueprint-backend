using System.Linq.Expressions;
using Common.Domain;
using FluentValidation;

namespace Catalog.Application.Products;

/// <summary>The price half of the boundary, shared by every command that sets one (§6.3, §10.5).</summary>
internal static class PriceRules
{
    /// <summary>The first amount decimal(19,4) cannot hold.</summary>
    private const decimal StorageCeiling = 1_000_000_000_000_000m;

    /// <summary>An amount and its currency, judged as <c>Money.Of</c> will take them.</summary>
    /// <remarks>
    /// Storage's bound, so an oversize price is a 400 rather than a 500 at SaveChanges; judged after the rounding to
    /// the currency's exponent (ADR-067), which can carry an amount under it up to it.
    /// </remarks>
    public static void Price<T>(
        AbstractValidator<T> validator,
        Expression<Func<T, decimal?>> amount,
        Expression<Func<T, string>> currency)
    {
        Func<T, string> currencyOf = currency.Compile();

        validator
            .RuleFor(amount)
            .NotNull()
            .GreaterThanOrEqualTo(0)
            .Must((command, value) => value is not { } given || FitsStorage(given, currencyOf(command)))
            .WithMessage("'{PropertyName}' is more than a price can hold once rounded to its currency's minor unit.");

        // NotEmpty first, since Matches alone skips null; letters, since Money.Of refuses "1$?" as a bug (§5.7);
        // \z, not $, since .NET's $ matches before a trailing newline.
        validator.RuleFor(currency).NotEmpty().Matches(@"^[A-Za-z]{3}\z");
    }

    // Money.Of's rounding, applied here because Money.Of would throw on the currency this rule is not judging.
    private static bool FitsStorage(decimal amount, string? currency)
    {
        int exponent = CurrencyMinorUnits.Of(currency ?? string.Empty);

        return decimal.Round(amount, exponent, MidpointRounding.ToEven) < StorageCeiling;
    }
}
