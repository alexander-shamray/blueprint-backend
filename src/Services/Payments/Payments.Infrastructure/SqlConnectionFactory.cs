using System.Data;
using Common.Application;
using Microsoft.Data.SqlClient;

namespace Payments.Infrastructure;

/// <summary>
/// The implementation behind §6.5's port, over the RUNTIME identity of §7.1
/// — a query has no business on the migrator's connection. Service-local
/// rather than a building block: moving it into <c>Common.Infrastructure</c>
/// would put <c>Microsoft.Data.SqlClient</c> in every service's dependency
/// graph to serve the ones that use SQL Server. <c>Create</c> only
/// constructs — Dapper opens a closed connection itself, and the caller
/// disposes (§6.5's <c>using</c> at every call site).
/// </summary>
internal sealed class SqlConnectionFactory(string connectionString) : IDbConnectionFactory
{
    public IDbConnection Create() => new SqlConnection(connectionString);
}
