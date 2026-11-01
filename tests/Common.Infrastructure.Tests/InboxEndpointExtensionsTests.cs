using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>§9.8's nesting at every endpoint: the in-memory outbox reaches a pipe only inside the inbox.</summary>
/// <remarks>
/// Typed beside the inbox on an endpoint, the outbox wraps it and a failed flush loses its sends (§9.8).
/// </remarks>
public sealed class InboxEndpointExtensionsTests
{
    private const string Helper = "src/BuildingBlocks/Common.Infrastructure/Inbox/InboxEndpointExtensions.cs";

    [Fact]
    public void No_source_adds_the_in_memory_outbox_but_the_helper()
    {
        string root = RepositoryRoot();

        Callers(root, ".UseInMemoryOutbox(").ShouldBe([Helper]);
    }

    [Fact]
    public void Every_service_and_the_bff_take_the_helper()
    {
        // Its subject is the scan: one that read no service would still pass the test above.
        string root = RepositoryRoot();
        string[] callers = Callers(root, ".UseInboxAndInMemoryOutbox(");

        foreach (string host in new[]
                 {
                     "Catalog", "Inventory", "Notifications", "Ordering", "Payments", "Privacy", "Shipping",
                     "BFF/Web.Bff"
                 })
        {
            callers.ShouldContain(c => c.Contains($"/{host}/", StringComparison.Ordinal), host);
        }
    }

    [Fact]
    public void Every_endpoint_that_takes_the_inbox_alone_takes_the_source_check_beside_it()
    {
        // The helper adds both, so only an endpoint without it can drop the check (ADR-191).
        string root = RepositoryRoot();
        string[] alone = [.. Callers(root, "UseConsumeFilter(typeof(InboxFilter<>)").Where(c => c != Helper)];

        alone.ShouldNotBeEmpty("the saga's endpoint takes the inbox alone, so a scan finding none read nothing");
        alone.ShouldBe(Callers(root, ".UseSourceExchangeCheck(").Where(c => c != Helper), ignoreOrder: true);
        foreach (string file in alone)
        {
            string text = File.ReadAllText(Path.Combine(root, file));
            Occurrences(text, ".UseSourceExchangeCheck(").ShouldBe(
                Occurrences(text, "UseConsumeFilter(typeof(InboxFilter<>)"),
                file);
        }
    }

    private static int Occurrences(string text, string call) =>
        text.Split(call, StringSplitOptions.None).Length - 1;

    private static string[] Callers(string root, string call) =>
    [
        .. Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains(call, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
    ];

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
