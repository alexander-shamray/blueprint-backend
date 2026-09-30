using Common.Application;
using Common.Domain;

namespace Catalog.TestSupport.Outbox;

/// <summary>The <c>Local</c> lane's cases, in an assembly the fixture adds to <c>MessageTypeSource</c>.</summary>
public sealed record AlwaysThrows : IDomainEvent
{
    public DateTimeOffset OccurredAt { get; init; }
}

public sealed record NoOpEvent : IDomainEvent
{
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>A payload longer than §7.2's 400-character string convention, which <c>Payload</c> outgrows.</summary>
    public string Note { get; init; } = "";
}

/// <summary>Has no handler at all, the state §9.4 throws on for a staged <c>Local</c> row.</summary>
public sealed record UnhandledEvent : IDomainEvent
{
    public DateTimeOffset OccurredAt { get; init; }
}

/// <summary>Blocks its handler until a test releases it, so a claim's lease can be observed while held.</summary>
public sealed record BlocksUntilReleased : IDomainEvent
{
    public DateTimeOffset OccurredAt { get; init; }
}

/// <summary>Holds <see cref="BlocksUntilReleased"/>'s handler; static, as the container resolves it per row.</summary>
public static class DeliveryGate
{
    private static TaskCompletionSource _open = Opened();

    /// <summary>Signals that a handler has entered and is now waiting.</summary>
    public static TaskCompletionSource Entered { get; private set; } = new();

    public static Task Wait => _open.Task;

    public static void Close()
    {
        _open = new TaskCompletionSource();
        Entered = new TaskCompletionSource();
    }

    public static void Open() => _open.TrySetResult();

    private static TaskCompletionSource Opened()
    {
        TaskCompletionSource source = new();
        source.SetResult();
        return source;
    }
}

/// <summary>Waits on the gate, so the row it belongs to stays claimed.</summary>
public sealed class BlockingProjection : IProjectionHandler<BlocksUntilReleased>
{
    public async Task HandleAsync(BlocksUntilReleased domainEvent, CancellationToken ct)
    {
        DeliveryGate.Entered.TrySetResult();
        await DeliveryGate.Wait;
    }
}

/// <summary>Fails every delivery, so a row backs off and accumulates attempts.</summary>
public sealed class AlwaysThrowsProjection : IProjectionHandler<AlwaysThrows>
{
    public Task HandleAsync(AlwaysThrows domainEvent, CancellationToken ct) =>
        throw new InvalidOperationException("this projection always throws");
}

/// <summary>Succeeds, so a row beside a failing one still completes.</summary>
public sealed class NoOpProjection : IProjectionHandler<NoOpEvent>
{
    public Task HandleAsync(NoOpEvent domainEvent, CancellationToken ct) => Task.CompletedTask;
}
