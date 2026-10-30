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
        // definition of a schema the library owns (§7.2), so KeySagaOutboxOnInboxKey amends its mapping and
        // never restates it, and ClusterInboxStateOnMessageKey likewise.
        modelBuilder.AddTransactionalOutboxEntities();
        KeySagaOutboxOnInboxKey(modelBuilder.Entity<SagaOutboxMessage>().Metadata);
        ClusterInboxStateOnMessageKey(modelBuilder.Entity<SagaInboxState>().Metadata);
    }

    /// <summary>
    /// Keys the library's saga outbox on its own inbox-message index, clustered, so a delivery's plan seeks however
    /// small the table was when it compiled (ADR-167) and no index orders a row by when it was staged (ADR-177);
    /// refuses a mapping that has moved, rather than restore it.
    /// </summary>
    private static void KeySagaOutboxOnInboxKey(IMutableEntityType outbox)
    {
        IMutableKey key = outbox.FindPrimaryKey() ?? throw MappingMoved("no primary key");
        if (key.Properties is not [{ Name: nameof(SagaOutboxMessage.SequenceNumber) } sequence])
            throw MappingMoved("a primary key other than SequenceNumber");
        if (sequence.GetValueGenerationStrategy() != SqlServerValueGenerationStrategy.IdentityColumn)
            throw MappingMoved("a SequenceNumber that is not an identity column");

        IMutableIndex index = outbox.GetIndexes()
                .SingleOrDefault(i => i.Properties.Select(p => p.Name).SequenceEqual(SagaOutboxInboxKey))
            ?? throw MappingMoved("no index on InboxMessageId, InboxConsumerId and SequenceNumber");
        if (!index.IsUnique || index.GetFilter() != SagaOutboxInboxKeyFilter)
            throw MappingMoved("an inbox-message index that is not unique or not filtered on its two nullable columns");

        IMutableIndex[] stagedAt = outbox.GetIndexes()
            .Where(i => i.Properties is [{ Name: StagedAtEnqueue or StagedAtExpiry }])
            .ToArray();
        if (stagedAt.Length != 2)
            throw MappingMoved("no single-column index on EnqueueTime or on ExpirationTime");

        // Only bus-outbox rows leave the two columns null, and Ordering keeps none, as ADR-167 notes; the identity's
        // own index put every staging and every removal on its last page, and the two times' on theirs (ADR-177).
        foreach (IMutableProperty property in index.Properties)
            property.IsNullable = false;
        outbox.RemoveIndex(index);
        foreach (IMutableIndex dropped in stagedAt)
            outbox.RemoveIndex(dropped);
        outbox.SetPrimaryKey(index.Properties)!.SetIsClustered(true);
        outbox.RemoveKey(key);

        // EF Core's conventions make only a single-column key an identity; the delivery still reads in its order.
        sequence.ValueGenerated = ValueGenerated.OnAdd;
        sequence.SetValueGenerationStrategy(SqlServerValueGenerationStrategy.IdentityColumn);
    }

    private static InvalidOperationException MappingMoved(string found) =>
        new($"MassTransit's OutboxMessage mapping has moved ({found}); "
            + "revisit ADR-167 and ADR-177 before it is re-keyed.");

    private const string StagedAtEnqueue = nameof(SagaOutboxMessage.EnqueueTime);

    private const string StagedAtExpiry = nameof(SagaOutboxMessage.ExpirationTime);

    private const string SagaOutboxInboxKeyFilter = "[InboxMessageId] IS NOT NULL AND [InboxConsumerId] IS NOT NULL";

    private static readonly string[] SagaOutboxInboxKey =
    [
        nameof(SagaOutboxMessage.InboxMessageId),
        nameof(SagaOutboxMessage.InboxConsumerId),
        nameof(SagaOutboxMessage.SequenceNumber)
    ];

    /// <summary>
    /// Clusters the library's inbox on its message key rather than its identity key, so a message's insert, locked
    /// read and update land where its id sorts rather than on one last page (ADR-171), and draws Id at random rather
    /// than from an identity, so its own index does not put them there either (ADR-177); refuses a moved mapping.
    /// </summary>
    private static void ClusterInboxStateOnMessageKey(IMutableEntityType inbox)
    {
        IMutableKey key = inbox.FindPrimaryKey() ?? throw InboxMappingMoved("no primary key");
        if (key.Properties is not [{ Name: nameof(SagaInboxState.Id) } id])
            throw InboxMappingMoved("a primary key other than Id");
        if (id.GetValueGenerationStrategy() != SqlServerValueGenerationStrategy.IdentityColumn)
            throw InboxMappingMoved("an Id that is not an identity column");

        IMutableKey messageKey = inbox.GetKeys()
                .SingleOrDefault(k => k.Properties.Select(p => p.Name).SequenceEqual(InboxStateMessageKey))
            ?? throw InboxMappingMoved("no alternate key on MessageId and ConsumerId");

        key.SetIsClustered(false);
        messageKey.SetIsClustered(true);

        // The library's UPDATE finds its row by Id, and its cleanup deletes by Id: the key stays, its order goes.
        id.SetValueGenerationStrategy(SqlServerValueGenerationStrategy.None);
        id.SetDefaultValueSql(InboxStateRandomId);
    }

    private static InvalidOperationException InboxMappingMoved(string found) =>
        new($"MassTransit's InboxState mapping has moved ({found}); "
            + "revisit ADR-171 and ADR-177 before it is re-keyed.");

    /// <summary>Sixty-four random bits: a collision fails one insert into the endpoint's retry (ADR-177).</summary>
    internal const string InboxStateRandomId = "CONVERT(bigint, CRYPT_GEN_RANDOM(8))";

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
