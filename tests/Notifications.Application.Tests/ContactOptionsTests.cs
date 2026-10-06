using Notifications.Application.Contacts;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

/// <summary>ADR-052's two freshness numbers, refused rather than clamped as <c>RetentionPolicy</c>'s are.</summary>
public class ContactOptionsTests
{
    [Fact]
    public void The_defaults_are_the_records_numbers()
    {
        ContactOptions options = new();

        options.Freshness.ShouldBe(TimeSpan.FromMinutes(15));
        options.StaleCeiling.ShouldBe(TimeSpan.FromHours(24));
    }

    [Fact]
    public void A_ceiling_below_the_freshness_is_refused_rather_than_clamped()
    {
        ArgumentOutOfRangeException thrown = Should.Throw<ArgumentOutOfRangeException>(
            () => new ContactOptions(TimeSpan.FromHours(1), TimeSpan.FromMinutes(59)));

        thrown.ParamName.ShouldBe("StaleCeiling");
        thrown.Message.ShouldContain("StaleCeiling must be at least Freshness");
    }

    [Fact]
    public void A_ceiling_equal_to_the_freshness_is_accepted()
    {
        ContactOptions options = new(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

        options.StaleCeiling.ShouldBe(options.Freshness);
    }

    [Fact]
    public void The_order_the_numbers_are_given_in_decides_nothing()
    {
        // A ceiling shorter than the default freshness is legal beside a shorter freshness still.
        ContactOptions options = new(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));

        options.Freshness.ShouldBe(TimeSpan.FromMinutes(5));
        options.StaleCeiling.ShouldBe(TimeSpan.FromMinutes(10));
    }

    public static TheoryData<TimeSpan> OutOfRange() =>
        [TimeSpan.Zero, TimeSpan.FromSeconds(-1), TimeSpan.FromDays(3651)];

    [Theory]
    [MemberData(nameof(OutOfRange))]
    public void A_freshness_out_of_range_is_refused(TimeSpan freshness)
    {
        Should
            .Throw<ArgumentOutOfRangeException>(() => new ContactOptions(freshness, TimeSpan.FromDays(3650)))
            .ParamName.ShouldBe("Freshness");
    }

    [Theory]
    [MemberData(nameof(OutOfRange))]
    public void A_ceiling_out_of_range_is_refused(TimeSpan staleCeiling)
    {
        Should
            .Throw<ArgumentOutOfRangeException>(() => new ContactOptions(TimeSpan.FromSeconds(1), staleCeiling))
            .ParamName.ShouldBe("StaleCeiling");
    }
}
