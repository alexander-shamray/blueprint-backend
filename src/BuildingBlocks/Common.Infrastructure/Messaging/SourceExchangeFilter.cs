using MassTransit;
using Microsoft.Extensions.Logging;

namespace Common.Infrastructure.Messaging;

/// <summary>A delivery reaches a consumer only through its type's exchange or the endpoint's own (ADR-191).</summary>
/// <remarks>
/// The envelope's message type picks the consumer and is the publisher's to write, so a peer granted only its own
/// exchanges could otherwise name another context's event on a queue that binds both (ADR-036).
/// </remarks>
public sealed class SourceExchangeFilter<T>(IBus bus, ILogger<SourceExchangeFilter<T>> log) : IFilter<ConsumeContext<T>>
    where T : class
{
    // The type arguments bind to the template by position, not by name.
    private static readonly Action<ILogger, string, string?, string, Guid?, Exception?> Refused =
        LoggerMessage.Define<string, string?, string, Guid?>(
            LogLevel.Warning,
            new EventId(1, nameof(Refused)),
            "Refused {MessageType} from exchange {Exchange} on {Endpoint}, message {MessageId}: it is neither the " +
            "type's exchange nor the endpoint's own.");

    public Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
    {
        Uri input = context.ReceiveContext.InputAddress;

        // The in-memory test transport has no exchanges to compare; any other without one is refused, not trusted.
        if (input.Scheme == "loopback")
            return next.Send(context);

        string endpoint = input.Segments[^1];
        string? exchange = context.TryGetPayload(out RabbitMqBasicConsumeContext? rabbit) ? rabbit.Exchange : null;
        string typeExchange = bus.Topology.Message<T>().EntityName;

        if (exchange is not null && SourceExchange.Admits(exchange, typeExchange, endpoint))
            return next.Send(context);

        // Not consumed by this pipe: with no other consumer taking it, MassTransit parks it in <queue>_skipped,
        // which pages (§13.6); a throw would spend the endpoint's retries on a message no retry admits.
        Refused(log, typeof(T).Name, exchange, endpoint, context.MessageId, null);
        return Task.CompletedTask;
    }

    /// <summary>MassTransit's diagnostic probe; the scope name identifies this filter in its probe result.</summary>
    public void Probe(ProbeContext context) => context.CreateFilterScope("source-exchange");
}
