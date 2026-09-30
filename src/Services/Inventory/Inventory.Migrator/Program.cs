using Inventory.Migrator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// §7.4's migration job, holding §7.1's DDL identity; ADR-007 is why no host migrates at startup.
// The host is not started, since a Job whose pod never completes never finishes its pre-upgrade hook.
using IHost host = MigratorHost.Build(args);
using IServiceScope scope = host.Services.CreateScope();

return await scope.ServiceProvider
    .GetRequiredService<MigrationRunner>()
    .RunAsync(CancellationToken.None);
