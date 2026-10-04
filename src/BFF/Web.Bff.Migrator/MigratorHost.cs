using Web.Bff.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Web.Bff.Migrator;

/// <summary>The §7.4 job host, outside <c>Program.cs</c> so a test can drive the wiring the Job runs.</summary>
public static class MigratorHost
{
    public static IHost Build(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        // §7.1's migrator identity, the only one with DDL. Reading the runtime key here would reduce the two
        // principals to a naming convention.
        builder.Services.AddDbContext<BffDbContext>(o =>
            o.UseSqlServer(
                builder.Configuration.GetConnectionString("BffMigrator"),
                sql => sql.EnableRetryOnFailure()));

        builder.Services.AddScoped<MigrationRunner>();

        return builder.Build();
    }
}
