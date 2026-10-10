using Microsoft.Extensions.Options;
using Privacy.Application.ErasureRequests;
using Privacy.Domain.ErasureRequests;

namespace Privacy.Infrastructure;

/// <summary>Refuses to start on a responder set or a service level no request could be raised with.</summary>
/// <remarks>Checked here as well as in the aggregate, so a mistake is the host's, not a request's (§15.4).</remarks>
internal sealed class PrivacyOptionsValidator : IValidateOptions<PrivacyOptions>
{
    public ValidateOptionsResult Validate(string? name, PrivacyOptions options)
    {
        List<string> failures = [];

        string? unfit = ErasureRequest.WhyNotAResponderSet(options.Responders);
        if (unfit is not null)
            failures.Add($"Privacy:Responders: {unfit} (ADR-092)");

        if (options.CompletionSlo <= TimeSpan.Zero)
            failures.Add("Privacy:CompletionSlo is missing or not positive; it is the adopter's reading of the law.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
