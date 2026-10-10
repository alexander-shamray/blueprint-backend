using System.Diagnostics.Metrics;
using Common.Contracts;
using Common.Contracts.Privacy.V1;
using Common.Domain;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Shipping.TestSupport;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>§11.7's erasure request over a real broker and database, since the harness replaces the endpoint.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ShippingErasureEndpointTests(ServiceFixture fixture) : IAsyncLifetime
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
    public async Task The_subjects_addresses_are_deleted_and_a_bystanders_are_not()
    {
        await SeedAddressAsync(Guid.CreateVersion7(), Subject);
        await SeedAddressAsync(Guid.CreateVersion7(), Subject);
        await SeedAddressAsync(Guid.CreateVersion7(), Bystander);

        await PublishRequestAsync(Guid.CreateVersion7());

        (await CountAddressesAsync(Subject)).ShouldBe(0);
        (await CountAddressesAsync(Bystander)).ShouldBe(1);
        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM shipping.DeliveryAddresses WHERE CustomerId = {0} " +
            "AND Line1 = '1 Rue Exemple' AND Line2 = 'Apt 2' AND City = 'Paris' AND PostalCode = '75001' " +
            "AND Country = 'FR'",
            Bystander)).ShouldBe(1, "a bystander keeps their whole address");
    }

    [Fact]
    public async Task The_audit_row_holds_a_count_and_a_hash_and_never_the_subject()
    {
        Guid request = Guid.CreateVersion7();
        await SeedAddressAsync(Guid.CreateVersion7(), Subject);

        await PublishRequestAsync(request);

        (await fixture.ScalarAsync<int>(
            "SELECT Value = Count FROM shipping.PersonalDataErasures WHERE RequestId = {0}",
            request)).ShouldBe(1);
        (await fixture.ScalarAsync<string>(
            "SELECT Value = SubjectHash FROM shipping.PersonalDataErasures WHERE RequestId = {0}",
            request)).ShouldBe(PersonalDataErasure.HashSubject(request, Subject));
    }

    [Fact]
    public async Task The_completion_is_sent_to_privacys_queue_once_the_erasure_has_committed()
    {
        await SeedAddressAsync(Guid.CreateVersion7(), Subject);

        await PublishRequestAsync(Guid.CreateVersion7());

        await Eventually(CountCompletionsAsync, expected: 1, because: "the send is released when the consumer succeeds");
    }

    [Fact]
    public async Task A_subject_with_nothing_here_still_gets_an_audit_row_and_a_completion()
    {
        Guid request = Guid.CreateVersion7();

        await PublishRequestAsync(request);

        (await fixture.ScalarAsync<int>(
            "SELECT Value = Count FROM shipping.PersonalDataErasures WHERE RequestId = {0}",
            request)).ShouldBe(0);
        await Eventually(CountCompletionsAsync, expected: 1, because: "silence cannot be told from success (§11.7)");
    }

    [Fact]
    public async Task A_reissued_request_keeps_one_audit_row_and_reports_again()
    {
        Guid request = Guid.CreateVersion7();
        await SeedAddressAsync(Guid.CreateVersion7(), Subject);
        await PublishRequestAsync(request);

        await PublishRequestAsync(request);

        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM shipping.PersonalDataErasures WHERE RequestId = {0}",
            request)).ShouldBe(1);
        (await fixture.ScalarAsync<int>(
            "SELECT Value = Count FROM shipping.PersonalDataErasures WHERE RequestId = {0}",
            request)).ShouldBe(1, "the second pass found nothing and kept the first pass's count");
        await Eventually(CountCompletionsAsync, expected: 2, because: "a reissue makes the holder answer again");
    }

    [Fact]
    public async Task The_same_request_message_delivered_twice_is_erased_and_reported_once()
    {
        Guid request = Guid.CreateVersion7();
        PersonalDataDeleteRequested message = Requested(request);
        await SeedAddressAsync(Guid.CreateVersion7(), Subject);

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
                TagValue(tags, "endpoint") == Shipping.Infrastructure.Messaging.DependencyInjection.PrivacyQueue)
            {
                suppressed.Release();
            }
        });

        listener.Start();

        await PublishRequestAsync(request, message);
        await PublishRequestAsync(request, message, drain: false);

        (await suppressed.WaitAsync(DeliveryBudget, TestContext.Current.CancellationToken))
            .ShouldBeTrue("the redelivery has to be counted as suppressed before the counts below are settled");

        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM shipping.PersonalDataErasures WHERE RequestId = {0}",
            request)).ShouldBe(1);
        await Eventually(CountCompletionsAsync, expected: 1, because: "the inbox dropped the second delivery");
    }

    private async Task SeedAddressAsync(Guid order, Guid customer) =>
        await fixture.ExecuteAsync(
            "INSERT INTO shipping.DeliveryAddresses (OrderId, CustomerId, Line1, Line2, City, PostalCode, Country, " +
            "FetchedAt) VALUES ({0}, {1}, '1 Rue Exemple', 'Apt 2', 'Paris', '75001', 'FR', SYSDATETIMEOFFSET())",
            order,
            customer);

    private static PersonalDataDeleteRequested Requested(Guid request) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = request,
        OccurredAt = DateTimeOffset.UtcNow,
        RequestId = request,
        SubjectId = Subject
    };

    private async Task PublishRequestAsync(Guid request, PersonalDataDeleteRequested? sent = null, bool drain = true)
    {
        PersonalDataDeleteRequested message = sent ?? Requested(request);

        await fixture.Factory.Services.GetRequiredService<IBus>().Publish(
            message,
            c =>
            {
                c.MessageId = message.MessageId;
                c.CorrelationId = message.CorrelationId;
            },
            TestContext.Current.CancellationToken);

        if (drain)
        {
            await Eventually(
                async () => (await fixture.InboxAsync(message.MessageId)).Count,
                expected: 1,
                because: "the inbox row is written after the erasure has committed (§9.5)");
        }
    }

    private Task<int> CountAddressesAsync(Guid customer) =>
        fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM shipping.DeliveryAddresses WHERE CustomerId = {0}",
            customer);

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
