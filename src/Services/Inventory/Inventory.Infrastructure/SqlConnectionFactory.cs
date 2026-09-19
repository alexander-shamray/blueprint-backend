using System.Data;
using Common.Application;
using Microsoft.Data.SqlClient;

namespace Inventory.Infrastructure;

/// <summary>
/// The implementation behind §6.5's port, over the RUNTIME identity of §7.1 —
/// a query has no business on the migrator's connection. Service-local rather
/// than a building block: moving it would put
/// <c>Microsoft.Data.SqlClient</c> in every service's dependency graph to
/// serve the ones that happen to use SQL Server, and §9.4's dispatcher
/// resolves the <em>port</em> from the container rather than this type.
/// </summary>
/// <remarks>
/// <c>Create</c> only constructs — Dapper opens a closed connection itself,
/// and the caller disposes (§6.5's <c>using</c> at every call site).
/// </remarks>
internal sealed class SqlConnectionFactory(string connectionString) : IDbConnectionFactory
{
    public IDbConnection Create() => new SqlConnection(connectionString);
}
