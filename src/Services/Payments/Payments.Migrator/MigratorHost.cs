using Payments.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Payments.Migrator;

/// <summary>The §7.4 job host, built here so a host test drives the wiring the Job runs.</summary>
public static class MigratorHost
{
    public static IHost Build(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        // §7.1's migrator identity, the one with DDL; reading "Payments" would merge the two principals.
        builder.Services.AddDbContext<PaymentsDbContext>(o =>
            o.UseSqlServer(
                builder.Configuration.GetConnectionString("PaymentsMigrator"),
                sql => sql.EnableRetryOnFailure()));

        builder.Services.AddScoped<MigrationRunner>();

        return builder.Build();
    }
}
