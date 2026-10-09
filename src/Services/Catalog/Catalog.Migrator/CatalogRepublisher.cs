using Catalog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Catalog.Migrator;

/// <summary>ADR-090's republish: the facts Catalog still holds for each product, staged again on the Broker lane.</summary>
/// <remarks>
/// SQL through the migration's own context, since §4.2 keeps the migrator off Application and Domain. A row
/// keeps the event's <c>OccurredAt</c> for Ordering's guards (§6.6) and has a fresh <c>MessageId</c> (§9.5).
/// </remarks>
public sealed class CatalogRepublisher(
    CatalogDbContext db,
    ILogger<CatalogRepublisher> logger,
    RepublishRequest request)
{
    // The persisted names MessageTypeMap gives the V1 contracts (§9.4), whose assembly the migrator cannot see.
    private const string ProductPublished = "Common.Contracts.Catalog.V1.ProductPublished";

    private const string PriceChanged = "Common.Contracts.Catalog.V1.PriceChanged";

    private const string ProductDiscontinued = "Common.Contracts.Catalog.V1.ProductDiscontinued";

    private static readonly Action<ILogger, int, int, Exception?> Staged =
        LoggerMessage.Define<int, int>(
            LogLevel.Information,
            new EventId(1, nameof(Staged)),
            "Republished {Products} product(s) as {Rows} outbox row(s); the dispatcher delivers them from here.");

    private static readonly Action<ILogger, Guid, Exception?> NoSuchProduct =
        LoggerMessage.Define<Guid>(
            LogLevel.Error,
            new EventId(2, nameof(NoSuchProduct)),
            "No product {ProductId} to republish. The job exits non-zero.");

    private static readonly Action<ILogger, Exception?> Failed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(3, nameof(Failed)),
            "The republish failed and staged nothing. The job exits non-zero.");

    /// <returns>0 once every matching product is staged; 1 if the id matched no product, or the run threw.</returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        // The run's own clock for the rows, so §13.7's age measures this backlog and not the products' ages.
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        bool all = request.Id is null;
        Guid id = request.Id ?? Guid.Empty;

        try
        {
            IExecutionStrategy strategy = db.Database.CreateExecutionStrategy();

            // One transaction, retried whole, so a product is never announced by some of its rows and not the rest.
            (int products, int rows) = await strategy.ExecuteAsync(
                async token =>
                {
                    await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(token);

                    int published = await StagePublishedAsync(all, id, now, token);
                    int repriced = await StagePriceChangedAsync(all, id, now, token);
                    int withdrawn = await StageDiscontinuedAsync(all, id, now, token);

                    await transaction.CommitAsync(token);
                    return (published, published + repriced + withdrawn);
                },
                ct);

            if (products == 0 && !all)
            {
                NoSuchProduct(logger, id, null);
                return 1;
            }

            Staged(logger, products, rows, null);
            return 0;
        }
        catch (Exception ex)
        {
            // Broad on purpose: an escaped exception would exit without the sentence an operator needs.
            Failed(logger, ex);
            return 1;
        }
    }

    // Every product was published, so every product gets this row; it carries the price it has now, stamped as
    // it was published, and a newer row in Ordering wins over it (§6.6's strict comparison).
    private Task<int> StagePublishedAsync(bool all, Guid id, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO catalog.OutboxMessages
                (MessageId, CorrelationId, MessageType, Payload, Lane, OccurredAt, Attempts)
            SELECT
                p.MessageId,
                p.Id,
                {ProductPublished},
                (
                    SELECT
                        MessageId     = p.MessageId,
                        CorrelationId = p.Id,
                        OccurredAt    = p.PublishedAt,
                        ProductId     = p.Id,
                        Name          = p.Name,
                        ThumbnailUrl  = p.ThumbnailUrl,
                        Amount        = p.PriceAmount,
                        Currency      = p.PriceCurrency
                    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES
                ),
                'Broker',
                {now},
                0
            FROM (
                SELECT MessageId = NEWID(), Id, Name, ThumbnailUrl, PriceAmount, PriceCurrency, PublishedAt
                FROM catalog.Products
                WHERE {all} = 1 OR Id = {id}
            ) AS p;
            """,
            ct);

    // A withdrawn product's last stamp is its withdrawal, so the price it had before is not kept and not needed.
    private Task<int> StagePriceChangedAsync(bool all, Guid id, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO catalog.OutboxMessages
                (MessageId, CorrelationId, MessageType, Payload, Lane, OccurredAt, Attempts)
            SELECT
                p.MessageId,
                p.Id,
                {PriceChanged},
                (
                    SELECT
                        MessageId     = p.MessageId,
                        CorrelationId = p.Id,
                        OccurredAt    = p.LastEventAt,
                        ProductId     = p.Id,
                        Amount        = p.PriceAmount,
                        Currency      = p.PriceCurrency
                    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES
                ),
                'Broker',
                {now},
                0
            FROM (
                SELECT MessageId = NEWID(), Id, PriceAmount, PriceCurrency, LastEventAt
                FROM catalog.Products
                WHERE ({all} = 1 OR Id = {id}) AND WithdrawnAt IS NULL AND LastEventAt > PublishedAt
            ) AS p;
            """,
            ct);

    private Task<int> StageDiscontinuedAsync(bool all, Guid id, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO catalog.OutboxMessages
                (MessageId, CorrelationId, MessageType, Payload, Lane, OccurredAt, Attempts)
            SELECT
                p.MessageId,
                p.Id,
                {ProductDiscontinued},
                (
                    SELECT
                        MessageId     = p.MessageId,
                        CorrelationId = p.Id,
                        OccurredAt    = p.WithdrawnAt,
                        ProductId     = p.Id
                    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES
                ),
                'Broker',
                {now},
                0
            FROM (
                SELECT MessageId = NEWID(), Id, WithdrawnAt
                FROM catalog.Products
                WHERE ({all} = 1 OR Id = {id}) AND WithdrawnAt IS NOT NULL
            ) AS p;
            """,
            ct);
}
