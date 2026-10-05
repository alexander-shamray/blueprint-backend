using System.Reflection;
using Common.Application;
using Common.Infrastructure.Messaging;

namespace Common.TestSupport;

/// <summary>A member of a declared type that bounds no holding, with the reason the map leaves it out.</summary>
public sealed record NotRetention(Type Type, string Member, string Reason);

/// <summary>docs/personal-data.md over the code: every retention member of a declared type is named there.</summary>
/// <remarks>
/// A member is every public <see cref="TimeSpan"/> a declared type holds, so a new one is mapped or excluded with a
/// reason rather than missed for its name. A type is in the check only once a host declares it.
/// </remarks>
public static class RetentionMapRule
{
    /// <summary>The building blocks' retention types, which every host's purge and claim read.</summary>
    public static IReadOnlyList<Type> BuildingBlocks { get; } =
    [
        typeof(RetentionPolicy),
        typeof(IdempotencyRetention)
    ];

    /// <summary>The building blocks' members that bound no holding.</summary>
    public static IReadOnlyList<NotRetention> BuildingBlocksNotRetention { get; } =
    [
        new(typeof(RetentionPolicy), nameof(RetentionPolicy.Interval), "the purge's pacing, which keeps nothing"),
        new(
            typeof(IdempotencyRetention),
            nameof(IdempotencyRetention.MarkerFloor),
            "the floor RetentionPolicy.IdempotencyWindow reads, which is Window itself")
    ];

    /// <summary>Every <see cref="TimeSpan"/> member the declared types hold, as <c>Type.Member</c>.</summary>
    public static IReadOnlyList<string> Members(IEnumerable<Type> declared) =>
        [.. declared.SelectMany(type => MembersOf(type).Select(member => $"{type.Name}.{member}")).Order()];

    /// <summary>Every member the map does not name and nothing excludes, and every stale exclusion.</summary>
    public static IReadOnlyList<string> Offenders(IEnumerable<Type> declared, IEnumerable<NotRetention> excluded) =>
        Offenders(declared, excluded, File.ReadAllText(MapPath));

    public static IReadOnlyList<string> Offenders(
        IEnumerable<Type> declared,
        IEnumerable<NotRetention> excluded,
        string map)
    {
        Type[] types = [.. declared];
        NotRetention[] exclusions = [.. excluded];
        HashSet<string> members = [.. Members(types)];
        HashSet<string> explained = [.. exclusions.Select(e => $"{e.Type.Name}.{e.Member}")];

        List<string> offenders =
        [
            .. types
                .Where(type => !MembersOf(type).Any())
                .Select(type => $"{type.Name} is declared and holds no TimeSpan, so it adds nothing to the check"),
            .. members
                .Except(explained)
                .Where(member => !map.Contains($"`{member}`", StringComparison.Ordinal))
                .Order()
                .Select(member =>
                    $"{member} holds a window docs/personal-data.md does not name: add it to the row of the " +
                    "holding it bounds, or declare it NotRetention with the reason it bounds none")
        ];

        offenders.AddRange(
            exclusions
                .Where(e => !members.Contains($"{e.Type.Name}.{e.Member}") || string.IsNullOrWhiteSpace(e.Reason))
                .Select(e =>
                    $"{e.Type.Name}.{e.Member} is excluded, and nothing declared holds it or it gives no reason"));

        return offenders;
    }

    private static string MapPath => Path.Combine(ComposeImage.RepositoryRoot(), "docs", "personal-data.md");

    private static IEnumerable<string> MembersOf(Type type)
    {
        const BindingFlags Visible = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.DeclaredOnly;

        return type
            .GetProperties(Visible)
            .Where(property => IsWindow(property.PropertyType))
            .Select(property => property.Name)
            .Concat(type.GetFields(Visible).Where(field => IsWindow(field.FieldType)).Select(field => field.Name));
    }

    private static bool IsWindow(Type type) => type == typeof(TimeSpan) || type == typeof(TimeSpan?);
}
