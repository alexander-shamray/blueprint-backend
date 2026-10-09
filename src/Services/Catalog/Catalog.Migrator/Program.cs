using Catalog.Migrator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// §7.4's migration job, which ADR-007 keeps out of every host's startup.
// The host is not started: a Job whose pod never completes never finishes its pre-upgrade hook.
using IHost host = MigratorHost.Build(args);
using IServiceScope scope = host.Services.CreateScope();

// ADR-090: a republish run replaces the migration run, and is registered only when asked for.
if (scope.ServiceProvider.GetService<CatalogRepublisher>() is { } republisher)
    return await republisher.RunAsync(CancellationToken.None);

return await scope.ServiceProvider
    .GetRequiredService<MigrationRunner>()
    .RunAsync(CancellationToken.None);
