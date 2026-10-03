using System.Text.Json.Nodes;
using Notifications.Application.Intake;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

/// <summary>The stored parameters round-trip, carry their version, and refuse any other (ADR-053 rule 4).</summary>
public class ParametersFormatTests
{
    private static readonly NotificationParameters Full = new()
    {
        OrderId = Guid.CreateVersion7(),
        OccurredAt = new DateTimeOffset(2026, 10, 2, 23, 59, 59, TimeSpan.Zero),
        Amount = 42.10m,
        Currency = "KZT",
        TrackingNumber = "KZ-ӘҒҚ-0042",
        CancelReason = "payment_timeout"
    };

    [Fact]
    public void Every_member_round_trips()
    {
        ParametersFormat.Read(ParametersFormat.Write(Full)).ShouldBe(Full);
    }

    [Fact]
    public void An_amount_keeps_its_scale_through_the_store()
    {
        // The renderer prints the decimal's own places, so a scale lost here is a rounding added there (ADR-053).
        NotificationParameters read = ParametersFormat.Read(ParametersFormat.Write(Full));

        read.Amount.ShouldBe(42.10m);
        read.Amount!.Value.Scale.ShouldBe((byte)2);
    }

    [Fact]
    public void An_absent_member_is_written_as_absent_and_read_back_as_null()
    {
        NotificationParameters bare = new() { OrderId = Guid.CreateVersion7(), OccurredAt = Full.OccurredAt };

        string stored = ParametersFormat.Write(bare);

        JsonNode.Parse(stored)!.AsObject().Select(p => p.Key).ShouldBe(["v", "orderId", "occurredAt"]);
        ParametersFormat.Read(stored).ShouldBe(bare);
    }

    [Fact]
    public void The_stored_object_names_its_version()
    {
        JsonNode.Parse(ParametersFormat.Write(Full))!["v"]!.GetValue<int>().ShouldBe(ParametersFormat.Version);
    }

    [Theory]
    [InlineData("""{"v":2,"orderId":"0199a9a0-0000-7000-8000-000000000001","occurredAt":"2026-10-02T09:00Z"}""")]
    [InlineData("""{"v":0,"orderId":"0199a9a0-0000-7000-8000-000000000001","occurredAt":"2026-10-02T09:00Z"}""")]
    [InlineData("""{"orderId":"0199a9a0-0000-7000-8000-000000000001","occurredAt":"2026-10-02T09:00:00+00:00"}""")]
    [InlineData("""{"v":"1","orderId":"0199a9a0-0000-7000-8000-000000000001"}""")]
    [InlineData("""{"v":1.5}""")]
    [InlineData("""[1]""")]
    public void A_version_it_does_not_know_is_refused_rather_than_guessed(string stored)
    {
        Should.Throw<UnreadableParametersException>(() => ParametersFormat.Read(stored));
    }

    [Theory]
    [InlineData("""{"v":1,"occurredAt":"2026-10-02T09:00:00+00:00"}""")]
    [InlineData("""{"v":1,"orderId":"not-a-guid","occurredAt":"2026-10-02T09:00:00+00:00"}""")]
    [InlineData("""{"v":1,"orderId":"0199a9a0-0000-7000-8000-000000000001","occurredAt":"2026-10-02","amount":"1"}""")]
    [InlineData("""{"v":1,""")]
    public void Version_one_s_shape_broken_is_refused_as_unreadable(string stored)
    {
        Should.Throw<UnreadableParametersException>(() => ParametersFormat.Read(stored));
    }

    [Fact]
    public void The_widest_values_the_intake_keeps_fit_the_column()
    {
        // Every bounded member at its bound, and an amount at decimal's full precision.
        NotificationParameters widest = Full with
        {
            Amount = decimal.MinValue,
            TrackingNumber = new string('Ә', InboundValues.MaxTrackingNumberLength),
            CancelReason = new string('r', InboundValues.MaxCodeLength)
        };

        ParametersFormat.Write(widest).Length.ShouldBeLessThanOrEqualTo(NotificationLimits.MaxParametersLength);
    }
}
