using System.Runtime.CompilerServices;
using Common.Assistant.Providers;

namespace Web.Bff.Assistant;

/// <summary>The provider <c>Assistant:Provider</c> calls <c>scripted</c>: one fixed reply, a word at a time.</summary>
/// <remarks>
/// It needs no key and calls nothing, so a stack with no provider account can drive the assistant end to end; the
/// options refuse it outside Development (ADR-156).
/// </remarks>
public sealed class ScriptedReplyProvider : IAssistantProvider
{
    public const string Model = "scripted";

    public const string Reply = "This is the scripted assistant, so no model read your message. It answers every " +
        "turn with this reply.";

    public string Id => AssistantOptions.Scripted;

    public ValueTask<ProviderStatus> StatusAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ProviderStatus(true, Model, null));

    public async IAsyncEnumerable<ProviderEvent> TurnAsync(
        TurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string[] words = Reply.Split(' ');

        for (int i = 0; i < words.Length; i++)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();

            yield return new TextDelta(i == 0 ? words[i] : $" {words[i]}");
        }

        yield return new RoundEnd(ProviderStop.Done);
    }
}
