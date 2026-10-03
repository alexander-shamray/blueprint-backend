using System.Text.Json.Nodes;
using Common.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Notifications.Application.Contacts;
using Notifications.Infrastructure.Contacts;
using Notifications.TestSupport;
using Shouldly;
using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>ADR-052's answers read from the client's side, over a stub Keycloak's admin API on loopback.</summary>
/// <remarks>Nothing here fills the breaker: each status is one the standard handler hands back (§9.7).</remarks>
public sealed class ContactSourceTests : IClassFixture<ContactSourceTests.KeycloakStub>
{
    /// <summary>One stub and one host for the class, as a host over an unreachable broker is slow to stop.</summary>
    public sealed class KeycloakStub : IAsyncLifetime
    {
        public WireMockServer Server { get; private set; } = null!;

        public PatientFactory Factory { get; private set; } = null!;

        public async ValueTask InitializeAsync()
        {
            Server = await StartStubAsync();
            Factory = new PatientFactory(Server.Urls[0] + "/");
        }

        public ValueTask DisposeAsync()
        {
            Factory.Dispose();
            Server.Stop();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A loopback stub, asked once first, since its first answer can outlast ContactHop's total.</summary>
    internal static async Task<WireMockServer> StartStubAsync()
    {
        WireMockServer server = WireMockServer.Start(new WireMockServerSettings { Urls = ["http://127.0.0.1:0"] });

        using HttpClient warm = new();
        using HttpResponseMessage answered =
            await warm.GetAsync(server.Urls[0] + "/", TestContext.Current.CancellationToken);
        server.ResetLogEntries();

        return server;
    }

    /// <summary>A host whose contact hop times out no attempt, so a cold first read cannot fill the breaker.</summary>
    /// <remarks>The generator, as <c>Timeout</c> is range-checked; the total still bounds every read (§9.7).</remarks>
    public sealed class PatientFactory(string contactSourceBaseUrl)
        : NotificationsWorkerFactory(Unreachable.Sql, Unreachable.Rabbit, contactSourceBaseUrl: contactSourceBaseUrl)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureTestServices(services =>
                services.PostConfigure<HttpStandardResilienceOptions>(
                    ContactHop.ResilienceOptionsName,
                    o => o.AttemptTimeout.TimeoutGenerator = _ => ValueTask.FromResult(Timeout.InfiniteTimeSpan)));
        }
    }

    private const string Mailbox = "aigerim@example.test";

    // Five starts, because a run that loses the race below five times over has something else wrong with it.
    private const int StartAttempts = 5;

    private readonly WireMockServer _keycloak;
    private readonly PatientFactory _factory;

    public ContactSourceTests(KeycloakStub stub)
    {
        _keycloak = stub.Server;
        _factory = stub.Factory;
        _keycloak.ResetMappings();
        _keycloak.ResetLogEntries();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string PathOf(Guid customer) =>
        $"/admin/realms/{NotificationsWorkerFactory.LocalRealm}/users/{customer:D}";

    private static async Task<ContactLookup> ReadAsync(WebApplicationFactory<Program> host, Guid customer)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IContactSource>().GetAsync(customer, Ct);
    }

    private Task<ContactLookup> ReadAsync(Guid customer) => ReadAsync(_factory, customer);

    /// <summary>A user as Keycloak represents one, with a name and an attribute the adapter must never bind.</summary>
    private static JsonObject User(string? email = Mailbox, bool enabled = true) =>
        new()
        {
            ["id"] = Guid.CreateVersion7().ToString(),
            ["username"] = "aigerim",
            ["firstName"] = "Айгерім",
            ["lastName"] = "Сейітқызы",
            ["enabled"] = enabled,
            ["email"] = email,
            ["attributes"] = new JsonObject { ["phone"] = new JsonArray("+77010000000") }
        };

    private Guid Answer(int status, string? body = null)
    {
        Guid customer = Guid.CreateVersion7();
        IResponseBuilder response = Response.Create().WithStatusCode(status);

        if (body is not null)
            response = response.WithHeader("Content-Type", "application/json").WithBody(body);

        _keycloak.Given(Request.Create().WithPath(PathOf(customer)).UsingGet()).RespondWith(response);

        return customer;
    }

    private Guid Answer(JsonObject user) => Answer(200, user.ToJsonString());

    private int Calls(string path) => _keycloak.LogEntries.Count(e => e.RequestMessage!.Path == path);

