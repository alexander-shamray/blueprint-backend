namespace Common.Infrastructure.Idempotency;

/// <summary>Where this service's idempotency markers live (§8.5), beside its outbox and inbox (§7.1).</summary>
public sealed class IdempotencyMarkerTable
{
    public IdempotencyMarkerTable(string schema)
    {
        QualifiedName = SqlSchema.Qualify(schema, "IdempotencyMarkers", nameof(schema));
        Schema = schema;
    }

    public string Schema { get; }

    /// <summary>Schema-qualified and delimited, ready to interpolate.</summary>
    public string QualifiedName { get; }
}
