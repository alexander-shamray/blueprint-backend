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

    // The operator switch only forwards a host's own delegate to the bus, so its calls declare no endpoint.
    private static readonly string[] Switch =
    [
        "src/BuildingBlocks/Common.Infrastructure/Switching/OperatorSwitchExtensions.cs",
        "src/BuildingBlocks/Common.Infrastructure/Switching/ReceiveEndpointSwitch.cs"
    ];

    private static readonly string[] Hosts =
        ["Catalog", "Inventory", "Notifications", "Ordering", "Payments", "Privacy", "Shipping", "BFF/Web.Bff"];

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

        foreach (string host in Hosts)
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
        foreach (string file in alone.Append(Helper))
        {
            // Each check typed before its inbox, or a refused delivery writes the inbox's row first.
            string text = File.ReadAllText(Path.Combine(root, file));
            int[] checks = Positions(text, ".UseSourceExchangeCheck(");
            int[] inboxes = Positions(text, "UseConsumeFilter(typeof(InboxFilter<>)");
            checks.Length.ShouldBe(inboxes.Length, file);
            for (int i = 0; i < inboxes.Length; i++)
            {
                checks[i].ShouldBeLessThan(inboxes[i], file);
                if (i > 0)
                    checks[i].ShouldBeGreaterThan(inboxes[i - 1], file);
            }
        }
    }

    [Fact]
    public void Every_receive_endpoint_takes_exactly_one_inbox()
    {
        // Per endpoint, not per file: a new endpoint in a file whose others take the helper would pass a
        // per-file check while §9.8 admits none without the inbox. Each is read to its own closing parenthesis,
        // so a delegate built outside the call reads as no inbox and fails, where every host's delegate is
        // inline. A file's inbox calls must also equal its endpoints, so one outside every endpoint fails.
        string root = RepositoryRoot();
        string[] files =
        [
            .. Callers(root, "ReceiveEndpoint(").Except(Switch)
        ];
        List<string> read = [];

        foreach (string file in files)
        {
            string code = SourceCode.Code(File.ReadAllText(Path.Combine(root, file)));
            string[] endpoints = Endpoints(code);
            foreach (string endpoint in endpoints)
                Inboxes(endpoint).ShouldBe(1, $"{file}: {Opening(endpoint)}");

            Inboxes(code).ShouldBe(endpoints.Length, file);

            if (endpoints.Length > 0)
                read.Add(file);
        }

        // Its subject is the scan, as above: one that split no host's endpoints would pass with nothing read.
        foreach (string host in Hosts)
        {
            read.ShouldContain(c => c.Contains($"/{host}/", StringComparison.Ordinal), host);
        }
    }

    [Fact]
    public void An_endpoint_beside_one_with_the_helper_is_read_on_its_own()
    {
        // The case's subject is the split: a per-file presence check would read this source as covered, and a
        // slice to the next endpoint or the file's end would give the last endpoint the call below it.
        const string source = """
                              cfg.ReceiveEndpoint(A, e => { e.UseInboxAndInMemoryOutbox(context); });
                              cfg.SwitchedReceiveEndpoint(B, e => { e.ConfigureConsumer<C>(context); });
                              x.ConnectReceiveEndpoint(D, e => { });
                              Later(e => e.UseInboxAndInMemoryOutbox(context));
                              """;

        string[] endpoints = Endpoints(source);

        endpoints.Length.ShouldBe(2);
        Inboxes(endpoints[0]).ShouldBe(1);
        Inboxes(endpoints[1]).ShouldBe(0);
    }

    [Fact]
    public void No_host_makes_an_endpoint_but_by_declaring_it()
    {
        // ConfigureEndpoints binds every unbound consumer with no inbox, and ConnectReceiveEndpoint is the
        // switch's plumbing: either outside the switch would make an endpoint the scan above never reads.
        string root = RepositoryRoot();

        foreach (string call in new[] { ".ConfigureEndpoints(", ".ConnectReceiveEndpoint(" })
        {
            string[] makers =
            [
                .. Callers(root, call).Except(Switch)
                    .Where(c => SourceCode.Code(File.ReadAllText(Path.Combine(root, c))).Contains(
                        call,
                        StringComparison.Ordinal))
            ];
            makers.ShouldBeEmpty(call);
        }
    }

    private static int Inboxes(string code) =>
        Positions(code, ".UseInboxAndInMemoryOutbox(").Length +
        Positions(code, "UseConsumeFilter(typeof(InboxFilter<>)").Length;

    // By its own text, as `Endpoints` lists the plain calls before the switched ones, not in source order.
    private static string Opening(string call)
    {
        string flat = string.Join(' ', call.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));
        return flat[..Math.Min(80, flat.Length)];
    }

    // Each `.ReceiveEndpoint(` or `.SwitchedReceiveEndpoint(` call; `ConnectReceiveEndpoint` is refused outside
    // the switch by the case above.
    private static string[] Endpoints(string code) =>
        [.. SourceCode.Calls(code, ".ReceiveEndpoint("), .. SourceCode.Calls(code, ".SwitchedReceiveEndpoint(")];

    private static int[] Positions(string text, string call)
    {
        List<int> found = [];
        for (int at = text.IndexOf(call, StringComparison.Ordinal);
             at >= 0;
             at = text.IndexOf(call, at + call.Length, StringComparison.Ordinal))
        {
            found.Add(at);
        }

        return [.. found];
    }

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
