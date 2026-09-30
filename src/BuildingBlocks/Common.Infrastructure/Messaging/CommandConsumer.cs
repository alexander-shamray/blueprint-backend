using Common.Application;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace Common.Infrastructure.Messaging;

/// <summary>§9.4's consumer for commands, which it dispatches through §6.3's behaviours as HTTP does.</summary>
public sealed class CommandConsumer<TMessage, TCommand>(
    IDispatcher dispatcher,
    ICommandMessageMapper<TMessage, TCommand> mapper,
    MessagingMetrics metrics,
    ILogger<CommandConsumer<TMessage, TCommand>> log)
    : IConsumer<TMessage>
    where TMessage : class
    where TCommand : ICommand<Result>
{
    // Compiled once per closed consumer, for CA1848 (ADR-019).
    private static readonly Action<ILogger, string, string, string, Guid?, Exception?> DomainRejected =
        LoggerMessage.Define<string, string, string, Guid?>(
            LogLevel.Warning,
            new EventId(1, nameof(DomainRejected)),
            "{MessageType} rejected by the domain: {ErrorCode} {ErrorDescription}. " +
            "CorrelationId {CorrelationId}.");

    public async Task Consume(ConsumeContext<TMessage> context)
    {
        // A ContractMappingException from here is excluded from retry by the endpoint's policy (§9.8).
        TCommand command = mapper.Map(context.Message);

        Result result = await dispatcher.SendAsync(command, context.CancellationToken);

        // A domain rejection is acked, counted and logged rather than thrown (§9.8).
        // Unavailable is a fault time may fix, and no caller is left to retry it, so it throws.
        if (result.IsFailure && result.Error.Type == ErrorType.Unavailable)
            throw new UnavailableResultException(result.Error);

        if (result.IsFailure)
        {
            metrics.Rejected(typeof(TMessage).Name, result.Error.Code);

            DomainRejected(
                log,
                typeof(TMessage).Name,
                result.Error.Code,
                result.Error.Description,
                context.CorrelationId,
                null);
        }
    }
}
