namespace Common.Contracts;

/// <summary>The unversioned contract §9.2 forbids, in the contracts' namespace but not their assembly.</summary>
public sealed record UnversionedProbe(Guid Id);

/// <summary>A public holder for the nested probe below.</summary>
public static class NestingProbe
{
    /// <summary>A contract nested in a public type, for which <c>Type.IsPublic</c> is false.</summary>
    public sealed record NestedProbe(Guid Id);
}
