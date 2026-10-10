using Microsoft.Extensions.Options;
using Privacy.Application.ErasureRequests;
using Privacy.Domain.ErasureRequests;

namespace Privacy.Infrastructure;

/// <summary>Refuses to start on a responder set or a service level no request could be raised with.</summary>
/// <remarks>Checked here as well as in the aggregate, so a mistake is the host's and not the first request's.</remarks>
internal sealed class PrivacyOptionsValidator : IValidateOptions<PrivacyOptions>
{
    public ValidateOptionsResult Validate(string? name, PrivacyOptions options)
    {
        List<string> failures = [];

        if (options.Responders.Count == 0)
            failures.Add("Privacy:Responders names no holder, so no request could ever close (ADR-092).");

        foreach (string responder in options.Responders)
        {
            if (!ErasureRequest.IsResponderName(responder))
                failures.Add($"Privacy:Responders holds '{responder}', which is not a holder's name.");
        }

        if (options.Responders.Distinct(StringComparer.Ordinal).Count() != options.Responders.Count)
            failures.Add("Privacy:Responders names a holder twice.");

        if (options.CompletionSlo <= TimeSpan.Zero)
            failures.Add("Privacy:CompletionSlo is missing or not positive; it is the adopter's reading of the law.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
