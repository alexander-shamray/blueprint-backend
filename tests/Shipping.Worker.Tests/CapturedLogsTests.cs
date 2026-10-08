using Microsoft.Extensions.Logging;
using Shipping.TestSupport;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>The capture the no-address searches read, held to seeing what an exporter is sent (ADR-052).</summary>
public sealed class CapturedLogsTests
{
    [Fact]
    public void A_scope_s_text_and_its_pairs_are_captured_as_an_exporter_would_write_them()
    {
        using CapturedLogs captured = new();
        ILogger logger = captured.CreateLogger("Probe");

        // The scope alone: the capture records it as it opens, before any line is written inside it.
        using IDisposable? scope = logger.BeginScope(new Dictionary<string, object?> { ["Line1"] = "1 Abay Avenue" });

        captured.Everything.ShouldContain(
            "Line1=1 Abay Avenue",
            "Common.Web sets IncludeScopes, so a scoped address reaches the exporter and must reach the search");
    }
}
