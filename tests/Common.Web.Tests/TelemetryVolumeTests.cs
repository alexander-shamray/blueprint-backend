using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>What a host sends under load: framework log levels (ADR-159).</summary>
public class TelemetryVolumeTests
{
    private static readonly Action<ILogger, Exception?> InformationProbe =
        LoggerMessage.Define(LogLevel.Information, new EventId(1, nameof(InformationProbe)), "probe");

    private static readonly Action<ILogger, Exception?> WarningProbe =
        LoggerMessage.Define(LogLevel.Warning, new EventId(2, nameof(WarningProbe)), "probe");

    // The categories the 2026-10-28 stress run's Loki lines came from, spelled out rather than read from
    // FrameworkCategories, as the meters are: a shared list would delete a category from the assertion too.
    [Theory]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics")]
    [InlineData("Microsoft.AspNetCore.Routing.EndpointMiddleware")]
    [InlineData("Microsoft.AspNetCore.Http.Result.OkObjectResult")]
    [InlineData("Yarp.ReverseProxy.Forwarder.HttpForwarder")]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command")]
    [InlineData("System.Net.Http.HttpClient.catalog.LogicalHandler")]
    public void A_framework_category_exports_a_warning_and_not_an_information_line(string category)
    {
        List<LogRecord> exported = Log(TelemetryHost.Builder(), category);

        exported.Select(r => r.LogLevel).ShouldBe([LogLevel.Warning], $"{category} exported");
    }

    // The platform's own lines stay: blueprint-admin's review queue reads Ordering's "flagged for review" line
    // from Loki, and LoggingBehavior's line is the request's own record (§13.3). So do the reasons for a 401 or a
    // 403, which no span carries.
    [Theory]
    [InlineData("Common.Application.LoggingBehavior")]
    [InlineData("Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerHandler")]
    [InlineData("Microsoft.AspNetCore.Authorization.DefaultAuthorizationService")]
    [InlineData("Ordering.Application.Orders.FlagOrderForReview.FlagOrderForReviewHandler")]
    [InlineData("Microsoft.Hosting.Lifetime")]
    public void A_platform_category_still_exports_an_information_line(string category)
    {
        List<LogRecord> exported = Log(TelemetryHost.Builder(), category);

        exported.Select(r => r.LogLevel).ShouldBe([LogLevel.Information, LogLevel.Warning], $"{category} exported");
    }

    [Fact]
    public void A_level_the_configuration_names_for_a_framework_category_wins()
    {
        // An operator lowering one category while debugging, through the Logging section every real host binds.
        HostApplicationBuilder builder = TelemetryHost.Builder();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Logging:LogLevel:Microsoft.AspNetCore.Hosting"] = "Information" });

        List<LogRecord> exported = Log(builder, "Microsoft.AspNetCore.Hosting.Diagnostics");

        exported.Select(r => r.LogLevel).ShouldBe([LogLevel.Information, LogLevel.Warning]);
    }

    /// <summary>An Information and a Warning line on <paramref name="category"/>, through the host.</summary>
    private static List<LogRecord> Log(HostApplicationBuilder builder, string category)
    {
        List<LogRecord> exported = [];

        // WebApplication.CreateBuilder binds this section before Program.cs runs; the empty builder does not.
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        builder.AddObservability();
        builder.Logging.AddOpenTelemetry(o => o.AddInMemoryExporter(exported));

        using (IHost host = builder.Build())
        {
            ILogger logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category);

            InformationProbe(logger, null);
            WarningProbe(logger, null);
        }

        return exported;
    }
}
