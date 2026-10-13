using Common.TestSupport;
using Testcontainers.RabbitMq;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>A real RabbitMQ holding §14.1's definitions, so the account has only its own grant (ADR-036).</summary>
public sealed class RabbitFixture : IAsyncLifetime
{
    /// <summary>A probe queue under the <c>payments-</c> prefix the definitions grant <c>payments-svc</c>.</summary>
    public const string Queue = "payments-operator-switch-probe";

    private readonly RabbitMqContainer _broker = Build();

    private static RabbitMqContainer Build()
    {
        TestcontainersWarmUp.Ensure();
        return new RabbitMqBuilder()
            .WithUsername("payments-svc")
            .WithPassword("local-dev-payments")
            .WithImage(ComposeImage.BaseOf("rabbitmq"))
            // A second copy of the Dockerfile's COPY targets, which check_permissions.py holds to it (ADR-036).
            .WithResourceMapping(
                new FileInfo(Path.Combine(BrokerContextPath(), "definitions.json")),
                "/etc/rabbitmq/")
            .WithResourceMapping(
                new FileInfo(Path.Combine(BrokerContextPath(), "20-commerce.conf")),
                "/etc/rabbitmq/conf.d/")
            .Build();
    }

    public string BrokerAddress => _broker.GetConnectionString();

    // ValueTask, not Task: xUnit v3 redefined IAsyncLifetime (§12.4).
    public async ValueTask InitializeAsync() =>
        await DaemonRetry.StartAsync(_broker, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await _broker.DisposeAsync();

    private static string BrokerContextPath()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "Platform.slnx")))
                continue;

            string context = Path.Combine(dir.FullName, "deploy", "compose", "rabbitmq");
            if (!File.Exists(Path.Combine(context, "Dockerfile")))
            {
                throw new InvalidOperationException(
                    $"Found the solution at {dir.FullName} but no Dockerfile at {context} (§14.1, ADR-021).");
            }

            return context;
        }

        throw new InvalidOperationException(
            $"No Platform.slnx above {AppContext.BaseDirectory}; the broker's definitions cannot be mapped.");
    }
}
