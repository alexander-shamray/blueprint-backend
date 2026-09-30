using FluentValidation;
using FluentValidation.Results;

namespace Common.Application;

/// <summary>Fails fast before any I/O; unconstrained, since a malformed query is as worth rejecting (§6.3).</summary>
public sealed class ValidationBehavior<TRequest, TResult>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResult>
{
    public async Task<TResult> HandleAsync(TRequest request, NextDelegate<TResult> next, CancellationToken ct)
    {
        if (!validators.Any())
            return await next();

        // A context per validator: a shared one reports every failure once per validator, and races under WhenAll.
        ValidationResult[] results = await Task.WhenAll(
            validators.Select(v => v.ValidateAsync(new ValidationContext<TRequest>(request), ct)));

        ValidationFailure[] failures = [.. results.SelectMany(r => r.Errors).Where(f => f is not null)];

        if (failures.Length > 0)
            throw new ValidationException(failures);

        return await next();
    }
}
