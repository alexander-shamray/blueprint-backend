using System.Diagnostics.Metrics;
using Common.Domain;
using Common.Contracts.Privacy.V1;
using Web.Bff.Persistence;
using Shouldly;
using Xunit;
using MessagingRegistration = Web.Bff.Messaging.DependencyInjection;

namespace Web.Bff.Tests;

/// <summary>§11.7's erasure request over a real broker and database, since the harness replaces the endpoint.</summary>
[Collection(nameof(BffIntegrationCollection))]
public sealed class BffErasureEndpointTests(BffServiceFixture fixture) : IAsyncLifetime
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
    public async Task The_subjects_orders_and_their_lines_go_and_a_bystanders_stay()
    {
        Guid subjectsOrder = Guid.CreateVersion7();
        Guid secondOrder = Guid.CreateVersion7();
        Guid bystandersOrder = Guid.CreateVersion7();
        await SeedOrderAsync(subjectsOrder, Subject);
        await SeedOrderAsync(secondOrder, Subject);
        await SeedOrderAsync(bystandersOrder, Bystander);

        await PublishRequestAsync(Guid.CreateVersion7());

        (await CountOrdersAsync(Subject)).ShouldBe(0);
        (await CountLinesAsync(subjectsOrder)).ShouldBe(0, "the lines go with their order, by the cascade");
        (await CountLinesAsync(secondOrder)).ShouldBe(0);
        (await CountOrdersAsync(Bystander)).ShouldBe(1);
        (await CountLinesAsync(bystandersOrder)).ShouldBe(1, "a bystander keeps their lines");
    }

    [Fact]
    public async Task An_order_no_event_has_named_a_customer_for_is_left_alone()
    {
        Guid unattributed = Guid.CreateVersion7();
        await fixture.ExecuteAsync(
            "INSERT INTO bff.Orders (OrderId, FirstSeenAt, AsOf) VALUES ({0}, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET())",
            unattributed);

        await PublishRequestAsync(Guid.CreateVersion7());

        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM bff.Orders WHERE OrderId = {0}",
            unattributed)).ShouldBe(1, "a row with no owner is not the subject's, and the gauge counts it (ADR-051)");
    }

    [Fact]
    public async Task The_audit_row_holds_a_count_and_a_hash_and_never_the_subject()
    {
        Guid request = Guid.CreateVersion7();
        await SeedOrderAsync(Guid.CreateVersion7(), Subject);
        await SeedOrderAsync(Guid.CreateVersion7(), Subject);

        await PublishRequestAsync(request);

        (await fixture.ScalarAsync<int>(
            "SELECT Value = Count FROM bff.PersonalDataErasures WHERE RequestId = {0}",
            request)).ShouldBe(2);
        (await fixture.ScalarAsync<string>(
            "SELECT Value = SubjectHash FROM bff.PersonalDataErasures WHERE RequestId = {0}",
            request)).ShouldBe(PersonalDataErasure.HashSubject(request, Subject));
    }

    [Fact]
    public async Task The_completion_is_sent_to_privacys_queue_once_the_erasure_has_committed()
    {
        await SeedOrderAsync(Guid.CreateVersion7(), Subject);

        await PublishRequestAsync(Guid.CreateVersion7());

        await Eventually(CountCompletionsAsync, expected: 1, because: "the send is released when the consumer succeeds");
    }

    [Fact]
    public async Task A_subject_with_nothing_here_still_gets_an_audit_row_and_a_completion()
    {
        Guid request = Guid.CreateVersion7();

        await PublishRequestAsync(request);

        (await fixture.ScalarAsync<int>(
            "SELECT Value = Count FROM bff.PersonalDataErasures WHERE RequestId = {0}",
            request)).ShouldBe(0);
        await Eventually(CountCompletionsAsync, expected: 1, because: "silence cannot be told from success (§11.7)");
    }

    [Fact]
    public async Task A_reissued_request_keeps_one_audit_row_and_reports_again()
    {
        Guid request = Guid.CreateVersion7();
        await SeedOrderAsync(Guid.CreateVersion7(), Subject);
        await PublishRequestAsync(request);

        await PublishRequestAsync(request, Requested(request, Guid.CreateVersion7()));

        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM bff.PersonalDataErasures WHERE RequestId = {0}",
            request)).ShouldBe(1);
        (await fixture.ScalarAsync<int>(
            "SELECT Value = Count FROM bff.PersonalDataErasures WHERE RequestId = {0}",
            request)).ShouldBe(1, "the second pass found nothing and kept the first pass's count");
        await Eventually(CountCompletionsAsync, expected: 2, because: "a reissue makes the holder answer again");
    }

    [Fact]
    public async Task The_same_request_message_delivered_twice_is_erased_and_reported_once()
    {
        Guid request = Guid.CreateVersion7();
        PersonalDataDeleteRequested message = Requested(request, Guid.CreateVersion7());
        await SeedOrderAsync(Guid.CreateVersion7(), Subject);

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
            "SELECT Value = COUNT(*) FROM bff.PersonalDataErasures WHERE RequestId = {0}",
            request)).ShouldBe(1);
        await Eventually(CountCompletionsAsync, expected: 1, because: "the inbox dropped the second delivery");
    }

    private async Task SeedOrderAsync(Guid order, Guid customer)
    {
        await fixture.ExecuteAsync(
            "INSERT INTO bff.Orders (OrderId, CustomerId, FirstSeenAt, AsOf) " +
            "VALUES ({0}, {1}, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET())",
            order,
            customer);
        await fixture.ExecuteAsync(
            "INSERT INTO bff.OrderLines (OrderId, LineNumber, ProductId, Quantity, UnitPrice) " +
            "VALUES ({0}, 1, {1}, 1, 9.99)",
            order,
            Guid.CreateVersion7());
    }

    private Task<int> CountOrdersAsync(Guid customer) =>
        fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM bff.Orders WHERE CustomerId = {0}", customer);

    private Task<int> CountLinesAsync(Guid order) =>
        fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM bff.OrderLines WHERE OrderId = {0}", order);

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
