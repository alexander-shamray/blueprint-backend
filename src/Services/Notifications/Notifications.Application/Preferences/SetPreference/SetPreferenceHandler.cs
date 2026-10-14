using Common.Application;

namespace Notifications.Application.Preferences.SetPreference;

/// <summary>Refuses a required pair, then hands the append to the journal, whose one batch decides it.</summary>
public sealed class SetPreferenceHandler(IConsentJournal journal, RequiredPairs required, TimeProvider clock)
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

        ConsentAppend appended = await journal.AppendAsync(command, clock.GetUtcNow(), ct);

        return Result.Success(
            new PreferenceWrite(appended.Outcome, ConsentRules.StateOf(pair, required, appended.Newest)));
    }
}
