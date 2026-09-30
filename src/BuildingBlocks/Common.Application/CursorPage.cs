namespace Common.Application;

/// <summary>§6.5's collection envelope; a null <see cref="NextCursor"/> is the last page (ADR-016).</summary>
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor);
