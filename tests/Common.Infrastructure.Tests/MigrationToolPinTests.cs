using System.Text.Json;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>
/// The dotnet-ef the tool manifest restores is the EF Core runtime's version, and so is the design-time package's,
/// because a tool a major away from the runtime fails with an error that names neither (docs/repo-map.md).
/// </summary>
public sealed class MigrationToolPinTests
{
    [Fact]
    public void The_dotnet_ef_tool_pin_and_the_EF_Core_Design_pin_are_the_EF_Core_runtime_pin()
    {
        string root = RepositoryRoot();
        XDocument packages = XDocument.Load(Path.Combine(root, "Directory.Packages.props"));

        string? Pin(string package) => packages
            .Descendants("PackageVersion")
            .SingleOrDefault(e => (string?)e.Attribute("Include") == package)
            ?.Attribute("Version")?.Value;

        string? runtime = Pin("Microsoft.EntityFrameworkCore");
        string? design = Pin("Microsoft.EntityFrameworkCore.Design");

        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, ".config", "dotnet-tools.json")));
        string? tool = manifest.RootElement.GetProperty("tools").GetProperty("dotnet-ef")
            .GetProperty("version").GetString();

        runtime.ShouldNotBeNullOrWhiteSpace();
        design.ShouldBe(runtime);
        tool.ShouldBe(runtime);
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Platform.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException($"No Platform.slnx above {AppContext.BaseDirectory}.");
    }
}
