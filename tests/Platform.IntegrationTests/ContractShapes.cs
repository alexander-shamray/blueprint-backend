using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Common.Contracts.Ordering.V1;
using Shapes = System.Collections.Generic.Dictionary<
    string,
    System.Collections.Generic.Dictionary<string, string>>;

namespace Platform.IntegrationTests;

/// <summary>The public shape of each contract, keyed by type and member, and what counts as breaking it (§9.2).</summary>
internal static class ContractShapes
{
    internal const string FileName = "contract-shapes.json";

    private const string Required = "required ";

    private static readonly JsonSerializerOptions Written = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // so a generic reads as C#, not as \u003C
        NewLine = "\n",
        WriteIndented = true
    };

    internal static Type[] Contracts() =>
        [.. typeof(OrderPlaced).Assembly.GetTypes().Where(ContractTests.IsContract)];

    internal static Shapes Live() => Of(Contracts());

    internal static Shapes Recorded() =>
        JsonSerializer.Deserialize<Shapes>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, FileName)))!;

    internal static Shapes Of(IEnumerable<Type> types)
    {
        NullabilityInfoContext context = new();
        Shapes shapes = [];

        foreach (Type type in types)
        {
            Dictionary<string, string> members = [];

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0)
                    continue;

                string declared = Render(property.PropertyType, context.Create(property));

                members[property.Name] = ContractTests.IsAlwaysSupplied(property, type)
                    ? Required + declared
                    : declared;
            }

            shapes[type.FullName!] = members;
        }

        return shapes;
    }

    /// <summary>Every difference a consumer built against <paramref name="recorded"/> can fail on.</summary>
    /// <remarks>A rename reads as a member gone; a new required member fails every payload staged before it (§9.2).</remarks>
    internal static string[] Breaks(Shapes recorded, Shapes live)
    {
        List<string> breaks = [];

        foreach ((string type, Dictionary<string, string> members) in recorded)
        {
            if (!live.TryGetValue(type, out Dictionary<string, string>? current))
            {
                breaks.Add($"{type} is gone");
                continue;
            }

            foreach ((string member, string declared) in members)
            {
                if (!current.TryGetValue(member, out string? now))
                    breaks.Add($"{type}.{member} is gone");
                else if (now != declared)
                    breaks.Add($"{type}.{member} was '{declared}' and is '{now}'");
            }

            breaks.AddRange(current
                .Where(m => !members.ContainsKey(m.Key) && m.Value.StartsWith(Required, StringComparison.Ordinal))
                .Select(m => $"{type}.{m.Key} is new and required"));
        }

        return [.. breaks];
    }

    /// <summary>Sorted ordinally, so the file is the same text whatever order reflection returns.</summary>
    internal static string Serialise(Shapes shapes) =>
        JsonSerializer.Serialize(
            new SortedDictionary<string, SortedDictionary<string, string>>(
                shapes.ToDictionary(t => t.Key, t => new SortedDictionary<string, string>(t.Value, StringComparer.Ordinal)),
                StringComparer.Ordinal),
            Written) + "\n";

    /// <summary>Writes beside the source, which a path-mapped CI build cannot reach and is never asked to.</summary>
    internal static void Record(Shapes shapes, [CallerFilePath] string source = "") =>
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(source)!, FileName), Serialise(shapes));

    private static string Render(Type type, NullabilityInfo nullability)
    {
        if (Nullable.GetUnderlyingType(type) is Type underlying)
            return Render(underlying, nullability.GenericTypeArguments.FirstOrDefault() ?? nullability) + "?";

        string mark = !type.IsValueType && nullability.ReadState == NullabilityState.Nullable ? "?" : "";

        if (type.IsArray)
            return $"{Render(type.GetElementType()!, nullability.ElementType!)}[]{mark}";

        if (!type.IsGenericType)
            return type.FullName + mark;

        string name = type.GetGenericTypeDefinition().FullName!;
        Type[] arguments = type.GetGenericArguments();

        string rendered = string.Join(
            ", ",
            arguments.Select((argument, i) => Render(argument, nullability.GenericTypeArguments[i])));

        return $"{name[..name.IndexOf('`', StringComparison.Ordinal)]}<{rendered}>{mark}";
    }
}
