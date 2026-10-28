using Common.Application;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Preferences;
using Notifications.Application.Preferences.GetPreferences;
using Notifications.Application.Preferences.SetPreference;
using Notifications.Application.Privacy.ErasePersonalData;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>
/// ADR-120's journal against a real database, through the shared store both hosts compose: the append's
/// precondition and repeat, its race, the erasure's anonymising and the retention pass. The API host's suite holds
/// the transport above it.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ConsentJournalTests(ServiceFixture fixture) : IAsyncLifetime
{
    private const string Topic = "shipment-dispatched";
    private const string Push = "push";
    private const string Client = "commerce-app";

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<PreferenceWrite> SetAsync(Guid subject, bool optedIn, Guid? readVersion)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        Result<PreferenceWrite> written = await scope.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync(
            new SetPreferenceCommand(subject, Topic, Push, optedIn, readVersion, Client),
            TestContext.Current.CancellationToken);

        return written.Value;
    }

    private async Task<PreferenceState> ReadAsync(Guid subject)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        IReadOnlyList<PreferenceState> states = await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .QueryAsync(new GetPreferencesQuery(subject), TestContext.Current.CancellationToken);

        return states.Single(s => s is { Topic: Topic, Channel: Push });
    }

    private async Task EraseAsync(Guid subject, Guid? request = null)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync(
            new ErasePersonalDataCommand(request ?? Guid.CreateVersion7(), subject),
            TestContext.Current.CancellationToken);
    }

    private Task<int> EntriesAsync(Guid entry) =>
        fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM notifications.ConsentEntries WHERE EntryId = {0}",
            entry);

    // Every entry and stamp moved back by more than the invented window, so only the rule decides what goes.
    private Task AgeJournalAsync() =>
        fixture.ExecuteAsync(
            """
            UPDATE notifications.ConsentEntries
            SET WrittenAt = DATEADD(day, -2000, WrittenAt), AnonymisedAt = DATEADD(day, -2000, AnonymisedAt);
            """);

    [Fact]
    public async Task A_first_write_appends_and_its_repeat_is_answered_as_it_was()
    {
        Guid subject = Guid.CreateVersion7();

        PreferenceWrite first = await SetAsync(subject, false, readVersion: null);
        PreferenceWrite repeat = await SetAsync(subject, false, readVersion: null);

        first.Outcome.ShouldBe(ConsentAppendOutcome.Appended);
        first.State.Source.ShouldBe(PreferenceSource.Set);
        first.State.OptedIn.ShouldBeFalse();
        repeat.ShouldBe(first with { Outcome = ConsentAppendOutcome.Repeated }, "a retry is answered as the first");
        (await ReadAsync(subject)).ShouldBe(first.State);
        (await fixture.ScalarAsync<string>(
            "SELECT Value = Client FROM notifications.ConsentEntries WHERE EntryId = {0}",
            first.State.Version!.Value)).ShouldBe(Client, "the entry records the azp it was written through");
    }

    [Fact]
    public async Task A_write_under_the_version_read_supersedes_it_and_the_journal_keeps_both()
    {
        Guid subject = Guid.CreateVersion7();
        PreferenceWrite withdrawn = await SetAsync(subject, false, null);

        PreferenceWrite optedIn = await SetAsync(subject, true, withdrawn.State.Version);

        optedIn.Outcome.ShouldBe(ConsentAppendOutcome.Appended);
        optedIn.State.Version.ShouldNotBe(withdrawn.State.Version);
        (await EntriesAsync(withdrawn.State.Version!.Value)).ShouldBe(1, "append-only: the first entry stays");
        (await fixture.ScalarAsync<Guid>(
            "SELECT Value = SupersedesEntryId FROM notifications.ConsentEntries WHERE EntryId = {0}",
            optedIn.State.Version!.Value)).ShouldBe(withdrawn.State.Version!.Value);
    }

    [Fact]
    public async Task A_stale_write_is_refused_with_the_pair_as_it_stands_and_its_own_retry_is_a_repeat()
    {
        // Device A withdraws; device B, which read the same version, opts in afterwards and must not undo it.
        Guid subject = Guid.CreateVersion7();
        PreferenceWrite read = await SetAsync(subject, true, null);
        PreferenceWrite withdrawal = await SetAsync(subject, false, read.State.Version);

        PreferenceWrite stale = await SetAsync(subject, true, read.State.Version);
        PreferenceWrite retried = await SetAsync(subject, false, read.State.Version);

        stale.Outcome.ShouldBe(ConsentAppendOutcome.PreconditionFailed);
        stale.State.ShouldBe(withdrawal.State, "a 412 carries the pair's present state");
        retried.ShouldBe(withdrawal with { Outcome = ConsentAppendOutcome.Repeated });
        (await ReadAsync(subject)).OptedIn.ShouldBeFalse("the withdrawal stands");
    }

    [Fact]
    public async Task A_first_write_for_a_pair_that_has_an_entry_lost_the_race()
    {
        Guid subject = Guid.CreateVersion7();
        await SetAsync(subject, false, null);

        (await SetAsync(subject, true, null)).Outcome.ShouldBe(ConsentAppendOutcome.PreconditionFailed);
    }

    [Fact]
    public async Task Two_first_writes_at_once_append_one_entry_and_the_other_is_refused()
    {
        Guid subject = Guid.CreateVersion7();

        PreferenceWrite[] both = await Task.WhenAll(SetAsync(subject, true, null), SetAsync(subject, false, null));

        both.Count(w => w.Outcome == ConsentAppendOutcome.Appended).ShouldBe(1);
        both.Count(w => w.Outcome == ConsentAppendOutcome.PreconditionFailed).ShouldBe(1);
        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM notifications.ConsentEntries WHERE SubjectId = {0}",
            subject)).ShouldBe(1);
    }

    [Fact]
    public async Task An_erasure_anonymises_every_entry_keeps_the_evidence_and_a_later_write_starts_afresh()
    {
        Guid subject = Guid.CreateVersion7();
        PreferenceWrite first = await SetAsync(subject, false, null);
        PreferenceWrite second = await SetAsync(subject, true, first.State.Version);

        await EraseAsync(subject);

        (await fixture.ScalarAsync<int>(
            """
            SELECT Value = COUNT(*)
            FROM notifications.ConsentEntries
            WHERE SubjectId IS NULL
                AND AnonymisedAt IS NOT NULL
                AND Topic = {0}
                AND Client = {1}
                AND EntryId IN ({2}, {3})
            """,
            Topic,
            Client,
            first.State.Version!.Value,
            second.State.Version!.Value)).ShouldBe(2, "ADR-120: kept, without the subject, stamped");
        (await ReadAsync(subject)).Source.ShouldBe(PreferenceSource.Default);

        // A write under a version read before the erasure names an entry that no longer holds the subject.
        (await SetAsync(subject, false, second.State.Version)).Outcome
            .ShouldBe(ConsentAppendOutcome.PreconditionFailed);
        (await SetAsync(subject, false, null)).Outcome.ShouldBe(ConsentAppendOutcome.Appended);
    }

    [Fact]
    public async Task A_reissue_keeps_an_entry_written_after_its_first_pass_and_a_later_request_anonymises_it()
    {
        Guid subject = Guid.CreateVersion7();
        Guid request = Guid.CreateVersion7();
        await EraseAsync(subject, request);

        PreferenceWrite written = await SetAsync(subject, false, null);
        await EraseAsync(subject, request);

        (await ReadAsync(subject)).Version.ShouldBe(written.State.Version, "the subject's own act after the request");
        (await fixture.ScalarAsync<string>(
            "SELECT Value = ErasedUnder FROM notifications.ConsentEntries WHERE EntryId = {0}",
            written.State.Version!.Value)).ShouldBe($"[\"{request}\"]");

        await EraseAsync(subject);

        (await ReadAsync(subject)).Source.ShouldBe(PreferenceSource.Default);
        (await fixture.ScalarAsync<int>(
            """
            SELECT Value = COUNT(*)
            FROM notifications.ConsentEntries
            WHERE EntryId = {0}
                AND SubjectId IS NULL
                AND ErasedUnder = '[]'
            """,
            written.State.Version!.Value)).ShouldBe(1, "anonymised, its requests gone with the subject");
    }

    [Fact]
    public async Task Retention_deletes_every_ended_entry_past_the_window_in_one_pass_and_never_the_newest()
    {
        Guid subject = Guid.CreateVersion7();
        PreferenceWrite first = await SetAsync(subject, false, null);
        PreferenceWrite second = await SetAsync(subject, true, first.State.Version);
        PreferenceWrite newest = await SetAsync(subject, false, second.State.Version);
        await AgeJournalAsync();

        (await fixture.PurgeNotificationsRetentionAsync()).Consent.ShouldBe(2);
        (await EntriesAsync(first.State.Version!.Value)).ShouldBe(0);
        (await EntriesAsync(second.State.Version!.Value)).ShouldBe(0);
        (await fixture.PurgeNotificationsRetentionAsync()).Consent.ShouldBe(0, "a current entry is never aged");

        // By version, as the ageing above moved the newest entry's time too.
        (await ReadAsync(subject)).Version.ShouldBe(newest.State.Version);
    }

    [Fact]
    public async Task An_entry_kept_for_a_send_never_reads_as_newest_once_the_entry_after_it_has_gone()
    {
        // ADR-120 keeps an entry while a send names the entry it ended, and no longer: here the second is kept for
        // the first's send, the third goes, and the second must not become the pair's newest again.
        Guid subject = Guid.CreateVersion7();
        PreferenceWrite first = await SetAsync(subject, true, null);
        PreferenceWrite second = await SetAsync(subject, false, first.State.Version);
        PreferenceWrite third = await SetAsync(subject, true, second.State.Version);
        PreferenceWrite newest = await SetAsync(subject, false, third.State.Version);
        Notifications.Application.Records.Notification sent = await fixture.PendingAsync(
            Notifications.Application.Rendering.TemplateKeys.OrderPlaced,
            Guid.CreateVersion7());
        await fixture.ExecuteAsync(
            "UPDATE notifications.NotificationLog SET ConsentEntryId = {1} WHERE NotificationId = {0};",
            sent.NotificationId,
            first.State.Version!.Value);
        await AgeJournalAsync();

        (await fixture.PurgeNotificationsRetentionAsync()).Consent.ShouldBe(1);
        (await EntriesAsync(third.State.Version!.Value)).ShouldBe(0);
        (await EntriesAsync(second.State.Version!.Value)).ShouldBe(1, "it shows when the sent consent ended");
        (await ReadAsync(subject)).Version.ShouldBe(newest.State.Version);

        // And the next write still names the newest, which the precondition reads by position as the read does.
        (await SetAsync(subject, true, newest.State.Version)).Outcome.ShouldBe(ConsentAppendOutcome.Appended);
    }

    [Fact]
    public async Task Retention_keeps_an_entry_a_kept_send_names_and_the_entry_that_ended_it()
    {
        Guid subject = Guid.CreateVersion7();
        PreferenceWrite decided = await SetAsync(subject, true, null);
        PreferenceWrite ending = await SetAsync(subject, false, decided.State.Version);
        await SetAsync(subject, true, ending.State.Version);
        Guid order = Guid.CreateVersion7();
        Notifications.Application.Records.Notification sent =
            await fixture.PendingAsync(Notifications.Application.Rendering.TemplateKeys.OrderPlaced, order);
        await fixture.ExecuteAsync(
            "UPDATE notifications.NotificationLog SET ConsentEntryId = {1} WHERE NotificationId = {0};",
            sent.NotificationId,
            decided.State.Version!.Value);
        await AgeJournalAsync();

        (await fixture.PurgeNotificationsRetentionAsync()).Consent.ShouldBe(0);
        (await EntriesAsync(decided.State.Version!.Value)).ShouldBe(1, "the send was decided under it");
        (await EntriesAsync(ending.State.Version!.Value)).ShouldBe(1, "it shows when that consent ended");
    }

    [Fact]
    public async Task Retention_deletes_an_anonymised_newest_entry_once_the_window_has_passed_since_the_erasure()
    {
        Guid subject = Guid.CreateVersion7();
        PreferenceWrite only = await SetAsync(subject, false, null);
        await EraseAsync(subject);

        (await fixture.PurgeNotificationsRetentionAsync()).Consent.ShouldBe(0, "the window runs from the erasure");
        await AgeJournalAsync();
        (await fixture.PurgeNotificationsRetentionAsync()).Consent.ShouldBe(1);
        (await EntriesAsync(only.State.Version!.Value)).ShouldBe(0);
    }
}
