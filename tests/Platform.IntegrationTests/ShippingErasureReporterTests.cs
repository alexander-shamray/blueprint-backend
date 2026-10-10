using Common.Contracts.Privacy.V1;
using MassTransit;
using Shipping.Infrastructure.Messaging;
using Shouldly;
using Xunit;

namespace Platform.IntegrationTests;

/// <summary>What Shipping tells Privacy, and where, which the endpoint test cannot read back off the queue.</summary>
public class ShippingErasureReporterTests
{
    private static readonly Guid Request = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task The_completion_names_shipping_and_the_request_and_goes_to_privacys_queue()
    {
        RecordingProvider provider = new();
        ErasureReporter reporter = new(provider);

        await reporter.ReportAsync(Request, 3, TestContext.Current.CancellationToken);

        provider.Addresses.ShouldBe([new Uri("queue:privacy-completions")]);
        provider.Sent.ShouldHaveSingleItem().ShouldBe(new PersonalDataDeleteCompleted(Request, "shipping", 3));
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
}
