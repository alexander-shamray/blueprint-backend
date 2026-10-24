using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Common.Assistant.Protocol;
using Common.Assistant.Providers;
using Common.Assistant.Tools;

namespace Common.Assistant.Tests;

/// <summary>A provider that plays one scripted round per request and keeps every request it was sent.</summary>
internal sealed class ScriptedProvider : IAssistantProvider
{
    private readonly Queue<Func<TurnRequest, CancellationToken, IAsyncEnumerable<ProviderEvent>>> rounds = new();

    public List<TurnRequest> Requests { get; } = [];

    public string Id => "scripted";

    public ScriptedProvider Round(params ProviderEvent[] events)
    {
        rounds.Enqueue((_, _) => Play(events));

        return this;
    }

    public ScriptedProvider Round(Func<TurnRequest, CancellationToken, IAsyncEnumerable<ProviderEvent>> round)
    {
        rounds.Enqueue(round);

        return this;
    }

    public ValueTask<ProviderStatus> StatusAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ProviderStatus(true, "scripted-1", null));

    public IAsyncEnumerable<ProviderEvent> TurnAsync(TurnRequest request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        return rounds.Dequeue()(request, cancellationToken);
    }

    private static async IAsyncEnumerable<ProviderEvent> Play(ProviderEvent[] events)
    {
        foreach (ProviderEvent value in events)
        {
            await Task.Yield();

            yield return value;
        }
    }

    /// <summary>A round that streams some text, then waits until it is cancelled.</summary>
    public static async IAsyncEnumerable<ProviderEvent> Hang(
        string text,
        TaskCompletionSource started,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new TextDelta(text);

        started.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }
}

/// <summary>A host source of read-only tools whose answers the test decides.</summary>
internal sealed class ScriptedTools(
    params (string Name, Func<JsonObject, ToolContext, CancellationToken, Task<ToolOutcome>> Run)[] tools)
    : IToolSource
{
    public IReadOnlyList<ToolDescriptor> Tools { get; } =
        [.. tools.Select(t => Given.ReadOnly(t.Name, ToolSide.Server))];

    public List<(string Name, JsonObject Input, ToolContext Context)> Calls { get; } = [];

    public async ValueTask<ToolOutcome> InvokeAsync(
        string name,
        JsonObject input,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        lock (Calls)
        {
            Calls.Add((name, input, context));
        }

        return await tools.Single(t => t.Name == name).Run(input, context, cancellationToken);
    }
}

/// <summary>A redactor that replaces each planted secret, so a test can see it never reached the provider.</summary>
internal sealed class PlantedRedactor(params string[] secrets) : IRedactor
{
    public string Redact(string text, Caller caller) =>
        secrets.Aggregate(text, (redacted, secret) => redacted.Replace(secret, "<redacted>", StringComparison.Ordinal));
}

internal static class Given
{
    public static ToolDescriptor ReadOnly(string name, ToolSide side) => new(
        name,
        name,
        $"Test tool {name}.",
        new JsonObject { ["type"] = "object" },
        new ToolAnnotations(ReadOnlyHint: true, DestructiveHint: false, IdempotentHint: true, OpenWorldHint: false),
        side);

    public static OpenConversation Open(string locale = "en-US", params ToolDescriptor[] clientTools) => new(
        new ClientInfo("test", "1"),
        locale,
        [new CatalogueEntry("logs", "Logs", "/logs", "The platform's log lines.")],
        clientTools,
        new JsonObject { ["identity"] = "demo" });

    public static StartTurn Turn(string text, string? locale = null) =>
        new(text, new JsonObject { ["page"] = "stack" }, locale, null);

    /// <summary>Follows the conversation's events until the running turn ends, and returns them.</summary>
    public static async Task<List<AssistantEvent>> UntilEnded(AssistantSession session, long afterId = 0)
    {
        List<AssistantEvent> seen = [];
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        await foreach ((long _, AssistantEvent value) in session.Events(afterId, timeout.Token))
        {
            seen.Add(value);

            if (value is TurnEndedEvent)
            {
                break;
            }
        }

        return seen;
    }
}
