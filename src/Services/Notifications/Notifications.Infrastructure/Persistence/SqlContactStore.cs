using System.Data;
using Common.Application;
using Dapper;
using Notifications.Application.Contacts;

namespace Notifications.Infrastructure.Persistence;

/// <summary>The only reader and writer of <c>notifications.ContactRecords</c> (ADR-052).</summary>
/// <remarks>
/// On its own connection: a send worker is to save before it renders or sends, outside any unit. The save is one
/// transaction, as ADR-052 accepts two replicas resolving one customer at once.
/// </remarks>
internal sealed class SqlContactStore(IDbConnectionFactory connections) : IContactStore
{
    // HOLDLOCK takes the key range, so a second first-save waits and then updates the row this one inserted.
    private const string SaveSql =
        """
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;

        UPDATE notifications.ContactRecords WITH (UPDLOCK, HOLDLOCK)
        SET Email = @Email, Locale = @Locale, FetchedAt = @FetchedAt
        WHERE CustomerId = @CustomerId;

        IF @@ROWCOUNT = 0
            INSERT INTO notifications.ContactRecords (CustomerId, Email, Locale, FetchedAt)
            VALUES (@CustomerId, @Email, @Locale, @FetchedAt);

        COMMIT TRANSACTION;
        """;

    private const string GetSql =
        """
        SELECT Email, Locale, FetchedAt
        FROM notifications.ContactRecords
        WHERE CustomerId = @CustomerId;
        """;

    private const string DeleteSql =
        "DELETE FROM notifications.ContactRecords WHERE CustomerId = @CustomerId;";

    public async Task SaveAsync(
        Guid customerId,
        ContactLookup.Found contact,
        DateTimeOffset fetchedAt,
        CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        await connection.ExecuteAsync(new CommandDefinition(
            SaveSql,
            new { CustomerId = customerId, contact.Email, contact.Locale, FetchedAt = fetchedAt },
            cancellationToken: ct));
    }

    public async Task<ContactRecord?> GetAsync(Guid customerId, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        return await connection.QuerySingleOrDefaultAsync<ContactRecord>(new CommandDefinition(
            GetSql, new { CustomerId = customerId }, cancellationToken: ct));
    }

    public async Task DeleteAsync(Guid customerId, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        await connection.ExecuteAsync(new CommandDefinition(
            DeleteSql, new { CustomerId = customerId }, cancellationToken: ct));
    }
}
