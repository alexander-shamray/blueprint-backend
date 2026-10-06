using System.Text.Json;
using Common.Web;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Common.TestSupport;

/// <summary>Each body a host's endpoint binds has an example that holds, and its document carries it.</summary>
/// <remarks>
/// Holds means it is the bound type, it survives the host's serialiser and every validator of that type passes it,
/// since an example a tool sends and the endpoint refuses is worse than none.
/// </remarks>
public static class RequestExampleRule
{
    private static readonly string[] Verbs = ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

    /// <summary>The endpoints that bind a request body, which the rule asks an example of.</summary>
    public static IReadOnlyList<Endpoint> Bodied(IEnumerable<Endpoint> endpoints) =>
        [.. endpoints.Where(endpoint => BodyOf(endpoint) is not null)];

    /// <summary>Each bodied endpoint with no example, or one that does not hold, as a sentence naming it.</summary>
    public static IReadOnlyList<string> Offenders(IEnumerable<Endpoint> endpoints, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        JsonSerializerOptions json = services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        using IServiceScope scope = services.CreateScope();
        List<string> offenders = [];

        foreach (Endpoint endpoint in Bodied(endpoints))
        {
            string name = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ??
                endpoint.DisplayName ??
                "an unnamed endpoint";
            Type body = BodyOf(endpoint)!;
            object? example = endpoint.Metadata.GetMetadata<RequestExampleMetadata>()?.Example;

            if (example is null)
            {
                offenders.Add($"{name} binds a {body.Name} and declares no example: add .WithRequestExample(...)");
                continue;
            }

            if (example.GetType() != body)
            {
                offenders.Add($"{name} binds a {body.Name} and its example is a {example.GetType().Name}");
                continue;
            }

            object read = JsonSerializer.Deserialize(JsonSerializer.Serialize(example, body, json), body, json)!;

            offenders.AddRange(
                scope.ServiceProvider
                    .GetServices(typeof(IValidator<>).MakeGenericType(body))
                    .Cast<IValidator>()
                    .SelectMany(validator => validator.Validate(new ValidationContext<object>(read)).Errors)
                    .Select((ValidationFailure failure) =>
                        $"{name}'s example fails its validator at {failure.PropertyName}: {failure.ErrorMessage}"));
        }

        return offenders;
    }

    /// <summary>Every operation a served document gives a request body, as <c>METHOD path</c>.</summary>
    public static IReadOnlyList<string> Bodies(JsonDocument document) =>
        [.. Operations(document).Where(op => op.Body is not null).Select(op => op.Name)];

    /// <summary>Every operation a served document gives a request body whose media types carry no example.</summary>
    public static IReadOnlyList<string> Unexampled(JsonDocument document) =>
    [
        .. Operations(document)
            .Where(op => op.Body is { } body && body.GetProperty("content").EnumerateObject().Any(LacksAnExample))
            .Select(op => op.Name)
    ];

    private static bool LacksAnExample(JsonProperty media) => !media.Value.TryGetProperty("example", out _);

    private static IEnumerable<(string Name, JsonElement? Body)> Operations(JsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        foreach (JsonProperty path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (JsonProperty operation in path.Value.EnumerateObject().Where(p => Verbs.Contains(p.Name)))
            {
                yield return (
                    $"{operation.Name.ToUpperInvariant()} {path.Name}",
                    operation.Value.TryGetProperty("requestBody", out JsonElement body) ? body : null);
            }
        }
    }

    // The body a minimal API binds, which is what puts a request body in the operation.
    private static Type? BodyOf(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IAcceptsMetadata>()?.RequestType;
}
