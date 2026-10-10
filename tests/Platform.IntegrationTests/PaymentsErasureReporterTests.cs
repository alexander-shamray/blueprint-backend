using Common.Contracts.Privacy.V1;
using MassTransit;
using Payments.Infrastructure.Messaging;
using Shouldly;
using Xunit;

namespace Platform.IntegrationTests;

/// <summary>What Payments tells Privacy, and where, which the endpoint test cannot read back off the queue.</summary>
public class PaymentsErasureReporterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Request = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task The_completion_names_payments_and_the_request_and_goes_to_privacys_queue()
    {
        RecordingProvider provider = new();
        ErasureReporter reporter = new(provider, new FixedClock(Now));

        await reporter.ReportAsync(Request, 3, TestContext.Current.CancellationToken);

        provider.Addresses.ShouldBe([new Uri("queue:privacy-completions")]);
        PersonalDataDeleteCompleted sent = provider.Sent
            .ShouldHaveSingleItem()
            .ShouldBeOfType<PersonalDataDeleteCompleted>();
        sent.RequestId.ShouldBe(Request);
        sent.CorrelationId.ShouldBe(Request);
        sent.Responder.ShouldBe("payments");
        sent.Count.ShouldBe(3);
        sent.OccurredAt.ShouldBe(Now);
        sent.MessageId.ShouldNotBe(Guid.Empty);
    }

    private sealed class RecordingProvider : ISendEndpointProvider
    {
        public List<Uri> Addresses { get; } = [];

        public List<object> Sent { get; } = [];

        public Task<ISendEndpoint> GetSendEndpoint(Uri address)
        {
            Addresses.Add(address);
            return Task.FromResult<ISendEndpoint>(new RecordingEndpoint(Sent));
        }

        public ConnectHandle ConnectSendObserver(ISendObserver observer) =>
            throw new NotSupportedException("Not exercised.");
    }

    private sealed class RecordingEndpoint(List<object> sent) : ISendEndpoint
    {
        public Task Send<T>(T message, CancellationToken cancellationToken = default)
            where T : class
        {
            sent.Add(message);
            return Task.CompletedTask;
        }

        public Task Send<T>(T message, IPipe<SendContext<T>> pipe, CancellationToken cancellationToken = default)
            where T : class =>
            Send(message, cancellationToken);

        public Task Send<T>(T message, IPipe<SendContext> pipe, CancellationToken cancellationToken = default)
            where T : class =>
            Send(message, cancellationToken);

        public Task Send(object message, CancellationToken cancellationToken = default) =>
            Send<object>(message, cancellationToken);

        public Task Send(object message, Type messageType, CancellationToken cancellationToken = default) =>
            Send<object>(message, cancellationToken);

        public Task Send(object message, IPipe<SendContext> pipe, CancellationToken cancellationToken = default) =>
            Send<object>(message, cancellationToken);

        public Task Send(
            object message,
            Type messageType,
            IPipe<SendContext> pipe,
            CancellationToken cancellationToken = default) =>
            Send<object>(message, cancellationToken);

        public Task Send<T>(object values, CancellationToken cancellationToken = default)
            where T : class =>
            throw new NotSupportedException("Not exercised.");

        public Task Send<T>(object values, IPipe<SendContext<T>> pipe, CancellationToken cancellationToken = default)
            where T : class =>
            throw new NotSupportedException("Not exercised.");

        public Task Send<T>(object values, IPipe<SendContext> pipe, CancellationToken cancellationToken = default)
            where T : class =>
            throw new NotSupportedException("Not exercised.");

        public ConnectHandle ConnectSendObserver(ISendObserver observer) =>
            throw new NotSupportedException("Not exercised.");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
