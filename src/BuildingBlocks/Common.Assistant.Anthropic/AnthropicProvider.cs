using System.Runtime.CompilerServices;
using Common.Assistant.Protocol;
using Common.Assistant.Providers;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace Common.Assistant.Anthropic;

/// <summary>
/// The Claude models behind <see cref="IAssistantProvider"/> (AI assistant design §8.2). It takes the SDK client its
/// host built and never builds one itself. Nothing here reads, keeps or reports a credential.
/// </summary>
public sealed class AnthropicProvider : IAssistantProvider
{
    public const string ProviderId = "anthropic";

    // A probe that found a credential is trusted for a while; one that did not is asked again sooner, so a login made
    // after the host started is noticed without a restart.
    private static readonly TimeSpan Trusted = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Doubted = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly AnthropicClient client;
    private readonly AnthropicSettings settings;
    private readonly TimeProvider time;
    private readonly Action<string>? probeFailed;
    private readonly Lock gate = new();

    private ProviderStatus? known;
    private DateTimeOffset knownUntil;
    private Task<ProviderStatus>? inFlight;

    /// <param name="probeFailed">
    /// Told the exception's type name when a probe is <c>unreachable</c>, so a host can log why: a wrong model id, a
    /// rate limit and a network failure share that one status. Never the message, which can echo what the API sent.
    /// </param>
    public AnthropicProvider(
        AnthropicClient client,
        AnthropicSettings settings,
        TimeProvider time,
        Action<string>? probeFailed = null)
    {
        // Where a credential comes from is the host's: a local host can leave it to the SDK's own resolution
        // (ANTHROPIC_API_KEY, or the profile ant auth login stores), and a deployed host can build the client from its
        // own secret store.
        this.client = client;
        this.settings = settings;
        this.time = time;
        this.probeFailed = probeFailed;
    }

    public string Id => ProviderId;

    /// <summary>
    /// Whether the model can be reached with the credential the SDK resolved: one authenticated read of the model's
    /// record, which costs no tokens. A rejected credential, or none, is <c>no-credential</c>; anything else that keeps
    /// the read from answering is <c>unreachable</c>.
    /// </summary>
    public ValueTask<ProviderStatus> StatusAsync(CancellationToken cancellationToken)
    {
        Task<ProviderStatus> asked;

        lock (gate)
        {
            if (known is not null && time.GetUtcNow() < knownUntil)
            {
                return ValueTask.FromResult(known);
            }

            // Callers that arrive while a probe is out share it, and none of them can cancel it for the others. A
            // finished probe is never shared: a probe that completes before this line returns would otherwise be
            // remembered.
            if (inFlight is null || inFlight.IsCompleted)
            {
                inFlight = RefreshAsync();
            }

            asked = inFlight;
        }

        return new ValueTask<ProviderStatus>(asked.WaitAsync(cancellationToken));
    }

    private async Task<ProviderStatus> RefreshAsync()
    {
        ProviderStatus status = await ProbeAsync();

        lock (gate)
        {
            known = status;
            knownUntil = time.GetUtcNow() + (status.Available ? Trusted : Doubted);
        }

        return status;
    }

    public async IAsyncEnumerable<ProviderEvent> TurnAsync(
        TurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        MessageCreateParams parameters = RequestBuilder.Build(request, settings);
        StreamMapper mapper = new();

        await foreach (BetaRawMessageStreamEvent streamEvent
            in client.Beta.Messages.CreateStreaming(parameters, cancellationToken))
        {
            foreach (ProviderEvent value in mapper.On(streamEvent))
            {
                yield return value;
            }
        }

        // A stream that ends without `message_stop` was cut off: a half reply must not be taken for a finished one, and
        // the core ends a turn it is told failed.
        if (!mapper.Stopped)
        {
            throw new IOException("The provider's stream ended before the reply was complete.");
        }
    }

    private async Task<ProviderStatus> ProbeAsync()
    {
        using CancellationTokenSource timeout = new(ProbeTimeout);

        try
        {
            await client.Models.Retrieve(settings.Model, cancellationToken: timeout.Token);

            return new ProviderStatus(true, settings.Model, null);
        }
        catch (Exception e) when (e is AnthropicUnauthorizedException or AnthropicForbiddenException)
        {
            return new ProviderStatus(false, settings.Model, StatusReason.NoCredential);
        }
        catch (Exception e)
        {
            // A connection that fails, a timeout, a 5xx: whatever kept the read from answering.
            Tell(e);

            return new ProviderStatus(false, settings.Model, StatusReason.Unreachable);
        }
    }

    private void Tell(Exception failure)
    {
        try
        {
            probeFailed?.Invoke(failure.GetType().Name);
        }
        catch (Exception)
        {
            // A hook that fails, a logger's sink for one, must not turn an unreachable status into a faulted probe.
        }
    }
}
