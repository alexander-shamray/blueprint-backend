using MassTransit;

namespace Common.Infrastructure.Inbox;

/// <summary>§9.8's nesting on a receive endpoint: §9.5's inbox outermost, the in-memory outbox inside it.</summary>
/// <remarks>
/// Not the two calls typed in order: the inbox is a scoped filter on each consumer's message pipe, and the endpoint's
/// outbox would wrap that pipe and flush after the inbox row commits (§9.8).
/// </remarks>
public static class InboxEndpointExtensions
{
    public static void UseInboxAndInMemoryOutbox(
        this IReceiveEndpointConfigurator endpoint,
        IRegistrationContext context)
    {
        endpoint.UseConsumeFilter(typeof(InboxFilter<>), context);

        // Connected after the inbox's observer, so each consumer's pipe gets the inbox first and the outbox inside it.
        endpoint.ConnectConsumerConfigurationObserver(new InnerInMemoryOutbox(context));
    }

    private sealed class InnerInMemoryOutbox(IRegistrationContext context) : IConsumerConfigurationObserver
    {
        public void ConsumerConfigured<TConsumer>(IConsumerConfigurator<TConsumer> configurator)
            where TConsumer : class
        {
        }

        public void ConsumerMessageConfigured<TConsumer, TMessage>(
            IConsumerMessageConfigurator<TConsumer, TMessage> configurator)
            where TConsumer : class
            where TMessage : class
        {
            if (configurator is not IConsumerMessageConfigurator<TMessage> message)
            {
                throw new ConfigurationException(
                    $"The in-memory outbox could not be added inside the inbox for {typeof(TMessage).Name}.");
            }

            message.UseInMemoryOutbox(context);
        }
    }
}
