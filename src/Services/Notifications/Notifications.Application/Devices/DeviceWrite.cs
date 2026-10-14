namespace Notifications.Application.Devices;

/// <summary>What a registration or sign-out did to its row, and the row's sequence after it (ADR-117).</summary>
public sealed record DeviceWrite(DeviceWriteOutcome Outcome, long Sequence);

/// <summary>A stale request changes nothing; the caller is told the row's sequence so the app can pass it.</summary>
public enum DeviceWriteOutcome
{
    /// <summary>
    /// The request was above the row's sequence and inside the step, and was applied; a sign-out finding no row, or
    /// another subject's, has nothing of the caller's to clear, and writes nothing.
    /// </summary>
    Applied,

    /// <summary>The request carried the row's own sequence and asked for what the row already holds.</summary>
    Repeated,

    /// <summary>Below the row's sequence, equal to it asking for something else, or past the step above it.</summary>
    Stale
}
