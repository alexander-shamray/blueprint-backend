using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Common.Contracts.Privacy.V1;
using Common.Domain;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Privacy.Api;
using Privacy.Infrastructure.Messaging;
using Privacy.TestSupport;
using Shouldly;
using Xunit;

namespace Privacy.Api.Tests;

/// <summary>Every holder's answer over a real broker and database: counted, closing, flagged or dropped (ADR-092).</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ErasureCompletionsTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(30);

    private static readonly Guid Subject = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static readonly string[] Holders = ["ordering", "payments", "shipping", "notifications", "bff"];

    public ValueTask InitializeAsync() => new(fixture.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_five_holders_answering_close_the_request_and_the_id_gives_way_to_the_hash()
    {
        Guid request = await RaiseAsync();

        foreach (string holder in Holders)
            await CompleteAsync(request, holder, 2);

        (await fixture.ScalarAsync<string>(
            "SELECT Value = Status FROM privacy.ErasureRequests WHERE RequestId = {0}",
            request)).ShouldBe("Closed");
        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM privacy.ErasureRequests WHERE RequestId = {0} AND SubjectId IS NULL",
            request)).ShouldBe(1, "the closed row no longer holds the subject's id");
        (await fixture.ScalarAsync<string>(
            "SELECT Value = SubjectHash FROM privacy.ErasureRequests WHERE RequestId = {0}",
            request)).ShouldBe(PersonalDataErasure.HashSubject(request, Subject));
        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM privacy.ErasureCompletions WHERE RequestId = {0} AND Counted = 1",
            request)).ShouldBe(5);
    }

    [Fact]
    public async Task Answers_that_arrive_together_are_all_counted_and_the_request_still_closes()
    {
        Guid request = await RaiseAsync();

        await Task.WhenAll(Holders.Select(holder => CompleteAsync(request, holder, 1)));

        (await fixture.ScalarAsync<string>(
            "SELECT Value = Status FROM privacy.ErasureRequests WHERE RequestId = {0}",
            request)).ShouldBe("Closed", "the request's lock takes five simultaneous answers in turn");
    }

    [Fact]
    public async Task One_holder_silent_leaves_the_request_open_and_the_status_route_names_it()
    {
        Guid request = await RaiseAsync();

        foreach (string holder in Holders.Where(h => h != "shipping"))
            await CompleteAsync(request, holder, 1);

        HttpResponseMessage response = await GetAsync(request);
        using JsonDocument view = JsonDocument.Parse(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));

        view.RootElement.GetProperty("status").GetString().ShouldBe("Open");
        view.RootElement.GetProperty("missing").EnumerateArray().Select(m => m.GetString()).ShouldBe(["shipping"]);
        view.RootElement.GetProperty("answers").GetArrayLength().ShouldBe(4);
        view.RootElement.GetRawText().ShouldNotContain(Subject.ToString(), Case.Insensitive);
    }

    [Fact]
    public async Task A_name_outside_the_set_is_kept_and_flagged_and_does_not_stand_in_for_a_holder()
    {
        Guid request = await RaiseAsync();

        await CompleteAsync(request, "catalog", 7);

        (await fixture.ScalarAsync<int>(
            "SELECT Value = CAST(Counted AS int) FROM privacy.ErasureCompletions " +
            "WHERE RequestId = {0} AND Responder = 'catalog'",
            request)).ShouldBe(0);
        (await fixture.ScalarAsync<string>(
            "SELECT Value = Status FROM privacy.ErasureRequests WHERE RequestId = {0}",
            request)).ShouldBe("Open");
    }

    [Fact]
    public async Task The_same_message_delivered_twice_counts_once_and_a_reissued_answer_keeps_the_larger_count()
    {
        Guid request = await RaiseAsync();
        PersonalDataDeleteCompleted first = Completion(request, "ordering", 5);

        await SendAsync(first);
        await SendAsync(first, awaitInbox: false);
        await SendAsync(Completion(request, "ordering", 0));

        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM privacy.ErasureCompletions WHERE RequestId = {0}",
            request)).ShouldBe(1);
        (await fixture.ScalarAsync<int>(
            "SELECT Value = Count FROM privacy.ErasureCompletions WHERE RequestId = {0}",
            request)).ShouldBe(5, "a reissue that finds nothing left must not overwrite what the first pass removed");
    }

    [Fact]
    public async Task An_answer_for_a_request_nobody_raised_is_acked_and_creates_nothing()
    {
        PersonalDataDeleteCompleted stray = Completion(Guid.CreateVersion7(), "ordering", 1);

        await SendAsync(stray);

        (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM privacy.ErasureRequests")).ShouldBe(0);
        (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM privacy.ErasureCompletions")).ShouldBe(0);
    }

    [Fact]
    public async Task A_late_answer_after_the_request_closed_changes_nothing()
    {
        Guid request = await RaiseAsync();
        foreach (string holder in Holders)
            await CompleteAsync(request, holder, 1);

        await CompleteAsync(request, "ordering", 99);
        await CompleteAsync(request, "catalog", 1);

        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM privacy.ErasureCompletions WHERE RequestId = {0}",
            request)).ShouldBe(5);
        (await fixture.ScalarAsync<int>(
            "SELECT Value = MAX(Count) FROM privacy.ErasureCompletions WHERE RequestId = {0}",
            request)).ShouldBe(1);
    }

    private async Task<Guid> RaiseAsync()
    {
        using HttpClient client = fixture.Factory.CreateClient();
        using HttpRequestMessage message = new(HttpMethod.Post, "/v1/privacy/erasure-requests")
        {
            Content = JsonContent.Create(new { subjectId = Subject })
        };
        Authenticate(message);

        HttpResponseMessage response = await client.SendAsync(message, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await response.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> GetAsync(Guid request)
    {
        using HttpClient client = fixture.Factory.CreateClient();
        using HttpRequestMessage message = new(HttpMethod.Get, $"/v1/privacy/erasure-requests/{request}");
        Authenticate(message);

        return await client.SendAsync(message, TestContext.Current.CancellationToken);
    }

    private static void Authenticate(HttpRequestMessage message)
    {
        message.Headers.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        message.Headers.Add(TestAuthHandler.PermissionsHeader, PrivacyPermissions.Erase);
    }

    private static PersonalDataDeleteCompleted Completion(Guid request, string responder, int count) =>
        new(request, responder, count);

    private Task CompleteAsync(Guid request, string responder, int count) =>
        SendAsync(Completion(request, responder, count));

    /// <summary>Sends to the queue as a holder does, then waits for the inbox row, written after the commit (§9.5).</summary>
    private async Task SendAsync(PersonalDataDeleteCompleted message, bool awaitInbox = true)
    {
        using CancellationTokenSource bounded =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bounded.CancelAfter(DeliveryBudget);

        // MessageId is the message's own, kept apart from the contract: a command carries no MessageId field.
        Guid messageId = Identity(message);

        ISendEndpoint endpoint = await fixture.Factory.Services
            .GetRequiredService<IBus>()
            .GetSendEndpoint(new Uri($"queue:{DependencyInjection.CompletionsQueue}"));

        await endpoint.Send(message, c => c.MessageId = messageId, bounded.Token);

        if (!awaitInbox)
            return;

        DateTimeOffset deadline = DateTimeOffset.UtcNow + DeliveryBudget;
        while ((await fixture.InboxAsync(messageId)).Count == 0)
        {
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"No inbox row for {messageId} within {DeliveryBudget}.");

            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        }
    }

    // One id per distinct message, so a redelivery of the same record is the same message and a new record is not.
    private readonly Dictionary<PersonalDataDeleteCompleted, Guid> _identities = [];

    private Guid Identity(PersonalDataDeleteCompleted message)
    {
        lock (_identities)
        {
            if (!_identities.TryGetValue(message, out Guid id))
                _identities[message] = id = Guid.CreateVersion7();

            return id;
        }
    }
}
