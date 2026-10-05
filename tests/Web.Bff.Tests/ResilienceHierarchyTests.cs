using Common.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>§9.7's timeout hierarchy, read off the built host's named options.</summary>
public class ResilienceHierarchyTests
{
    private static HttpStandardResilienceOptions Configured()
    {
        using BffFactory factory = new();

        // Services throws until the host is built, and CreateClient builds it.
        using HttpClient client = factory.CreateClient();

        return factory.Services
            .GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>()
            .Get(PricingHop.ResilienceOptionsName);
    }

    [Fact]
    public void The_outbound_total_sits_below_the_service_operation_budget()
    {
        HttpStandardResilienceOptions options = Configured();

        // Strictly below; a configuration ordering, as no request-timeout middleware enforces OperationTimeout.
        options.TotalRequestTimeout.Timeout.ShouldBeLessThan(
            ServiceOptions.OperationTimeout,
            "§9.7: the outbound client total must be below the service operation total.");
    }

    [Fact]
    public void The_attempts_and_their_backoff_fit_inside_the_total()
    {
        HttpStandardResilienceOptions options = Configured();

        TimeSpan attempts = options.AttemptTimeout.Timeout * (options.Retry.MaxRetryAttempts + 1);

        // The waits between attempts too, bounded by MaxDelay, as jitter makes the nominal a draw (§9.7).
        options.Retry.MaxDelay.ShouldNotBeNull(
            "with UseJitter the nominal delay is not an upper bound, so the budget below " +
            "would be asserting a number the runtime is free to exceed (§9.7).");

        TimeSpan backoff = options.Retry.MaxDelay.Value * options.Retry.MaxRetryAttempts;

        (attempts + backoff).ShouldBeLessThanOrEqualTo(
            options.TotalRequestTimeout.Timeout,
            "the last attempt must be able to finish inside the total budget, otherwise it is " +
            "cancelled part-way and the retry never had a chance to help (§9.7).");
    }

    [Fact]
    public void The_attempt_timeout_is_inside_the_band_the_hierarchy_names()
    {
        HttpStandardResilienceOptions options = Configured();

        // §9.7's 1–2 s band, where too short an attempt turns a slow dependency into a failure.
        options.AttemptTimeout.Timeout.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
        options.AttemptTimeout.Timeout.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void TotalRequestTimeout_is_not_left_at_its_default()
    {
        HttpStandardResilienceOptions options = Configured();

        // §9.7's named trap, as its own test so a failure names the property.
        options.TotalRequestTimeout.Timeout.ShouldNotBe(
            TimeSpan.FromSeconds(30),
            "TotalRequestTimeout is at its default, which every resilience handler must set (§9.7).");
    }
}
