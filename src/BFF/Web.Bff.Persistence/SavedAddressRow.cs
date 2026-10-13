namespace Web.Bff.Persistence;

/// <summary>A customer's saved delivery address, one row each, kept as the checkout's prefill (ADR-107).</summary>
/// <remarks>
/// Written and read through SQL, never through this type, which exists to define the table (§7.2). The row is
/// personal data with no window: the customer replaces or deletes it, and an erasure deletes it.
/// </remarks>
public sealed class SavedAddressRow
{
    public Guid CustomerId { get; private set; }

    public string Line1 { get; private set; } = string.Empty;

    public string? Line2 { get; private set; }

    public string City { get; private set; } = string.Empty;

    public string PostalCode { get; private set; } = string.Empty;

    public string Country { get; private set; } = string.Empty;

    public DateTimeOffset SavedAt { get; private set; }
}
