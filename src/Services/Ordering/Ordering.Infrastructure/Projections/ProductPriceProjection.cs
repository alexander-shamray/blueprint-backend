using System.Data;
using Common.Application;
using Common.Contracts.Catalog.V1;
using Dapper;

namespace Ordering.Infrastructure.Projections;

/// <summary>§6.6's price projection, the read model <c>PlaceOrder</c>'s write path depends on.</summary>
/// <remarks>
/// Public, because §6.2's scan is public-only and an internal handler registers as nothing with the endpoint
/// still bound. It has no rebuild path; §6.6 names the republish that is owed.
/// </remarks>
public sealed class ProductPriceProjection(IDbConnectionFactory connections)
    : IIntegrationEventHandler<ProductPublished>,
      IIntegrationEventHandler<PriceChanged>,
      IIntegrationEventHandler<ProductDiscontinued>
{
    /// <summary>§6.6's upsert; each <c>WITH (HOLDLOCK)</c> guards a different absence (§6.6).</summary>
    private const string UpsertSql =
        """
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;

        -- The watermark is read FIRST, and under HOLDLOCK, and both halves are
        -- load-bearing. A withdrawal newer than this event means Catalog has
        -- since pulled the product, so the row this statement is about to
        -- write is not orderable — whether or not a row for this currency
        -- existed when the withdrawal ran.
        --
        -- HOLDLOCK because the interesting answer is an ABSENCE: at read
        -- committed the lock is released immediately, so a discontinuation can
        -- commit between this read and the insert below and leave a withdrawn
        -- product available. A key-range lock on this ProductId is what makes
        -- "no withdrawal" hold until COMMIT. HOLDLOCK on ProductPrices does
        -- not reach this table.
        --
        -- FIRST because the discontinue statement takes these two tables in
        -- this order as well. Same order, no cycle — the deadlock that
        -- otherwise appears the moment both statements run concurrently for
        -- one product.
        DECLARE @IsAvailable bit =
            CASE
                WHEN EXISTS (
                    SELECT 1
                    FROM ordering.ProductWithdrawals WITH (HOLDLOCK)
                    WHERE ProductId = @ProductId
                        AND WithdrawnAt >= @OccurredAt)
                THEN 0
                ELSE 1
            END;

        MERGE ordering.ProductPrices WITH (HOLDLOCK) AS target
        USING (SELECT ProductId = @ProductId, Currency = @Currency) AS source
            ON target.ProductId = source.ProductId
            AND target.Currency = source.Currency
        -- NOT MATCHED is the branch no UpdatedAt comparison can cover, because
        -- there is no target row to compare against — which is why the
        -- withdrawal watermark exists at all.
        WHEN NOT MATCHED THEN
            INSERT (ProductId, Currency, Amount, IsAvailable, UpdatedAt)
            VALUES (@ProductId, @Currency, @Amount, @IsAvailable, @OccurredAt)
        -- The out-of-order guard. At-least-once delivery (§9.4) means a
        -- redelivered ProductPublished can arrive after the PriceChanged that
        -- superseded it, and without this line the older amount wins and stays
        -- won — a wrong price on the write path, with nothing failing.
        --
        -- STRICT here, where the withdrawal comparison is not, and §6.6 argues
        -- the asymmetry: a tie between a price and a withdrawal has a business
        -- answer (only a later price re-lists), and a tie between two prices
        -- has none — the publisher said they happened at the same instant, so
        -- delivery order decides and OccurredAt is not a total order. Ranking
        -- them needs a per-product sequence in §9.1's envelope, which is a
        -- platform decision rather than this statement's.
        WHEN MATCHED AND target.UpdatedAt < @OccurredAt THEN
            UPDATE SET Amount = @Amount, IsAvailable = @IsAvailable, UpdatedAt = @OccurredAt;

        COMMIT;
        """;

    /// <summary>§6.6's discontinue: a product-level watermark and the existing rows, in one transaction.</summary>
    /// <remarks>
    /// Flags rather than deletes, so a past order keeps its price; <see cref="Persistence.ProductWithdrawal"/>
    /// covers the rows that do not exist yet (§6.6).
    /// </remarks>
    private const string DiscontinueSql =
        """
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;

        -- The watermark first, because it is the half that must survive having
        -- no price row to write to. Monotonic: a redelivered or stale
        -- withdrawal must not move it backwards over a later one.
        MERGE ordering.ProductWithdrawals WITH (HOLDLOCK) AS target
        USING (SELECT ProductId = @ProductId) AS source
            ON target.ProductId = source.ProductId
        WHEN NOT MATCHED THEN
            INSERT (ProductId, WithdrawnAt)
            VALUES (@ProductId, @OccurredAt)
        WHEN MATCHED AND target.WithdrawnAt < @OccurredAt THEN
            UPDATE SET WithdrawnAt = @OccurredAt;

        -- Then the rows that already exist. The watermark covers the ones that
        -- do not, so between them every currency is reached.
        UPDATE ordering.ProductPrices
        SET IsAvailable = 0, UpdatedAt = @OccurredAt
        WHERE ProductId = @ProductId
            AND UpdatedAt <= @OccurredAt;

        COMMIT;
        """;

    public Task HandleAsync(ProductPublished integrationEvent, CancellationToken ct) =>
        UpsertAsync(
            integrationEvent.ProductId,
            integrationEvent.Currency,
            integrationEvent.Amount,
            integrationEvent.OccurredAt,
            ct);

    public Task HandleAsync(PriceChanged integrationEvent, CancellationToken ct) =>
        UpsertAsync(
            integrationEvent.ProductId,
            integrationEvent.Currency,
            integrationEvent.Amount,
            integrationEvent.OccurredAt,
            ct);

    public Task HandleAsync(ProductDiscontinued integrationEvent, CancellationToken ct) =>
        ExecuteAsync(
            DiscontinueSql,
            new { integrationEvent.ProductId, integrationEvent.OccurredAt },
            ct);

    /// <summary>One statement for both price-bearing events, so neither can end up without the guard (§6.6).</summary>
    /// <remarks>Upper-cased on both sides, since nothing upstream normalises it (§6.4, §6.6).</remarks>
    private Task UpsertAsync(
        Guid productId,
        string currency,
        decimal amount,
        DateTimeOffset occurredAt,
        CancellationToken ct) =>
        ExecuteAsync(
            UpsertSql,
            new
            {
                ProductId = productId,
                Currency = currency.ToUpperInvariant(),
                Amount = amount,
                OccurredAt = occurredAt
            },
            ct);

    /// <summary>Its own connection, never the write transaction (§6.6, §7.5).</summary>
    private async Task ExecuteAsync(string sql, object parameters, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
    }
}
