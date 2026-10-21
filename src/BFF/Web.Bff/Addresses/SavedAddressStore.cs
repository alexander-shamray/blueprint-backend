using System.Data;
using System.Data.Common;
using System.Text.Json;
using Common.Application;
using Dapper;
using Web.Bff.Privacy;

namespace Web.Bff.Addresses;

/// <summary>§10.9's three operations over <c>bff.SavedAddresses</c>, each bound to a customer the caller never
/// names.</summary>
public sealed class SavedAddressStore(IDbConnectionFactory connections, TimeProvider clock)
{
    private const string ReadSql =
        """
        SELECT Line1, Line2, City, PostalCode, Country
        FROM bff.SavedAddresses
        WHERE CustomerId = @CustomerId;
        """;

    /// <summary>
    /// The customer's key, held to the commit even where there is no row, so an erasure's delete waits for the save
    /// or the save waits for the erasure's audit row and then reads it (ADR-135).
    /// </summary>
    private const string LockSql =
        """
        SELECT CustomerId
        FROM bff.SavedAddresses WITH (UPDLOCK, HOLDLOCK)
        WHERE CustomerId = @CustomerId;
        """;

    /// <summary>HOLDLOCK, so two saves racing for a customer's first row cannot both insert.</summary>
    private const string SaveSql =
        """
        MERGE bff.SavedAddresses WITH (HOLDLOCK) AS target
        USING (SELECT CustomerId = @CustomerId) AS source ON target.CustomerId = source.CustomerId
        WHEN MATCHED THEN
            UPDATE SET Line1 = @Line1, Line2 = @Line2, City = @City, PostalCode = @PostalCode,
                Country = @Country, SavedAt = @SavedAt, ErasedUnder = @ErasedUnder
        WHEN NOT MATCHED THEN
            INSERT (CustomerId, Line1, Line2, City, PostalCode, Country, SavedAt, ErasedUnder)
            VALUES (@CustomerId, @Line1, @Line2, @City, @PostalCode, @Country, @SavedAt, @ErasedUnder);
        """;

    private const string DeleteSql = "DELETE FROM bff.SavedAddresses WHERE CustomerId = @CustomerId;";

    public async Task<Result<SavedAddress>> FindAsync(Guid customerId, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        SavedAddress? row = await connection.QuerySingleOrDefaultAsync<SavedAddress>(
            new CommandDefinition(ReadSql, new { CustomerId = customerId }, cancellationToken: ct));

        return row is null
            ? Result.Failure<SavedAddress>(SavedAddressErrors.NotFound)
            : Result.Success(row);
    }

    /// <summary>
    /// Replaces whatever the customer had, stamped with the erasures already applied to them, which each keep it;
    /// a repeat of the same request leaves the same row.
    /// </summary>
    public async Task SaveAsync(Guid customerId, SavedAddress address, CancellationToken ct)
    {
        // The base type, for the asynchronous open, begin and commit that IDbConnection lacks.
        await using DbConnection connection = (DbConnection)connections.Create();
        await connection.OpenAsync(ct);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(ct);

        await connection.ExecuteAsync(
            new CommandDefinition(LockSql, new { CustomerId = customerId }, transaction, cancellationToken: ct));

        // One instant for the stamp and the save: the row is stamped with what was asked as of when it was taken
        // (ADR-139).
        DateTimeOffset savedAt = clock.GetUtcNow();
        IReadOnlyList<Guid> erasedUnder =
            await SqlErasedSubjects.RequestsNamingAsync(connection, transaction, customerId, savedAt, ct);

        await connection.ExecuteAsync(
            new CommandDefinition(
                SaveSql,
                new
                {
                    CustomerId = customerId,
                    address.Line1,
                    address.Line2,
                    address.City,
                    address.PostalCode,
                    address.Country,
                    SavedAt = savedAt,
                    ErasedUnder = JsonSerializer.Serialize(erasedUnder)
                },
                transaction,
                cancellationToken: ct));

        await transaction.CommitAsync(ct);
    }

    /// <summary>Deletes the customer's row, if any; a customer with none is as they asked to be left.</summary>
    public async Task DeleteAsync(Guid customerId, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        await connection.ExecuteAsync(
            new CommandDefinition(DeleteSql, new { CustomerId = customerId }, cancellationToken: ct));
    }
}
