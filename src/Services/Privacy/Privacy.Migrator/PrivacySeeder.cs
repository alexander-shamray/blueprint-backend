using Microsoft.Extensions.Logging;

namespace Privacy.Migrator;

/// <summary>§14.3's development seed, which holds no rows until this service has an aggregate to seed.</summary>
public sealed class PrivacySeeder(ILogger<PrivacySeeder> logger)
{
    private static readonly Action<ILogger, Exception?> NothingToSeed =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(1, nameof(NothingToSeed)),
            "Privacy has no seed rows yet; the gate is open and nothing was written.");

    public Task SeedAsync(CancellationToken ct)
    {
        NothingToSeed(logger, null);
        return Task.CompletedTask;
    }
}
