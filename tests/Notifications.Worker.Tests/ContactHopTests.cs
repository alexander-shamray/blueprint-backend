using Notifications.Infrastructure.Contacts;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The contact hop's arithmetic, which <see cref="ContactHop"/>'s numbers satisfy together (§9.7).</summary>
public sealed class ContactHopTests
{
    [Fact]
    public void Every_attempt_and_every_bounded_delay_fit_inside_the_total()
    {
        TimeSpan worst = ContactHop.AttemptTimeout * (ContactHop.MaxRetryAttempts + 1)
                         + ContactHop.MaxRetryDelay * ContactHop.MaxRetryAttempts;

        worst.ShouldBeLessThan(ContactHop.TotalRequestTimeout,
            "a total that cancels the last retry makes the retry count a fiction");

        ContactHop.TotalRequestTimeout.ShouldBeLessThan(Common.Web.ServiceOptions.OperationTimeout);
    }

    [Fact]
    public void The_hop_sits_inside_the_bands_because_keycloak_is_this_deployments_own()
    {
        ContactHop.AttemptTimeout.ShouldBeInRange(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        ContactHop.TotalRequestTimeout.ShouldBeInRange(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void The_breaker_samples_at_least_twice_an_attempt()
    {
        ContactHop.CircuitBreakerSamplingDuration.ShouldBeGreaterThanOrEqualTo(ContactHop.AttemptTimeout * 2,
            "the standard handler's options validation refuses a shorter window at start");
    }
}
