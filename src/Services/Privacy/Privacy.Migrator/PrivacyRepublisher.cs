using Microsoft.Extensions.Logging;

namespace Privacy.Migrator;

/// <summary>ADR-090's republish, which this service cannot run until it has an aggregate to announce.</summary>
public sealed class PrivacyRepublisher(ILogger<PrivacyRepublisher> logger)
{
    private static readonly Action<ILogger, Exception?> NothingToRepublish =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(1, nameof(NothingToRepublish)),
            "Privacy has nothing to republish yet. The job exits non-zero.");

    public Task<int> RunAsync(CancellationToken ct)
    {
        NothingToRepublish(logger, null);
        return Task.FromResult(1);
    }
}
