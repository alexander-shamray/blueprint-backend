using Common.Application;
using Common.Domain;

namespace Payments.TestSupport.Outbox;

/// <summary>
/// The <c>Local</c> lane's cases, as real domain events in an
/// assembly the fixture adds to <c>MessageTypeSource</c> (§9.4). Payments
/// registers no projection handler of its own — §8.4's cache invalidator
/// needs a cached query to invalidate, and there is not one yet — so the
/// lane's behaviour is proven here rather than by inventing a read model
/// for it. In <c>Payments.TestSupport</c> because the fixture that
/// registers them is here.
/// </summary>
public sealed record AlwaysThrows : IDomainEvent
{
    public DateTimeOffset OccurredAt { get; init; }
}

public sealed record NoOpEvent : IDomainEvent
{
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// Somewhere to put a payload longer than §7.2's 400-character string
    /// convention, which the outbox's <c>Payload</c> column has to outgrow.
    /// </summary>
    public string Note { get; init; } = "";
}

/// <summary>
/// Has no handler at all, which is the state §9.4 throws on: a <c>Local</c>
/// row was staged, so a handler <em>was</em> found earlier, so finding none
/// now means one was implemented and never registered.
/// </summary>
public sealed record UnhandledEvent : IDomainEvent
{
    public DateTimeOffset OccurredAt { get; init; }
}

/// <summary>
/// Blocks its handler until a test releases it, so a claim can be observed
/// while it is still held. Nothing else can show the 60-second lease: a row
/// that fails immediately has its lease replaced by the retry backoff, and one
/// that succeeds is processed before a second pass could run.
/// </summary>
public sealed record BlocksUntilReleased : IDomainEvent
{
    public DateTimeOffset OccurredAt { get; init; }
}

/// <summary>
/// Holds <see cref="BlocksUntilReleased"/>'s handler until a test opens it.
/// Static because the handler is resolved by the container per row and a test
/// has no reference to the instance — and reset by each test that uses it, so
/// no state carries between them (§12.8).
/// </summary>
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
