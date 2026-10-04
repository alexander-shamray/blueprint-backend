using Common.Application;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Web.Bff.Persistence;

namespace Web.Bff;

/// <summary>ADR-051's schema as this host reaches it: context, connection port, purge and readiness.</summary>
public static class BffPersistence
{
    public static IServiceCollection AddBffPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        // Eager, so a host with no database does not start; an empty environment variable counts as none.
        // A literal key, as every service spells it, because smoke.sh holds the chart to the key it greps (§15.3).
        string? connectionString = configuration.GetConnectionString("Bff");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:Bff is not configured. The buyer's order read is a projection " +
                "this host owns (ADR-051), and it cannot be written or read without its database (§7.1).");
        }

        // EnableRetryOnFailure is what makes an execution strategy a real retry rather than a no-op (§6.3).
        services.AddDbContext<BffDbContext>(o =>
            o.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));

        // §6.5's port, which the purge, the projection's handlers and the read go through.
        services.AddSingleton<IDbConnectionFactory>(new SqlConnectionFactory(connectionString));

        // No OutboxTable and no marker half: the BFF publishes nothing and runs no command pipeline (§9.5).
        services.AddSingleton(new InboxTable(BffSchema.Name));
        services.AddSingleton(new RetentionPolicy());
        services.AddHostedService<RetentionPurgeService>();

        // Readiness (§13.5): this host's own SQL; Catalog's hop stays out, or its outage would unready the BFF.
        services
            .AddHealthChecks()
            .AddSqlServer(connectionString, name: "sql", tags: ["ready"]);

        return services;
    }
}
