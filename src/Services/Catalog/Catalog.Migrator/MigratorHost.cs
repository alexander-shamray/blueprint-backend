using Catalog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Catalog.Migrator;

/// <summary>The §7.4 job host, outside <c>Program.cs</c> so a test can drive the wiring the Job runs.</summary>
public static class MigratorHost
{
    public static IHost Build(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        // §7.1's migrator identity, the only one with DDL. Reading the runtime key here would reduce the two
        // principals to a naming convention.
        builder.Services.AddDbContext<CatalogDbContext>(o =>
            o.UseSqlServer(
                builder.Configuration.GetConnectionString("CatalogMigrator"),
                sql => sql.EnableRetryOnFailure()));

        builder.Services.AddScoped<MigrationRunner>();

        // §14.3's gate, failing closed on both halves: parsed, since GetValue<bool> throws on "" and fails the
        // hook, and Development only, an environment name no chart sets.
        bool requested = bool.TryParse(builder.Configuration["Seed:Enabled"], out bool enabled) && enabled;

        if (requested && builder.Environment.IsDevelopment())
            builder.Services.AddScoped<CatalogSeeder>();

        return builder.Build();
    }
}
