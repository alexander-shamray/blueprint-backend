using System.Net;
using System.Text;
using Common.Contracts.Inventory.V1;
using Inventory.TestSupport;
using Shouldly;
using Xunit;

namespace Inventory.Api.Tests;

/// <summary>§8.5 end to end for the reinstatement: HTTP, the registered pipeline, real Redis and SQL Server.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ReinstateReservationIdempotencyTests(ServiceFixture fixture) : IAsyncLifetime
{
    // The key's middle segment, spelled out because a changed name orphans every live key (§8.5).
    private const string Operation = "inventory.reservation.reinstate";

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_reinstatement_repeated_under_one_command_id_answers_as_the_first_did_and_takes_stock_once()
    {
        (Guid product, Guid order) = await ReleasedAsync();
        int levels = await LevelsAsync();
        using HttpClient client = Admin(Guid.CreateVersion7());
        var commandId = Guid.CreateVersion7();

        HttpResponseMessage first = await ReservationTestSupport.ReinstateAsync(client, order, commandId);
        HttpResponseMessage second = await ReservationTestSupport.ReinstateAsync(client, order, commandId);

        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        second.StatusCode.ShouldBe(
            HttpStatusCode.NoContent,
            "the repeat is the first request again, so it is answered as the first was (§8.5)");
        (await StatusAsync(order)).ShouldBe("Reserved");
        (await Available(product)).ShouldBe(1, "two of the three were taken, once");
        (await LevelsAsync()).ShouldBe(levels + 1, "a replay runs no handler, so it moves no level");
    }

    [Fact]
    public async Task Two_reinstatements_at_once_under_one_command_id_apply_one()
    {
        (Guid product, Guid order) = await ReleasedAsync();
        int levels = await LevelsAsync();
        using HttpClient client = Admin(Guid.CreateVersion7());
        var commandId = Guid.CreateVersion7();

        HttpResponseMessage[] responses = await Task.WhenAll(
            ReservationTestSupport.ReinstateAsync(client, order, commandId),
            ReservationTestSupport.ReinstateAsync(client, order, commandId));

        responses.ShouldContain(r => r.StatusCode == HttpStatusCode.NoContent, "one of the two held the claim");
        foreach (HttpResponseMessage response in responses.Where(r => r.StatusCode != HttpStatusCode.NoContent))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict, "the other met the claim, never the handler");
            (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
                .ShouldContain("request.in_progress");
        }

        (await StatusAsync(order)).ShouldBe("Reserved");
        (await Available(product)).ShouldBe(1);
        (await LevelsAsync()).ShouldBe(levels + 1, "one handler ran, whichever answer the other was given");
    }

    [Fact]
    public async Task A_committed_reinstatement_leaves_its_marker_in_this_service_s_schema()
    {
        (_, Guid order) = await ReleasedAsync();
        var caller = Guid.CreateVersion7();
        var commandId = Guid.CreateVersion7();
        using HttpClient client = Admin(caller);

        HttpResponseMessage response = await ReservationTestSupport.ReinstateAsync(client, order, commandId);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await fixture.IdempotencyMarkerCountAsync(Key(caller, commandId))).ShouldBe(
            1,
            "§6.3 writes the marker in the reinstatement's own transaction, under the key the claim took");
    }

    [Fact]
    public async Task A_refused_reinstatement_stores_nothing_so_the_same_id_carries_the_next_attempt()
    {
        (Guid product, Guid order) = await ReleasedAsync(available: 2);
        await fixture.ExecuteAsync("UPDATE inventory.StockItems SET Available = 1 WHERE ProductId = {0}", product);
        var caller = Guid.CreateVersion7();
        var commandId = Guid.CreateVersion7();
        using HttpClient client = Admin(caller);

        HttpResponseMessage shortage = await ReservationTestSupport.ReinstateAsync(client, order, commandId);

        shortage.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await fixture.IdempotencyMarkerCountAsync(Key(caller, commandId))).ShouldBe(0);
        (await fixture.IdempotencyClaims.GetAsync(Key(caller, commandId), TestContext.Current.CancellationToken))
            .ShouldBeNull("a refusal releases its claim (§8.5)");

        await fixture.ExecuteAsync("UPDATE inventory.StockItems SET Available = 2 WHERE ProductId = {0}", product);
        HttpResponseMessage retried = await ReservationTestSupport.ReinstateAsync(client, order, commandId);

        retried.StatusCode.ShouldBe(HttpStatusCode.NoContent, "the id was never spent, so it is not replayed");
        (await StatusAsync(order)).ShouldBe("Reserved");
        (await Available(product)).ShouldBe(0);
    }

    [Fact]
    public async Task A_refusal_repeated_under_its_id_is_refused_again_rather_than_replayed_or_held()
    {
        using HttpClient client = Admin(Guid.CreateVersion7());
        var unknown = Guid.CreateVersion7();
        var missing = Guid.CreateVersion7();

        (await ReservationTestSupport.ReinstateAsync(client, unknown, missing))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReservationTestSupport.ReinstateAsync(client, unknown, missing))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound, "neither a replayed success nor a claim left held");

        var tombstoned = Guid.CreateVersion7();
        var refused = Guid.CreateVersion7();
        await client.PostAsync(
            $"/v1/inventory/reservations/{tombstoned}/release", null, TestContext.Current.CancellationToken);

        (await ReservationTestSupport.ReinstateAsync(client, tombstoned, refused))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ReservationTestSupport.ReinstateAsync(client, tombstoned, refused))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_command_id_names_one_act_so_its_repeat_after_a_later_release_retakes_nothing()
    {
        (Guid product, Guid order) = await ReleasedAsync();
        using HttpClient client = Admin(Guid.CreateVersion7());
        var commandId = Guid.CreateVersion7();
        (await ReservationTestSupport.ReinstateAsync(client, order, commandId))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsync(
                $"/v1/inventory/reservations/{order}/release", null, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        HttpResponseMessage repeat = await ReservationTestSupport.ReinstateAsync(client, order, commandId);

        repeat.StatusCode.ShouldBe(HttpStatusCode.NoContent, "the recorded answer, not a second reinstatement");
        (await StatusAsync(order)).ShouldBe("Released", "a new act takes a new id; this one was answered");
        (await Available(product)).ShouldBe(3);
    }

    [Fact]
    public async Task Another_admin_sending_the_same_command_id_is_judged_on_the_reservation_and_not_replayed()
    {
        (Guid product, Guid order) = await ReleasedAsync();
        var commandId = Guid.CreateVersion7();
        using HttpClient first = Admin(Guid.CreateVersion7());
        using HttpClient second = Admin(Guid.CreateVersion7());
        (await ReservationTestSupport.ReinstateAsync(first, order, commandId))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        HttpResponseMessage other = await ReservationTestSupport.ReinstateAsync(second, order, commandId);

        other.StatusCode.ShouldBe(
            HttpStatusCode.UnprocessableEntity,
            "the key's first segment is the caller, so one admin's id is no key of another's (§8.5)");
        (await other.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain("reservation.not_reinstatable");
        (await Available(product)).ShouldBe(1);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"commandId":"00000000-0000-0000-0000-000000000000"}""")]
    public async Task An_omitted_or_empty_command_id_is_400_naming_the_field_and_writes_nothing(string body)
    {
        (Guid product, Guid order) = await ReleasedAsync();
        var caller = Guid.CreateVersion7();
        using HttpClient client = Admin(caller);

        HttpResponseMessage response = await PostAsync(client, order, body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain("CommandId");
        (await StatusAsync(order)).ShouldBe("Released");
        (await Available(product)).ShouldBe(3);
        (await fixture.IdempotencyClaims.GetAsync(Key(caller, Guid.Empty), TestContext.Current.CancellationToken))
            .ShouldBeNull("validation runs before any claim (§6.3)");
    }

    /// <summary>§8.5's key as the behaviour builds it: subject, operation, command id.</summary>
    private static string Key(Guid caller, Guid commandId) => $"{caller}:{Operation}:{commandId}";

    /// <summary>A raw body, so a shape no client library would send still reaches the endpoint.</summary>
    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Guid order, string body) =>
        client.PostAsync(
            $"/v1/inventory/reservations/{order}/reinstate",
            new StringContent(body, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

    /// <summary>Two of <paramref name="available"/> reserved and released again, as the runbook finds them.</summary>
    private async Task<(Guid Product, Guid Order)> ReleasedAsync(int available = 3)
    {
        var product = Guid.CreateVersion7();
        var order = Guid.CreateVersion7();
        await ReservationTestSupport.SeedStock(fixture, product, available);
        await ReservationTestSupport.SendAsync(fixture, new ReserveStock(order, [new StockLine(product, 2)]));
        await ReservationTestSupport.EventuallyStatus(fixture, order, "Reserved");
        await ReservationTestSupport.SendAsync(fixture, new ReleaseStock(order));
        await ReservationTestSupport.EventuallyStatus(fixture, order, "Released");

        return (product, order);
    }

    private async Task<int> LevelsAsync() =>
        (await fixture.OutboxAsync())
            .Count(r => r.MessageType.Contains("StockLevelChanged", StringComparison.Ordinal));

    private Task<int> Available(Guid product) => ReservationTestSupport.Available(fixture, product);

    private Task<string> StatusAsync(Guid orderId) => ReservationTestSupport.StatusAsync(fixture, orderId);

    private HttpClient Admin(Guid caller) => ReservationTestSupport.Admin(fixture, caller);
}
