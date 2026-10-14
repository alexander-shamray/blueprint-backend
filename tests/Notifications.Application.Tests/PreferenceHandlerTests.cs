using Common.Application;
using Notifications.Application.Preferences;
using Notifications.Application.Preferences.GetPreferences;
using Notifications.Application.Preferences.SetPreference;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

/// <summary>ADR-120's two handlers over a fake journal; the append's batch is the worker suite's.</summary>
public class PreferenceHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Subject = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly ConsentPair PlacedByEmail = new("order-placed", "email");

    private readonly FakeJournal _journal = new();

    private static SetPreferenceCommand Write(string topic = "order-placed", string channel = "email") =>
        new(Subject, topic, channel, false, null, "commerce-app");

    [Fact]
    public async Task A_write_naming_a_required_pair_is_refused_and_never_reaches_the_journal()
    {
        SetPreferenceHandler handler = new(_journal, new RequiredPairs([PlacedByEmail]), new FixedClock(Now));

        Result<PreferenceWrite> written = await handler.HandleAsync(Write(), TestContext.Current.CancellationToken);

        written.IsFailure.ShouldBeTrue();
        written.Error.Code.ShouldBe(SetPreferenceHandler.RequiredCode);
        written.Error.Type.ShouldBe(ErrorType.Rule, "§10.5's 422: well formed, and not allowed");
        _journal.Appended.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_write_is_answered_with_the_pair_as_the_journal_left_it()
    {
        ConsentEntry newest = new(Guid.CreateVersion7(), "order-placed", "push", false, Now, "commerce-app", null);
        _journal.Answer = new ConsentAppend(ConsentAppendOutcome.Appended, newest);
        SetPreferenceHandler handler = new(_journal, new RequiredPairs([PlacedByEmail]), new FixedClock(Now));

        Result<PreferenceWrite> written = await handler.HandleAsync(
            Write(channel: "push"),
            TestContext.Current.CancellationToken);

        written.Value.ShouldBe(
            new PreferenceWrite(
                ConsentAppendOutcome.Appended,
                new PreferenceState("order-placed", "push", false, PreferenceSource.Set, Now, newest.EntryId)));
        _journal.Appended.ShouldHaveSingleItem().now.ShouldBe(Now, "the clock's, so a test host substitutes it");
    }

    [Fact]
    public async Task A_lost_race_on_a_pair_with_no_entry_is_answered_with_its_default()
    {
        _journal.Answer = new ConsentAppend(ConsentAppendOutcome.PreconditionFailed, null);
        SetPreferenceHandler handler = new(_journal, new RequiredPairs([]), new FixedClock(Now));

        Result<PreferenceWrite> written = await handler.HandleAsync(Write(), TestContext.Current.CancellationToken);

        written.Value.Outcome.ShouldBe(ConsentAppendOutcome.PreconditionFailed);
        written.Value.State.Source.ShouldBe(PreferenceSource.Default);
        written.Value.State.OptedIn.ShouldBeTrue();
    }

    [Fact]
    public async Task The_read_answers_every_pair_in_the_vocabulary_order_each_decided_as_a_send_is()
    {
        ConsentEntry withdrawn = new(Guid.CreateVersion7(), "shipment-dispatched", "push", false, Now, "web", null);
        _journal.Newest = [withdrawn];
        GetPreferencesHandler handler = new(_journal, new RequiredPairs([PlacedByEmail]));

        IReadOnlyList<PreferenceState> states = await handler.HandleAsync(
            new GetPreferencesQuery(Subject),
            TestContext.Current.CancellationToken);

        states.Select(s => $"{s.Topic}/{s.Channel}").ShouldBe(
            [
                .. from topic in ConsentVocabulary.Topics
                   from channel in ConsentVocabulary.Channels
                   select $"{topic}/{channel}"
            ]);
        states.Single(s => s is { Topic: "order-placed", Channel: "email" }).Source
            .ShouldBe(PreferenceSource.Required);
        states.Single(s => s is { Topic: "shipment-dispatched", Channel: "push" })
            .ShouldBe(new PreferenceState("shipment-dispatched", "push", false, PreferenceSource.Set, Now,
                withdrawn.EntryId));
        states.Count(s => s.Source == PreferenceSource.Default).ShouldBe(12);
    }

    [Theory]
    [InlineData("newsletter", "email", "Topic")]
    [InlineData("order-placed", "sms", "Channel")]
    [InlineData("order-placed", "email", "Client")]
    public void The_validator_refuses_a_word_outside_the_vocabulary_or_a_missing_client(
        string topic,
        string channel,
        string field)
    {
        SetPreferenceCommand command = Write(topic, channel) with { Client = field == "Client" ? "" : "web" };

        new SetPreferenceValidator().Validate(command).Errors.ShouldContain(e => e.PropertyName == field);
    }

    [Fact]
    public void The_validator_refuses_an_empty_version_which_names_no_entry()
    {
        new SetPreferenceValidator().Validate(Write() with { ReadVersion = Guid.Empty }).IsValid.ShouldBeFalse();
        new SetPreferenceValidator().Validate(Write() with { ReadVersion = Guid.CreateVersion7() }).IsValid
            .ShouldBeTrue();
    }

    private sealed class FakeJournal : IConsentJournal
    {
        public IReadOnlyList<ConsentEntry> Newest { get; set; } = [];

        public ConsentAppend Answer { get; set; } = new(ConsentAppendOutcome.PreconditionFailed, null);

        public List<(SetPreferenceCommand command, DateTimeOffset now)> Appended { get; } = [];

        public Task<IReadOnlyList<ConsentEntry>> NewestAsync(Guid subjectId, CancellationToken ct) =>
            Task.FromResult(Newest);

        public Task<ConsentEntry?> NewestAsync(Guid subjectId, ConsentPair pair, CancellationToken ct) =>
            Task.FromResult(Newest.SingleOrDefault(e => e.Topic == pair.Topic && e.Channel == pair.Channel));

        public Task<ConsentAppend> AppendAsync(SetPreferenceCommand command, DateTimeOffset now, CancellationToken ct)
        {
            Appended.Add((command, now));
            return Task.FromResult(Answer);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
