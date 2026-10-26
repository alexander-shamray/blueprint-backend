using Common.Infrastructure.Validation;
using Common.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Notifications.Infrastructure.Delivery;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The send loops' pace: production's numbers by default, and only a lease a row outlives refused.</summary>
public sealed class SendPacingOptionsTests
{
    private static ValidateOptionsResult Validate(SendPacingOptions options) =>
        new AnnotatedOptionsValidator<SendPacingOptions>().Validate(null, options);

    [Fact]
    public void An_unset_section_runs_the_loops_at_the_pace_ADR_063_sizes()
    {
        // Literals, not the workers' constants: a default that moved with its constant would pass by construction.
        SendPacingOptions pace = new();

        pace.SendClaimBatchSize.ShouldBe(10);
        pace.SendLeaseSeconds.ShouldBe(45);
        pace.SendTick.ShouldBe(TimeSpan.FromSeconds(5));
        pace.PushClaimBatchSize.ShouldBe(10);
        pace.PushLeaseSeconds.ShouldBe(30);
        pace.PushTick.ShouldBe(TimeSpan.FromSeconds(5));

        Validate(pace).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void A_batch_needs_no_longer_lease_since_a_pass_sends_its_rows_at_once()
    {
        Validate(
                new SendPacingOptions
                {
                    SendClaimBatchSize = SendPacingOptions.MaximumClaimBatchSize,
                    PushClaimBatchSize = SendPacingOptions.MaximumClaimBatchSize
                })
            .Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void A_lease_one_row_outlives_or_a_pace_the_loops_cannot_keep_is_refused()
    {
        Validate(new SendPacingOptions { SendLeaseSeconds = 10 })
            .FailureMessage.ShouldNotBeNull().ShouldContain(nameof(SendPacingOptions.SendLeaseSeconds));
        Validate(new SendPacingOptions { PushLeaseSeconds = 5 })
            .FailureMessage.ShouldNotBeNull().ShouldContain(nameof(SendPacingOptions.PushLeaseSeconds));
        Validate(new SendPacingOptions { SendClaimBatchSize = 0 }).Failed.ShouldBeTrue();
        Validate(new SendPacingOptions { PushTick = TimeSpan.Zero }).Failed.ShouldBeTrue();

        // A tick so slow the relay's breaker could never see enough sends inside its window to open.
        Validate(new SendPacingOptions { SendTick = TimeSpan.FromMinutes(1) })
            .FailureMessage.ShouldNotBeNull().ShouldContain(nameof(SendPacingOptions.SendTick));
    }

    [Fact]
    public void The_stress_override_unpaces_both_loops_with_a_pace_the_validator_admits()
    {
        // Read from the override: a pace refused here would stop the stress run's worker at start.
        IConfigurationSection section = StressOverride.Configuration().GetSection(SendPacingOptions.SectionName);
        SendPacingOptions stress = section.Get<SendPacingOptions>().ShouldNotBeNull();

        Validate(stress).Succeeded.ShouldBeTrue();

        SendPacingOptions production = new();
        (stress.SendClaimBatchSize / stress.SendTick.TotalSeconds).ShouldBeGreaterThan(
            production.SendClaimBatchSize / production.SendTick.TotalSeconds,
            "the override exists to take the relay's rate out of the run");
        (stress.PushClaimBatchSize / stress.PushTick.TotalSeconds).ShouldBeGreaterThan(
            production.PushClaimBatchSize / production.PushTick.TotalSeconds,
            "and the push providers'");
    }
}
