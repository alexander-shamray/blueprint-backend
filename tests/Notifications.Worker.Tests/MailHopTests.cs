using Notifications.Infrastructure.Mail;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The relay hop's arithmetic, which <see cref="MailHop"/>'s numbers satisfy together (§9.7).</summary>
public sealed class MailHopTests
{
    [Fact]
    public void Every_attempt_and_every_bounded_delay_fit_inside_the_total()
    {
        TimeSpan worst = MailHop.AttemptTimeout * (MailHop.MaxRetryAttempts + 1) +
            MailHop.MaxRetryDelay * MailHop.MaxRetryAttempts;

        worst.ShouldBeLessThan(
            MailHop.TotalTimeout,
            "a total that cancels the last retry makes the retry count a fiction");

        MailHop.TotalTimeout.ShouldBeLessThan(
            Common.Web.ServiceOptions.OperationTimeout,
            "§9.7: the outbound client total must be strictly below the service operation total");
    }

    [Fact]
    public void The_attempt_timeout_is_outside_the_band_a_waiting_caller_is_sized_to()
    {
        // §9.7's band is a waiting caller's; this hop's row backs off, so it is sized to a third party.
        MailHop.AttemptTimeout.ShouldBeGreaterThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void The_breaker_opens_inside_one_window_at_the_workers_slowest_rate()
    {
        // The breaker sits inside the retry, so a transient failure is MaxRetryAttempts + 1 attempts, any other one.
        int sends = (int)Math.Ceiling(
            (double)MailHop.CircuitBreakerMinimumThroughput / (MailHop.MaxRetryAttempts + 1));

        (MailHop.SendTick * sends).ShouldBeLessThan(
            MailHop.CircuitBreakerSamplingDuration,
            "a breaker whose throughput a loop at one send a tick never reaches would never open");

        (MailHop.SendTick * MailHop.CircuitBreakerMinimumThroughput).ShouldBeLessThan(
            MailHop.CircuitBreakerSamplingDuration,
            "a send no retry repeats is one attempt, so failures of that kind alone must open the breaker too");
    }
}
