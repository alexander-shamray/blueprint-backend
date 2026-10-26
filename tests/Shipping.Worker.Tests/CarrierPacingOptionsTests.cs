using Common.Infrastructure.Validation;
using Common.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Shipping.Infrastructure.Carrier;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>The carrier loops' pace: production's by default, and only a lease a pass outlives refused.</summary>
public sealed class CarrierPacingOptionsTests
{
    private static ValidateOptionsResult Validate(CarrierPacingOptions options) =>
        new AnnotatedOptionsValidator<CarrierPacingOptions>().Validate(null, options);

    [Fact]
    public void An_unset_section_runs_the_loops_at_the_pace_ADR_063_sizes()
    {
        // Literals, not the workers' constants: a default that moved with its constant would pass by construction.
        CarrierPacingOptions pace = new();

        pace.FulfilmentClaimBatchSize.ShouldBe(1);
        pace.FulfilmentLeaseSeconds.ShouldBe(60);
        pace.FulfilmentTick.ShouldBe(TimeSpan.FromSeconds(5));
        pace.TrackingClaimBatchSize.ShouldBe(20);
        pace.TrackingLeaseSeconds.ShouldBe(45);
        pace.TrackingTick.ShouldBe(TimeSpan.FromSeconds(5));
        pace.TrackingPollInterval.ShouldBe(TimeSpan.FromSeconds(30));

        Validate(pace).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void A_fulfilment_batch_its_lease_cannot_cover_in_turn_is_refused()
    {
        // Two rows of 42.5 seconds each, booked one after the other, outrun the default minute.
        ValidateOptionsResult refused = Validate(new CarrierPacingOptions { FulfilmentClaimBatchSize = 2 });

        refused.Failed.ShouldBeTrue();
        refused.FailureMessage.ShouldNotBeNull().ShouldContain(nameof(CarrierPacingOptions.FulfilmentLeaseSeconds));

        Validate(new CarrierPacingOptions { FulfilmentClaimBatchSize = 2, FulfilmentLeaseSeconds = 90 })
            .Succeeded.ShouldBeTrue("a lease grown with the batch covers both turns");
    }

    [Fact]
    public void A_tracking_batch_needs_no_longer_lease_since_its_rows_are_polled_at_once()
    {
        Validate(new CarrierPacingOptions { TrackingClaimBatchSize = CarrierPacingOptions.MaximumClaimBatchSize })
            .Succeeded.ShouldBeTrue();

        Validate(new CarrierPacingOptions { TrackingLeaseSeconds = 19 })
            .FailureMessage.ShouldNotBeNull().ShouldContain(nameof(CarrierPacingOptions.TrackingLeaseSeconds));
    }

    [Fact]
    public void A_pace_the_loops_cannot_keep_is_refused()
    {
        Validate(new CarrierPacingOptions { FulfilmentClaimBatchSize = 0 }).Failed.ShouldBeTrue();
        Validate(new CarrierPacingOptions { TrackingTick = TimeSpan.Zero }).Failed.ShouldBeTrue();
        Validate(new CarrierPacingOptions { TrackingPollInterval = TimeSpan.FromMilliseconds(500) })
            .FailureMessage.ShouldNotBeNull().ShouldContain(nameof(CarrierPacingOptions.TrackingPollInterval));
        Validate(new CarrierPacingOptions { TrackingTick = TimeSpan.FromSeconds(30) })
            .FailureMessage.ShouldNotBeNull().ShouldContain(nameof(CarrierPacingOptions.TrackingTick));
    }

    [Fact]
    public void The_stress_override_unpaces_both_loops_with_a_pace_the_validator_admits()
    {
        // Read from the override: a pace refused here would stop the stress run's worker at start.
        IConfigurationSection section = StressOverride.Configuration().GetSection(CarrierPacingOptions.SectionName);
        CarrierPacingOptions stress = section.Get<CarrierPacingOptions>().ShouldNotBeNull();

        Validate(stress).Succeeded.ShouldBeTrue();

        CarrierPacingOptions production = new();
        (stress.FulfilmentClaimBatchSize / stress.FulfilmentTick.TotalSeconds).ShouldBeGreaterThan(
            production.FulfilmentClaimBatchSize / production.FulfilmentTick.TotalSeconds,
            "the override exists to take the carrier's booking rate out of the run");
        stress.TrackingPollInterval.ShouldBeLessThan(production.TrackingPollInterval);
    }
}
