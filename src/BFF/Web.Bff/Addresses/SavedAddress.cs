using FluentValidation;
using Web.Bff.Persistence;

namespace Web.Bff.Addresses;

/// <summary>The caller's saved delivery address, as it is sent and as it is read back (E.6.12).</summary>
/// <remarks>
/// The fields are the order request's own, so a client prefills the checkout by copying them across. The subject is
/// never a member: it is the principal's (§11.4).
/// </remarks>
public sealed record SavedAddress(string Line1, string? Line2, string City, string PostalCode, string Country)
{
    /// <summary>Trimmed, an empty second line absent and the country upper case, as the order stores an address.</summary>
    public SavedAddress Normalised() =>
        new(
            Line1.Trim(),
            string.IsNullOrWhiteSpace(Line2) ? null : Line2.Trim(),
            City.Trim(),
            PostalCode.Trim(),
            Country.ToUpperInvariant());
}

/// <summary>§6.4's validator, which the endpoint calls itself because this host has no command pipeline.</summary>
/// <remarks>
/// Presence and the shape of an alpha-2 code, and the order's own widths, and no postcode format (ADR-107).
/// A message names the field and never repeats its value, since an address is not an exception's text.
/// </remarks>
internal sealed class SavedAddressValidator : AbstractValidator<SavedAddress>
{
    public SavedAddressValidator()
    {
        RuleFor(x => x.Line1).NotEmpty().MaximumLength(AddressLimits.Line1MaxLength);
        RuleFor(x => x.Line2).MaximumLength(AddressLimits.Line2MaxLength);
        RuleFor(x => x.City).NotEmpty().MaximumLength(AddressLimits.CityMaxLength);
        RuleFor(x => x.PostalCode).NotEmpty().MaximumLength(AddressLimits.PostalCodeMaxLength);

        // NotEmpty first, because Matches skips null; \z, because .NET's $ matches before a trailing newline.
        RuleFor(x => x.Country).NotEmpty().Matches(@"^[A-Za-z]{2}\z");
    }
}
