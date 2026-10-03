using System.Globalization;
using System.Reflection;
using System.Text;

namespace Notifications.Application.Rendering;

/// <summary>Every template the service ships, parsed and checked once, so a defect fails the host at start.</summary>
/// <remarks>
/// A cancellation's map is a file beside its template and versioned with it, so a row's version reproduces the reason
/// it gave as well as the text (ADR-053 rule 4), and a third language is files alone (ADR-053 rule 2).
/// </remarks>
public sealed class TemplateSet
{
    /// <summary>The prefix of every shipped resource, whose manifest name is its path under the project.</summary>
    public const string Folder = "Templates/";

    /// <summary>The entry for a code the map does not know, since Ordering adds codes on its own schedule.</summary>
    public const string OtherReason = "*";

    private const string Extension = ".txt";
    private const string ReasonsSuffix = "reasons";
    private const string SubjectPrefix = "Subject: ";

    private static readonly Lazy<TemplateSet> Shipped = new(() => Parse(EmbeddedFiles()));

    private readonly Dictionary<(string Key, int Version, string Language), Template> _templates;
    private readonly Dictionary<(int Version, string Language), IReadOnlyDictionary<string, string>> _reasons;
    private readonly Dictionary<string, int> _current;

    private TemplateSet(
        Dictionary<(string Key, int Version, string Language), Template> templates,
        Dictionary<(int Version, string Language), IReadOnlyDictionary<string, string>> reasons,
        Dictionary<string, int> current)
    {
        _templates = templates;
        _reasons = reasons;
        _current = current;
        Languages = new HashSet<string>(templates.Keys.Select(k => k.Language), StringComparer.Ordinal);
    }

    /// <summary>The compiled-in files, or a <see cref="TemplateSetException"/> naming each fault.</summary>
    public static TemplateSet Embedded => Shipped.Value;

    /// <summary>Every language some template is written in.</summary>
    public IReadOnlySet<string> Languages { get; }

    /// <summary>The highest version present for a key, which is the one sent (ADR-053 rule 4).</summary>
    public int CurrentVersion(string key) =>
        _current.TryGetValue(key, out int version)
            ? version
            : throw new ArgumentOutOfRangeException(nameof(key), key, "Not one of TemplateKeys.");

    public Template? Find(string key, int version, string language) =>
        _templates.GetValueOrDefault((key, version, language));

    /// <summary>A cancellation's map for one version and language, by code and <see cref="OtherReason"/>.</summary>
    public IReadOnlyDictionary<string, string>? Reasons(int version, string language) =>
        _reasons.GetValueOrDefault((version, language));

    /// <summary>The files these languages need at each key's current version, by the name each ships as.</summary>
    public IReadOnlyList<string> MissingFor(IEnumerable<string> languages)
    {
        ArgumentNullException.ThrowIfNull(languages);

        List<string> missing = [];
        foreach (string language in languages)
        {
            foreach (string key in TemplateKeys.Placeholders.Keys.Order(StringComparer.Ordinal))
            {
                int version = _current[key];

                if (!_templates.ContainsKey((key, version, language)))
                    missing.Add(NameOf(key, version, language, reasons: false));

                if (key == TemplateKeys.OrderCancelled && !_reasons.ContainsKey((version, language)))
                    missing.Add(NameOf(key, version, language, reasons: true));
            }
        }

        return missing;
    }

    /// <summary>Checks every file and throws one <see cref="TemplateSetException"/> naming each that fails.</summary>
    public static TemplateSet Parse(IEnumerable<TemplateFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        List<string> refusals = [];
        Dictionary<(string Key, int Version, string Language), Template> templates = [];
        Dictionary<(int Version, string Language), IReadOnlyDictionary<string, string>> reasons = [];

        // By name rather than by what parsed, so a broken newest file is refused and never quietly passed over.
        Dictionary<string, int> current = new(StringComparer.Ordinal);

        foreach (TemplateFile file in files)
        {
            FileName? name = FileName.Read(file.Name);
            if (name is null)
            {
                refusals.Add($"{file.Name}: not {Folder}{{key}}.v{{version}}.{{language}}{Extension}.");
                continue;
            }

            if (!TemplateKeys.Placeholders.ContainsKey(name.Key) ||
                (name.IsReasons && name.Key != TemplateKeys.OrderCancelled))
            {
                refusals.Add($"{file.Name}: names no template key that takes this file.");
                continue;
            }

            if (!name.IsReasons)
                current[name.Key] = Math.Max(current.GetValueOrDefault(name.Key), name.Version);

            // A checkout's line endings are no part of a message.
            string text = file.Text.Replace("\r\n", "\n", StringComparison.Ordinal);

            if (name.IsReasons)
            {
                if (ReasonsIn(file.Name, text, refusals) is Dictionary<string, string> map)
                    reasons[(name.Version, name.Language)] = map;
            }
            else if (TemplateIn(file.Name, name, text, refusals) is Template template)
            {
                templates[(name.Key, name.Version, name.Language)] = template;
            }
        }

        foreach (string key in TemplateKeys.Placeholders.Keys.Where(k => !current.ContainsKey(k)))
            refusals.Add($"{Folder}{key}: no template is shipped for this key at any version.");

        if (refusals.Count > 0)
            throw new TemplateSetException(refusals);

        return new TemplateSet(templates, reasons, current);
    }

    private static string NameOf(string key, int version, string language, bool reasons) =>
        reasons
            ? $"{Folder}{key}.v{version.ToString(CultureInfo.InvariantCulture)}.{language}.{ReasonsSuffix}{Extension}"
            : $"{Folder}{key}.v{version.ToString(CultureInfo.InvariantCulture)}.{language}{Extension}";

