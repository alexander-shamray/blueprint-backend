using System.Text.RegularExpressions;
using Privacy.TestSupport;
using Shipping.TestSupport;
using Shouldly;
using Xunit;

namespace Platform.IntegrationTests.Journey;

/// <summary>Every service that erases is a holder in the set Compose ships and the factory raises with.</summary>
/// <remarks>
/// A holder missing from the set is silence read as success (ADR-092). Holders are read off the source. The
/// chart ships no set, so the one the Helm smoke renders stands for a deployment's.
/// </remarks>
public sealed partial class ResponderCoverageTests
{
    private const string ComposeFile = "deploy/compose/services/privacy.yml";
    private const string HelmSmokeFile = "deploy/helm/smoke.sh";

    [Fact]
    public void Every_service_that_erases_has_a_table_and_a_reporter()
    {
        string[] tables = Services("PersonalDataErasureConfiguration.cs");
        string[] reporters = Services("ErasureReporter.cs");

        // A service read as absent would leave every set below equal to nothing.
        reporters.Length.ShouldBeGreaterThan(0);

        tables.ShouldBe(reporters, ignoreOrder: true, "a service holds the erasure table and reports, or neither");
    }

    [Fact]
    public void The_compose_responder_set_names_every_service_that_erases()
    {
        Holders().ShouldBe(ComposeResponders(), ignoreOrder: true, $"{ComposeFile} must list exactly the holders");
    }

    [Fact]
    public void The_set_a_test_raises_requests_with_is_the_set_compose_ships()
    {
        string[] factory =
        [
            .. PrivacyApiFactory.PrivacySettings
                .Where(s => s.Key.StartsWith("Privacy:Responders:", StringComparison.Ordinal))
                .Select(s => s.Value!)
        ];

        factory.ShouldBe(ComposeResponders(), ignoreOrder: true, "PrivacyApiFactory.PrivacySettings drifted from Compose");
    }

    [Fact]
    public void The_set_the_helm_smoke_renders_is_the_set_of_holders()
    {
        Holders().ShouldBe(HelmResponders(), ignoreOrder: true, $"{HelmSmokeFile} must list exactly the holders");
    }

    private static string[] Holders()
    {
        string[] holders = [.. Reporters().Select(r => r.Responder)];

        holders.Length.ShouldBeGreaterThan(0);

        return holders;
    }

    private static string[] ComposeResponders()
    {
        string text = File.ReadAllText(Path.Combine(SimulatorMappings.RepositoryRoot(), ComposeFile));
        string[] responders = [.. ComposeResponder().Matches(text).Select(m => m.Groups[1].Value)];

        responders.ShouldNotBeEmpty($"{ComposeFile} declares no Privacy__Responders__n");

        return responders;
    }

    private static string[] HelmResponders()
    {
        string text = File.ReadAllText(Path.Combine(SimulatorMappings.RepositoryRoot(), HelmSmokeFile));
        Match declared = HelmHolders().Match(text);

        declared.Success.ShouldBeTrue($"{HelmSmokeFile} declares no PRIVACY_HOLDERS");

        return declared.Groups[1].Value.Split(',');
    }

    /// <summary>The directory each file sits under, i.e. the service, which names a holder whatever its layout.</summary>
    private static string[] Services(string fileName) =>
        [.. Directory.EnumerateFiles(Source(), fileName, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(f => Path.GetRelativePath(Source(), f).Split(Path.DirectorySeparatorChar))
            .Select(parts => parts[0] == "BFF" ? "BFF" : $"{parts[0]}/{parts[1]}")];

    private static IEnumerable<(string File, string Responder)> Reporters() =>
        Directory.EnumerateFiles(Source(), "ErasureReporter.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(f => (f, Responder().Match(File.ReadAllText(f))))
            .Select(pair => pair.Item2.Success
                ? (pair.f, pair.Item2.Groups[1].Value)
                : throw new InvalidOperationException($"{pair.f} declares no `const string Responder`."));

    private static string Source() => Path.Combine(SimulatorMappings.RepositoryRoot(), "src");

    [GeneratedRegex(@"Privacy__Responders__\d+:\s*""([^""]+)""")]
    private static partial Regex ComposeResponder();

    [GeneratedRegex("""^PRIVACY_HOLDERS='([^']+)'\r?$""", RegexOptions.Multiline)]
    private static partial Regex HelmHolders();

    [GeneratedRegex("""const string Responder = "([^"]+)";""")]
    private static partial Regex Responder();
}
