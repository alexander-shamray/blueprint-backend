using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Common.Web;

/// <summary>§13.4's never-log rule, given a mechanism in the OpenTelemetry logging pipeline.</summary>
/// <remarks>Scopes are out of a processor's reach; <see cref="RedactingScopeProvider"/> covers them (§13.4).</remarks>
public sealed class SensitiveDataRedactor : BaseProcessor<LogRecord>
{
    private const string OriginalFormat = "{OriginalFormat}";

    /// <inheritdoc />
    public override void OnEnd(LogRecord record)
    {
        if (record.Attributes is null)
            return;

        List<KeyValuePair<string, object?>>? scrubbed = null;
        List<string>? secrets = null;
        bool hasTemplate = false;

        for (int i = 0; i < record.Attributes.Count; i++)
        {
            KeyValuePair<string, object?> attribute = record.Attributes[i];

            if (attribute.Key == OriginalFormat)
                hasTemplate = true;

            // The template is exempt from the value check, because the fallback below depends on it.
            if (!SensitiveKeys.Matches(attribute.Key) &&
                (attribute.Key == OriginalFormat || !SensitiveKeys.LooksLikeSecret(attribute.Value)))
            {
                continue;
            }

            // Copied only on a match, because this runs on every log record.
            scrubbed ??= [.. record.Attributes];

            if (attribute.Value?.ToString() is { Length: > 0 } secret)
                (secrets ??= []).Add(secret);

            scrubbed[i] = new KeyValuePair<string, object?>(attribute.Key, "[redacted]");
        }

        if (scrubbed is null)
            return;

        record.Attributes = scrubbed;

        // The exporter ships FormattedMessage as the body, and Body is only a safe template with one (§13.4).
        record.FormattedMessage = hasTemplate && record.Body is not null
            ? record.Body
            : "[redacted]";

        // OTLP serialises the exception separately, so one that repeats a redacted value is dropped (§13.4).
        if (record.Exception is not null && secrets is not null && Reveals(record.Exception, secrets))
            record.Exception = null;
    }

    private static bool Reveals(Exception exception, List<string> secrets)
    {
        string text = exception.ToString();

        foreach (string secret in secrets)
        {
            if (text.Contains(secret, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
