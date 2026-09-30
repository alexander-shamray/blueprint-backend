using System.Data;
using Common.Application;
using Microsoft.Data.SqlClient;

namespace Catalog.Infrastructure;

/// <summary>§6.5's port over §7.1's runtime identity, since a query has no business on the migrator's.</summary>
/// <remarks><c>Create</c> only constructs: Dapper opens a closed connection, and the caller disposes (§6.5).</remarks>
internal sealed class SqlConnectionFactory(string connectionString) : IDbConnectionFactory
{
    public IDbConnection Create() => new SqlConnection(connectionString);
}
