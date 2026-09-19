using Payments.Migrator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// §7.4's migration job: the one process in the platform holding the DDL
// identity of §7.1, with no network listener and no user input. It runs the
// migrations and exits — ADR-007 is why no host does this at startup.
//
// The host is not started. Host.RunAsync would keep this process alive with
// nothing to serve, and a Job whose pod never completes is a deploy that never
// finishes its pre-upgrade hook.
using IHost host = MigratorHost.Build(args);
using IServiceScope scope = host.Services.CreateScope();

return await scope.ServiceProvider
    .GetRequiredService<MigrationRunner>()
    .RunAsync(CancellationToken.None);
