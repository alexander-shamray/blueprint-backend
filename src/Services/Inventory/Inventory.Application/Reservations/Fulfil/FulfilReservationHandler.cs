using Common.Application;
using Inventory.Domain.Reservations;
using Microsoft.Extensions.Logging;

namespace Inventory.Application.Reservations.Fulfil;

/// <summary>§3.2's derivation for ShipmentDispatched: the stock has left.</summary>
public sealed class FulfilReservationHandler(
    IReservationRepository reservations,
    IStockLedger ledger,
    TimeProvider clock,
    ILogger<FulfilReservationHandler> log)
    : ICommandHandler<FulfilReservationCommand, Result>
{
    private static readonly Action<ILogger, Guid, string, Exception?> NothingHeld =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(1, nameof(NothingHeld)),
            "ShipmentDispatched for order {OrderId} met no held reservation: {State}.");

    private static readonly Action<ILogger, Guid, Exception?> Unreserved =
        LoggerMessage.Define<Guid>(
            LogLevel.Warning,
            new EventId(2, nameof(Unreserved)),
            "ShipmentDispatched for order {OrderId} met a released reservation: the level is now " +
            "wrong by its lines (ADR-029).");

    public async Task<Result> HandleAsync(FulfilReservationCommand command, CancellationToken ct)
    {
        var order = new OrderId(command.OrderId);
        DateTimeOffset now = clock.GetUtcNow();
        Reservation? reservation = await reservations.GetForUpdateAsync(order, ct);

        switch (reservation?.Status)
        {
            case null:
                NothingHeld(log, command.OrderId, "no reservation", null);
                return Result.Success();

            case ReservationStatus.Failed:
                NothingHeld(log, command.OrderId, "the reserve failed", null);
                return Result.Success();

            case ReservationStatus.Released when reservation.Lines.Count == 0:
                NothingHeld(log, command.OrderId, "a tombstone", null);
                return Result.Success();

            case ReservationStatus.Released:
                Unreserved(log, command.OrderId, null);
                reservation.RecordDespatchUnreserved(now);
                return Result.Success();

            case ReservationStatus.Fulfilled:
                return Result.Success();

            case ReservationStatus.Reserved:
                await ledger.FulfilAsync(reservation.Lines, ct);
                reservation.Fulfil(now);
                return Result.Success();

            default:
                throw new InvalidOperationException($"No fulfilment path for {reservation.Status}.");
        }
    }
}
