using Microsoft.Extensions.Hosting;

namespace Common.Infrastructure.Outbox;

/// <summary>Resolves <see cref="MessageTypeMap"/> at startup, so its refusals fail the host (§9.4).</summary>
/// <remarks>Not built at registration: <see cref="MessageTypeSource"/> is mutable until the container is.</remarks>
public sealed class MessageTypeMapValidator(MessageTypeMap types) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Resolving it is the job: the constructor throws on a bad map.
        _ = types.StageableDomainEvents;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