    [Fact]
    public async Task A_customer_answers_with_the_mailbox_and_the_request_is_the_admin_apis_and_carries_the_token()
    {
        Guid customer = Answer(User());

        ContactLookup lookup = await ReadAsync(customer);

        lookup.ShouldBe(new ContactLookup.Found(Mailbox, null));

        ILogEntry call = _keycloak.LogEntries.ShouldHaveSingleItem();
        call.RequestMessage!.Method.ShouldBe("GET");
        call.RequestMessage.Path.ShouldBe(PathOf(customer));
        call.RequestMessage.Headers!["Authorization"][0].ShouldStartWith("Bearer token-");
        call.RequestMessage.Headers["Accept"][0].ShouldContain("application/json");
    }

    [Fact]
    public async Task The_token_is_asked_for_under_the_scope_whose_mapper_writes_the_grant()
    {
        int before = _factory.Tokens.Scopes.Count;

        await ReadAsync(Answer(User()));

        string[] asked = [.. _factory.Tokens.Scopes.Skip(before)];
        asked.ShouldNotBeEmpty("the read drew no token");
        asked.ShouldAllBe(s => s == NotificationsWorkerFactory.ContactScope);
    }

    [Theory]
    [InlineData("kk", "kk")]
    [InlineData("en-GB", "en-GB")]
    [InlineData("not a locale", null)]
    [InlineData("kk\n", null)]
    [InlineData("<script>", null)]
    [InlineData("", null)]
    public async Task A_locale_is_kept_when_it_has_the_shape_and_dropped_rather_than_refused_when_not(
        string stored,
        string? expected)
    {
        JsonObject user = User();
        user["attributes"]!["locale"] = new JsonArray(stored);

        (await ReadAsync(Answer(user))).ShouldBe(new ContactLookup.Found(Mailbox, expected));
    }

    [Fact]
    public async Task A_locale_with_two_values_is_dropped_rather_than_one_chosen()
    {
        JsonObject user = User();
        user["attributes"]!["locale"] = new JsonArray("kk", "ru");

        (await ReadAsync(Answer(user))).ShouldBe(new ContactLookup.Found(Mailbox, null));
    }

    [Fact]
    public async Task A_user_with_no_attributes_at_all_has_no_locale()
    {
        JsonObject user = User();
        user.Remove("attributes");

        (await ReadAsync(Answer(user))).ShouldBe(new ContactLookup.Found(Mailbox, null));
    }

    [Fact]
    public async Task No_such_user_is_no_such_customer_after_one_call()
    {
        Guid customer = Answer(404);

        (await ReadAsync(customer)).ShouldBeOfType<ContactLookup.NoSuchCustomer>();
        Calls(PathOf(customer)).ShouldBe(1);
    }

