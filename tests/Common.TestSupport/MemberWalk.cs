using System.Collections;
using System.Globalization;
using System.Reflection;

namespace Common.TestSupport;

/// <summary>Every public member of a value, flattened to <c>path=value</c> lines, whatever its serialiser says.</summary>
/// <remarks>
/// For a round trip: two values with the same lines have the same members, where a re-serialisation compares only
/// what the serialiser chose to write, and a <c>JsonIgnore</c>, a field or a rounding converter is in both strings.
/// Collections compare by element whatever their type, which structural equivalence does not.
/// </remarks>
public static class MemberWalk
{
    public static IReadOnlyList<string> Of(object? value) => [.. Walk(value, "$")];

    private static IEnumerable<string> Walk(object? value, string path)
    {
        if (value is null)
        {
            yield return $"{path}=<null>";
            yield break;
        }

        Type type = value.GetType();

        if (type.IsPrimitive || type.IsEnum || value is string or decimal or Guid or DateTimeOffset or DateTime or TimeSpan)
        {
            yield return $"{path}={Convert.ToString(value, CultureInfo.InvariantCulture)}";
            yield break;
        }

        if (value is IEnumerable sequence)
        {
            int index = 0;

            foreach (object? element in sequence)
            {
                foreach (string line in Walk(element, $"{path}[{index++}]"))
                    yield return line;
            }

            yield return $"{path}.Count={index}";
            yield break;
        }

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            foreach (string line in Walk(property.GetValue(value), $"{path}.{property.Name}"))
                yield return line;
        }

        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            foreach (string line in Walk(field.GetValue(value), $"{path}.{field.Name}"))
                yield return line;
        }
    }
}
