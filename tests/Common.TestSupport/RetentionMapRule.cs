using System.Reflection;
using System.Text.RegularExpressions;
using Common.Application;
using Common.Infrastructure.Messaging;
using Common.Web;

namespace Common.TestSupport;

/// <summary>A member of a declared type that bounds no holding, with the reason the map leaves it out.</summary>
public sealed record NotRetention(Type Type, string Member, string Reason);

/// <summary>docs/personal-data.md over the code: every retention member of a declared type is named there.</summary>
/// <remarks>
/// A member is every public <see cref="TimeSpan"/> a declared type holds, so a new one is mapped or excluded with a
/// reason rather than missed for its name. A type is in the check once a host declares it, and <see cref="Undeclared"/>
/// finds the ones a host holds by name and has not.
/// </remarks>
public static partial class RetentionMapRule
{
    /// <summary>The building blocks' retention types, which every host's purge and claim read.</summary>
    public static IReadOnlyList<Type> BuildingBlocks { get; } =
    [
        typeof(RetentionPolicy),
        typeof(IdempotencyRetention),
        typeof(ServiceOptions)
    ];

    /// <summary>The building blocks' members that bound no holding.</summary>
    public static IReadOnlyList<NotRetention> BuildingBlocksNotRetention { get; } =
    [
        new(typeof(RetentionPolicy), nameof(RetentionPolicy.Interval), "the purge's pacing, which keeps nothing"),
        new(
            typeof(IdempotencyRetention),
            nameof(IdempotencyRetention.MarkerFloor),
            "the floor RetentionPolicy.IdempotencyWindow reads, which is Window itself"),
        new(typeof(ServiceOptions), nameof(ServiceOptions.OperationTimeout), "one call's deadline, which keeps nothing")
    ];

    /// <summary>Every <see cref="TimeSpan"/> member the declared types hold, as <c>Type.Member</c>.</summary>
    public static IReadOnlyList<string> Members(IEnumerable<Type> declared) =>
        [.. declared.SelectMany(type => MembersOf(type).Select(member => $"{type.Name}.{member}")).Order()];

    /// <summary>Every member the map does not name and nothing excludes, every stale exclusion, and every map name a
    /// declared type no longer holds.</summary>
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

        HashSet<string> names = [.. types.Select(type => type.Name)];
        offenders.AddRange(
            MapName()
                .Matches(map)
                .Where(name => names.Contains(name.Groups["type"].Value) && !members.Contains(name.Groups["name"].Value))
                .Select(name => name.Groups["name"].Value)
                .Distinct()
                .Order()
                .Select(name => $"docs/personal-data.md names {name}, which its declared type no longer holds"));

        return offenders;
    }

    /// <summary>Each type a host's own assemblies hold that bears a window by name and is not declared.</summary>
    /// <remarks>By name: an <c>Options</c> or <c>Retention</c> type holding a <see cref="TimeSpan"/>, in the
    /// assemblies <paramref name="host"/> reaches through references whose names start with a prefix.</remarks>
    public static IReadOnlyList<string> Undeclared(Assembly host, IEnumerable<Type> declared, params string[] prefixes)
    {
        ArgumentNullException.ThrowIfNull(host);

        HashSet<Type> known = [.. declared];

        return
        [
            .. Reached(host, prefixes)
                .SelectMany(assembly => assembly.GetTypes())
                .Where(type => !known.Contains(type) && BearsAWindow(type))
                .Select(type =>
                    $"{type.FullName} holds a window by name and this host does not declare it: declare it beside " +
                    "the host's types, so its members are mapped or excluded")
                .Order()
        ];
    }

    // The closure, as a test assembly's own references keep only the assemblies its code names.
    private static IReadOnlyList<Assembly> Reached(Assembly host, string[] prefixes)
    {
        Dictionary<string, Assembly> reached = [];
        Queue<Assembly> pending = new([host]);

        while (pending.TryDequeue(out Assembly? assembly))
        {
            foreach (AssemblyName name in assembly.GetReferencedAssemblies())
            {
                if (prefixes.Any(p => name.Name!.StartsWith(p, StringComparison.Ordinal)) &&
                    !name.Name!.Contains("Test", StringComparison.Ordinal) &&
                    !reached.ContainsKey(name.Name))
                {
                    reached[name.Name] = Assembly.Load(name);
                    pending.Enqueue(reached[name.Name]);
                }
            }
        }

        return [.. reached.Values];
    }

    private static bool BearsAWindow(Type type) =>
        (type.Name.EndsWith("Options", StringComparison.Ordinal) ||
            type.Name.Contains("Retention", StringComparison.Ordinal)) &&
        MembersOf(type).Any();

    // A backticked Type.Member, the form the map names a window in.
    [GeneratedRegex(@"`(?<name>(?<type>[A-Z][A-Za-z0-9]*)\.[A-Z][A-Za-z0-9]*)`")]
    private static partial Regex MapName();

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
