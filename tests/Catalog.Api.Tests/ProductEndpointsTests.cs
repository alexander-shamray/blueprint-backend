using System.Net;
using System.Net.Http.Json;
using Catalog.TestSupport;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary>§12.4's third level: status codes, serialisation and authorization, over HTTP.</summary>
/// <remarks>Every write states the narrowest principal that works, as §12.4 requires of a fixture.</remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class ProductEndpointsTests(ServiceFixture fixture) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _client = fixture.Factory.CreateClient();
        await fixture.ResetAsync();
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed record PageDto(List<ItemDto> Items, string? NextCursor);

    private sealed record ItemDto(
        Guid ProductId,
        string Name,
        string? ThumbnailUrl,
        decimal Amount,
        string Currency,
        DateTimeOffset PublishedAt,
        int? QuantityAvailable);

    /// <summary>A fresh <c>CommandId</c> per call, since a reused one would replay the first publish (§8.5).</summary>
    private Task<HttpResponseMessage> PublishAsync(string name, decimal amount = 10m) =>
        PostAsync(
            new
            {
                CommandId = Guid.CreateVersion7(),
                Name = name,
                ThumbnailUrl = (string?)null,
                Amount = amount,
                Currency = "EUR"
            },
            CatalogPermissions.Write);

    /// <summary>A publish as a caller holding <paramref name="permissions"/>, or as no principal when null.</summary>
    private Task<HttpResponseMessage> PostAsync(object body, string? permissions)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/v1/catalog/products")
        {
            Content = JsonContent.Create(body)
        };

        if (permissions is not null)
        {
            request.Headers.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
            request.Headers.Add(TestAuthHandler.PermissionsHeader, permissions);
        }

        return _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_same_command_id_from_the_same_caller_replays_instead_of_publishing_twice()
    {
        // §8.5 end to end, from HTTP through the registered pipeline and a real Redis to SQL. The caller is pinned
        // because the key is subject:operation:commandId, and PostAsync mints a fresh caller per call.
        var caller = Guid.CreateVersion7();
        var commandId = Guid.CreateVersion7();
        object body = new
        {
            CommandId = commandId,
            Name = "Walnut desk",
            ThumbnailUrl = (string?)null,
            Amount = 10m,
            Currency = "EUR"
        };

        HttpResponseMessage first = await PostAsAsync(body, caller);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        HttpResponseMessage second = await PostAsAsync(body, caller);

        // The status alone proves little (§10.5), so the id below separates a replay from a second run.
        second.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            "a replay returns the first attempt's outcome, not a fresh decision");

        Guid firstId = await first.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken);
        Guid secondId = await second.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken);

        secondId.ShouldBe(
            firstId,
            "the replayed payload is the first attempt's ProductId — a second id would mean the " +
            "command ran again and the response merely looked the same");

        // The half no status code carries: two runs would leave two rows.
        int rows = await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM catalog.Products");
        rows.ShouldBe(1, "the claim is what stops the second attempt reaching the handler (§8.5)");
    }

    /// <summary><see cref="PostAsync"/> with the caller pinned, for a test whose subject is the key.</summary>
    private Task<HttpResponseMessage> PostAsAsync(object body, Guid caller)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/v1/catalog/products")
        {
            Content = JsonContent.Create(body)
        };

        request.Headers.Add(TestAuthHandler.UserHeader, caller.ToString());
        request.Headers.Add(TestAuthHandler.PermissionsHeader, CatalogPermissions.Write);

        return _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Publishing_without_a_token_is_a_401()
    {
        // No X-Test-User header, so the challenge stands. This catches the policy being dropped from the endpoint,
        // not UseAuthentication being dropped, since WebApplication adds that middleware itself (§4.2).
        HttpResponseMessage response = await PostAsync(
            new { Name = "Walnut desk", ThumbnailUrl = (string?)null, Amount = 10m, Currency = "EUR" },
            permissions: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // In the platform's one error shape (§10.5), which a bodiless challenge gets from UseStatusCodePages.
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        int rows = await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM catalog.Products");
        rows.ShouldBe(0, "a refused request must not reach the handler");
    }

    [Fact]
    public async Task Publishing_with_the_wrong_permission_is_a_403()
    {
        // A real caller with the wrong grant, the case a fixture that grants everything hides.
        HttpResponseMessage response = await PostAsync(
            new { Name = "Walnut desk", ThumbnailUrl = (string?)null, Amount = 10m, Currency = "EUR" },
            permissions: "catalog:read");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        int rows = await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM catalog.Products");
        rows.ShouldBe(0, "a refused request must not reach the handler");

        // Which scheme forbids, which a bare 403 cannot show: DefaultForbidScheme is unset and falls back to the
        // challenge scheme, so the test scheme answers.
        IAuthenticationSchemeProvider schemes =
            fixture.Factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        AuthenticationScheme? forbid = await schemes.GetDefaultForbidSchemeAsync();

        forbid?.Name.ShouldBe(TestAuthHandler.SchemeName);
    }

    [Fact]
    public async Task The_listing_is_reachable_without_a_token()
    {
        // §10.2's catalog-public route names `anonymous`, and only a request without a token shows the group's
        // policy does not reach this endpoint.
        HttpResponseMessage response = await _client.GetAsync(
            "/v1/catalog/products",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Publishing_a_product_returns_200_with_the_new_id()
    {
        // 200 with a Guid body is also the overload pin §10.5 warns about: a
        // Result<T> that resolved to the void ToHttpResult would 204 the id
        // away, and only this boundary can see that.
        HttpResponseMessage response = await PublishAsync("Walnut desk");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Guid id = await response.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken);
        id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Publishing_without_an_amount_is_a_400_not_a_free_product()
    {
        // An omitted amount is the validator's 400 rather than a free product; authorised, so the 400 is not a 401.
        HttpResponseMessage response = await PostAsync(
            new { Name = "Walnut desk", Currency = "EUR" },
            CatalogPermissions.Write);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("Amount");

        int rows = await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM catalog.Products");
        rows.ShouldBe(0);
    }

    [Fact]
    public async Task Publishing_an_invalid_product_returns_400_with_field_keyed_errors()
    {
        // The ValidationBehavior path over the wire (§6.3 → §10.5): thrown
        // ValidationException, translated to problem+json with an errors
        // dictionary keyed by field — no handler ran, no row exists.
        HttpResponseMessage response = await PublishAsync("", amount: -1m);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("Name");
        body.ShouldContain("Amount");
    }

    [Fact]
    public async Task The_published_product_comes_back_through_the_listing()
    {
        // The whole slice, wire to wire: POST persists through the real
        // pipeline and transaction behaviour, GET reads it back over Dapper.
        HttpResponseMessage published = await PublishAsync("Walnut desk", 19.99m);
        Guid id = await published.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken);

        PageDto? page = await _client.GetFromJsonAsync<PageDto>(
            "/v1/catalog/products",
            TestContext.Current.CancellationToken);

        page.ShouldNotBeNull();
        ItemDto item = page.Items.ShouldHaveSingleItem();
        item.ProductId.ShouldBe(id);
        item.Name.ShouldBe("Walnut desk");
        item.Amount.ShouldBe(19.99m);
        item.Currency.ShouldBe("EUR");
        page.NextCursor.ShouldBeNull("one row is one page");
    }

    [Fact]
    public async Task An_unreported_product_lists_a_null_level_rather_than_omitting_the_member()
    {
        // null and 0 are different facts to a screen: Inventory has said
        // nothing about this product, and the member says so rather than
        // vanishing and leaving the reader to guess between the two.
        await PublishAsync("Walnut desk", 19.99m);

        string body = await _client.GetStringAsync("/v1/catalog/products", TestContext.Current.CancellationToken);

        body.ShouldContain("\"quantityAvailable\":null");
    }

    [Fact]
    public async Task The_listing_pages_forward_with_the_returned_cursor()
    {
        // Three POSTs can share a clock tick, and the id tiebreak is not publish order, so only paging is asserted.
        List<Guid> published = [];
        string[] names = ["First", "Second", "Third"];
        foreach (string name in names)
        {
            HttpResponseMessage response = await PublishAsync(name);
            response.EnsureSuccessStatusCode();
            published.Add(await response.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken));
        }

        PageDto? first = await _client.GetFromJsonAsync<PageDto>(
            "/v1/catalog/products?limit=2",
            TestContext.Current.CancellationToken);

        first.ShouldNotBeNull();
        first.Items.Count.ShouldBe(2);
        first.NextCursor.ShouldNotBeNull();

        PageDto? second = await _client.GetFromJsonAsync<PageDto>(
            $"/v1/catalog/products?limit=2&cursor={Uri.EscapeDataString(first.NextCursor)}",
            TestContext.Current.CancellationToken);

        second.ShouldNotBeNull();
        second.Items.ShouldHaveSingleItem();
        second.NextCursor.ShouldBeNull();

        // No overlap, nothing skipped (ADR-016's whole point).
        Guid[] seen = [.. first.Items.Concat(second.Items).Select(i => i.ProductId)];
        seen.ShouldBeUnique();
        seen.ShouldBe(published, ignoreOrder: true);
    }
}
