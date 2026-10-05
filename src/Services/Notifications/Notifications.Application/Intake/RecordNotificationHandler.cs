using Common.Application;
using Microsoft.Extensions.Logging;
using Notifications.Application.Records;
using Notifications.Application.Rendering;

namespace Notifications.Application.Intake;

/// <summary>Writes the notice an event owes and, for Ordering's, the order record, in one transaction (§6.3).</summary>
/// <remarks>
/// It calls nothing outside this service's database, so no fault it meets is a wait (ADR-052); a repeated arrival
/// returns, and a value failing <see cref="InboundValues"/> is dropped and logged by member (§13.4).
/// </remarks>
public sealed class RecordNotificationHandler(
    INotificationRepository notifications,
    IOrderRecordRepository orders,
    TimeProvider clock,
    ILogger<RecordNotificationHandler> log)
    : ICommandHandler<RecordNotificationCommand, Result>
{
    // CA1848 (ADR-019); ids and member names only, never a value another service wrote.
    private static readonly Action<ILogger, string, Guid, string, Exception?> Dropped =
        LoggerMessage.Define<string, Guid, string>(
            LogLevel.Warning,
            new EventId(1, nameof(Dropped)),
            "Dropped {Member} of event {EventId} for {TemplateKey}: it failed the intake's check and was dropped.");

    private static readonly Action<ILogger, Guid, string, Exception?> AlreadyOwed =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Information,
            new EventId(2, nameof(AlreadyOwed)),
            "Event {EventId} already owes {TemplateKey}; a redelivery past the inbox records nothing.");

    private static readonly Action<ILogger, Guid, Exception?> CancellationKept =
        LoggerMessage.Define<Guid>(
            LogLevel.Information,
            new EventId(3, nameof(CancellationKept)),
            "Order {OrderId} is already recorded cancelled; the first cancellation stands.");

    public async Task<Result> HandleAsync(RecordNotificationCommand command, CancellationToken ct)
    {
        DateTimeOffset now = clock.GetUtcNow();
        Guid orderId = command.Parameters.OrderId;

        // Before the notice, and idempotent, so a redelivery that finds the notice still completes the record.
        if (command.Order is OrderFact order)
            await RecordOrderAsync(command, orderId, order, now, ct);

        if (await notifications.ExistsAsync(command.EventId, command.TemplateKey, ct))
        {
            AlreadyOwed(log, command.EventId, command.TemplateKey, null);
            return Result.Success();
        }

        Notification pending = Notification.Pending(
            command.EventId,
            command.CorrelationId,
            command.TemplateKey,
            orderId,
            ParametersFormat.Write(Checked(command)),
            now);

        notifications.Add(pending);

        return Result.Success();
    }

    private async Task RecordOrderAsync(
        RecordNotificationCommand command,
        Guid orderId,
        OrderFact order,
        DateTimeOffset now,
        CancellationToken ct)
    {
        OrderRecord? record = await orders.GetAsync(orderId, ct);

        if (record is null)
        {
            record = OrderRecord.For(orderId, order.CustomerId, now);
            orders.Add(record);
        }

        if (order.Cancellation is not OrderCancellation cancellation)
            return;

        string? reason = Kept(command, nameof(OrderCancellation.Reason), cancellation.Reason, InboundValues.Code);
        string? origin = Kept(command, nameof(OrderCancellation.Origin), cancellation.Origin, InboundValues.Code);

        // Set once and never cleared: the tombstone a late OrderPlaced finds and leaves alone (ADR-049).
        if (!record.Cancel(reason, origin, cancellation.At))
            CancellationKept(log, orderId, null);
    }

    private NotificationParameters Checked(RecordNotificationCommand command)
    {
        NotificationParameters given = command.Parameters;

        return given with
        {
            Currency = Kept(command, nameof(given.Currency), given.Currency, InboundValues.Currency),
            TrackingNumber = Kept(
                command,
                nameof(given.TrackingNumber),
                given.TrackingNumber,
                v => InboundValues.Text(v, InboundValues.MaxTrackingNumberLength)),
            CancelReason = Kept(command, nameof(given.CancelReason), given.CancelReason, InboundValues.Code),
        };
    }

    private string? Kept(RecordNotificationCommand command, string member, string? given, Func<string?, string?> check)
    {
        string? kept = check(given);

        if (given is not null && kept is null)
            Dropped(log, member, command.EventId, command.TemplateKey, null);

        return kept;
    }
}
