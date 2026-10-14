using Notifications.Application.Devices;
using Notifications.Infrastructure.Push;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>Each provider's hop holds together as §9.7's rules ask, and Apple's token window is kept.</summary>
public sealed class PushHopTests
{
    public static TheoryData<string> Hops => [FcmPushProvider.Name, ApnsPushProvider.Name];

    [Theory]
    [MemberData(nameof(Hops))]
    public void The_worst_case_of_every_attempt_and_delay_fits_inside_the_total(string provider)
    {
        PushHopBudget hop = Budget(provider);
        TimeSpan worst = (hop.AttemptTimeout * (hop.MaxRetryAttempts + 1)) + (hop.MaxRetryDelay * hop.MaxRetryAttempts);

        worst.ShouldBeLessThanOrEqualTo(hop.TotalTimeout, "or the total cancels an attempt the budget promised");
    }

    [Theory]
    [MemberData(nameof(Hops))]
    public void A_window_holds_more_than_one_subjects_fan_out_before_the_breaker_may_open(string provider)
    {
        Budget(provider).MinimumThroughput.ShouldBeGreaterThanOrEqualTo(new DeviceTokenOptions().MaxPerSubject);
    }

    [Fact]
    public void An_apns_token_lives_inside_apples_window()
    {
        ApnsHop.ProviderTokenLifetime.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMinutes(20));
        ApnsHop.ProviderTokenLifetime.ShouldBeLessThan(TimeSpan.FromMinutes(60));
    }

    private static PushHopBudget Budget(string provider) =>
        provider == FcmPushProvider.Name ? FcmHop.Budget : ApnsHop.Budget;
}
