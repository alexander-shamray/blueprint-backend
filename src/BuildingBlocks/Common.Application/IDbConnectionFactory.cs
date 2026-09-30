using System.Data;

namespace Common.Application;

/// <summary>A connection for §6.5's Dapper reads, disposed by the caller; never inside §6.3's transaction.</summary>
public interface IDbConnectionFactory
{
    IDbConnection Create();
}
