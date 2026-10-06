using System.Net.Http.Json;
using System.Text.Json;
using Common.Web;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Common.TestSupport;

/// <summary>Each body a host's endpoint binds has an example that holds, and its document carries it.</summary>
/// <remarks>Holds: of the bound type, through the host's serialiser, past its validators, and no 4xx from its endpoint
/// (<see cref="RefusedAsync"/>), since an example the endpoint refuses is worse than none.</remarks>
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
            string name = NameOf(endpoint);
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

    /// <summary>Each bodied endpoint that answers its own example with a 4xx, sent as a caller sends it.</summary>
    /// <remarks>Whoever refuses: a validator of the built command, a handler's parse, the route or policy. A 5xx or no
    /// answer within <paramref name="budget"/> is past them all: validation runs before idempotency (§6.3).</remarks>
    public static async Task<IReadOnlyList<string>> RefusedAsync(
        IEnumerable<Endpoint> endpoints,
        HttpClient client,
        Action<HttpRequestMessage> authenticate,
        TimeSpan budget,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(authenticate);

        List<string> offenders = [];

        foreach (RouteEndpoint endpoint in Bodied(endpoints).OfType<RouteEndpoint>())
        {
            if (endpoint.Metadata.GetMetadata<RequestExampleMetadata>()?.Example is not { } example)
                continue;

            using HttpRequestMessage request = new(
                new HttpMethod(endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods[0]),
                new Uri(PathOf(endpoint.RoutePattern), UriKind.Relative));
            request.Content = JsonContent.Create(example, example.GetType());
            authenticate(request);

            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(budget);

            try
            {
                using HttpResponseMessage response = await client.SendAsync(request, deadline.Token);

                if ((int)response.StatusCode is >= 400 and < 500)
                    offenders.Add($"{NameOf(endpoint)} refuses its own example with {(int)response.StatusCode}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Past every refusal and waiting on infrastructure the host cannot reach.
            }
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

    private static string NameOf(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? endpoint.DisplayName ?? "unnamed";

    private static string PathOf(RoutePattern pattern) =>
        "/" + string.Join("/", pattern.PathSegments.Select(segment => string.Concat(segment.Parts.Select(TextOf))));

    // A parameter takes one GUID, the only type a bodied route here constrains one to; another is the route's 404.
    private static string TextOf(RoutePatternPart part) =>
        part switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternSeparatorPart separator => separator.Content,
            _ => "0199b0c4-0000-7000-8000-000000000001"
        };

    // The body a minimal API binds, which is what puts a request body in the operation.
    private static Type? BodyOf(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IAcceptsMetadata>()?.RequestType;
}
