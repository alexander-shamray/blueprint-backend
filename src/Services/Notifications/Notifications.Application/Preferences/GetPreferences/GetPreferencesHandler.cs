using Common.Application;

namespace Notifications.Application.Preferences.GetPreferences;

/// <summary>Every pair of the vocabulary, each decided as a send would decide it, topic by topic.</summary>
public sealed class GetPreferencesHandler(IConsentJournal journal, RequiredPairs required)
    : IQueryHandler<GetPreferencesQuery, IReadOnlyList<PreferenceState>>
{
    public async Task<IReadOnlyList<PreferenceState>> HandleAsync(GetPreferencesQuery query, CancellationToken ct)
    {
        Dictionary<ConsentPair, ConsentEntry> newest = (await journal.NewestAsync(query.SubjectId, ct))
            .ToDictionary(entry => new ConsentPair(entry.Topic, entry.Channel));

        return
        [
            .. from topic in ConsentVocabulary.Topics
               from channel in ConsentVocabulary.Channels
               let pair = new ConsentPair(topic, channel)
               select ConsentRules.StateOf(pair, required, newest.GetValueOrDefault(pair))
        ];
    }
}
