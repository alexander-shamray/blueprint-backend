using System.Net;
using System.Net.Http.Json;
using Catalog.TestSupport;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary>
/// §12.4's third level: HTTP in, HTTP out, covering what the levels below
/// structurally cannot — status codes, serialisation and authorization.
/// </summary>
/// <remarks>Every write states the narrowest principal that works: §12.4's
/// rule is that a fixture handing out a blanket claim set makes the §11.4
/// policies untestable and, worse, makes them look tested — the endpoints are
/// reached, the assertions pass, and the one behaviour nobody exercises is
/// the refusal. So a refusal is asserted both with nothing granted and with
/// the wrong thing granted.</remarks>
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

    /// <summary>
    /// A fresh <c>CommandId</c> per call, and it is load-bearing rather than
    /// incidental since §8.5's behaviour took the fourth pipeline seat: several
    /// tests here publish twice, and one reused value would have the second
    /// replay the first's id instead of running.
    /// </summary>
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

    /// <summary>
    /// A publish request as a caller holding <paramref name="permissions"/> —
    /// a space-separated grant, or null for no principal at all. Explicit at
    /// every call site rather than defaulted into the client's headers: a
    /// default grant is how a suite ends up proving the policy is applied by
    /// never once arriving without it.
    /// </summary>
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
        // §8.5 end to end, and it is the only test in the solution that crosses
        // the whole stack: HTTP → the registered pipeline → RedisIdempotencyStore
        // on a real container → SQL. Everything else covers a piece. The
        // behaviour's own suite replays from an in-memory double, and the store's
        // suite reads payloads back through the store — neither can see the DI
        // wiring, the serialisation of THIS command's result, the interaction
        // with §6.3's transaction, or the reconstruction of a Result<T> from
        // what was stored. All four are what a duplicate POST actually meets.
        //
        // **The subject is pinned, and that is not incidental.** The key is
        // subject:operation:commandId (§8.5), and PostAsync mints a fresh
        // X-Test-User per call — so a test that reused only the CommandId would
        // claim two different keys, publish twice, and pass every assertion
        // below by never reaching the replay path at all.
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

        // The stored outcome, not a fresh one. Result<Guid> maps to 200 with
        // the value as the body (§10.5), so the status alone proves little —
        // the identity below is what separates a replay from a second run.
        second.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            "a replay returns the first attempt's outcome, not a fresh decision");

        Guid firstId = await first.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken);
        Guid secondId = await second.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken);

        secondId.ShouldBe(
            firstId,
            "the replayed payload is the first attempt's ProductId — a second id would mean the " +
            "command ran again and the response merely looked the same");

        // The half no status code can carry, and the one §8.5 exists for. Two
        // runs produce two rows under two ids, and the client sees a 200 both
        // times either way.
        int rows = await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM catalog.Products");
        rows.ShouldBe(1, "the claim is what stops the second attempt reaching the handler (§8.5)");
    }

    /// <summary>
    /// <see cref="PostAsync"/> with the caller pinned, for the one test whose
    /// subject is the key rather than the endpoint.
    /// </summary>
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
        // No X-Test-User header, so TestAuthHandler returns NoResult and the
        // challenge stands.
        //
        // §12.4 calls this "the test that catches UseAuthentication being
        // dropped from the pipeline". It is not, and the claim was removed from
        // the chapter rather than restated here: commenting that line out
        // leaves every test in this class green, because WebApplication adds
        // the authentication middleware itself whenever the services are
        // registered. The explicit call moves it earlier; it is not what puts
        // it there, so deleting it changes the pipeline's order and nothing a
        // status code can see. AuthenticationMiddlewareTests carries that
        // whole argument and the regression guard under it.
        //
        // (PolicyEvaluator is not the reason, though it is the plausible one:
        // for a policy naming no schemes it succeeds with the existing
        // HttpContext.User rather than authenticating anything itself. This
        // comment said otherwise until a review checked it.)
        //
        // What this one does catch is the policy being dropped from the
        // endpoint, which is the commoner edit and the one a reviewer skims
        // past.
        HttpResponseMessage response = await PostAsync(
            new { Name = "Walnut desk", ThumbnailUrl = (string?)null, Amount = 10m, Currency = "EUR" },
            permissions: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // And in the platform's one error shape (§10.5). A challenge is written
        // by the middleware before any endpoint runs and carries no body of its
        // own, so without UseStatusCodePages the promise §10.5 opens with has a
        // hole in it on the status a client meets first.
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        int rows = await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM catalog.Products");
        rows.ShouldBe(0, "a refused request must not reach the handler");
    }

    [Fact]
    public async Task Publishing_with_the_wrong_permission_is_a_403()
    {
        // Authenticated, and carrying a permission that is not the one this
        // endpoint requires — the case a fixture that grants everything hides.
        // catalog:read is deliberately a permission no policy in this service
        // registers (CatalogPermissions has one entry): the caller is real, the
        // grant is real, and it is simply not this grant.
        HttpResponseMessage response = await PostAsync(
            new { Name = "Walnut desk", ThumbnailUrl = (string?)null, Amount = 10m, Currency = "EUR" },
            permissions: "catalog:read");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        int rows = await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM catalog.Products");
        rows.ShouldBe(0, "a refused request must not reach the handler");

        // Which handler answered it, because CatalogApiFactory makes a claim
        // about that and the status code cannot tell them apart — a bare 403 is
        // what the bearer handler and the test one both produce.
        // DefaultForbidScheme is unset and the provider falls back to
        // DefaultChallengeScheme before DefaultScheme, so the test scheme
        // answers, which is why this needs no reachable authority.
        IAuthenticationSchemeProvider schemes =
            fixture.Factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        AuthenticationScheme? forbid = await schemes.GetDefaultForbidSchemeAsync();

        forbid?.Name.ShouldBe(TestAuthHandler.SchemeName);
    }

    [Fact]
    public async Task The_listing_is_reachable_without_a_token()
    {
        // §10.2's catalog-public route is GET-only and names `anonymous`, so
        // the group's RequireAuthorization must not reach this endpoint. Over
        // the wire is the only place that is visible: AllowAnonymous is
        // metadata, and metadata that fails to suppress the group's policy
        // looks identical to metadata that succeeds until a request without a
        // token arrives.
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
        // A bare decimal cannot say "absent": an omitted amount would bind as
        // 0 and publish a free product indistinguishable from a deliberate
        // one. The command's nullable Amount plus the validator's NotNull
        // turn the omission into the field-keyed 400 every other bad field
        // gets — and only this boundary can see the omission at all.
        // Authorised, so the 400 is the validator's answer and not the
        // pipeline's: an unauthenticated request would 401 here and read as
        // this assertion passing on the wrong grounds.
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
        // Three in-process POSTs can share one clock tick, and within a tied
        // PublishedAt the id tiebreak is not publish order — so this asserts
        // the paging mechanics (no overlap, nothing skipped, a terminal null),
        // and the deterministic ordering lives in the handler tests, where
        // the seeding controls the clock.
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
