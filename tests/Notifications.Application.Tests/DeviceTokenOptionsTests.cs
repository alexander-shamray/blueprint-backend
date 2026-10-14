using Notifications.Application.Devices;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

/// <summary>ADR-117's three numbers, refused rather than clamped as <c>ContactOptions</c>' are.</summary>
public class DeviceTokenOptionsTests
{
    [Fact]
    public void The_defaults_are_the_records_numbers()
    {
        DeviceTokenOptions options = new();

        options.StaleAfter.ShouldBe(TimeSpan.FromDays(30));
        options.MaxPerSubject.ShouldBe(10);
        options.MaxSequenceStep.ShouldBe(1000);
    }

    [Fact]
    public void A_window_at_fcms_own_expiry_is_accepted()
    {
        new DeviceTokenOptions(TimeSpan.FromDays(270), 1, 1).StaleAfter.ShouldBe(TimeSpan.FromDays(270));
    }

    [Theory]
    [InlineData(0, 10, 1000, "StaleAfter")]
    [InlineData(271, 10, 1000, "StaleAfter")]
    [InlineData(30, 0, 1000, "MaxPerSubject")]
    [InlineData(30, 10, 0, "MaxSequenceStep")]
    public void A_number_out_of_bounds_is_refused_naming_its_property(
        int staleDays, int maxPerSubject, long maxSequenceStep, string member)
    {
        ArgumentOutOfRangeException thrown = Should.Throw<ArgumentOutOfRangeException>(
            () => new DeviceTokenOptions(TimeSpan.FromDays(staleDays), maxPerSubject, maxSequenceStep));

        thrown.ParamName.ShouldBe(member);
    }
}
