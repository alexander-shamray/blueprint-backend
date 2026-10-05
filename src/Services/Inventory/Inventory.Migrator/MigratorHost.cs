using Inventory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Inventory.Migrator;

/// <summary>The §7.4 job host, built here so a host test drives the wiring the Job runs.</summary>
public static class MigratorHost
{
    public static IHost Build(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        // §7.1's migrator identity, the one with DDL; reading "Inventory" would merge the two principals.
        builder.Services.AddDbContext<InventoryDbContext>(o =>
            o.UseSqlServer(
                builder.Configuration.GetConnectionString("InventoryMigrator"),
                sql => sql.EnableRetryOnFailure()));

        builder.Services.AddScoped<MigrationRunner>();

        // §14.3's gate, failing closed on both halves: parsed, since GetValue<bool> throws on "" and fails the
        // hook, and Development only, an environment name no chart sets.
        bool requested = bool.TryParse(builder.Configuration["Seed:Enabled"], out bool enabled) && enabled;

        if (requested && builder.Environment.IsDevelopment())
            builder.Services.AddScoped<InventorySeeder>();

        return builder.Build();
    }
}
