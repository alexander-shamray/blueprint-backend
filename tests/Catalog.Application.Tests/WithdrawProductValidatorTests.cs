using Catalog.Application.Products.WithdrawProduct;
using Shouldly;
using Xunit;

namespace Catalog.Application.Tests;

/// <summary>Both ids are required, since <c>Guid.Empty</c> is what an omitted one binds as.</summary>
public class WithdrawProductValidatorTests
{
    private static readonly WithdrawProductValidator Validator = new();

    private static WithdrawProductCommand Valid() => new(Guid.CreateVersion7(), Guid.CreateVersion7());

    [Fact]
    public void A_valid_command_passes()
    {
        Validator.Validate(Valid()).IsValid.ShouldBeTrue();
    }

    public static TheoryData<string, WithdrawProductCommand> Invalid() => new()
    {
        // An omitted CommandId binds as Guid.Empty, one shared idempotency key rather than an absent one (§8.5).
        { "CommandId", Valid() with { CommandId = Guid.Empty } },
        { "ProductId", Valid() with { ProductId = Guid.Empty } }
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void An_invalid_command_is_refused_on_its_field(string field, WithdrawProductCommand command)
    {
        Validator.Validate(command).Errors.ShouldContain(e => e.PropertyName == field);
    }
}
