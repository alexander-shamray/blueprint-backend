namespace Web.Bff.Persistence;

/// <summary>A customer's saved delivery address, one row each, kept as the checkout's prefill (ADR-107).</summary>
/// <remarks>
/// Written and read through SQL, never through this type, which exists to define the table (§7.2). The row is
/// personal data with no window: the customer replaces or deletes it, and so does any erasure it was not saved under.
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

    /// <summary>The erasure requests the address was saved under, as a JSON array; each keeps it (ADR-135).</summary>
    public string ErasedUnder { get; private set; } = "[]";
}
