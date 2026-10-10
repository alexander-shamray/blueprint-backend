using Common.Contracts;
using Common.Contracts.Privacy.V1;
using Common.Domain;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Payments.TestSupport;
using Shouldly;
using Xunit;

namespace Payments.Api.Tests;

/// <summary>§11.7's erasure request over a real broker and database, since the harness replaces the endpoint.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class PaymentsErasureEndpointTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(30);

    private static readonly Guid Subject = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Bystander = new("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private const string PrivacyQueue = "privacy-completions";

    private int _baseline;

    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync();
        _baseline = await fixture.QueueDepthAsync(PrivacyQueue);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_subjects_orders_lose_their_payer_and_a_bystanders_do_not()
    {
        Guid request = Guid.CreateVersion7();
        await SeedOrderAsync(Guid.CreateVersion7(), Subject);
        await SeedOrderAsync(Guid.CreateVersion7(), Subject);
        await SeedOrderAsync(Guid.CreateVersion7(), Bystander);

        await PublishRequestAsync(request);

        (await CountPayerAsync(Subject)).ShouldBe(0);
        (await CountPayerAsync(Guid.Empty)).ShouldBe(2, "the money stays, under the id that names nobody");
        (await CountPayerAsync(Bystander)).ShouldBe(1);
    }

    [Fact]
    public async Task The_audit_row_holds_a_count_and_a_hash_and_never_the_subject()
    {
        Guid request = Guid.CreateVersion7();
        await SeedOrderAsync(Guid.CreateVersion7(), Subject);

        await PublishRequestAsync(request);

        (await fixture.ScalarAsync<int>(
            "SELECT Value = Count FROM payments.PersonalDataErasures WHERE RequestId = {0}",
            request)).ShouldBe(1);
        (await fixture.ScalarAsync<string>(
            "SELECT Value = SubjectHash FROM payments.PersonalDataErasures WHERE RequestId = {0}",
            request)).ShouldBe(PersonalDataErasure.HashSubject(request, Subject));
    }

    [Fact]
    public async Task The_completion_is_sent_to_privacys_queue_once_the_erasure_has_committed()
    {
        Guid request = Guid.CreateVersion7();
        await SeedOrderAsync(Guid.CreateVersion7(), Subject);

        await PublishRequestAsync(request);

        await Eventually(CountCompletionsAsync, expected: 1, because: "the send is released when the consumer succeeds");
    }

    [Fact]
    public async Task A_subject_with_nothing_here_still_gets_an_audit_row_and_a_completion()
    {
        Guid request = Guid.CreateVersion7();

        await PublishRequestAsync(request);

        (await fixture.ScalarAsync<int>(
            "SELECT Value = Count FROM payments.PersonalDataErasures WHERE RequestId = {0}",
            request)).ShouldBe(0);
        await Eventually(CountCompletionsAsync, expected: 1, because: "silence cannot be told from success (§11.7)");
    }

    [Fact]
    public async Task A_reissued_request_keeps_one_audit_row_and_reports_again()
    {
        Guid request = Guid.CreateVersion7();
        await SeedOrderAsync(Guid.CreateVersion7(), Subject);
        await PublishRequestAsync(request);

        await PublishRequestAsync(request);

        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM payments.PersonalDataErasures WHERE RequestId = {0}",
            request)).ShouldBe(1);
        (await fixture.ScalarAsync<int>(
            "SELECT Value = Count FROM payments.PersonalDataErasures WHERE RequestId = {0}",
            request)).ShouldBe(1, "the second pass found nothing and kept the first pass's count");
        await Eventually(CountCompletionsAsync, expected: 2, because: "a reissue makes the holder answer again");
    }

    private async Task SeedOrderAsync(Guid order, Guid customer) =>
        await fixture.ExecuteAsync(
            "INSERT INTO payments.PaymentOrders (OrderId, CustomerId, TotalAmount, Currency, PlacedAt) " +
            "VALUES ({0}, {1}, 42.10, 'EUR', SYSDATETIMEOFFSET())",
            order,
            customer);

    private async Task PublishRequestAsync(Guid request)
    {
        PersonalDataDeleteRequested message = new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = request,
            OccurredAt = DateTimeOffset.UtcNow,
            RequestId = request,
            SubjectId = Subject
        };

        await fixture.Factory.Services.GetRequiredService<IBus>().Publish(
            message,
            c =>
            {
                c.MessageId = message.MessageId;
                c.CorrelationId = message.CorrelationId;
            },
            TestContext.Current.CancellationToken);

        await Eventually(
            async () => (await fixture.InboxAsync(message.MessageId)).Count,
            expected: 1,
            because: "the inbox row is written after the erasure has committed (§9.5)");
    }

    private Task<int> CountPayerAsync(Guid customer) =>
        fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM payments.PaymentOrders WHERE CustomerId = {0}",
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
}
