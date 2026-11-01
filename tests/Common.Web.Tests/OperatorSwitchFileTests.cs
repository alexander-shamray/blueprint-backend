using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>The operator switch file is layered last, so a key outside <c>Switches:Off</c> in it would win over the
/// Secret-sourced environment; only the switch reaches the configuration (ADR-187).</summary>
public sealed class OperatorSwitchFileTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("operator-switch-").FullName;

    private string FilePath => Path.Combine(_directory, "switches.json");

    [Fact]
    public void The_switch_names_bind_and_every_other_key_in_the_file_is_left_out()
    {
        File.WriteAllText(FilePath, """
            {
              "Switches": { "Off": [ "notifications-email" ] },
              "ConnectionStrings": { "Notifications": "Server=elsewhere" },
              "Authentication": { "Authority": "https://elsewhere" }
            }
            """);

        IConfigurationRoot configuration = Build();

        configuration["Switches:Off:0"].ShouldBe("notifications-email");
        configuration["ConnectionStrings:Notifications"].ShouldBe("Server=from-the-secret");
        configuration["Authentication:Authority"].ShouldBeNull();
    }

    [Fact]
    public void A_reload_that_brings_another_key_changes_only_the_switch()
    {
        File.WriteAllText(FilePath, """{ "Switches": { "Off": [ "a" ] } }""");
        IConfigurationRoot configuration = Build();

        File.WriteAllText(FilePath, """
            { "Switches": { "Off": [ "b" ] }, "ConnectionStrings": { "Notifications": "Server=elsewhere" } }
            """);
        configuration.Reload();

        configuration["Switches:Off:0"].ShouldBe("b");
        configuration["ConnectionStrings:Notifications"].ShouldBe("Server=from-the-secret");
    }

    /// <summary>ADR-147's refusal reads a scalar at <c>Switches:Off</c>, so the filter must not be what hides
    /// one.</summary>
    [Fact]
    public void A_scalar_at_the_switch_still_reaches_the_configuration()
    {
        File.WriteAllText(FilePath, """{ "Switches": { "Off": "notifications-email" } }""");

        Build()["Switches:Off"].ShouldBe("notifications-email");
    }

    [Fact]
    public void A_key_that_only_shares_the_switch_prefix_is_left_out()
    {
        File.WriteAllText(FilePath, """{ "Switches": { "OffAlso": "x", "Other": "y" } }""");

        IConfigurationRoot configuration = Build();

        configuration["Switches:OffAlso"].ShouldBeNull();
        configuration["Switches:Other"].ShouldBeNull();
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private IConfigurationRoot Build() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection([new("ConnectionStrings:Notifications", "Server=from-the-secret")])
            .AddOperatorSwitchFile(FilePath)
            .Build();
}
