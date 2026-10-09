using Catalog.Migrator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary>ADR-090's gate at <see cref="MigratorHost"/>: a republisher only for a flag that parses true.</summary>
public class RepublishGateTests
{
    // Never opened: building the host and resolving a context connects to nothing.
    private const string Runtime = "--ConnectionStrings:Catalog=Server=republish-gate.invalid;Database=Catalog";

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("false", false)]
    [InlineData("1", false)]
    [InlineData("yes", false)]
    public void The_republisher_is_registered_only_for_a_flag_that_parses_true(string? flag, bool registered)
    {
        string[] republish = flag is null ? [] : [$"--Republish:Enabled={flag}"];

        using IHost host = MigratorHost.Build([Runtime, .. republish]);
        using IServiceScope scope = host.Services.CreateScope();

        (scope.ServiceProvider.GetService<CatalogRepublisher>() is not null).ShouldBe(registered);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("12345")]
    public void An_id_that_is_not_a_guid_refuses_the_host_and_does_not_widen_the_run(string id)
    {
        string[] args = [Runtime, "--Republish:Enabled=true", $"--Republish:Id={id}"];

        Should.Throw<InvalidOperationException>(() => MigratorHost.Build(args))
            .Message.ShouldBe("Republish:Id is not a GUID.");
    }

    [Fact]
    public void A_republish_handed_only_the_migrator_key_is_refused()
    {
        string[] args =
        [
            "--ConnectionStrings:CatalogMigrator=Server=republish-gate.invalid;Database=Catalog",
            "--Republish:Enabled=true"
        ];

        Should.Throw<InvalidOperationException>(() => MigratorHost.Build(args))
            .Message.ShouldBe("Republish needs ConnectionStrings:Catalog, the runtime key (§7.1).");
    }
}
