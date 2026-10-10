using System.Diagnostics.Metrics;
using Common.Domain;
using Common.Contracts.Privacy.V1;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Notifications.TestSupport;
using Shouldly;
using Xunit;
using MessagingRegistration = Notifications.Infrastructure.Messaging.DependencyInjection;

namespace Notifications.Worker.Tests;

/// <summary>§11.7's erasure request over a real broker and database, since the harness replaces the endpoint.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class NotificationsErasureEndpointTests(ServiceFixture fixture) : IAsyncLifetime
{
    private const string PrivacyQueue = "privacy-completions";

    private static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(30);

    private static readonly Guid Subject = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Bystander = new("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private int _baseline;

    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync();
        _baseline = await fixture.QueueDepthAsync(PrivacyQueue);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_subjects_contact_and_order_records_go_and_a_bystanders_stay()
    {
        Guid subjectsOrder = Guid.CreateVersion7();
        Guid bystandersOrder = Guid.CreateVersion7();
        await fixture.OrderAsync(subjectsOrder, Subject);
        await fixture.OrderAsync(bystandersOrder, Bystander);
        await fixture.StageContactAsync(Subject, "subject@example.test", "en", TimeSpan.FromMinutes(5));
        await fixture.StageContactAsync(Bystander, "bystander@example.test", "en", TimeSpan.FromMinutes(5));

        await PublishRequestAsync(Guid.CreateVersion7());

        (await fixture.ContactAsync(Subject)).ShouldBeNull();
        (await fixture.OrderRecordAsync(subjectsOrder)).ShouldBeNull();
        (await fixture.ContactAsync(Bystander)).ShouldNotBeNull();
        (await fixture.OrderRecordAsync(bystandersOrder)).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_waiting_notice_is_deleted_whether_or_not_its_customer_was_resolved()
    {
        Guid unresolvedOrder = Guid.CreateVersion7();
        Guid resolvedOrder = Guid.CreateVersion7();
        Guid bystandersOrder = Guid.CreateVersion7();
        await fixture.OrderAsync(unresolvedOrder, Subject);
        await fixture.OrderAsync(bystandersOrder, Bystander);
        Notification unresolved = await fixture.PendingAsync(TemplateKeys.OrderConfirmed, unresolvedOrder);
        Notification resolved = await fixture.PendingAsync(TemplateKeys.OrderConfirmed, resolvedOrder);
        Notification bystanders = await fixture.PendingAsync(TemplateKeys.OrderConfirmed, bystandersOrder);
        await fixture.ExecuteAsync(
            "UPDATE notifications.NotificationLog SET CustomerId = {1} WHERE NotificationId = {0}",
            resolved.NotificationId,
            Subject);

        await PublishRequestAsync(Guid.CreateVersion7());

        (await NoticeCountAsync(unresolved.NotificationId)).ShouldBe(0, "found through the order record");
        (await NoticeCountAsync(resolved.NotificationId)).ShouldBe(0, "found through its own customer id");
        (await NoticeCountAsync(bystanders.NotificationId)).ShouldBe(1);
    }

    [Fact]
    public async Task An_ended_notice_keeps_its_evidence_and_loses_everything_that_names_the_customer()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.OrderAsync(order, Subject);
        Notification notice = await fixture.PendingAsync(TemplateKeys.OrderConfirmed, order);
        await fixture.ExecuteAsync(
            "UPDATE notifications.NotificationLog SET Status = 'Sent', CustomerId = {1}, Languages = 'en', " +
            "TemplateVersion = 3, CompletedAt = SYSDATETIMEOFFSET() WHERE NotificationId = {0}",
            notice.NotificationId,
            Subject);

        await PublishRequestAsync(Guid.CreateVersion7());

        Notification ended = await fixture.NotificationAsync(notice.NotificationId);
        ended.CustomerId.ShouldBeNull();
        ended.Languages.ShouldBeNull();
        ended.Parameters.ShouldBeEmpty("the order, amount and reason merged into the message are the customer's");
        ended.Status.ShouldBe(NotificationStatus.Sent, "ADR-053 rule 4 keeps the proof that a notice was sent");
        ended.TemplateKey.ShouldBe(TemplateKeys.OrderConfirmed);
        ended.TemplateVersion.ShouldBe(3);
        ended.CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task An_ended_notice_is_found_by_either_of_its_two_links_and_a_bystanders_is_left_alone()
    {
        Guid viaOrder = Guid.CreateVersion7();
        Guid viaCustomer = Guid.CreateVersion7();
        Guid bystandersOrder = Guid.CreateVersion7();
        await fixture.OrderAsync(viaOrder, Subject);
        await fixture.OrderAsync(bystandersOrder, Bystander);

        // Ended before its customer was assigned, so only the order record names the subject.
        Notification orderOnly = await EndedAsync(viaOrder, customer: null, status: "Suppressed");

        // Resolved, but its order record is already gone to retention, so only its own id names the subject.
        Notification customerOnly = await EndedAsync(viaCustomer, Subject, status: "Undeliverable");
        Notification bystanders = await EndedAsync(bystandersOrder, Bystander, status: "Sent");

        await PublishRequestAsync(Guid.CreateVersion7());

        Notification first = await fixture.NotificationAsync(orderOnly.NotificationId);
        first.Parameters.ShouldBeEmpty("found through the order record, which the notices are erased before");
        first.Status.ShouldBe(NotificationStatus.Suppressed);

        Notification second = await fixture.NotificationAsync(customerOnly.NotificationId);
        second.CustomerId.ShouldBeNull();
        second.Parameters.ShouldBeEmpty();
        second.Status.ShouldBe(NotificationStatus.Undeliverable);

        Notification untouched = await fixture.NotificationAsync(bystanders.NotificationId);
        untouched.CustomerId.ShouldBe(Bystander);
        untouched.Languages.ShouldBe("en");
        untouched.Parameters.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task The_audit_row_holds_a_count_and_a_hash_and_never_the_subject()
    {
        Guid request = Guid.CreateVersion7();
        await fixture.OrderAsync(Guid.CreateVersion7(), Subject);
        await fixture.StageContactAsync(Subject, "subject@example.test", "en", TimeSpan.FromMinutes(5));

        await PublishRequestAsync(request);

        (await fixture.ScalarAsync<int>(
            "SELECT Value = Count FROM notifications.PersonalDataErasures WHERE RequestId = {0}",
            request)).ShouldBe(2, "one order record and one contact");
        (await fixture.ScalarAsync<string>(
            "SELECT Value = SubjectHash FROM notifications.PersonalDataErasures WHERE RequestId = {0}",
            request)).ShouldBe(PersonalDataErasure.HashSubject(request, Subject));
    }

    [Fact]
    public async Task The_completion_is_sent_to_privacys_queue_once_the_erasure_has_committed()
    {
        await fixture.OrderAsync(Guid.CreateVersion7(), Subject);

        await PublishRequestAsync(Guid.CreateVersion7());

        await Eventually(CountCompletionsAsync, expected: 1, because: "the send is released when the consumer succeeds");
    }

    [Fact]
    public async Task A_subject_with_nothing_here_still_gets_an_audit_row_and_a_completion()
    {
        Guid request = Guid.CreateVersion7();

        await PublishRequestAsync(request);

        (await fixture.ScalarAsync<int>(
            "SELECT Value = Count FROM notifications.PersonalDataErasures WHERE RequestId = {0}",
            request)).ShouldBe(0);
        await Eventually(CountCompletionsAsync, expected: 1, because: "silence cannot be told from success (§11.7)");
    }

    [Fact]
    public async Task A_reissued_request_keeps_one_audit_row_and_reports_again()
    {
        Guid request = Guid.CreateVersion7();
        await fixture.OrderAsync(Guid.CreateVersion7(), Subject);
        await PublishRequestAsync(request);

        await PublishRequestAsync(request, Requested(request, Guid.CreateVersion7()));

        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM notifications.PersonalDataErasures WHERE RequestId = {0}",
            request)).ShouldBe(1);
        (await fixture.ScalarAsync<int>(
            "SELECT Value = Count FROM notifications.PersonalDataErasures WHERE RequestId = {0}",
            request)).ShouldBe(1, "the second pass found nothing and kept the first pass's count");
        await Eventually(CountCompletionsAsync, expected: 2, because: "a reissue makes the holder answer again");
    }

    [Fact]
    public async Task The_same_request_message_delivered_twice_is_erased_and_reported_once()
    {
        Guid request = Guid.CreateVersion7();
        PersonalDataDeleteRequested message = Requested(request, Guid.CreateVersion7());
        await fixture.OrderAsync(Guid.CreateVersion7(), Subject);

        // The filter counts a drop before it returns, so waiting on the instrument proves the drop happened (§9.5).
        using SemaphoreSlim suppressed = new(0);
        using MeterListener listener = new();

        listener.InstrumentPublished = (instrument, active) =>
        {
            if (instrument.Meter.Name == "Commerce.Messaging" &&
                instrument.Name == "messaging.inbox.suppressed")
            {
                active.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            if (TagValue(tags, "message") == nameof(PersonalDataDeleteRequested) &&
                TagValue(tags, "endpoint") == MessagingRegistration.PrivacyQueue)
            {
                suppressed.Release();
            }
        });

        listener.Start();

        await PublishRequestAsync(request, message);
        await PublishRequestAsync(request, message);

        (await suppressed.WaitAsync(DeliveryBudget, TestContext.Current.CancellationToken))
            .ShouldBeTrue("the redelivery has to be counted as suppressed before the counts below are settled");

        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM notifications.PersonalDataErasures WHERE RequestId = {0}",
            request)).ShouldBe(1);
        await Eventually(CountCompletionsAsync, expected: 1, because: "the inbox dropped the second delivery");
    }

    private async Task<Notification> EndedAsync(Guid order, Guid? customer, string status)
    {
        Notification notice = await fixture.PendingAsync(TemplateKeys.OrderConfirmed, order);
        await fixture.ExecuteAsync(
            "UPDATE notifications.NotificationLog SET Status = {1}, Languages = 'en', " +
            "CompletedAt = SYSDATETIMEOFFSET() WHERE NotificationId = {0}",
            notice.NotificationId,
            status);

        if (customer is not null)
        {
            await fixture.ExecuteAsync(
                "UPDATE notifications.NotificationLog SET CustomerId = {1} WHERE NotificationId = {0}",
                notice.NotificationId,
                customer.Value);
        }

        return notice;
    }

    private static PersonalDataDeleteRequested Requested(Guid request, Guid messageId) => new()
    {
        MessageId = messageId,
        CorrelationId = request,
        OccurredAt = DateTimeOffset.UtcNow,
        RequestId = request,
        SubjectId = Subject
    };

    private Task PublishRequestAsync(Guid request, PersonalDataDeleteRequested? sent = null) =>
        fixture.DeliverAsync(sent ?? Requested(request, Guid.CreateVersion7()), MessagingRegistration.PrivacyQueue);

    private Task<int> NoticeCountAsync(Guid notification) =>
        fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM notifications.NotificationLog WHERE NotificationId = {0}",
            notification);

    /// <summary>What reached Privacy's queue since this test began; nobody drains it and a reset keeps it.</summary>
    private async Task<int> CountCompletionsAsync() => await fixture.QueueDepthAsync(PrivacyQueue) - _baseline;

    private static async Task Eventually(Func<Task<int>> read, int expected, string because)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + DeliveryBudget;
        int actual = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            actual = await read();

            if (actual == expected)
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }

        actual.ShouldBe(expected, because);
    }

    /// <summary>One tag off a measurement, read inside the callback since a span cannot be captured.</summary>
    private static string TagValue(ReadOnlySpan<KeyValuePair<string, object?>> tags, string name)
    {
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (tag.Key == name)
                return tag.Value?.ToString() ?? string.Empty;
        }

        return string.Empty;
    }
}
