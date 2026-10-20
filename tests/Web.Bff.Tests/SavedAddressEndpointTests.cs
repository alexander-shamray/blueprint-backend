using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Web.Bff.Addresses;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>E.6.12's three routes through the real host: the subject from the principal, the 404, the rules.</summary>
[Collection(nameof(BffIntegrationCollection))]
public sealed class SavedAddressEndpointTests(BffServiceFixture fixture) : IAsyncLifetime
{
    private const string Route = "/v1/saved-address";

    private static readonly SavedAddress Home = new("1 Test Street", "Flat 2", "Testville", "T1 2AB", "KZ");

    private readonly Guid _buyer = Guid.CreateVersion7();

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient As(Guid subject)
    {
        HttpClient client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, subject.ToString());

        return client;
    }

    [Fact]
    public async Task An_anonymous_caller_is_challenged_on_every_route()
    {
        using HttpClient anonymous = fixture.Factory.CreateClient();

        (await anonymous.GetAsync(Route, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PutAsJsonAsync(Route, Home, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.DeleteAsync(Route, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_caller_with_none_saved_is_answered_404_with_the_code_a_client_switches_on()
    {
        using HttpClient client = As(_buyer);

        HttpResponseMessage response = await client.GetAsync(Route, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        body.RootElement.GetProperty("code").GetString().ShouldBe("address.not_found");
    }

    [Fact]
    public async Task A_saved_address_is_read_back_as_it_was_sent()
    {
        using HttpClient client = As(_buyer);

        HttpResponseMessage put = await client.PutAsJsonAsync(Route, Home, Ct);
        SavedAddress? read = await client.GetFromJsonAsync<SavedAddress>(Route, Ct);

        put.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await put.Content.ReadFromJsonAsync<SavedAddress>(Ct)).ShouldBe(Home);
        read.ShouldBe(Home);
    }

    [Fact]
    public async Task A_second_save_replaces_the_first_and_leaves_one_row()
    {
        using HttpClient client = As(_buyer);
        SavedAddress work = new("9 Office Road", null, "Worktown", "W9", "GB");

        await client.PutAsJsonAsync(Route, Home, Ct);
        await client.PutAsJsonAsync(Route, work, Ct);

        (await client.GetFromJsonAsync<SavedAddress>(Route, Ct)).ShouldBe(work);
        (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM bff.SavedAddresses")).ShouldBe(1);
    }

    [Fact]
    public async Task The_address_is_stored_as_an_order_stores_one()
    {
        using HttpClient client = As(_buyer);

        await client.PutAsJsonAsync(Route, new SavedAddress("  1 Test Street ", "   ", " Testville", "T1 ", "kz"), Ct);

        (await client.GetFromJsonAsync<SavedAddress>(Route, Ct))
            .ShouldBe(new SavedAddress("1 Test Street", null, "Testville", "T1", "KZ"));
    }

    [Fact]
    public async Task One_buyer_s_address_is_not_another_s()
    {
        using HttpClient mine = As(_buyer);
        using HttpClient stranger = As(Guid.CreateVersion7());
        await mine.PutAsJsonAsync(Route, Home, Ct);

        (await stranger.GetAsync(Route, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await stranger.DeleteAsync(Route, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await mine.GetFromJsonAsync<SavedAddress>(Route, Ct))
            .ShouldBe(Home, "the stranger's delete touched their own");
    }

    [Fact]
    public async Task A_delete_removes_the_address_and_a_repeat_is_the_same_answer()
    {
        using HttpClient client = As(_buyer);
        await client.PutAsJsonAsync(Route, Home, Ct);

        HttpResponseMessage first = await client.DeleteAsync(Route, Ct);
        HttpResponseMessage again = await client.DeleteAsync(Route, Ct);

        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        again.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync(Route, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("", "Testville", "T1", "KZ")]
    [InlineData("1 Test Street", " ", "T1", "KZ")]
    [InlineData("1 Test Street", "Testville", "", "KZ")]
    [InlineData("1 Test Street", "Testville", "T1", "")]
    [InlineData("1 Test Street", "Testville", "T1", "KAZ")]
    [InlineData("1 Test Street", "Testville", "T1", "K1")]
    [InlineData("1 Test Street", "Testville", "T1", "K")]
    public async Task An_address_missing_a_part_or_with_a_bad_country_shape_is_refused_and_nothing_is_saved(
        string line1,
        string city,
        string postalCode,
        string country)
    {
        using HttpClient client = As(_buyer);

        HttpResponseMessage response =
            await client.PutAsJsonAsync(Route, new SavedAddress(line1, null, city, postalCode, country), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM bff.SavedAddresses")).ShouldBe(0);
    }

    [Fact]
    public async Task A_part_past_the_order_s_width_is_refused()
    {
        using HttpClient client = As(_buyer);

        HttpResponseMessage response = await client.PutAsJsonAsync(
            Route,
            Home with { PostalCode = new string('9', 21) },
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task No_postcode_format_is_enforced_because_none_is_one_country_s_alone()
    {
        using HttpClient client = As(_buyer);

        HttpResponseMessage response = await client.PutAsJsonAsync(
            Route,
            Home with { PostalCode = "not a postcode", Country = "ZZ" },
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_refusal_names_the_field_and_never_repeats_the_address()
    {
        using HttpClient client = As(_buyer);
        const string Marker = "Unmistakable-Marker-Street-42";

        HttpResponseMessage response =
            await client.PutAsJsonAsync(Route, new SavedAddress(Marker, null, "Testville", "T1", "KAZ"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain(Marker);
    }

    [Fact]
    public async Task The_wire_carries_the_order_request_s_own_member_names()
    {
        using HttpClient client = As(_buyer);
        await client.PutAsJsonAsync(Route, Home, Ct);

        using JsonDocument body = JsonDocument.Parse(await client.GetStringAsync(Route, Ct));

        body.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray()
            .ShouldBe(["city", "country", "line1", "line2", "postalCode"]);
    }
}
