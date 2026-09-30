namespace Payments.Application.Admin.GetPayment;

/// <summary>What Payments holds for one order, without the payer: an operator needs the money's state.</summary>
public sealed record PaymentView(Guid OrderId, OrderView Order, IntentView? Intent, RefundView? Refund);

/// <summary>Both nullable, since either event can arrive first (§9.4).</summary>
public sealed record OrderView(DateTimeOffset? PlacedAt, DateTimeOffset? CancelledAt);

public sealed record IntentView(
    string Status,
    string? Reference,
    decimal Amount,
    string Currency,
    string? DeclineReason,
    DateTimeOffset CreatedAt);

public sealed record RefundView(string Reference, DateTimeOffset VoidedAt);
