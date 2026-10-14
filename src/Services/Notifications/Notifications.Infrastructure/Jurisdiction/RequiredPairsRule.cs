using Microsoft.Extensions.Options;
using Notifications.Application.Preferences;

namespace Notifications.Infrastructure.Jurisdiction;

/// <summary>ADR-120's start-up refusal of a required pair, and the set both hosts decide by.</summary>
/// <remarks>
/// A pair must name a topic and a channel of the vocabulary, and a service topic, so no deployment can require a
/// promotion; a pair named twice is refused as the typo it most likely is.
/// </remarks>
internal static class RequiredPairsRule
{
    private const string Key =
        $"{NotificationsJurisdictionOptions.SectionName}:{nameof(NotificationsJurisdictionOptions.Required)}";

    public static IEnumerable<string> Refusals(IReadOnlyList<string>? required)
    {
        HashSet<ConsentPair> seen = [];

        for (int i = 0; i < (required?.Count ?? 0); i++)
        {
            string value = required![i];

            if (!ConsentPair.TryParse(value, out ConsentPair? pair))
            {
                yield return $"{Key}:{i} is '{value}', which is not a topic/channel pair of this service's.";
            }
            else if (!ConsentVocabulary.IsService(pair.Value.Topic))
            {
                yield return $"{Key}:{i} names '{pair.Value.Topic}', which is not a service topic (ADR-120).";
            }
            else if (!seen.Add(pair.Value))
            {
                yield return $"{Key}:{i} names '{value}' a second time.";
            }
        }
    }

    /// <summary>The set the validators passed at start; a pair that fails to parse here is a defect.</summary>
    public static RequiredPairs From(IOptions<NotificationsJurisdictionOptions> options) =>
        new(
            (options.Value.Required ?? []).Select(value =>
                ConsentPair.TryParse(value, out ConsentPair? pair)
                    ? pair.Value
                    : throw new InvalidOperationException($"{Key} holds a pair start-up should have refused.")));
}

/// <summary>The API host's validation of the one member it reads; the worker's validator holds this rule too.</summary>
internal sealed class RequiredPairsValidator : IValidateOptions<NotificationsJurisdictionOptions>
{
    public ValidateOptionsResult Validate(string? name, NotificationsJurisdictionOptions options)
    {
        string[] failures = [.. RequiredPairsRule.Refusals(options.Required)];

        return failures.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
