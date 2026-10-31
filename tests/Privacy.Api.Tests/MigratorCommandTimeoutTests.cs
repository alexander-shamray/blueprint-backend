using Privacy.Infrastructure.Persistence;
using Privacy.Migrator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Privacy.Api.Tests;

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
            "--ConnectionStrings:PrivacyMigrator=Server=command-timeout.invalid;Database=Privacy"
        ];

        using IHost host = MigratorHost.Build(args);
        using IServiceScope scope = host.Services.CreateScope();

        PrivacyDbContext db = scope.ServiceProvider.GetRequiredService<PrivacyDbContext>();

        db.Database.GetCommandTimeout().ShouldBe(MigratorCommand.TimeoutSeconds);
    }
}
