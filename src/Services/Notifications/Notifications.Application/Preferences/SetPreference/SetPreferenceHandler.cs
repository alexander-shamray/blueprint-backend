using Common.Application;
using Notifications.Application.Privacy;

namespace Notifications.Application.Preferences.SetPreference;

/// <summary>Refuses a required pair, then hands the append to the journal, whose one batch decides it.</summary>
/// <remarks>
/// The erasures are read before the append, so the entry names only requests that ran before it and a reissue of
/// one keeps it (ADR-138).
/// </remarks>
public sealed class SetPreferenceHandler(
    IConsentJournal journal,
    IPersonalDataErasureRepository erasures,
    RequiredPairs required,
    TimeProvider clock)
    : ICommandHandler<SetPreferenceCommand, Result<PreferenceWrite>>
{
    /// <summary>The 422's code: a duty the deployment has is not the subject's to switch (§10.5).</summary>
    public const string RequiredCode = "preference.required";

    public async Task<Result<PreferenceWrite>> HandleAsync(SetPreferenceCommand command, CancellationToken ct)
    {
        ConsentPair pair = new(command.Topic, command.Channel);

        if (required.Contains(pair))
        {
            return Result.Failure<PreferenceWrite>(
                Error.Rule(
                    RequiredCode,
                    "This deployment is obliged to send this topic on this channel, so it takes no preference."));
        }

        DateTimeOffset now = clock.GetUtcNow();
        IReadOnlyList<Guid> erasedUnder = await erasures.RequestsErasingAsync(command.SubjectId, now, ct);
        ConsentAppend appended = await journal.AppendAsync(command, erasedUnder, now, ct);

        return Result.Success(
            new PreferenceWrite(appended.Outcome, ConsentRules.StateOf(pair, required, appended.Newest)));
    }
}
