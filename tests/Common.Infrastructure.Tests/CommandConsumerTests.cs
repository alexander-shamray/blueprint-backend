using Common.Application;
using Common.Infrastructure.Messaging;
using MassTransit;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>§9.4's command consumer, whose domain rejections are acked and counted (§9.8).</summary>
public class CommandConsumerTests
{
    public sealed record ProbeMessage(Guid OrderId);

    public sealed record ProbeCommand(Guid OrderId) : ICommand<Result>;

    private static readonly Error Refused =
        new("probe.refused", "the domain refused this", ErrorType.Rule);

    private static readonly Error Unreachable =
        new("probe.unreachable", "a dependency is down", ErrorType.Unavailable);

    private static CommandConsumer<ProbeMessage, ProbeCommand> Build(
        Result outcome,
        MessagingMetrics metrics,
        ILogger<CommandConsumer<ProbeMessage, ProbeCommand>>? log = null)
    {
        IDispatcher dispatcher = Substitute.For<IDispatcher>();
        dispatcher
            .SendAsync(Arg.Any<ICommand<Result>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(outcome));

        ICommandMessageMapper<ProbeMessage, ProbeCommand> mapper =
            Substitute.For<ICommandMessageMapper<ProbeMessage, ProbeCommand>>();
        mapper.Map(Arg.Any<ProbeMessage>()).Returns(c => new ProbeCommand(c.Arg<ProbeMessage>().OrderId));

        return new CommandConsumer<ProbeMessage, ProbeCommand>(
            dispatcher,
            mapper,
            metrics,
            log ?? Substitute.For<ILogger<CommandConsumer<ProbeMessage, ProbeCommand>>>());
    }

    private static ConsumeContext<ProbeMessage> Context(ProbeMessage message)
    {
        ConsumeContext<ProbeMessage> context = Substitute.For<ConsumeContext<ProbeMessage>>();
        context.Message.Returns(message);
        context.CancellationToken.Returns(TestContext.Current.CancellationToken);
        context.CorrelationId.Returns(message.OrderId);

        return context;
    }

    private static MessagingMetrics Metrics() =>
        new(new TestMeterFactory());

    [Fact]
    public async Task A_message_borne_command_goes_through_the_application_dispatcher()
    {
        IDispatcher dispatcher = Substitute.For<IDispatcher>();
        dispatcher
            .SendAsync(Arg.Any<ICommand<Result>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        ICommandMessageMapper<ProbeMessage, ProbeCommand> mapper =
            Substitute.For<ICommandMessageMapper<ProbeMessage, ProbeCommand>>();
        var orderId = Guid.CreateVersion7();
        mapper.Map(Arg.Any<ProbeMessage>()).Returns(new ProbeCommand(orderId));

        CommandConsumer<ProbeMessage, ProbeCommand> consumer = new(
            dispatcher,
            mapper,
            Metrics(),
            Substitute.For<ILogger<CommandConsumer<ProbeMessage, ProbeCommand>>>());

        await consumer.Consume(Context(new ProbeMessage(orderId)));

        // The mapped command, not the wire message (§9.4).
        await dispatcher.Received(1).SendAsync(
            Arg.Is<ICommand<Result>>(c => ((ProbeCommand)c).OrderId == orderId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_domain_rejection_is_acked_rather_than_thrown()
    {
        // §9.8: a domain rejection is neither retried nor thrown.
        CommandConsumer<ProbeMessage, ProbeCommand> consumer =
            Build(Result.Failure(Refused), Metrics());

        await Should.NotThrowAsync(() => consumer.Consume(Context(new ProbeMessage(Guid.CreateVersion7()))));
    }

    [Fact]
    public async Task A_domain_rejection_is_counted_with_the_error_code_as_its_tag()
    {
        using TestMeterFactory factory = new();
        using RecordedMeasurements measurements = new(factory, "Commerce.Messaging");

        CommandConsumer<ProbeMessage, ProbeCommand> consumer =
            Build(Result.Failure(Refused), new MessagingMetrics(factory));

        await consumer.Consume(Context(new ProbeMessage(Guid.CreateVersion7())));

        RecordedMeasurements.Measurement rejection =
            measurements.For("command.domain_rejected").ShouldHaveSingleItem();

        rejection.Value.ShouldBe(1);
        rejection.Tag("message").ShouldBe(nameof(ProbeMessage));

        rejection.Tag("error").ShouldBe(Refused.Code);
    }

    [Fact]
    public async Task A_successful_command_records_no_rejection()
    {
        using TestMeterFactory factory = new();
        using RecordedMeasurements measurements = new(factory, "Commerce.Messaging");

        CommandConsumer<ProbeMessage, ProbeCommand> consumer =
            Build(Result.Success(), new MessagingMetrics(factory));

        await consumer.Consume(Context(new ProbeMessage(Guid.CreateVersion7())));

        measurements.For("command.domain_rejected").ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unavailable_result_is_thrown_so_the_retry_policy_can_see_it()
    {
        // §9.8: retry is for faults time might fix, and Unavailable is one arriving as a value.
        CommandConsumer<ProbeMessage, ProbeCommand> consumer =
            Build(Result.Failure(Unreachable), Metrics());

        UnavailableResultException thrown = await Should.ThrowAsync<UnavailableResultException>(
            () => consumer.Consume(Context(new ProbeMessage(Guid.CreateVersion7()))));

        // The code travels with the fault, the only record this outcome gets.
        thrown.Error?.Code.ShouldBe(Unreachable.Code);
    }

    [Fact]
    public async Task An_unavailable_result_is_not_counted_as_a_domain_rejection()
    {
        using TestMeterFactory factory = new();
        using RecordedMeasurements measurements = new(factory, "Commerce.Messaging");

        CommandConsumer<ProbeMessage, ProbeCommand> consumer =
            Build(Result.Failure(Unreachable), new MessagingMetrics(factory));

        await Should.ThrowAsync<UnavailableResultException>(
            () => consumer.Consume(Context(new ProbeMessage(Guid.CreateVersion7()))));

        measurements.For("command.domain_rejected").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_mapping_failure_propagates_so_the_endpoint_policy_can_see_it()
    {
        // Excluded from retry by §9.8 only if it leaves the consumer.
        IDispatcher dispatcher = Substitute.For<IDispatcher>();

        ICommandMessageMapper<ProbeMessage, ProbeCommand> mapper =
            Substitute.For<ICommandMessageMapper<ProbeMessage, ProbeCommand>>();
        mapper
            .Map(Arg.Any<ProbeMessage>())
            .Returns(_ => throw new ContractMappingException("unknown reason code 'wat'"));

        CommandConsumer<ProbeMessage, ProbeCommand> consumer = new(
            dispatcher,
            mapper,
            Metrics(),
            Substitute.For<ILogger<CommandConsumer<ProbeMessage, ProbeCommand>>>());

        await Should.ThrowAsync<ContractMappingException>(
            () => consumer.Consume(Context(new ProbeMessage(Guid.CreateVersion7()))));

        await dispatcher.DidNotReceive().SendAsync(
            Arg.Any<ICommand<Result>>(),
            Arg.Any<CancellationToken>());
    }
}
