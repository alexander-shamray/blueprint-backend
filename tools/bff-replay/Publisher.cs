using Common.Infrastructure.Outbox;

namespace BffReplay;

/// <summary>A service whose outbox holds some of ADR-051's eight events, read by an operator's login.</summary>
public sealed record Publisher(string Name, string Schema)
{
    /// <summary>The four, each by the schema its service's <c>OutboxMessageConfiguration</c> maps.</summary>
    public static readonly IReadOnlyList<Publisher> All =
    [
        new("Catalog", "catalog"),
        new("Ordering", "ordering"),
        new("Payments", "payments"),
        new("Shipping", "shipping")
    ];

    /// <summary>The environment key its read-only connection is taken from.</summary>
    public string Key => $"ConnectionStrings__{Name}Outbox";

    /// <summary>The namespace its contracts live in, which selects its share of the eight (§9.2).</summary>
    public string ContractNamespace => $"Common.Contracts.{Name}.V1";

    /// <summary>Its outbox, delimited by the type every service registers its own with (§9.4).</summary>
    public string Outbox => new OutboxTable(Schema).QualifiedName;
}

/// <summary>A publisher beside the connection string that reads its outbox.</summary>
public sealed record PublisherConnection(Publisher Publisher, string ConnectionString);
