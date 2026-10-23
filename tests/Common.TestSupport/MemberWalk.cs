using System.Collections;
using System.Globalization;
using System.Reflection;

namespace Common.TestSupport;

/// <summary>Every public member of a value as <c>path=value</c> lines, whatever its serialiser writes.</summary>
/// <remarks>
/// A rounding converter or a <c>JsonIgnore</c> is in both strings of a <see cref="System.Text.Json.JsonSerializer"/>
/// round trip; collections compare by element whatever their type, which structural equivalence does not.
/// </remarks>
public static class MemberWalk
{
    // A domain event is a few levels deep; a cycle or a type that reaches itself is far past this.
    private const int MaxDepth = 24;

    public static IReadOnlyList<string> Of(object? value) => [.. Walk(value, "$", 0)];

    private static IEnumerable<string> Walk(object? value, string path, int depth)
    {
        if (value is null)
        {
            yield return $"{path}=<null>";
            yield break;
        }

        if (depth > MaxDepth)
            throw new InvalidOperationException($"{path} nests more than {MaxDepth} members deep: a cycle?");

        Type type = value.GetType();

        if (type.IsPrimitive || type.IsEnum ||
            value is string or decimal or Guid or DateTimeOffset or DateTime or TimeSpan)
        {
            yield return $"{path}={Leaf(value)}";
            yield break;
        }

        if (value is IEnumerable sequence)
        {
            int index = 0;

            foreach (object? element in sequence)
            {
                foreach (string line in Walk(element, $"{path}[{index++}]", depth + 1))
                    yield return line;
            }

            yield return $"{path}.Count={index}";
            yield break;
        }

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            foreach (string line in Walk(property.GetValue(value), $"{path}.{property.Name}", depth + 1))
                yield return line;
        }

        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            foreach (string line in Walk(field.GetValue(value), $"{path}.{field.Name}", depth + 1))
                yield return line;
        }
    }

    // "O" keeps the fractional seconds and the offset or kind that the general format drops.
    private static string? Leaf(object value) => value switch
    {
        DateTimeOffset moment => moment.ToString("O", CultureInfo.InvariantCulture),
        DateTime moment => moment.ToString("O", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };
}
