using BffReplay;
using Common.Application;
using Common.Contracts;
using Common.Contracts.Ordering.V1;
using Common.Infrastructure.Outbox;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>What the rebuild reads, each held to the code that owns it.</summary>
public sealed class ReplayTargetTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private static readonly MessageTypeMap Contracts = new([typeof(IIntegrationEvent).Assembly]);

    public static TheoryData<string> Publishers => [.. Publisher.All.Select(p => p.Name)];

    [Fact]
    public void The_tool_replays_exactly_the_events_the_queue_binds() =>
        ReplayedEvents.Types.ShouldBe(MessagingRegistrationTests.Consumed, ignoreOrder: true);

    [Fact]
    public void Every_replayed_event_belongs_to_exactly_one_publisher()
    {
        foreach (Type replayed in ReplayedEvents.Types)
            Publisher.All.Count(p => ReplayedEvents.PublishedBy(p).Contains(replayed)).ShouldBe(1, replayed.Name);
    }

    [Theory]
    [MemberData(nameof(Publishers))]
    public void Each_publisher_s_outbox_is_the_table_its_service_maps(string name)
    {
        Publisher publisher = Publisher.All.Single(p => p.Name == name);
        string configuration = File.ReadAllText(
            RepositoryFile.Locate(
                $"src/Services/{name}/{name}.Infrastructure/Persistence/OutboxMessageConfiguration.cs"));

        configuration.ShouldContain($"builder.ToTable(\"OutboxMessages\", \"{publisher.Schema}\");");
    }

    [Fact]
    public void A_staged_event_reads_back_as_the_event_its_publisher_wrote()
    {
        OrderPlaced placed = OrderEvents.Placed(Guid.CreateVersion7(), Guid.CreateVersion7(), At);
        OutboxRow row = Row(placed, placed.MessageId);

        IIntegrationEvent read = ReplayPayload.Read(row, Contracts, new OutboxJson([]));

        OrderPlaced back = read.ShouldBeOfType<OrderPlaced>();
        back.MessageId.ShouldBe(placed.MessageId);
        back.OccurredAt.ShouldBe(placed.OccurredAt);
        back.Lines.ShouldBe(placed.Lines);
    }

    [Fact]
    public void A_row_whose_payload_names_another_message_stops_the_replay()
    {
        OrderPlaced placed = OrderEvents.Placed(Guid.CreateVersion7(), Guid.CreateVersion7(), At);
        OutboxRow row = Row(placed, Guid.CreateVersion7());

        InvalidOperationException refused = Should.Throw<InvalidOperationException>(
            () => ReplayPayload.Read(row, Contracts, new OutboxJson([])));

        refused.Message.ShouldContain("sends the row's own message or nothing");
    }

    // Staged through the publishers' own path, so the payload is the bytes a service writes (§9.4).
    private static OutboxRow Row(OrderPlaced message, Guid rowMessageId)
    {
        OutboxMessage staged = OutboxMessage.Stage(
            message,
            OutboxLane.Broker,
            message.CorrelationId,
            Contracts,
            new OutboxJson([]));

        return new OutboxRow(
            1,
            rowMessageId,
            staged.CorrelationId,
            staged.MessageType,
            staged.Payload,
            staged.OccurredAt);
    }
}