    [Fact]
    public async Task A_disabled_user_is_no_such_customer_though_keycloak_still_answers_with_a_mailbox()
    {
        (await ReadAsync(Answer(User(enabled: false)))).ShouldBeOfType<ContactLookup.NoSuchCustomer>(
            "an account taken back from somebody is most often a disabled one (ADR-052)");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_user_with_no_mailbox_is_no_such_customer(string? email)
    {
        (await ReadAsync(Answer(User(email)))).ShouldBeOfType<ContactLookup.NoSuchCustomer>();
    }

    [Fact]
    public async Task A_user_whose_email_member_is_absent_is_no_such_customer()
    {
        JsonObject user = User();
        user.Remove("email");

        (await ReadAsync(Answer(user))).ShouldBeOfType<ContactLookup.NoSuchCustomer>();
    }

    [Fact]
    public async Task A_mailbox_carrying_a_line_break_arrives_as_it_is_stored_for_the_mail_channel_to_refuse()
    {
        const string broken = "aigerim@example.test\r\nbcc: someone@example.test";

        (await ReadAsync(Answer(User(broken)))).ShouldBe(new ContactLookup.Found(broken, null));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task A_refused_token_is_counted_once_and_thrown_rather_than_read_as_an_absence(int status)
    {
        using OutboundCount counted = OutboundCounter.ContactRefused(_factory.Services);
        Guid customer = Answer(status);

        await Should.ThrowAsync<ContactSourceRefusedException>(() => ReadAsync(customer));

        counted.Value.ShouldBe(1, "a revoked grant is a defect somebody must see, not an outage to wait out");
        Calls(PathOf(customer)).ShouldBe(1, "a refusal is no transient fault, so nothing retries it");
    }

    [Fact]
    public async Task A_redirect_is_not_followed_so_the_token_goes_nowhere_else()
    {
        Guid customer = Guid.CreateVersion7();
        _keycloak.Given(Request.Create().WithPath(PathOf(customer)).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(302)
                .WithHeader("Location", _keycloak.Urls[0] + "/elsewhere"));
        _keycloak.Given(Request.Create().WithPath("/elsewhere").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(User().ToJsonString()));

        await Should.ThrowAsync<HttpRequestException>(() => ReadAsync(customer));

        Calls("/elsewhere").ShouldBe(0);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"email":"aigerim@example.test"}""")]
    [InlineData("""{"enabled":"true","email":"aigerim@example.test"}""")]
    [InlineData("""{"enabled":true,"email":42}""")]
    public async Task A_body_that_is_not_a_user_is_refused_by_shape_and_quotes_none_of_it(string body)
    {
        using OutboundCount counted = OutboundCounter.ContactRefused(_factory.Services);

        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => ReadAsync(Answer(200, body)));

        thrown.Message.ShouldNotContain("aigerim");
        thrown.InnerException.ShouldBeNull("the parser's message can quote the body");
        counted.Value.ShouldBe(0, "a malformed answer is not a refused credential");
    }

    [Fact]
    public async Task A_mailbox_wider_than_the_owners_own_column_is_refused_by_field_and_never_by_value()
    {
        string wide = new string('ж', ContactLimits.MaxEmailLength) + "@example.test";

        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => ReadAsync(Answer(User(wide))));

        thrown.Message.ShouldContain("email");
        thrown.Message.ShouldNotContain("жжж");
    }

    [Fact]
    public async Task A_mailbox_at_the_owners_own_width_is_accepted()
    {
        string widest = new string('a', ContactLimits.MaxEmailLength - "@example.test".Length) + "@example.test";

        (await ReadAsync(Answer(User(widest)))).ShouldBe(new ContactLookup.Found(widest, null));
    }

    [Fact]
    public async Task A_base_address_with_a_path_keeps_it_and_the_realm_follows_it()
    {
        using PatientFactory prefixed = new(_keycloak.Urls[0] + "/auth");
        Guid customer = Guid.CreateVersion7();
        _keycloak.Given(Request.Create().WithPath("/auth" + PathOf(customer)).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(User().ToJsonString()));

        (await ReadAsync(prefixed, customer)).ShouldBeOfType<ContactLookup.Found>();
    }

    [Fact]
    public void The_built_pipeline_carries_the_hops_numbers()
    {
        // Off the built host, by the name the handler registered under, so this checks the registration (§9.7).
        HttpStandardResilienceOptions options = _factory.Services
            .GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>()
            .Get(ContactHop.ResilienceOptionsName);

        options.AttemptTimeout.Timeout.ShouldBe(ContactHop.AttemptTimeout);
        options.Retry.MaxRetryAttempts.ShouldBe(ContactHop.MaxRetryAttempts);
        options.Retry.MaxDelay.ShouldBe(ContactHop.MaxRetryDelay, "jitter makes the nominal delay no bound");
        options.Retry.ShouldRetryAfterHeader.ShouldBeFalse("a Retry-After past MaxDelay would spend the budget");
        options.TotalRequestTimeout.Timeout.ShouldBe(ContactHop.TotalRequestTimeout);
        options.CircuitBreaker.MinimumThroughput.ShouldBe(ContactHop.CircuitBreakerMinimumThroughput);
    }

    [Fact]
    public void The_host_draws_its_token_through_the_grant_check()
    {
        using ProgramTokensFactory host = new();

        host.Services.GetRequiredService<ITokenCache>().ShouldBeOfType<GrantCheckedTokenCache>();
    }

    [Fact]
    public void A_host_given_no_client_secret_does_not_start()
    {
        Exception refusal = Refusal(() => _factory.WithWebHostBuilder(b =>
            b.UseSetting($"{ServiceIdentityOptions.SectionName}:ClientSecret", "")));

        refusal.ShouldBeOfType<OptionsValidationException>()
            .Message.ShouldContain(nameof(ServiceIdentityOptions.ClientSecret));
    }

    // A start refused at validation can lose a race with its own disposal and say only that, so it is asked again.
    private static Exception Refusal(Func<WebApplicationFactory<Program>> derive)
    {
        Exception refusal = null!;

        for (int attempt = 0; attempt < StartAttempts; attempt++)
        {
            using WebApplicationFactory<Program> host = derive();

            refusal = Should.Throw<Exception>(() => host.Services);

            if (refusal is not ObjectDisposedException { ObjectName: nameof(IServiceProvider) })
                return refusal;
        }

        return refusal;
    }

    /// <summary>The host with <c>Program</c>'s own token source left in place.</summary>
    private sealed class ProgramTokensFactory() : NotificationsWorkerFactory(Unreachable.Sql, Unreachable.Rabbit)
    {
        protected override void ConfigureTokens(IServiceCollection services)
        {
        }
    }
}
