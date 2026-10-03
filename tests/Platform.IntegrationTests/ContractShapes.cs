using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using Common.Contracts.Ordering.V1;
using Shapes = System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>;

namespace Platform.IntegrationTests;

/// <summary>Each contract's public members as declarations, and which changes break a consumer (§9.2).</summary>
internal static class ContractShapes
{
    internal const string FileName = "contract-shapes.json";

    private const string Required = "required ";

    private static readonly JsonSerializerOptions Written = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // so a generic reads as C#, not as \u003C
        NewLine = "\r\n",
        WriteIndented = true
    };

    /// <summary>The live shape, written to the output directory for the record to be replaced from.</summary>
    internal static string Received => Path.Combine(AppContext.BaseDirectory, "contract-shapes.received.json");

    internal static Type[] Contracts() => [.. typeof(OrderPlaced).Assembly.GetTypes().Where(ContractTests.IsContract)];

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
    /// <remarks>A rename reads as a member gone; a new required member fails an older payload (§9.2).</remarks>
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
    internal static string Serialise(Shapes shapes)
    {
        SortedDictionary<string, SortedDictionary<string, string>> sorted = new(StringComparer.Ordinal);

        foreach ((string type, Dictionary<string, string> members) in shapes)
            sorted[type] = new SortedDictionary<string, string>(members, StringComparer.Ordinal);

        return JsonSerializer.Serialize(sorted, Written) + "\r\n";
    }

    private static string Render(Type type, NullabilityInfo nullability)
    {
        if (Nullable.GetUnderlyingType(type) is Type underlying)
            return Render(underlying, nullability) + "?";

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
