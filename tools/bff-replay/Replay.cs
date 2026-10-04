namespace BffReplay;

/// <summary>ADR-051's rebuild; the body arrives with the reset and the read.</summary>
public static class Replay
{
    public const string Queue = "bff-order-events";

    public static readonly TimeSpan BrokerDeadline = TimeSpan.FromSeconds(30);

    public static Task<ReplayReport> RunAsync(
        ReplaySettings settings,
        bool reset,
        TextWriter output,
        TimeSpan brokerDeadline,
        CancellationToken ct) =>
        throw new NotImplementedException();
}

/// <summary>What a replay sent.</summary>
public sealed class ReplayReport
{
    public IReadOnlyList<Guid> SentMessageIds => throw new NotImplementedException();
}
