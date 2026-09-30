using System.Reflection;

namespace Common.Infrastructure.Outbox;

/// <summary>The assemblies whose events may be staged, mutable so a test host can add its own (§12.4).</summary>
public sealed class MessageTypeSource(params Assembly[] assemblies)
{
    private readonly List<Assembly> _assemblies = [.. assemblies];
    private readonly Dictionary<string, Type> _aliases = [];
    private readonly Dictionary<Type, string> _written = [];

    public IEnumerable<Assembly> Assemblies => _assemblies;

    public MessageTypeSource Add(Assembly assembly)
    {
        _assemblies.Add(assembly);
        return this;
    }

    /// <summary>A type's name before a rename, so both resolve to it inward only (§9.4).</summary>
    public MessageTypeSource Alias(string persistedName, Type type)
    {
        _aliases.Add(persistedName, type);
        return this;
    }

    /// <summary>Keeps writing a type's previous name, so instances not yet replaced can read it (§9.4).</summary>
    /// <remarks>Pairs with <see cref="Alias"/>, and both name the old name.</remarks>
    public MessageTypeSource WriteAs(Type type, string persistedName)
    {
        _written.Add(type, persistedName);
        return this;
    }

    public IReadOnlyDictionary<string, Type> Aliases => _aliases;

    public IReadOnlyDictionary<Type, string> WrittenNames => _written;
}
