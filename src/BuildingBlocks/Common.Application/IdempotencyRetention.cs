namespace Common.Application;

/// <summary>How long §8.5's Redis claim survives, in one place so the marker's retention reads it.</summary>
public static class IdempotencyRetention
{
    /// <summary>Every entry expires, completed or not, so this bounds §8.5's Redis guarantee.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>The least marker retention, read by <c>RetentionPolicy</c> rather than restated.</summary>
    /// <remarks>Bounds the guarantee's length, not its truth: the purge asks the claim (ADR-039).</remarks>
    public static TimeSpan MarkerFloor => Window;
}
