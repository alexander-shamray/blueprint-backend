using System.Data;
using Common.Application;
using Microsoft.Data.SqlClient;

namespace Inventory.Infrastructure;

/// <summary>
/// The implementation behind §6.5's port, over the RUNTIME identity of §7.1 —
/// a query has no business on the migrator's connection. Service-local rather
/// than a building block, and PR-14 changed the reason without changing the
/// answer. The old one was that <c>Common.Infrastructure</c> took no project
/// references, so a factory naming <c>IDbConnectionFactory</c> would draw the
/// <c>Common.Application</c> edge; that edge now exists, and §9.4's dispatcher
/// resolves the <em>port</em> from the container rather than this type. What
/// keeps the implementation here is the provider: moving it would put
/// <c>Microsoft.Data.SqlClient</c> in every service's dependency graph to
/// serve the ones that happen to use SQL Server.
/// </summary>
/// <remarks>
/// <c>Create</c> only constructs — Dapper opens a closed connection itself,
/// and the caller disposes (§6.5's <c>using</c> at every call site).
/// </remarks>
internal sealed class SqlConnectionFactory(string connectionString) : IDbConnectionFactory
{
    public IDbConnection Create() => new SqlConnection(connectionString);
}
