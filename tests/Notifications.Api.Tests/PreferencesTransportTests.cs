using System.Security.Claims;
using System.Security.Cryptography;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Notifications.Preferences.V1;
using Shouldly;
using Xunit;
using PreferencesGrpc = Notifications.Preferences.V1.Preferences;

namespace Notifications.Api.Tests;

/// <summary>
/// ADR-120's methods up to the database, which this host cannot reach here: authentication, the client claim, and
/// the refusals the pipeline makes before a transaction opens. The journal itself is the worker suite's.
/// </summary>
public sealed class PreferencesTransportTests : IDisposable
{
    private readonly NotificationsApiFactory _production = new();
    private readonly AuthenticatedApiFactory _authenticated = new();

    public void Dispose()
    {
        _production.Dispose();
        _authenticated.Dispose();
    }

    private static PreferencesGrpc.PreferencesClient Client(NotificationsApiFactory factory, out GrpcChannel channel)
    {
        channel = GrpcChannel.ForAddress(
            factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = factory.Server.CreateHandler() });

        return new PreferencesGrpc.PreferencesClient(channel);
    }

    private static Metadata Caller(string? client = "commerce-app")
    {
        Metadata headers = [new(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString())];
        if (client is not null)
            headers.Add(TestAuthHandler.ClientHeader, client);

        return headers;
    }

    private static SetPreferenceRequest Valid() =>
        new() { Topic = "shipment-dispatched", Channel = "push", OptedIn = false, ReadVersion = "" };

    private async Task<RpcException> RefusedAsync(SetPreferenceRequest request, Metadata caller)
    {
        PreferencesGrpc.PreferencesClient client = Client(_authenticated, out GrpcChannel channel);
        using (channel)
        {
            return await Should.ThrowAsync<RpcException>(
                () => client.SetAsync(request, caller, cancellationToken: TestContext.Current.CancellationToken)
                    .ResponseAsync);
        }
    }

    [Fact]
    public async Task A_call_with_no_token_is_unauthenticated()
    {
        PreferencesGrpc.PreferencesClient client = Client(_production, out GrpcChannel channel);
        using (channel)
        {
            RpcException refused = await Should.ThrowAsync<RpcException>(
                () => client
                    .GetAsync(new GetPreferencesRequest(), cancellationToken: TestContext.Current.CancellationToken)
                    .ResponseAsync);

            refused.StatusCode.ShouldBe(StatusCode.Unauthenticated);
        }
    }

    [Theory]
    [InlineData("topic")]
    [InlineData("channel")]
    [InlineData("version")]
    public async Task A_malformed_write_is_an_invalid_argument_before_any_transaction(string field)
    {
        SetPreferenceRequest request = Valid();
        switch (field)
        {
            case "topic":
                request.Topic = "newsletter";
                break;
            case "channel":
                request.Channel = "sms";
                break;
            default:
                request.ReadVersion = "not-a-guid";
                break;
        }

        // The database is unreachable, so an answer other than InvalidArgument would mean a transaction opened.
        (await RefusedAsync(request, Caller())).StatusCode.ShouldBe(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task A_write_under_a_token_naming_no_client_is_refused_since_the_entry_must_record_one()
    {
        (await RefusedAsync(Valid(), Caller(client: null))).StatusCode.ShouldBe(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task The_client_claim_reaches_the_principal_under_its_own_name()
    {
        // JwtBearer's inbound map, as §11.3 sets it: it renames sub, and a rename of azp would leave every write's
        // client empty and refused. The service reads the claim as "azp".
        SymmetricSecurityKey key = new(RandomNumberGenerator.GetBytes(32));
        JsonWebTokenHandler handler = new() { MapInboundClaims = true };
        string token = handler.CreateToken(
            new SecurityTokenDescriptor
            {
                Issuer = "issuer",
                Audience = "audience",
                Claims = new Dictionary<string, object> { ["sub"] = Guid.CreateVersion7().ToString(), ["azp"] = "web" },
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
            });

        TokenValidationResult validated = await handler.ValidateTokenAsync(
            token,
            new TokenValidationParameters
            {
                ValidIssuer = "issuer",
                ValidAudience = "audience",
                IssuerSigningKey = key
            });

        validated.IsValid.ShouldBeTrue();
        validated.ClaimsIdentity.FindFirst(ClaimTypes.NameIdentifier).ShouldNotBeNull("the map ran: a control");
        validated.ClaimsIdentity.FindFirst("azp")!.Value.ShouldBe("web");
    }
}
