using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json.Serialization;
using Common.Application;

namespace Common.TestSupport;

/// <summary>ADR-057's rule over an idempotent command: each member is one its fingerprint sees, and stably.</summary>
/// <remarks>
/// It reads declared types, so it refuses the ones it cannot see past: <see cref="object"/>, a class that is not
/// sealed, and a collection outside the ordered ones it lists (ADR-058).
/// </remarks>
public static class CommandFingerprintRule
{
    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

    private static readonly Type[] WrittenWhole =
    [
        typeof(string),
        typeof(decimal),
        typeof(Guid),
        typeof(DateTime),
        typeof(DateTimeOffset),
        typeof(DateOnly),
        typeof(TimeOnly),
        typeof(TimeSpan),
        typeof(Uri)
    ];

    private static readonly Type[] OrderedSequences =
    [
        typeof(IReadOnlyList<>),
        typeof(IList<>),
        typeof(List<>),
        typeof(ImmutableArray<>),
        typeof(ImmutableList<>),
        typeof(IImmutableList<>)
    ];

    /// <summary>The offenders of each idempotent command <paramref name="application"/> declares, by name.</summary>
    public static IReadOnlyList<string> Offenders(Assembly application) =>
    [
        .. IdempotentCommands(application)
            .OrderBy(command => command.Name, StringComparer.Ordinal)
            .SelectMany(Offenders)
    ];

    /// <summary>Every member of <paramref name="command"/> the fingerprint does not see, or sees unstably.</summary>
    public static IReadOnlyList<string> Offenders(Type command)
    {
        List<string> offenders = [];

        Walk(command, command.Name, [], offenders);

        return offenders;
    }

    /// <summary>Each idempotent command's <see cref="IIdempotentCommand.OperationName"/>, for §8.5's key.</summary>
    public static IReadOnlyList<string> OperationNames(Assembly application) =>
    [
        .. IdempotentCommands(application)
            .Select(command => (string)command
                .GetProperty(nameof(IIdempotentCommand.OperationName), BindingFlags.Public | BindingFlags.Static)!
                .GetValue(null)!)
    ];

    /// <summary>An assembly's idempotent commands, as <see cref="WriteEndpointRule"/> selects them too.</summary>
    /// <remarks>A struct as well as a class: the pipeline's generic TCommand fingerprints either (ADR-057).</remarks>
    internal static IEnumerable<Type> IdempotentCommands(Assembly application) =>
        application
            .GetTypes()
            .Where(typeof(IIdempotentCommand).IsAssignableFrom)
            .Where(type => !type.IsAbstract);

    // The types enclosing this one, not every type met, so a cycle stops and a type on two paths is read on each.
    private static void Walk(Type type, string path, HashSet<Type> enclosing, List<string> offenders)
    {
        if (!enclosing.Add(type))
            return;

        foreach (FieldInfo field in type.GetFields(PublicInstance))
            offenders.Add($"{path}.{field.Name} is a public field, which the fingerprint does not read (ADR-057)");

        foreach (PropertyInfo property in Readable(type))
        {
            string member = $"{path}.{property.Name}";

            // The serialiser reads the attribute on the declaration it writes, not on an overridden base.
            if (property.IsDefined(typeof(JsonIgnoreAttribute), inherit: false))
                offenders.Add($"{member} is marked [JsonIgnore], so it hashes as nothing (ADR-057)");
            else
                Classify(property.PropertyType, member, enclosing, offenders);
        }

        enclosing.Remove(type);
    }

    private static void Classify(Type declared, string path, HashSet<Type> enclosing, List<string> offenders)
    {
        Type type = Nullable.GetUnderlyingType(declared) ?? declared;

        if (type.IsPrimitive || type.IsEnum || WrittenWhole.Contains(type))
            return;

        if (type == typeof(object))
        {
            offenders.Add($"{path} is declared as object, so what it holds cannot be read here (ADR-058)");
            return;
        }

        if (ElementOf(type) is { } element)
        {
            Classify(element, $"{path}[]", enclosing, offenders);
            return;
        }

        if (typeof(IEnumerable).IsAssignableFrom(type))
        {
            offenders.Add(
                $"{path} is declared as {NameOf(type)}, a collection whose type promises no order, so an honest " +
                "retry may hash differently (ADR-057); declare IReadOnlyList<T>");
            return;
        }

        if (!type.IsSealed)
        {
            offenders.Add(
                $"{path} is declared as {NameOf(type)}, which is not sealed, so the fingerprint hashes what that " +
                "type declares and not what a derived one adds (ADR-057)");
            return;
        }

        if (!Readable(type).Any())
        {
            offenders.Add(
                $"{path} is a {NameOf(type)}, which exposes no public property, so it hashes as {{}} (ADR-057)");
        }

        Walk(type, path, enclosing, offenders);
    }

    // What the serialiser writes of an object: a public getter and no index parameter.
    private static IEnumerable<PropertyInfo> Readable(Type type) =>
        type
            .GetProperties(PublicInstance)
            .Where(property => property.GetGetMethod() is not null && property.GetIndexParameters().Length == 0);

    private static Type? ElementOf(Type type)
    {
        if (type.IsArray)
            return type.GetElementType();

        return type.IsGenericType && OrderedSequences.Contains(type.GetGenericTypeDefinition())
            ? type.GetGenericArguments()[0]
            : null;
    }

    private static string NameOf(Type type)
    {
        if (type.IsArray)
            return $"{NameOf(type.GetElementType()!)}[]";

        int arity = type.Name.IndexOf('`', StringComparison.Ordinal);

        return arity < 0
            ? type.Name
            : $"{type.Name[..arity]}<{string.Join(", ", type.GetGenericArguments().Select(NameOf))}>";
    }
}
