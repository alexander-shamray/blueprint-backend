using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Preferences;

namespace Notifications.Infrastructure.Delivery;

/// <summary>The preference rule both senders ask before a send, held once for both passes (ADR-120).</summary>
internal static class ConsentChecks
{
    /// <summary>One pair under its requirement, the customer's newest entry or its default (ADR-120).</summary>
    public static async Task<PreferenceState> StateAsync(
        IServiceProvider sp,
        Guid customer,
        string topic,
        string channel,
        CancellationToken ct)
    {
        ConsentPair pair = new(topic, channel);
        RequiredPairs required = sp.GetRequiredService<RequiredPairs>();

        // A required pair takes no entry, so the journal is not asked for one.
        ConsentEntry? newest = required.Contains(pair)
            ? null
            : await sp.GetRequiredService<IConsentJournal>().NewestAsync(customer, pair, ct);

        return ConsentRules.StateOf(pair, required, newest);
    }
}
