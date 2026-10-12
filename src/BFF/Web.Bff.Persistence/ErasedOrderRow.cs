namespace Web.Bff.Persistence;

/// <summary>An order the BFF deleted for an erased customer, kept as its id alone so a later event cannot revive it.</summary>
/// <remarks>
/// Written and read through SQL, never through this type, which exists to define the table (§7.2). The id names no
/// one once the owner has anonymised the order, so the row carries no personal data and has no window (ADR-098).
/// </remarks>
public sealed class ErasedOrderRow
{
    public Guid OrderId { get; private set; }

    public DateTimeOffset ErasedAt { get; private set; }
}
