namespace Common.Infrastructure.Inbox;

/// <summary>Where this service's inbox lives (§9.5), beside its outbox (§7.1).</summary>
public sealed class InboxTable
{
    public InboxTable(string schema)
    {
        QualifiedName = SqlSchema.Qualify(schema, "InboxMessages", nameof(schema));
        Schema = schema;
    }

    public string Schema { get; }

    /// <summary>Schema-qualified and delimited, ready to interpolate.</summary>
    public string QualifiedName { get; }
}
