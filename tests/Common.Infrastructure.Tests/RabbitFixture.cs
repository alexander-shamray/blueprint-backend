using Common.TestSupport;
using Testcontainers.RabbitMq;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>A real RabbitMQ on §14.1's stock image, for the suites that prove a transport behaviour (§12.4).</summary>
public sealed class RabbitFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _broker = Build();

    private static RabbitMqContainer Build()
    {
        TestcontainersWarmUp.Ensure();
        return new RabbitMqBuilder().WithImage(ComposeImage.BaseOf("rabbitmq")).Build();
    }

    public string BrokerAddress => _broker.GetConnectionString();

    // ValueTask, not Task: xUnit v3 redefined IAsyncLifetime (§12.4).
    public async ValueTask InitializeAsync() =>
        await DaemonRetry.StartAsync(_broker, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await _broker.DisposeAsync();
}
