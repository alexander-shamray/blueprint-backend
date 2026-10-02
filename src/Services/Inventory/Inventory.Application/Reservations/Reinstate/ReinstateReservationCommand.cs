using Common.Application;

namespace Inventory.Application.Reservations.Reinstate;

/// <summary>The runbook's reinstatement, keyed so that a repeat whose answer was lost is replayed (§8.5).</summary>
public sealed record ReinstateReservationCommand(Guid CommandId, Guid OrderId) : ICommand<Result>, IIdempotentCommand
{
    /// <summary>Declared, never derived from the type name, so a rename cannot change a live key (§8.5).</summary>
    public static string OperationName => "inventory.reservation.reinstate";
}
