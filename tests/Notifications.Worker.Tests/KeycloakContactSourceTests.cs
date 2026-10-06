using System.Buffers.Text;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text.Json;
using Common.Web;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Contacts;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>ADR-052's contact read and its client's grant, against the pinned Keycloak and the shipped realm.</summary>
[Collection(nameof(KeycloakCollection))]
public sealed class KeycloakContactSourceTests(KeycloakFixture keycloak)
{
    private static string Mailbox() => $"c{Guid.CreateVersion7():N}@example.test";

    private static async Task<ContactLookup> ReadAsync(KeycloakFixture.ContactHost host, Guid customer)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();

        return await scope.ServiceProvider
            .GetRequiredService<IContactSource>()
            .GetAsync(customer, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_customer_answers_with_the_mailbox_and_no_locale_while_the_realm_holds_none()
    {
        string mailbox = Mailbox();
        Guid customer = await keycloak.CreateUserAsync(mailbox);

        ContactLookup lookup = await ReadAsync(keycloak.Granted, customer);

        // A user who never chose a locale holds none, and an absent locale is an answer (ADR-052).
        lookup.ShouldBe(new ContactLookup.Found(mailbox, null));
    }

    [Fact]
    public async Task No_such_user_is_no_such_customer()
    {
        (await ReadAsync(keycloak.Granted, Guid.CreateVersion7())).ShouldBeOfType<ContactLookup.NoSuchCustomer>();
    }

    [Fact]
    public async Task A_realm_this_server_does_not_hold_is_a_fault_rather_than_no_such_customer()
    {
        Guid customer = await keycloak.CreateUserAsync(Mailbox());

        HttpRequestException thrown = await Should.ThrowAsync<HttpRequestException>(
            () => ReadAsync(keycloak.WrongRealm, customer));

        // Keycloak answers this 404 too, and a terminal answer here would end every customer's work (ADR-052).
        thrown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_disabled_user_is_no_such_customer()
    {
        Guid customer = await keycloak.CreateUserAsync(Mailbox(), enabled: false);

        (await ReadAsync(keycloak.Granted, customer)).ShouldBeOfType<ContactLookup.NoSuchCustomer>();
    }

    [Fact]
    public async Task A_user_with_no_email_is_no_such_customer()
    {
        Guid customer = await keycloak.CreateUserAsync(email: null);

        (await ReadAsync(keycloak.Granted, customer)).ShouldBeOfType<ContactLookup.NoSuchCustomer>();
    }

    [Fact]
    public async Task An_email_carrying_a_line_break_arrives_as_keycloak_holds_it()
    {
        // Kept whole, since refusing it as no mailbox is the mail channel's decision rather than the reader's.
        string broken = $"{Mailbox()}\r\nbcc: someone@example.test";
        Guid customer = await keycloak.CreateUserAsync(broken);

        (await ReadAsync(keycloak.Granted, customer)).ShouldBe(new ContactLookup.Found(broken, null));
    }

    [Fact]
    public async Task A_locale_of_the_shape_is_kept()
    {
        string mailbox = Mailbox();
        Guid customer = await keycloak.CreateUserAsync(mailbox, locale: "kk");

        (await ReadAsync(keycloak.Granted, customer)).ShouldBe(new ContactLookup.Found(mailbox, "kk"));
    }

    [Fact]
    public async Task A_locale_that_is_not_one_is_dropped_and_the_customer_still_found()
    {
        string mailbox = Mailbox();
        Guid customer = await keycloak.CreateUserAsync(mailbox, locale: "not a locale!");

        (await ReadAsync(keycloak.Granted, customer)).ShouldBe(new ContactLookup.Found(mailbox, null));
    }

    [Fact]
    public async Task The_container_holds_the_values_the_adapter_is_asked_to_keep_or_drop()
    {
        // The dropped-locale case above passes vacuously if Keycloak never stored the value; this says it did.
        string broken = $"{Mailbox()}\r\nbcc: someone@example.test";
        Guid customer = await keycloak.CreateUserAsync(broken, locale: "not a locale!");

        JsonElement user = await keycloak.UserAsAdminAsync(customer);

        user.GetProperty("email").GetString().ShouldBe(broken);
        user.GetProperty("attributes").GetProperty("locale")[0].GetString().ShouldBe("not a locale!");
    }

    [Fact]
    public async Task The_token_issued_to_the_contact_reader_carries_exactly_the_grant_and_no_service_audience()
    {
        string token = await keycloak.TokenAsync(
            KeycloakFixture.ContactClient,
            KeycloakFixture.ContactSecret,
            NotificationsWorkerFactory.ContactScope);

        JwtSecurityToken jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        using JsonDocument payload = JsonDocument.Parse(Base64Url.DecodeFromChars(jwt.RawPayload));

        string[] roles =
        [
            .. payload.RootElement
                .GetProperty("resource_access")
                .GetProperty("realm-management")
                .GetProperty("roles")
                .EnumerateArray()
                .Select(r => r.GetString())
                .OfType<string>()
        ];

        // Exactly the three, which is the pinned Keycloak expanding view-users's composite into the token.
        roles.ShouldBe(["query-groups", "query-users", "view-users"], ignoreOrder: true);

        // No audience any service validates and no permission: a stolen secret reads users and calls nothing.
        jwt.Audiences.ShouldNotContain(AuthenticationExtensions.Audience);
        jwt.Claims.ShouldNotContain(c => c.Type == PermissionClaim.Type);
    }

    [Fact]
    public async Task A_token_issued_to_a_client_without_the_grant_is_refused_by_the_hosts_own_check()
    {
        using OutboundCount counted = OutboundCounter.ContactRefused(keycloak.Ungranted.Services);
        Guid customer = await keycloak.CreateUserAsync(Mailbox());

        ContactSourceRefusedException refused =
            await Should.ThrowAsync<ContactSourceRefusedException>(() => ReadAsync(keycloak.Ungranted, customer));

        // GrantCheckedTokenCache's wording, since Keycloak's own 403 would throw and count the same.
        refused.Message.ShouldContain("realm-management role(s)");
        counted.Value.ShouldBe(1, "counted where the refusal was decided");
    }

    [Fact]
    public async Task Keycloak_itself_refuses_a_token_without_the_grant_and_the_refusal_is_counted()
    {
        // The check bypassed, so this is the owner's enforcement and the adapter's mapping of it (ADR-052).
        using OutboundCount counted = OutboundCounter.ContactRefused(keycloak.UncheckedUngranted.Services);
        Guid customer = await keycloak.CreateUserAsync(Mailbox());

        ContactSourceRefusedException refused = await Should.ThrowAsync<ContactSourceRefusedException>(
            () => ReadAsync(keycloak.UncheckedUngranted, customer));

        refused.Message.ShouldContain("Keycloak refused");
        counted.Value.ShouldBe(1);
    }

    [Fact]
    public async Task A_refused_client_secret_is_a_refusal_rather_than_an_outage()
    {
        using OutboundCount counted = OutboundCounter.ContactRefused(keycloak.WrongSecret.Services);
        Guid customer = await keycloak.CreateUserAsync(Mailbox());

        await Should.ThrowAsync<ContactSourceRefusedException>(() => ReadAsync(keycloak.WrongSecret, customer));

        counted.Value.ShouldBe(1, "the token endpoint refusing the client is ADR-052's fourth row");
    }
}
