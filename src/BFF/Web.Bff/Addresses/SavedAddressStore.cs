using System.Data;
using Common.Application;
using Dapper;

namespace Web.Bff.Addresses;

/// <summary>§10.9's three operations over <c>bff.SavedAddresses</c>, each bound to a customer the caller never names.</summary>
public sealed class SavedAddressStore(IDbConnectionFactory connections, TimeProvider clock)
{
    private const string ReadSql =
        """
        SELECT Line1, Line2, City, PostalCode, Country
        FROM bff.SavedAddresses
        WHERE CustomerId = @CustomerId;
        """;

    /// <summary>HOLDLOCK, so two saves racing for a customer's first row cannot both insert.</summary>
    private const string SaveSql =
        """
        MERGE bff.SavedAddresses WITH (HOLDLOCK) AS target
        USING (SELECT CustomerId = @CustomerId) AS source ON target.CustomerId = source.CustomerId
        WHEN MATCHED THEN
            UPDATE SET Line1 = @Line1, Line2 = @Line2, City = @City, PostalCode = @PostalCode,
                Country = @Country, SavedAt = @SavedAt
        WHEN NOT MATCHED THEN
            INSERT (CustomerId, Line1, Line2, City, PostalCode, Country, SavedAt)
            VALUES (@CustomerId, @Line1, @Line2, @City, @PostalCode, @Country, @SavedAt);
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

    /// <summary>Replaces whatever the customer had; a repeat of the same request leaves the same row.</summary>
    public async Task SaveAsync(Guid customerId, SavedAddress address, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

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
                    SavedAt = clock.GetUtcNow()
                },
                cancellationToken: ct));
    }

    /// <summary>Deletes the customer's row, if any; a customer with none is as they asked to be left.</summary>
    public async Task DeleteAsync(Guid customerId, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        await connection.ExecuteAsync(
            new CommandDefinition(DeleteSql, new { CustomerId = customerId }, cancellationToken: ct));
    }
}
