using MassTransit;

namespace Ordering.Infrastructure.Messaging;

/// <summary>§9.6's saga instance: every field its transitions need, and nothing else.</summary>
public sealed class OrderFulfilmentState : SagaStateMachineInstance
{
    /// <summary>Always the order's id, since §9.6 correlates every event by <c>OrderId</c>.</summary>
    public Guid CorrelationId { get; set; }

    public string CurrentState { get; set; } = null!;

    /// <summary>The same value as <see cref="CorrelationId"/>, assigned once in <c>Initially</c>.</summary>
    public Guid OrderId { get; set; }

    // No CustomerId, and its absence is load-bearing (ADR-028): no command this machine sends carries a subject.
    public decimal Total { get; set; }

    public string Currency { get; set; } = null!;

    public DateTimeOffset StartedAt { get; set; }

    /// <summary>Set on entry to <c>Compensating</c>, read by its two stock exits.</summary>
    public string CancelReason { get; set; } = null!;

    /// <summary>A verdict is owed; a timeout ends the wait, not this obligation.</summary>
    /// <remarks>Stored: <c>Compensating</c> is reached five ways and cannot derive it (§9.6, ADR-025).</remarks>
    public bool PaymentVerdictOutstanding { get; set; }

    /// <summary>The stock half has come to rest, released or given up on.</summary>
    /// <remarks>Read with <see cref="PaymentVerdictOutstanding"/>, never alone (ADR-025).</remarks>
    public bool StockReleaseSettled { get; set; }

    /// <summary>A <c>StockReleased</c> arrived in a state that sent no <c>ReleaseStock</c>; never cleared.</summary>
    /// <remarks>Narrows the window before the saga's own cancellation lands; it does not close it (§9.6).</remarks>
    public bool CancellationObserved { get; set; }

    // One token per schedule. ADR-021's scheduler cannot cancel, but MassTransit discards a scheduled message
    // whose token no longer matches, so a stale timeout reaches no transition (§9.6).
    public Guid? StockTimeoutTokenId { get; set; }

    public Guid? PaymentTimeoutTokenId { get; set; }

    public Guid? ConfirmationTimeoutTokenId { get; set; }

    public Guid? DespatchTimeoutTokenId { get; set; }

    public Guid? ReleaseTimeoutTokenId { get; set; }
}
