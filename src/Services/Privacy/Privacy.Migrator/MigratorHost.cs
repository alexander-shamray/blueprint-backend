using Privacy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Privacy.Migrator;

/// <summary>The §7.4 job host, outside <c>Program.cs</c> so a test can drive the wiring the Job runs.</summary>
public static class MigratorHost
{
    public static IHost Build(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        // ADR-079, checked here: Program.cs never starts this host, so a start-up validator would never run.
        if (MigratorTransport.Refusal(builder.Configuration, builder.Environment) is { } refusal)
            throw new InvalidOperationException(refusal);

        // ADR-090's republish is a run of its own: it migrates nothing and reaches production, so it has no
        // environment gate. Parsed, since GetValue<bool> throws on "" and a run must not fail on an empty flag.
        bool republish = bool.TryParse(builder.Configuration["Republish:Enabled"], out bool asked) && asked;

        // §7.1's migrator identity, the only one with DDL. Reading the runtime key here would reduce the two
        // principals to a naming convention. A republish is the exception that proves it: it reads and writes
        // data and holds no DDL, and the migrator login has no read role, so it takes the runtime key instead.
        string? connection = republish
            ? builder.Configuration.GetConnectionString("Privacy")
            : builder.Configuration.GetConnectionString("PrivacyMigrator");

        if (republish && string.IsNullOrEmpty(connection))
            throw new InvalidOperationException("Republish needs ConnectionStrings:Privacy, the runtime key (§7.1).");

        builder.Services.AddDbContext<PrivacyDbContext>(o =>
            o.UseSqlServer(connection, sql => sql.EnableRetryOnFailure()));

        builder.Services.AddScoped<MigrationRunner>();

        // §14.3's gate, failing closed on both halves: parsed, since GetValue<bool> throws on "" and fails the
        // hook, and Development only, an environment name no chart sets.
        bool requested = bool.TryParse(builder.Configuration["Seed:Enabled"], out bool enabled) && enabled;

        if (requested && builder.Environment.IsDevelopment())
            builder.Services.AddScoped<PrivacySeeder>();

        if (republish)
        {
            // An id that does not parse throws, since a run that dropped it would republish everything.
            Guid? only = builder.Configuration["Republish:Id"] switch
            {
                null or "" => null,
                string text when Guid.TryParse(text, out Guid id) => id,
                _ => throw new InvalidOperationException("Republish:Id is not a GUID.")
            };

            builder.Services.AddSingleton(new RepublishRequest(only));
            builder.Services.AddScoped<PrivacyRepublisher>();
        }

        return builder.Build();
    }
}
