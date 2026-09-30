namespace Common.Application.Tests;

/// <summary>One implementation per scanned interface the requests leave uncovered (§6.2).</summary>
public sealed record ScannedEvent(Guid Id);

public sealed class ScannedEventHandler : IIntegrationEventHandler<ScannedEvent>
{
    public Task HandleAsync(ScannedEvent integrationEvent, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>A wire contract, standing in for one a service would accept.</summary>
public sealed record ScannedMessage(Guid Id);

/// <summary>The application command it maps to.</summary>
public sealed record ScannedCommand(Guid Id) : ICommand<Result>;

public sealed class ScannedCommandMapper : ICommandMessageMapper<ScannedMessage, ScannedCommand>
{
    public ScannedCommand Map(ScannedMessage message) => new(message.Id);
}

/// <summary>A projection handler over the same event.</summary>
public sealed class ScannedProjection : IProjectionHandler<ScannedEvent>
{
    public Task HandleAsync(ScannedEvent domainEvent, CancellationToken ct) => Task.CompletedTask;
}
