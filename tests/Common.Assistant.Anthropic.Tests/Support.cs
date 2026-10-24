using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Common.Assistant.Protocol;
using Common.Assistant.Providers;
using Common.Assistant.Tools;
using Anthropic;

namespace Common.Assistant.Anthropic.Tests;

/// <summary>One request the SDK sent: what a test reads to check the wire, with no network behind it.</summary>
internal sealed record Sent(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, JsonNode? Body);

/// <summary>
/// An HTTP stand-in that answers each request with the next queued response and keeps what it was sent.
/// </summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> responses = new();

    public List<Sent> Requests { get; } = [];

    public FakeHttp Stream(string recording)
    {
        responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Recorded(recording), Encoding.UTF8, "text/event-stream"),
        });

        return this;
    }

    public FakeHttp Status(
        HttpStatusCode status,
        string body = """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""")
    {
        responses.Enqueue(() => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

        return this;
    }

    public FakeHttp Json(string body)
    {
        responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

        return this;
    }

    public FakeHttp Fails()
    {
        responses.Enqueue(() => throw new HttpRequestException("connection refused"));

        return this;
    }

    public AnthropicClient Client() =>
        new() { ApiKey = "sk-ant-test", HttpClient = new HttpClient(this), MaxRetries = 0 };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Dictionary<string, string> headers =
            request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));

        Requests.Add(new Sent(
            request.Method,
            request.RequestUri!,
            headers,
            string.IsNullOrEmpty(body) ? null : JsonNode.Parse(body)));

        return responses.Dequeue()();
    }

    private static string Recorded(string name)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"{name}.sse")
            ?? throw new InvalidOperationException($"There is no recorded stream named {name}.");
        using StreamReader reader = new(stream);

        return reader.ReadToEnd();
    }
}

internal static class Given
{
    public static ToolDescriptor Tool(string name, ToolSide side = ToolSide.Server) =>
        new(
            name,
            name,
            $"The {name} tool.",
            JsonNode.Parse(
                """{"type":"object","properties":{"path":{"type":"string","description":"An id."}},"required":""" +
                """["path"]}""")!.AsObject(),
            new ToolAnnotations(ReadOnlyHint: true, DestructiveHint: false, IdempotentHint: true, OpenWorldHint: false),
            side);

    public static TurnRequest Request(params HistoryMessage[] history) =>
        new(
            [Tool("open_screen", ToolSide.Client), Tool("stack_status")],
            "You are an assistant.",
            [new CatalogueEntry("logs", "Logs", "/logs", "Follow the platform's logs.")],
            history,
            2048);

    public static async Task<List<ProviderEvent>> Played(this IAssistantProvider provider, TurnRequest request)
    {
        List<ProviderEvent> events = [];

        await foreach (ProviderEvent value in provider.TurnAsync(request, CancellationToken.None))
        {
            events.Add(value);
        }

        return events;
    }
}
