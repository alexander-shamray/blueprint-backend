using System.Text.Json;
using Common.Application;
using Common.Infrastructure.Outbox;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary><c>Stage</c> is pure, so this is the cheapest guard on §9.1's single-identity rule.</summary>
public class OutboxMessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 2, 26, 0, TimeSpan.Zero);

    private static readonly MessageTypeMap Types = new([typeof(SampleDomainEvent).Assembly]);

    // No converters, since nothing in this assembly has a value object to convert.
    private static readonly OutboxJson Json = new([]);

    [Fact]
    public void Stage_takes_both_identities_from_the_envelope()
    {
        SampleIntegrationEvent message = new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = Now,
            Note = "published"
        };

        OutboxMessage row = OutboxMessage.Stage(
            message,
            OutboxLane.Broker,
            correlationId: Guid.CreateVersion7(),
            types: Types,
            json: Json);

        // Both from the envelope, since the mapper decides the correlation (§9.3).
        row.MessageId.ShouldBe(message.MessageId);
        row.CorrelationId.ShouldBe(message.CorrelationId);
    }

    [Fact]
    public void Stage_mints_an_id_for_a_domain_event_and_takes_the_callers_correlation()
    {
        var correlationId = Guid.CreateVersion7();

        OutboxMessage row = OutboxMessage.Stage(
            new SampleDomainEvent(Now, "raised"),
            OutboxLane.Local,
            correlationId,
            Types,
            Json);

        // A domain event has no envelope, so the row mints its own identity.
        row.MessageId.ShouldNotBe(Guid.Empty);
        row.CorrelationId.ShouldBe(correlationId);
    }

    [Fact]
    public void Stage_writes_the_persisted_name_and_the_payload()
    {
        OutboxMessage row = OutboxMessage.Stage(
            new SampleDomainEvent(Now, "raised"),
            OutboxLane.Local,
            Guid.CreateVersion7(),
            Types,
            Json);

        row.MessageType.ShouldBe(Types.NameOf(typeof(SampleDomainEvent)));
        row.Lane.ShouldBe(OutboxLane.Local);

        // The event's own instant, not the staging clock (§9.4).
        row.OccurredAt.ShouldBe(Now);

        // Through the runtime type, so a payload staged as `object` does not serialise as `{}`.
        JsonSerializer
            .Deserialize<SampleDomainEvent>(row.Payload, Json.Options)
            .ShouldBe(new SampleDomainEvent(Now, "raised"));
    }

    [Fact]
    public void Staging_an_unstageable_type_throws_before_a_row_exists()
    {
        Should.Throw<InvalidOperationException>(() => OutboxMessage.Stage(
            new NotAMessage("nope"),
            OutboxLane.Broker,
            Guid.CreateVersion7(),
            Types,
            Json));
    }

    [Fact]
    public void A_domain_event_cannot_be_staged_on_the_broker_lane()
    {
        // §9.3's allow-list made structural: a mapper returning its domain event cannot publish it (§5.5).
        Should
            .Throw<InvalidOperationException>(() => OutboxMessage.Stage(
                new SampleDomainEvent(Now, "raised"),
                OutboxLane.Broker,
                Guid.CreateVersion7(),
                Types,
                Json))
            .Message.ShouldContain("Broker lane");
    }

    [Fact]
    public void A_type_that_is_both_kinds_of_event_is_refused_on_either_lane()
    {
        // Such a payload passes both lane checks (§9.4), so both directions are asserted.
        Conflated message = new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = Now
        };

        OutboxLane[] lanes = [OutboxLane.Broker, OutboxLane.Local];

        foreach (OutboxLane lane in lanes)
        {
            Should
                .Throw<InvalidOperationException>(() => OutboxMessage.Stage(
                    message, lane, Guid.CreateVersion7(), Types, Json))
                .Message.ShouldContain("different things");
        }
    }

    [Fact]
    public void A_lane_that_is_no_lane_is_refused()
    {
        // C# does not confine an enum to its declared members (§9.4).
        Should
            .Throw<InvalidOperationException>(() => OutboxMessage.Stage(
                new SampleDomainEvent(Now, "raised"),
                (OutboxLane)42,
                Guid.CreateVersion7(),
                Types,
                Json))
            .Message.ShouldContain("is not a lane");
    }

    [Fact]
    public void A_contract_cannot_be_staged_on_the_local_lane()
    {
        // The Local lane feeds this service's own projection handlers, which no contract is for.
        SampleIntegrationEvent message = new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = Now,
            Note = "published"
        };

        Should
            .Throw<InvalidOperationException>(() => OutboxMessage.Stage(
                message, OutboxLane.Local, Guid.CreateVersion7(), Types, Json))
            .Message.ShouldContain("Local lane");
    }

    [Fact]
    public void The_outbox_options_do_not_rescue_a_renamed_member()
    {
        // §9.4 pins these options, since both sides have to agree.
        string lowered = """{"occurredAt":"2026-08-11T02:26:00+00:00","note":"raised"}""";

        JsonSerializer
            .Deserialize<SampleDomainEvent>(lowered, Json.Options)!
            .Note.ShouldBeNull();
    }
}
