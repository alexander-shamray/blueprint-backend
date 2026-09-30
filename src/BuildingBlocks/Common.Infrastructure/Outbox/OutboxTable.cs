namespace Common.Infrastructure.Outbox;

/// <summary>Where this service's outbox lives: a registered schema, since §9.4's SQL names Ordering's.</summary>
/// <remarks>The schema is shape-checked by <see cref="SqlSchema"/>, which the inbox and marker tables share.</remarks>
public sealed class OutboxTable
{
    public OutboxTable(string schema)
    {
        QualifiedName = SqlSchema.Qualify(schema, "OutboxMessages", nameof(schema));
        Schema = schema;
    }

    public string Schema { get; }

    /// <summary>Schema-qualified and delimited, ready to interpolate.</summary>
    public string QualifiedName { get; }
}
