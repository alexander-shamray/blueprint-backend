using System.Data;
using System.Text.Json;
using Common.Application;
using Dapper;
using Ordering.Application.Orders;
using Ordering.Domain.Common;
using Ordering.Domain.Orders;
using Ordering.Domain.Orders.Events;

namespace Ordering.Infrastructure.Projections;

/// <summary>§6.6's order summary, kept from the five lifecycle events, and §13.3's only metrics call site.</summary>
/// <remarks>
/// Every write ends in <see cref="RecordPendingFactsAsync"/>, since the local lane is unordered (§9.4) and any write
/// may complete a pair. Public, because §6.2's scan is public-only.
/// </remarks>
public sealed class OrderSummaryProjection(IDbConnectionFactory connections, OrderMetrics metrics)
    : IProjectionHandler<OrderPlacedDomainEvent>,
      IProjectionHandler<OrderStockConfirmedDomainEvent>,
      IProjectionHandler<OrderConfirmedDomainEvent>,
      IProjectionHandler<OrderShippedDomainEvent>,
      IProjectionHandler<OrderCancelledDomainEvent>
{
    /// <summary>The placement, which may meet a row a status event created first (§6.6).</summary>
    private const string PlacedSql =
        """
        MERGE ordering.OrderSummaries WITH (HOLDLOCK) AS target
        -- An order already erased (the empty id) must not bring its buyer back through a late placement. The order
        -- is read by its key alone and held to the insert, so an erasure not yet at it finds the summary (ADR-137).
        USING (
            SELECT OrderId = @OrderId,
                CustomerId = CASE
                    WHEN (SELECT CustomerId FROM ordering.Orders WITH (REPEATABLEREAD) WHERE Id = @OrderId) = @Erased
                    THEN NULL
                    ELSE @CustomerId
                END) AS source
            ON target.OrderId = source.OrderId
        WHEN NOT MATCHED THEN
            INSERT (OrderId, CustomerId, Status, TotalAmount, Currency, LineCount, Products, PlacedAt, UpdatedAt)
            VALUES (
                @OrderId,
                source.CustomerId,
                @Status,
                @Total,
                @Currency,
                @LineCount,
                @Products,
                @OccurredAt,
                @OccurredAt)
        -- PlacedAt IS NULL fires once: a redelivery finds it set and writes nothing.
        WHEN MATCHED AND target.PlacedAt IS NULL THEN
            UPDATE SET
                CustomerId  = source.CustomerId,
                TotalAmount = @Total,
                Currency    = @Currency,
                LineCount   = @LineCount,
                Products    = @Products,
                PlacedAt    = @OccurredAt,
                Status      = CASE WHEN target.UpdatedAt < @OccurredAt THEN @Status ELSE target.Status END,
                UpdatedAt   = CASE WHEN target.UpdatedAt < @OccurredAt THEN @OccurredAt ELSE target.UpdatedAt END;
        """;

    /// <summary>A transition, which may arrive before its placement or after a later transition (§6.6).</summary>
    private const string StatusSql =
        """
        MERGE ordering.OrderSummaries WITH (HOLDLOCK) AS target
        USING (SELECT OrderId = @OrderId) AS source
            ON target.OrderId = source.OrderId
        WHEN NOT MATCHED THEN
            INSERT (OrderId, Status, UpdatedAt, ConfirmedAt, CancelReason)
            VALUES (@OrderId, @Status, @OccurredAt, @ConfirmedAt, @CancelReason)
        -- Status only moves forward. ConfirmedAt and CancelReason happen once, so an older event still fills them:
        -- a Confirmed applied after its Shipped would otherwise never record the fulfilment duration.
        WHEN MATCHED THEN
            UPDATE SET
                Status       = CASE WHEN target.UpdatedAt < @OccurredAt THEN @Status ELSE target.Status END,
                UpdatedAt    = CASE WHEN target.UpdatedAt < @OccurredAt THEN @OccurredAt ELSE target.UpdatedAt END,
                ConfirmedAt  = COALESCE(target.ConfirmedAt, @ConfirmedAt),
                CancelReason = COALESCE(target.CancelReason, @CancelReason);
        """;

    /// <summary>Each claim flips its flag and returns the values in one statement, so it fires once (§13.3).</summary>
    private const string ClaimPlacedSql =
        """
        UPDATE ordering.OrderSummaries
        SET PlacedCounted = 1
        OUTPUT inserted.TotalAmount, inserted.Currency
        WHERE OrderId = @OrderId
            AND PlacedAt IS NOT NULL
            AND PlacedCounted = 0;
        """;

    /// <summary><c>PlacedCounted = 1</c>, so a cancellation is never counted before its placement (§13.3).</summary>
    private const string ClaimCancelledSql =
        """
        UPDATE ordering.OrderSummaries
        SET CancelledCounted = 1
        OUTPUT inserted.CancelReason
        WHERE OrderId = @OrderId
            AND PlacedCounted = 1
            AND CancelReason IS NOT NULL
            AND CancelledCounted = 0;
        """;

    private const string ClaimFulfilmentSql =
        """
        UPDATE ordering.OrderSummaries
        SET FulfilmentCounted = 1
        OUTPUT inserted.PlacedAt, inserted.ConfirmedAt
        WHERE OrderId = @OrderId
            AND PlacedAt IS NOT NULL
            AND ConfirmedAt IS NOT NULL
            AND FulfilmentCounted = 0;
        """;

    // One round trip and one commit per write, not one per statement (ADR-166).
    // XACT_ABORT rolls back a batch that fails.
    private const string Begin = "SET XACT_ABORT ON;\nBEGIN TRANSACTION;\n";

    private const string ClaimsThenCommit =
        "\n" + ClaimPlacedSql + "\n" + ClaimCancelledSql + "\n" + ClaimFulfilmentSql + "\nCOMMIT TRANSACTION;";

    private const string PlacedBatchSql = Begin + PlacedSql + ClaimsThenCommit;

    private const string StatusBatchSql = Begin + StatusSql + ClaimsThenCommit;

    public async Task HandleAsync(OrderPlacedDomainEvent domainEvent, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();
        using SqlMapper.GridReader claims = await connection.QueryMultipleAsync(
            new CommandDefinition(
                PlacedBatchSql,
                new
                {
                    OrderId = domainEvent.OrderId.Value,
                    CustomerId = domainEvent.CustomerId.Value,
                    Erased = Guid.Empty,
                    Status = nameof(OrderStatus.AwaitingStock),
                    Total = domainEvent.Total.Amount,
                    domainEvent.Total.Currency,
                    LineCount = domainEvent.Lines.Count,
                    // Ids only: a name is Catalog's fact about the product, resolved on read (ADR-027).
                    Products = JsonSerializer.Serialize(domainEvent.Lines.Select(l => l.ProductId.Value)),
                    domainEvent.OccurredAt
                },
                cancellationToken: ct));

        await RecordPendingFactsAsync(claims);
    }

    public Task HandleAsync(OrderStockConfirmedDomainEvent domainEvent, CancellationToken ct) =>
        SetStatusAsync(domainEvent.OrderId, OrderStatus.AwaitingPayment, domainEvent.OccurredAt, ct);

    public Task HandleAsync(OrderConfirmedDomainEvent domainEvent, CancellationToken ct) =>
        SetStatusAsync(
            domainEvent.OrderId,
            OrderStatus.Confirmed,
            domainEvent.OccurredAt,
            ct,
            confirmedAt: domainEvent.OccurredAt);

    public Task HandleAsync(OrderShippedDomainEvent domainEvent, CancellationToken ct) =>
        SetStatusAsync(domainEvent.OrderId, OrderStatus.Shipped, domainEvent.OccurredAt, ct);

    // The wire code, not the enum's name, so renaming a member cannot split the series (§13.3).
    public Task HandleAsync(OrderCancelledDomainEvent domainEvent, CancellationToken ct) =>
        SetStatusAsync(
            domainEvent.OrderId,
            OrderStatus.Cancelled,
            domainEvent.OccurredAt,
            ct,
            cancelReason: CancellationReasons.ToCode(domainEvent.Reason));

    private async Task SetStatusAsync(
        OrderId orderId,
        OrderStatus status,
        DateTimeOffset occurredAt,
        CancellationToken ct,
        DateTimeOffset? confirmedAt = null,
        string? cancelReason = null)
    {
        using IDbConnection connection = connections.Create();
        using SqlMapper.GridReader claims = await connection.QueryMultipleAsync(
            new CommandDefinition(
                StatusBatchSql,
                new
                {
                    OrderId = orderId.Value,
                    Status = status.ToString(),
                    OccurredAt = occurredAt,
                    ConfirmedAt = confirmedAt,
                    CancelReason = cancelReason
                },
                cancellationToken: ct));

        await RecordPendingFactsAsync(claims);
    }

    /// <summary>Records every fact the row now supports and has not yet counted (§13.3).</summary>
    private async Task RecordPendingFactsAsync(SqlMapper.GridReader claims)
    {
        PlacedFact? placed = await claims.ReadSingleOrDefaultAsync<PlacedFact>();
        string? cancelled = await claims.ReadSingleOrDefaultAsync<string>();

        // The last read moves the reader past the commit, so a batch that rolled back records nothing (ADR-166).
        FulfilmentFact? fulfilment = await claims.ReadSingleOrDefaultAsync<FulfilmentFact>();

        // The column only ever holds Money's own three letters, so it returns unpadded to Money.Of (§5.3).
        if (placed is not null)
            metrics.Placed(Money.Of(placed.TotalAmount, placed.Currency));

        if (cancelled is not null)
            metrics.Cancelled(cancelled);

        if (fulfilment is not null)
            metrics.Fulfilled(fulfilment.ConfirmedAt - fulfilment.PlacedAt);
    }

    private sealed record PlacedFact(decimal TotalAmount, string Currency);

    private sealed record FulfilmentFact(DateTimeOffset PlacedAt, DateTimeOffset ConfirmedAt);
}
