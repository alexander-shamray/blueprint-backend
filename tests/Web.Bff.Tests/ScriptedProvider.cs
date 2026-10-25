using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Common.Assistant.Providers;

namespace Web.Bff.Tests;

/// <summary>A provider whose rounds a test scripts, recording every request the core sends it (§12.4).</summary>
public sealed class ScriptedProvider : IAssistantProvider
{
    public const string Reply = "Your order is on its way.";

    private readonly ConcurrentQueue<ProviderEvent[]> _rounds = new();

    private readonly ConcurrentQueue<TurnRequest> _requests = new();

    public string Id => "scripted";

    public IReadOnlyList<TurnRequest> Requests => [.. _requests];

    /// <summary>Queues one round; a turn with none queued answers <see cref="Reply"/> and ends.</summary>
    public void Then(params ProviderEvent[] round) => _rounds.Enqueue(round);

    public ValueTask<ProviderStatus> StatusAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ProviderStatus(true, "scripted-model", null));

    public async IAsyncEnumerable<ProviderEvent> TurnAsync(
        TurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _requests.Enqueue(request);

        ProviderEvent[] round = _rounds.TryDequeue(out ProviderEvent[]? scripted)
            ? scripted
            : [new TextDelta(Reply), new RoundEnd(ProviderStop.Done)];

        foreach (ProviderEvent providerEvent in round)
        {
            await Task.Yield();

            yield return providerEvent;
        }
    }
}