    private static Template? TemplateIn(string file, FileName name, string text, List<string> refusals)
    {
        int before = refusals.Count;
        int end = text.IndexOf('\n', StringComparison.Ordinal);
        string first = end < 0 ? text : text[..end];
        string body = end < 0 ? "" : text[(end + 1)..].Trim('\n');

        if (!first.StartsWith(SubjectPrefix, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(first[SubjectPrefix.Length..]))
        {
            refusals.Add($"{file}: its first line is not '{SubjectPrefix}' and a subject.");
            return null;
        }

        string subject = first[SubjectPrefix.Length..].Trim();

        // No value another service wrote may reach a header, so a subject names no placeholder at all.
        if (subject.AsSpan().IndexOfAny('{', '}') >= 0)
            refusals.Add($"{file}: its subject names a placeholder, and a subject may name none.");

        if (string.IsNullOrWhiteSpace(body))
            refusals.Add($"{file}: it has no body.");

        List<TemplatePart>? parts = PartsOf(file, body, refusals);
        foreach (TemplatePart part in parts?.Where(p => p.IsPlaceholder) ?? [])
        {
            if (!TemplateKeys.Placeholders[name.Key].Contains(part.Text))
                refusals.Add($"{file}: names {{{part.Text}}}, which is not among {name.Key}'s placeholders.");
        }

        return refusals.Count == before && parts is not null
            ? new Template(name.Key, name.Version, name.Language, subject, parts)
            : null;
    }

    // Every brace opens a placeholder of letters alone: a template is text and {Name}, never an expression.
    private static List<TemplatePart>? PartsOf(string file, string body, List<string> refusals)
    {
        List<TemplatePart> parts = [];
        int position = 0;

        while (true)
        {
            int open = body.IndexOfAny(['{', '}'], position);
            if (open < 0)
                break;

            int close = body.IndexOf('}', open);
            if (body[open] == '}' || close < 0 || !IsName(body.AsSpan(open + 1, close - open - 1)))
            {
                refusals.Add(
                    $"{file}: the brace at offset {open.ToString(CultureInfo.InvariantCulture)} opens no placeholder.");
                return null;
            }

            if (open > position)
                parts.Add(new TemplatePart(body[position..open], IsPlaceholder: false));

            parts.Add(new TemplatePart(body[(open + 1)..close], IsPlaceholder: true));
            position = close + 1;
        }

        if (position < body.Length)
            parts.Add(new TemplatePart(body[position..], IsPlaceholder: false));

        return parts;
    }

    private static bool IsName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty)
            return false;

        foreach (char c in name)
        {
            if (!char.IsAsciiLetter(c))
                return false;
        }

        return true;
    }

    private static Dictionary<string, string>? ReasonsIn(string file, string text, List<string> refusals)
    {
        int before = refusals.Count;
        Dictionary<string, string> map = new(StringComparer.Ordinal);

        foreach (string line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            string code = colon < 0 ? line.Trim() : line[..colon];
            string phrase = colon < 0 ? "" : line[(colon + 2)..].Trim();

            if (code != OtherReason && !TemplateKeys.CancellationCodes.Contains(code))
                refusals.Add($"{file}: '{code}' is not a CancelReasons code or '{OtherReason}'.");
            else if (phrase.Length == 0 || phrase.AsSpan().IndexOfAny('{', '}') >= 0)
                refusals.Add($"{file}: '{code}' has no phrase, or a phrase naming a placeholder.");
            else if (!map.TryAdd(code, phrase))
                refusals.Add($"{file}: '{code}' is phrased twice.");
        }

        foreach (string code in TemplateKeys.CancellationCodes.Append(OtherReason))
        {
            if (!map.ContainsKey(code))
                refusals.Add($"{file}: '{code}' has no phrase.");
        }

        return refusals.Count == before ? map : null;
    }

    private static IEnumerable<TemplateFile> EmbeddedFiles()
    {
        Assembly assembly = typeof(TemplateSet).Assembly;
        string[] names =
        [
            .. assembly.GetManifestResourceNames().Where(n => n.StartsWith(Folder, StringComparison.Ordinal))
        ];

        foreach (string name in names)
        {
            using Stream stream = assembly.GetManifestResourceStream(name)!;
            using StreamReader reader = new(stream, Encoding.UTF8);

            yield return new TemplateFile(name, reader.ReadToEnd());
        }
    }

    /// <summary>A resource name read as its parts, or null when it is not one this set can hold.</summary>
    private sealed record FileName(string Key, int Version, string Language, bool IsReasons)
    {
        public static FileName? Read(string name)
        {
            if (!name.StartsWith(Folder, StringComparison.Ordinal) ||
                !name.EndsWith(Extension, StringComparison.Ordinal))
            {
                return null;
            }

            string[] parts = name[Folder.Length..^Extension.Length].Split('.');
            bool reasons = parts.Length == 4 && parts[3] == ReasonsSuffix;

            if (parts.Length != (reasons ? 4 : 3))
                return null;

            string version = parts[1];
            string language = parts[2];

            bool versionShaped = version.Length > 1 && version[0] == 'v' && version[1] != '0' &&
                int.TryParse(version.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out _);
            bool languageShaped = language.Length is 2 or 3 && language.All(char.IsAsciiLetterLower);

            return versionShaped && languageShaped && parts[0].Length > 0
                ? new FileName(
                    parts[0],
                    int.Parse(version.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture),
                    language,
                    reasons)
                : null;
        }
    }
}
