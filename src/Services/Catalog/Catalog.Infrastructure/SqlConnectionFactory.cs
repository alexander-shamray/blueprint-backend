using System.Data;
using Common.Application;
using Microsoft.Data.SqlClient;

namespace Catalog.Infrastructure;

/// <summary>
/// The implementation behind §6.5's port, over the RUNTIME identity of §7.1 —
/// a query has no business on the migrator's connection.
/// </summary>
/// <remarks>
/// Service-local rather than a building block: moving it would put
/// <c>Microsoft.Data.SqlClient</c> in every service's graph to serve the ones
/// using SQL Server. <c>Create</c> only constructs — Dapper opens a closed
/// connection, and the caller disposes (§6.5).
/// </remarks>
internal sealed class SqlConnectionFactory(string connectionString) : IDbConnectionFactory
{
    public IDbConnection Create() => new SqlConnection(connectionString);
}
