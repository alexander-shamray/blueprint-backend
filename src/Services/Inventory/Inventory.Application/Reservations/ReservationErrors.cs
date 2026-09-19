using Common.Application;
using Inventory.Domain.Stock;

namespace Inventory.Application.Reservations;

public static class ReservationErrors
{
    public static readonly Error NotFound =
        Error.NotFound("reservation.not_found", "No reservation for that order.");

    public static readonly Error NotReinstatable =
        Error.Rule("reservation.not_reinstatable", "Only a released reservation with lines can be reinstated.");

    // The ids travel in the description because that is the one member
    // ResultExtensions serialises, and an operator reinstating by hand needs
    // to know which product to receive before trying again.
    public static Error Unavailable(IReadOnlyList<ProductId> products) =>
        Error.Rule(
            "reservation.unavailable",
            $"Not enough stock for: {string.Join(", ", products.Select(p => p.Value))}.");
}
