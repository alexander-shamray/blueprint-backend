namespace BffReplay;

/// <summary>The command line: no argument repairs, <c>--reset</c> rebuilds, and anything else is refused.</summary>
public static class ReplayCommand
{
    /// <summary>The replay ran to the end. A run that fails throws, and the process exits non-zero on it.</summary>
    public const int Succeeded = 0;

    /// <summary>Refused before anything was opened: a bad argument or a missing connection.</summary>
    public const int Refused = 2;

    public const string ResetFlag = "--reset";

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        Func<string, string?> environment,
        TextWriter output,
        TextWriter error,
        CancellationToken ct)
    {
        if (args.Count > 1 || (args.Count == 1 && args[0] != ResetFlag))
        {
            await error.WriteLineAsync(
                $"Usage: bff-replay [{ResetFlag}]. Without it the replay repairs over the projection; with it " +
                "the order rows and the queue's inbox rows go first (tools/bff-replay/README.md).");
            return Refused;
        }

        ReplaySettings settings;
        try
        {
            settings = ReplaySettings.FromEnvironment(environment);
        }
        catch (ReplaySettingsException refused)
        {
            await error.WriteLineAsync(refused.Message);
            return Refused;
        }

        ReplayReport report = await Replay.RunAsync(settings, args.Count == 1, output, Replay.BrokerDeadline, ct);
        await output.WriteLineAsync($"Sent {report.SentMessageIds.Count} event(s) to {Replay.Queue}.");
        return Succeeded;
    }
}
