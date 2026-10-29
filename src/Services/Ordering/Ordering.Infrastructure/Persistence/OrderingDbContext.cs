using Common.Domain;
using Common.Infrastructure.Idempotency;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Outbox;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Ordering.Application;
using Ordering.Domain.Orders;
using SagaInboxState = MassTransit.EntityFrameworkCoreIntegration.InboxState;
using SagaOutboxMessage = MassTransit.EntityFrameworkCoreIntegration.OutboxMessage;

namespace Ordering.Infrastructure.Persistence;

/// <summary>The write-side context (§7.2); the architecture gates, not the modifier, confine it.</summary>
public sealed class OrderingDbContext(DbContextOptions<OrderingDbContext> options) : DbContext(options)
{
    /// <summary>§5.4's aggregate root; <c>OrderLine</c> is reached through it, so it has no set of its own.</summary>
    public DbSet<Order> Orders => Set<Order>();

    /// <summary>§9.4's outbox, on this context so the row enlists in the aggregate's transaction.</summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>§9.5's inbox; common code reaches it through <c>Set</c>, so the property states the model.</summary>
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    /// <summary>§8.5's markers; common code reaches them through <c>Set</c>, so this states the model.</summary>
    public DbSet<IdempotencyMarker> IdempotencyMarkers => Set<IdempotencyMarker>();

    /// <summary>§11.7's audit rows: a hash, a count and two times per request, holding no personal data.</summary>
    public DbSet<PersonalDataErasure> PersonalDataErasures => Set<PersonalDataErasure>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("ordering");

        // §7.2 maps in configuration classes, never attributes, which would put EF Core in Ordering.Domain.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrderingDbContext).Assembly);

        // ADR-032's three tables, which MassTransit maps itself; a configuration of ours would be a second
        // definition of a schema the library owns (§7.2), so ClusterSagaOutboxOnInboxKey amends its mapping and
        // never restates it, and ClusterInboxStateOnMessageKey likewise.
        modelBuilder.AddTransactionalOutboxEntities();
        ClusterSagaOutboxOnInboxKey(modelBuilder.Entity<SagaOutboxMessage>().Metadata);
        ClusterInboxStateOnMessageKey(modelBuilder.Entity<SagaInboxState>().Metadata);
    }

    /// <summary>
    /// Clusters the library's own inbox-message index rather than its identity key, so a delivery's plan seeks however
    /// small the table was when it compiled (ADR-167); refuses a mapping that has moved, rather than restore it.
    /// </summary>
    private static void ClusterSagaOutboxOnInboxKey(IMutableEntityType outbox)
    {
        IMutableKey key = outbox.FindPrimaryKey() ?? throw MappingMoved("no primary key");
        if (key.Properties is not [{ Name: nameof(SagaOutboxMessage.SequenceNumber) }])
            throw MappingMoved("a primary key other than SequenceNumber");

        IMutableIndex index = outbox.GetIndexes()
                .SingleOrDefault(i => i.Properties.Select(p => p.Name).SequenceEqual(SagaOutboxInboxKey))
            ?? throw MappingMoved("no index on InboxMessageId, InboxConsumerId and SequenceNumber");
        if (!index.IsUnique || index.GetFilter() != SagaOutboxInboxKeyFilter)
            throw MappingMoved("an inbox-message index that is not unique or not filtered on its two nullable columns");

        // SQL Server clusters no filtered index; the filter only ever excluded bus-outbox rows, which ADR-167 notes.
        key.SetIsClustered(false);
        index.SetIsClustered(true);
        index.SetFilter(null);
    }

    private static InvalidOperationException MappingMoved(string found) =>
        new($"MassTransit's OutboxMessage mapping has moved ({found}); revisit ADR-167 before it is re-clustered.");

    private const string SagaOutboxInboxKeyFilter = "[InboxMessageId] IS NOT NULL AND [InboxConsumerId] IS NOT NULL";

    private static readonly string[] SagaOutboxInboxKey =
    [
        nameof(SagaOutboxMessage.InboxMessageId),
        nameof(SagaOutboxMessage.InboxConsumerId),
        nameof(SagaOutboxMessage.SequenceNumber)
    ];

    /// <summary>
    /// Clusters the library's inbox on its message key rather than its identity key, so a message's insert, locked
    /// read and update land where its id sorts rather than on one last page (ADR-171); refuses a moved mapping.
    /// </summary>
    private static void ClusterInboxStateOnMessageKey(IMutableEntityType inbox)
    {
        IMutableKey key = inbox.FindPrimaryKey() ?? throw InboxMappingMoved("no primary key");
        if (key.Properties is not [{ Name: nameof(SagaInboxState.Id) }])
            throw InboxMappingMoved("a primary key other than Id");

        IMutableKey messageKey = inbox.GetKeys()
                .SingleOrDefault(k => k.Properties.Select(p => p.Name).SequenceEqual(InboxStateMessageKey))
            ?? throw InboxMappingMoved("no alternate key on MessageId and ConsumerId");

        key.SetIsClustered(false);
        messageKey.SetIsClustered(true);
    }

    private static InvalidOperationException InboxMappingMoved(string found) =>
        new($"MassTransit's InboxState mapping has moved ({found}); revisit ADR-171 before it is re-clustered.");

    private static readonly string[] InboxStateMessageKey =
        [nameof(SagaInboxState.MessageId), nameof(SagaInboxState.ConsumerId)];

    /// <summary>§7.2's global conventions, for every row this context maps.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(OrderAmounts.Precision, OrderAmounts.Scale);
        configurationBuilder.Properties<string>().HaveMaxLength(400);
        configurationBuilder.Properties<DateTimeOffset>().HaveColumnType("datetimeoffset(7)");
    }
}
