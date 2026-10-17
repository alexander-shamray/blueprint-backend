namespace Notifications.Application.Preferences;

/// <summary>ADR-120's decision, a pure function over the requirement and the pair's newest entry.</summary>
/// <remarks>
/// One function for the read the subject is shown and for the send, so E.6.14's route shows what the send pass
/// acts on, while both hosts are given one required list (ADR-120).
/// </remarks>
public static class ConsentRules
{
    /// <summary>A requirement first, then the newest entry, then the class's default.</summary>
    /// <param name="newest">The pair's newest entry for the subject, or null when it has none.</param>
    public static PreferenceState StateOf(ConsentPair pair, RequiredPairs required, ConsentEntry? newest)
    {
        ArgumentNullException.ThrowIfNull(required);

        // A requirement takes no entry, so one written before the deployment required the pair is not shown.
        if (required.Contains(pair))
            return new PreferenceState(pair.Topic, pair.Channel, true, PreferenceSource.Required, null, null);

        if (newest is not null)
        {
            return new PreferenceState(
                pair.Topic,
                pair.Channel,
                newest.OptedIn,
                PreferenceSource.Set,
                newest.WrittenAt,
                newest.EntryId);
        }

        return new PreferenceState(
            pair.Topic,
            pair.Channel,
            ConsentVocabulary.IsService(pair.Topic),
            PreferenceSource.Default,
            null,
            null);
    }
}
