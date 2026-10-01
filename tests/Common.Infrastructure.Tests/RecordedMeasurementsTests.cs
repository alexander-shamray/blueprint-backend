using Common.Infrastructure.Messaging;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>The listener records one factory's meter, not every meter that shares its name.</summary>
public class RecordedMeasurementsTests
{
    [Fact]
    public void A_meter_of_the_same_name_from_another_factory_is_not_recorded()
    {
        using TestMeterFactory mine = new();
        using TestMeterFactory theirs = new();
        using RecordedMeasurements measurements = new(mine, "Commerce.Messaging");

        new MessagingMetrics(theirs).Delivered("Theirs", TimeSpan.FromSeconds(1));
        new MessagingMetrics(mine).Delivered("Mine", TimeSpan.FromSeconds(2));

        measurements.For("messaging.delivery.lag").ShouldHaveSingleItem().Tag("message").ShouldBe("Mine");
    }
}
