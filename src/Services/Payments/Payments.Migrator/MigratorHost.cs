using Payments.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Payments.Migrator;

/// <summary>
/// The §7.4 job host. Built here rather than inline in <c>Program.cs</c> so the
/// smoke test drives the same wiring the Kubernetes Job runs — including which
/// connection string is read, which is the §7.1 claim most easily broken by a
/// one-word edit and least visible in review.
/// </summary>
public static class MigratorHost
{
    public static IHost Build(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        // The MIGRATOR identity of §7.1 — DDL on this database, and nothing
        // else in the platform holds it. Reading "Payments" here would collapse
        // the two principals into a naming convention: the secret that grants
        // schema rights is mounted into this workload alone, and a host that
        // reads either key can be handed either one.
        builder.Services.AddDbContext<PaymentsDbContext>(o =>
            o.UseSqlServer(
                builder.Configuration.GetConnectionString("PaymentsMigrator"),
                sql => sql.EnableRetryOnFailure()));

        builder.Services.AddScoped<MigrationRunner>();

        return builder.Build();
    }
}
