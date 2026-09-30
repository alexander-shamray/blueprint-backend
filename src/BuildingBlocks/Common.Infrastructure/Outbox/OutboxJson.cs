using System.Text.Json;
using System.Text.Json.Serialization;

namespace Common.Infrastructure.Outbox;

/// <summary>The payload's persisted format: one registered instance, resolved by both sides (§9.4).</summary>
/// <remarks>A value object needs a converter, which its service's Infrastructure registers (§9.4).</remarks>
public sealed class OutboxJson
{
    public OutboxJson(IEnumerable<JsonConverter> converters)
    {
        Options = new JsonSerializerOptions
        {
            // Explicit defaults: a payload that round-trips only by lenient matching breaks on a rename.
            PropertyNamingPolicy = null,
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.Strict
        };

        foreach (JsonConverter converter in converters)
            Options.Converters.Add(converter);

        // Frozen, since a singleton shared across threads is thread-safe only read-only.
        // populateMissingResolver attaches the reflection default, which nothing trimmed or AOT here contradicts.
        Options.MakeReadOnly(populateMissingResolver: true);
    }

    public JsonSerializerOptions Options { get; }
}
