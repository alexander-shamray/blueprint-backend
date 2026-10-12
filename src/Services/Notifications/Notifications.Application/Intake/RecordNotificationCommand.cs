using Common.Application;
using Notifications.Application.Rendering;

namespace Notifications.Application.Intake;

/// <summary>An event owes a notice; one of Ordering's also says what the order record keeps.</summary>
/// <remarks>Its values are as published; <see cref="RecordNotificationHandler"/> checks each text value.</remarks>
public sealed record RecordNotificationCommand(
    Guid EventId,
    Guid CorrelationId,
    string TemplateKey,
    NotificationParameters Parameters,
    OrderFact? Order) : ICommand<Result>;

/// <summary>
/// What one of Ordering's events says of its order: whose it is, whether it was cancelled, and the erasure requests
/// its customer was already under when it was placed, which an older publisher does not say (ADR-099).
/// </summary>
public sealed record OrderFact(
    Guid CustomerId,
    OrderCancellation? Cancellation,
    IReadOnlyList<Guid>? ErasedUnder = null);

/// <summary>An <c>OrderCancelled</c>'s reason and origin as published, and its instant.</summary>
public sealed record OrderCancellation(string Reason, string? Origin, DateTimeOffset At);
