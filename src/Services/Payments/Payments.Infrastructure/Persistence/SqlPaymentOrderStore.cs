using System.Data.Common;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Payments.Application.Orders;
using Payments.Domain.Orders;

namespace Payments.Infrastructure.Persistence;

internal sealed class SqlPaymentOrderStore(PaymentsDbContext db) : IPaymentOrderStore
{
    // UPDLOCK with HOLDLOCK on the update: two first writes for one order
    // meet on the key-range lock rather than on the primary key, so the loser
    // updates the winner's row instead of failing its insert.
    private const string PlacedSql =
        """
        UPDATE payments.PaymentOrders WITH (UPDLOCK, HOLDLOCK)
        SET CustomerId = @CustomerId, TotalAmount = @TotalAmount, Currency = @Currency, PlacedAt = @PlacedAt
        WHERE OrderId = @OrderId;

        IF @@ROWCOUNT = 0
            INSERT INTO payments.PaymentOrders (OrderId, CustomerId, TotalAmount, Currency, PlacedAt)
            VALUES (@OrderId, @CustomerId, @TotalAmount, @Currency, @PlacedAt);
        """;

    // COALESCE keeps the first cancellation's instant: a redelivery says the
    // order was cancelled, not that it was cancelled again later.
    private const string CancelledSql =
        """
        UPDATE payments.PaymentOrders WITH (UPDLOCK, HOLDLOCK)
        SET CancelledAt = COALESCE(CancelledAt, @CancelledAt)
        WHERE OrderId = @OrderId;

        IF @@ROWCOUNT = 0
            INSERT INTO payments.PaymentOrders (OrderId, CancelledAt)
            VALUES (@OrderId, @CancelledAt);
        """;

    // HOLDLOCK takes a key-range lock when the row is absent, so a
    // cancellation's first insert waits behind this read as an update would.
    private const string LockSql =
        """
        SELECT OrderId, CustomerId, TotalAmount, Currency, PlacedAt, CancelledAt
        FROM payments.PaymentOrders WITH (UPDLOCK, HOLDLOCK)
        WHERE OrderId = @OrderId;
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
        CancellationToken ct)
    {
        (DbConnection connection, DbTransaction transaction) = Current();

        await connection.ExecuteAsync(new CommandDefinition(
            PlacedSql,
            new
            {
                OrderId = id.Value,
                CustomerId = customerId,
                TotalAmount = total,
                Currency = currency,
                PlacedAt = placedAt
            },
            transaction,
            cancellationToken: ct));
    }

    public async Task RecordCancelledAsync(OrderId id, DateTimeOffset cancelledAt, CancellationToken ct)
    {
        (DbConnection connection, DbTransaction transaction) = Current();

        await connection.ExecuteAsync(new CommandDefinition(
            CancelledSql, new { OrderId = id.Value, CancelledAt = cancelledAt }, transaction, cancellationToken: ct));
    }

    public async Task<PaymentOrderRecord?> LockAsync(OrderId id, CancellationToken ct)
    {
        (DbConnection connection, DbTransaction transaction) = Current();

        Row? row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            LockSql, new { OrderId = id.Value }, transaction, cancellationToken: ct));

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

    private (DbConnection, DbTransaction) Current()
    {
        IDbContextTransaction? current = db.Database.CurrentTransaction;

        // The refusal EfUnitOfWork.ExecuteRawAsync makes: a statement with no
        // transaction autocommits outside the unit the caller believes it is in.
        if (current is null)
        {
            throw new InvalidOperationException(
                "The order record is written only inside the unit of work's transaction (§6.3).");
        }

        return (db.Database.GetDbConnection(), current.GetDbTransaction());
    }
}
