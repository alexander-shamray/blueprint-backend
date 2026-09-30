using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Shipping.Infrastructure;

/// <summary>Runs an options class's annotations at start (§15.4), without the package that ships its twin.</summary>
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
