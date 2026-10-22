using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Payments.Application.Orders;
using Payments.Domain.Orders;

namespace Payments.Infrastructure.Persistence;

internal sealed class SqlPaymentOrderStore(PaymentsDbContext db) : IPaymentOrderStore
{
    // HOLDLOCK: two first writes meet on the key-range lock, so the loser updates rather than failing its insert.
    private const string PlacedSql =
        """
        UPDATE payments.PaymentOrders WITH (UPDLOCK, HOLDLOCK)
        SET CustomerId = @CustomerId, TotalAmount = @TotalAmount, Currency = @Currency, PlacedAt = @PlacedAt,
            ErasedUnder = @ErasedUnder
        WHERE OrderId = @OrderId;

        IF @@ROWCOUNT = 0
            INSERT INTO payments.PaymentOrders (OrderId, CustomerId, TotalAmount, Currency, PlacedAt, ErasedUnder)
            VALUES (@OrderId, @CustomerId, @TotalAmount, @Currency, @PlacedAt, @ErasedUnder);
        """;

    // COALESCE keeps the first cancellation's instant, which a redelivery does not move.
    private const string CancelledSql =
        """
        UPDATE payments.PaymentOrders WITH (UPDLOCK, HOLDLOCK)
        SET CancelledAt = COALESCE(CancelledAt, @CancelledAt)
        WHERE OrderId = @OrderId;

        IF @@ROWCOUNT = 0
            INSERT INTO payments.PaymentOrders (OrderId, CancelledAt)
            VALUES (@OrderId, @CancelledAt);
        """;

    // HOLDLOCK locks the key range when the row is absent, so a first insert waits behind this read.
    private const string LockSql =
        """
        SELECT OrderId, CustomerId, TotalAmount, Currency, PlacedAt, CancelledAt
        FROM payments.PaymentOrders WITH (UPDLOCK, HOLDLOCK)
        WHERE OrderId = @OrderId;
        """;

    // The empty id names nobody and, unlike NULL, is not "the order has not arrived yet" (§3.2); the money stays.
    // HOLDLOCK takes a range lock even where the subject has no order, held to the commit that writes the audit row:
    // a placement meanwhile waits, and its erased check then sees that row. An order listing the request was placed
    // under it, so it keeps its payer (ADR-141). The list goes with the payer: it names the buyer to whoever can
    // join it to an open request (ADR-141).
    private const string AnonymiseSql =
        """
        UPDATE payments.PaymentOrders WITH (UPDLOCK, HOLDLOCK)
        SET CustomerId = '00000000-0000-0000-0000-000000000000', ErasedUnder = N'[]'
        WHERE CustomerId = @SubjectId
            AND NOT EXISTS (SELECT 1 FROM OPENJSON(ErasedUnder) AS u WHERE u.value = @RequestId);
        """;

    private const string AnonymiseOrderSql =
        """
        UPDATE payments.PaymentOrders
        SET CustomerId = '00000000-0000-0000-0000-000000000000', ErasedUnder = N'[]'
        WHERE OrderId = @OrderId AND CustomerId = @CustomerId;
        """;

    private sealed record Row(
        Guid OrderId,
        Guid? CustomerId,
        decimal? TotalAmount,
        string? Currency,
        DateTimeOffset? PlacedAt,
        DateTimeOffset? CancelledAt);

    public async Task RecordPlacedAsync(
        OrderId id,
        Guid customerId,
        decimal total,
        string currency,
        DateTimeOffset placedAt,
        IReadOnlyList<Guid> erasedUnder,
        CancellationToken ct)
    {
        (DbConnection connection, DbTransaction transaction) = Current();

        await connection.ExecuteAsync(
            new CommandDefinition(
                PlacedSql,
                new
                {
                    OrderId = id.Value,
                    CustomerId = customerId,
                    TotalAmount = total,
                    Currency = currency,
                    PlacedAt = placedAt,
                    ErasedUnder = JsonSerializer.Serialize(erasedUnder)
                },
                transaction,
                cancellationToken: ct));
    }

    public async Task RecordCancelledAsync(OrderId id, DateTimeOffset cancelledAt, CancellationToken ct)
    {
        (DbConnection connection, DbTransaction transaction) = Current();

        await connection.ExecuteAsync(
            new CommandDefinition(
                CancelledSql,
                new { OrderId = id.Value, CancelledAt = cancelledAt },
                transaction,
                cancellationToken: ct));
    }

    public async Task<PaymentOrderRecord?> LockAsync(OrderId id, CancellationToken ct)
    {
        (DbConnection connection, DbTransaction transaction) = Current();

        Row? row = await connection.QuerySingleOrDefaultAsync<Row>(
            new CommandDefinition(LockSql, new { OrderId = id.Value }, transaction, cancellationToken: ct));

        return row is null
            ? null
            : new PaymentOrderRecord(
                new OrderId(row.OrderId),
                row.CustomerId,
                row.TotalAmount,
                row.Currency?.Trim(),
                row.PlacedAt,
                row.CancelledAt);
    }

    public async Task<int> AnonymiseCustomerAsync(Guid subjectId, Guid requestId, CancellationToken ct)
    {
        (DbConnection connection, DbTransaction transaction) = Current();

        // As the column's JSON spells a Guid, which is what an order's list is matched against.
        return await connection.ExecuteAsync(
            new CommandDefinition(
                AnonymiseSql,
                new { SubjectId = subjectId, RequestId = requestId.ToString() },
                transaction,
                cancellationToken: ct));
    }

    public async Task<int> AnonymiseOrderAsync(OrderId id, Guid customerId, CancellationToken ct)
    {
        (DbConnection connection, DbTransaction transaction) = Current();

        return await connection.ExecuteAsync(
            new CommandDefinition(
                AnonymiseOrderSql,
                new { OrderId = id.Value, CustomerId = customerId },
                transaction,
                cancellationToken: ct));
    }

    private (DbConnection, DbTransaction) Current()
    {
        IDbContextTransaction? current = db.Database.CurrentTransaction;

        // As in EfUnitOfWork.ExecuteRawAsync: with no transaction, the statement would autocommit outside the unit.
        if (current is null)
        {
            throw new InvalidOperationException(
                "The order record is written only inside the unit of work's transaction (§6.3).");
        }

        return (db.Database.GetDbConnection(), current.GetDbTransaction());
    }
}
