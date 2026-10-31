using Ordering.Infrastructure.Persistence;
using Ordering.Migrator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>§7.4's command timeout, read from the context the job host builds, not the constant.</summary>
public class MigratorCommandTimeoutTests
{
    [Fact]
    public void The_migrators_context_carries_the_raised_command_timeout()
    {
        // Never opened: building the host and resolving a context connects to nothing.
        string[] args =
        [
            "--environment=Development",
            "--ConnectionStrings:OrderingMigrator=Server=command-timeout.invalid;Database=Ordering"
        ];

        using IHost host = MigratorHost.Build(args);
        using IServiceScope scope = host.Services.CreateScope();

        OrderingDbContext db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        db.Database.GetCommandTimeout().ShouldBe(MigratorCommand.TimeoutSeconds);
    }
}
