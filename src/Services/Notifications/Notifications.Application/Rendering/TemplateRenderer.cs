using System.Globalization;
using System.Text;

namespace Notifications.Application.Rendering;

/// <summary>Renders a notice in the deployment's languages and zone, substituting and never evaluating.</summary>
/// <remarks>
/// A customer whose locale the deployment's set holds is sent that language alone; any other, or none, is sent every
/// language of the set in one message, never a default the build agent chose (ADR-053).
/// </remarks>
public sealed class TemplateRenderer
{
    /// <summary>What joins the subjects of a message rendered in more than one language.</summary>
    public const string SubjectSeparator = " / ";

    /// <summary>The fixed rule between the bodies of a message rendered in more than one language.</summary>
    public const string LanguageRule = "\n\n----------------------------------------\n\n";

    /// <summary>What a dropped value renders as: one mark in every language, so no phrase is owed.</summary>
    public const string Absent = "—";

    private readonly TemplateSet _templates;
    private readonly string[] _languages;
    private readonly Dictionary<string, CultureInfo> _cultures;
    private readonly TimeZoneInfo _zone;

    private TemplateRenderer(TemplateSet templates, string[] languages, TimeZoneInfo zone)
    {
        _templates = templates;
        _languages = languages;
        _zone = zone;
        _cultures = languages.ToDictionary(l => l, CultureInfo.GetCultureInfo, StringComparer.Ordinal);
    }

    /// <summary>The deployment's languages, in the order a message in all of them shows them.</summary>
    public IReadOnlyList<string> Languages => _languages;

    /// <summary>A renderer for one deployment, or a <see cref="TemplateSetException"/> naming every refusal.</summary>
    public static TemplateRenderer Create(TemplateSet templates, IReadOnlyList<string> languages, string timeZone)
    {
        IReadOnlyList<string> refusals = Refusals(templates, languages, timeZone);
        if (refusals.Count > 0)
            throw new TemplateSetException(refusals);

        return new TemplateRenderer(templates, [.. languages], TimeZoneInfo.FindSystemTimeZoneById(timeZone));
    }

    /// <summary>Why the templates cannot serve these values, each refusal naming the file or value at fault.</summary>
    public static IReadOnlyList<string> Refusals(
        TemplateSet templates,
        IReadOnlyList<string> languages,
        string timeZone)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(languages);

        List<string> refusals = [];

        if (languages.Count == 0)
            refusals.Add("The language set is empty; ADR-053 makes it a set of at least one.");

        string[] repeated =
        [
            .. languages
                .GroupBy(l => l, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
        ];

        foreach (string language in repeated)
        {
            refusals.Add($"The language set names '{language}' twice.");
        }

        foreach (string language in languages.Distinct(StringComparer.Ordinal))
        {
            if (!KnowsCulture(language))
                refusals.Add($"The runtime has no culture for '{language}', so its dates and amounts cannot be read.");
        }

        IReadOnlyList<string> missing = templates.MissingFor(languages.Distinct(StringComparer.Ordinal));
        refusals.AddRange(missing.Select(file => $"{file} is missing, and the language set requires it."));

        // An IANA id, so a Windows name that converts on one host and not another is refused everywhere.
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out TimeZoneInfo? zone) || !zone.HasIanaId)
            refusals.Add($"'{timeZone}' is not an IANA time zone this runtime knows.");

        return refusals;
    }

    /// <summary>One row's notice, in the locale's language when the set holds it, else in all of them.</summary>
    public RenderedMessage Render(string templateKey, NotificationParameters parameters, string? locale)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        int version = _templates.CurrentVersion(templateKey);
        string[] languages = LanguagesFor(locale);
        List<string> subjects = [];
        List<string> bodies = [];

        foreach (string language in languages)
        {
            // Create refused every gap at start, so a miss here is a set that changed beneath a running host.
            Template template = _templates.Find(templateKey, version, language) ??
                throw new InvalidOperationException($"{templateKey} v{version} has no {language} template.");

            subjects.Add(template.Subject);
            bodies.Add(Fill(template, parameters));
        }

        return new RenderedMessage(
            string.Join(SubjectSeparator, subjects),
            string.Join(LanguageRule, bodies),
            version,
            languages);
    }

    private static bool KnowsCulture(string language)
    {
        try
        {
            return !CultureInfo.GetCultureInfo(language).Equals(CultureInfo.InvariantCulture);
        }
        catch (CultureNotFoundException)
        {
            // An image in invariant globalisation mode lands here for every language but the invariant one.
            return false;
        }
    }

    // The locale's primary subtag, so a realm's "ru-RU" selects a deployment's "ru".
    private string[] LanguagesFor(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
            return _languages;

        int end = locale.IndexOfAny(['-', '_']);
        string language = (end < 0 ? locale : locale[..end]).ToLowerInvariant();

        return Array.IndexOf(_languages, language) >= 0 ? [language] : _languages;
    }

    private string Fill(Template template, NotificationParameters parameters)
    {
        StringBuilder text = new();

        foreach (TemplatePart part in template.Body)
            text.Append(part.IsPlaceholder ? Value(part.Text, template, parameters) : part.Text);

        return text.ToString();
    }

    private string Value(string placeholder, Template template, NotificationParameters parameters)
    {
        CultureInfo culture = _cultures[template.Language];

        return placeholder switch
        {
            PlaceholderNames.OrderId => parameters.OrderId.ToString("D"),
            PlaceholderNames.Date => TimeZoneInfo.ConvertTime(parameters.OccurredAt, _zone).ToString("D", culture),

            // The decimal's own scale, so 10.125 renders three places and 42.10 two: nothing here rounds (ADR-053).
            PlaceholderNames.Amount => parameters.Amount is decimal amount
                ? amount.ToString("N" + amount.Scale.ToString(CultureInfo.InvariantCulture), culture)
                : Absent,
            PlaceholderNames.Currency => parameters.Currency ?? Absent,
            PlaceholderNames.TrackingNumber => parameters.TrackingNumber ?? Absent,
            PlaceholderNames.CancelReason => Phrase(template, parameters.CancelReason),
            _ => throw new InvalidOperationException($"{template.Key} names {placeholder}, which no value fills."),
        };
    }

    private string Phrase(Template template, string? code)
    {
        IReadOnlyDictionary<string, string> reasons = _templates.Reasons(template.Version, template.Language) ??
            throw new InvalidOperationException($"{template.Key} v{template.Version} has no {template.Language} map.");

        return code is not null && reasons.TryGetValue(code, out string? phrase)
            ? phrase
            : reasons[TemplateSet.OtherReason];
    }
}
