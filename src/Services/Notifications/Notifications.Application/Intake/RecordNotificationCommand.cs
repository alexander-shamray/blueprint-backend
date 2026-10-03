using Common.Application;
using Notifications.Application.Rendering;

namespace Notifications.Application.Intake;

/// <summary>An event owes a notice; one of Ordering's also says what the order record keeps.</summary>
/// <remarks>Its values are as published; <see cref="RecordNotificationHandler"/> checks each one.</remarks>
public sealed record RecordNotificationCommand(
    Guid EventId,
    string TemplateKey,
    NotificationParameters Parameters,
    OrderFact? Order) : ICommand<Result>;

/// <summary>What one of Ordering's events says of its order: whose it is, and whether it was cancelled.</summary>
public sealed record OrderFact(Guid CustomerId, OrderCancellation? Cancellation);

/// <summary>An <c>OrderCancelled</c>'s reason and origin as published, and its instant.</summary>
public sealed record OrderCancellation(string Reason, string? Origin, DateTimeOffset At);
