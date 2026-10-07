using Catalog.Application.Products.ChangePrice;
using Shouldly;
using Xunit;

namespace Catalog.Application.Tests;

/// <summary>The price rules are <c>PriceRules</c>', so these cases show they reach this command.</summary>
public class ChangePriceValidatorTests
{
    private static readonly ChangePriceValidator Validator = new();

    private static ChangePriceCommand Valid() => new(Guid.CreateVersion7(), Guid.CreateVersion7(), 24.50m, "EUR");

    [Fact]
    public void A_valid_command_passes()
    {
        Validator.Validate(Valid()).IsValid.ShouldBeTrue();
    }

    public static TheoryData<string, ChangePriceCommand> Invalid() => new()
    {
        // An omitted CommandId binds as Guid.Empty, one shared idempotency key rather than an absent one (§8.5).
        { "CommandId", Valid() with { CommandId = Guid.Empty } },
        { "ProductId", Valid() with { ProductId = Guid.Empty } },
        // Omitted binds as null, so it is a 400 rather than a free product.
        { "Amount", Valid() with { Amount = null } },
        { "Amount", Valid() with { Amount = -1m } },
        { "Amount", Valid() with { Amount = 1_000_000_000_000_000m } },
        { "Currency", Valid() with { Currency = "EU" } },
        { "Currency", Valid() with { Currency = "1$?" } }
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void An_invalid_command_fails_on_its_field(string field, ChangePriceCommand command)
    {
        Validator
            .Validate(command)
            .Errors
            .ShouldContain(e => e.PropertyName == field, $"{field} should have been refused");
    }
}
