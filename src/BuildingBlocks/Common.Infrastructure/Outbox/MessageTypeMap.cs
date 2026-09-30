using System.Collections.Frozen;
using System.Reflection;
using Common.Contracts;
using Common.Domain;

namespace Common.Infrastructure.Outbox;

/// <summary>Two-way map between a stageable type and its persisted name, a persisted contract (§9.4).</summary>
public sealed class MessageTypeMap
{
    /// <summary>The widest name the <c>MessageType</c> column holds; the map decides what is stageable.</summary>
    public const int MaxNameLength = 300;

    private readonly FrozenDictionary<string, Type> _byName;
    private readonly FrozenDictionary<Type, string> _byType;

    public MessageTypeMap(IEnumerable<Assembly> assemblies)
        : this(assemblies, new Dictionary<string, Type>())
    {
    }

    public MessageTypeMap(IEnumerable<Assembly> assemblies, IReadOnlyDictionary<string, Type> aliases)
        : this(assemblies, aliases, new Dictionary<Type, string>())
    {
    }

    public MessageTypeMap(
        IEnumerable<Assembly> assemblies,
        IReadOnlyDictionary<string, Type> aliases,
        IReadOnlyDictionary<Type, string> writtenNames)
    {
        // FullName, not AssemblyQualifiedName: no version, and a contract's namespace is already versioned (§9.2).
        (string Name, Type Type)[] pairs =
        [
            .. assemblies
                .SelectMany(a => a.GetTypes())
                // Not `IsClass`: a record struct domain event is stageable too.
                .Where(t => t is { IsAbstract: false, IsInterface: false } &&
                    (t.IsAssignableTo(typeof(IIntegrationEvent)) ||
                        t.IsAssignableTo(typeof(IDomainEvent))))
                .Select(t => (Name: t.FullName!, Type: t))
        ];

        // Checked at startup rather than failing the insert on truncation at SaveChanges.
        foreach ((string Name, Type Type) pair in pairs)
        {
            if (pair.Name.Length > MaxNameLength)
            {
                throw new InvalidOperationException(
                    $"{pair.Type.Name}'s persisted name is {pair.Name.Length} characters and the " +
                    $"outbox column holds {MaxNameLength}. Shorten the namespace, or move the type.");
            }
        }

        IGrouping<string, (string Name, Type Type)>? clash =
            pairs.GroupBy(p => p.Name).FirstOrDefault(g => g.Count() > 1);
        if (clash is not null)
        {
            throw new InvalidOperationException(
                $"Two staged types share the name '{clash.Key}'. The outbox " +
                "column cannot distinguish them.");
        }

        // An alias resolves inward only; writing an old name is WriteAs's, the second of §9.4's three releases.
        foreach ((string Name, Type Type) alias in aliases.Select(a => (a.Key, a.Value)))
        {
            // An alias is typed by hand, so it is the one name that can exceed the column.
            if (alias.Name.Length > MaxNameLength)
            {
                throw new InvalidOperationException(
                    $"The alias '{alias.Name}' is {alias.Name.Length} characters and the outbox " +
                    $"column holds {MaxNameLength}. No row can carry it.");
            }

            if (pairs.Any(p => p.Name == alias.Name))
            {
                throw new InvalidOperationException(
                    $"'{alias.Name}' is an alias and also a live type name. One of them resolves " +
                    "and which is not decidable — rename the alias or drop it.");
            }

            // The target must be stageable, or an alias would reopen the leak Stage's guards close.
            if (!pairs.Any(p => p.Type == alias.Type))
            {
                throw new InvalidOperationException(
                    $"'{alias.Name}' aliases {alias.Type.Name}, which this map does not carry. An " +
                    "alias names a type that is still stageable — one that is not is a row nobody " +
                    "can deliver and a guard nobody applies.");
            }
        }

        _byName = pairs
            .Select(p => (p.Name, p.Type))
            .Concat(aliases.Select(a => (Name: a.Key, Type: a.Value)))
            .ToFrozenDictionary(p => p.Name, p => p.Type);

        // An overridden name must be one this map can read back.
        foreach ((Type Type, string Name) written in writtenNames.Select(w => (w.Key, w.Value)))
        {
            if (!_byName.TryGetValue(written.Name, out Type? resolves))
            {
                throw new InvalidOperationException(
                    $"{written.Type.Name} is written as '{written.Name}', which this map cannot " +
                    "resolve. Alias that name to the type in the same release, or the rows this " +
                    "instance stages are rows it cannot itself deliver.");
            }

            // And back to this type: a name resolving to another deserialises the payload as something it never was.
            if (resolves != written.Type)
            {
                throw new InvalidOperationException(
                    $"{written.Type.Name} is written as '{written.Name}', which resolves to " +
                    $"{resolves.Name}. Every row staged for {written.Type.Name} would be read " +
                    $"back as {resolves.Name} — a substitution, not a delivery failure.");
            }
        }

        _byType = pairs.ToFrozenDictionary(
            p => p.Type,
            p => writtenNames.TryGetValue(p.Type, out string? written) ? written : p.Name);
    }

    /// <summary>Fails in the transaction, so the command fails rather than staging an undeliverable row.</summary>
    public string NameOf(Type type) =>
        _byType.TryGetValue(type, out string? name) ? name
            : throw new InvalidOperationException(
                $"{type.Name} is not a stageable message type. Staging it would " +
                "write a row the dispatcher cannot resolve.");

    /// <summary>The Local lane's payload types — §12.4 round-trips each.</summary>
    public IEnumerable<Type> StageableDomainEvents =>
        _byType.Keys.Where(t => t.IsAssignableTo(typeof(IDomainEvent)));

    /// <summary>Fails on the dispatcher, so the retry log names the departed type.</summary>
    public Type Resolve(string name) =>
        _byName.TryGetValue(name, out Type? type) ? type
            : throw new InvalidOperationException(
                $"Unknown message type '{name}'. A type was renamed or removed " +
                "while rows naming it were still unprocessed — drain the outbox " +
                "before deleting a message type (§9.4).");
}
