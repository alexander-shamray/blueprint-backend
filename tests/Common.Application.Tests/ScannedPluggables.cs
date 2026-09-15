namespace Common.Application.Tests;

/// <summary>
/// One implementation per entry in <see cref="PluggableInterfaces.All"/> that
/// the request types next door do not already cover, so the §6.2 scan is
/// exercised for every interface rather than for the ones a handler happens to
/// implement.
/// </summary>
/// <remarks>
/// A guard that asks <c>PluggableInterfaces.All</c> what to look for cannot
/// fail for an entry deleted from it; naming each closed type in source can.
/// </remarks>
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
