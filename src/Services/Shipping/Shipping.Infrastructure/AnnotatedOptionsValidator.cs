using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Shipping.Infrastructure;

/// <summary>
/// Runs an options class's annotations at start (§15.4). Written out because
/// <c>ValidateDataAnnotations</c> ships in
/// <c>Microsoft.Extensions.Options.DataAnnotations</c>, which
/// <c>Directory.Packages.props</c> does not pin, and a pin is a package
/// decision this service has no need of: <c>Validator.TryValidateObject</c> is
/// what that package runs. The annotations are still the one rule.
/// </summary>
internal sealed class AnnotatedOptionsValidator<TOptions> : IValidateOptions<TOptions>
    where TOptions : class
{
    public ValidateOptionsResult Validate(string? name, TOptions options)
    {
        List<ValidationResult> failures = [];
        ValidationContext context = new(options);

        return Validator.TryValidateObject(options, context, failures, validateAllProperties: true)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures.Select(f => f.ErrorMessage ?? "Invalid."));
    }
}
