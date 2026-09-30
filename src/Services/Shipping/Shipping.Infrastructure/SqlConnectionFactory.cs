using System.Data;
using Common.Application;
using Microsoft.Data.SqlClient;

namespace Shipping.Infrastructure;

/// <summary>§6.5's port over §7.1's runtime identity; it only constructs, and the caller disposes.</summary>
internal sealed class SqlConnectionFactory(string connectionString) : IDbConnectionFactory
{
    public IDbConnection Create() => new SqlConnection(connectionString);
}
