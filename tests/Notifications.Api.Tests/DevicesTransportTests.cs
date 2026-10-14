using Grpc.Core;
using Grpc.Net.Client;
using Notifications.Devices.V1;
using Shouldly;
using Xunit;
using DevicesGrpc = Notifications.Devices.V1.Devices;

namespace Notifications.Api.Tests;

/// <summary>
/// ADR-117's methods up to the database, which this host cannot reach here: authentication, and the refusals the
/// pipeline makes before a transaction opens. The rows themselves are the worker suite's, over a real database.
/// </summary>
public sealed class DevicesTransportTests : IDisposable
{
    private const string AToken = "fcm-token-that-must-never-appear-in-a-status";

    private readonly NotificationsApiFactory _production = new();
    private readonly AuthenticatedApiFactory _authenticated = new();

    public void Dispose()
    {
        _production.Dispose();
        _authenticated.Dispose();
    }

    private static DevicesGrpc.DevicesClient Client(NotificationsApiFactory factory, out GrpcChannel channel)
    {
        channel = GrpcChannel.ForAddress(
            factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = factory.Server.CreateHandler() });

        return new DevicesGrpc.DevicesClient(channel);
    }

    private static Metadata Caller() => [new(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString())];

    private static RegisterRequest Valid() =>
        new()
        {
            InstallationId = Guid.CreateVersion7().ToString(),
            Platform = "fcm",
            AppId = "com.example.shop",
            Token = AToken,
            Sequence = 1
        };

    [Fact]
    public async Task A_call_with_no_token_is_unauthenticated()
    {
        // The production scheme: the subject is the caller's token's, so no token means no subject to register.
        DevicesGrpc.DevicesClient client = Client(_production, out GrpcChannel channel);
        using (channel)
        {
            RpcException refused = await Should.ThrowAsync<RpcException>(
                () => client.RegisterAsync(Valid(), cancellationToken: TestContext.Current.CancellationToken)
                    .ResponseAsync);

            refused.StatusCode.ShouldBe(StatusCode.Unauthenticated);
        }
    }

    [Fact]
    public async Task An_installation_id_that_is_not_a_guid_is_an_invalid_argument()
    {
        DevicesGrpc.DevicesClient client = Client(_authenticated, out GrpcChannel channel);
        using (channel)
        {
            RegisterRequest request = Valid();
            request.InstallationId = "not-a-guid";

            RpcException refused = await Should.ThrowAsync<RpcException>(
                () => client.RegisterAsync(request, Caller(), cancellationToken: TestContext.Current.CancellationToken)
                    .ResponseAsync);

            refused.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        }
    }

    [Theory]
    [InlineData("platform")]
    [InlineData("token")]
    [InlineData("sequence")]
    public async Task A_malformed_registration_is_an_invalid_argument_that_quotes_no_value(string field)
    {
        DevicesGrpc.DevicesClient client = Client(_authenticated, out GrpcChannel channel);
        using (channel)
        {
            RegisterRequest request = Valid();
            switch (field)
            {
                case "platform":
                    request.Platform = "sms";
                    break;
                case "token":
                    request.Token = AToken + " with a space";
                    break;
                default:
                    request.Sequence = 0;
                    break;
            }

            RpcException refused = await Should.ThrowAsync<RpcException>(
                () => client.RegisterAsync(request, Caller(), cancellationToken: TestContext.Current.CancellationToken)
                    .ResponseAsync);

            // Before the transaction, so the unreachable database is never asked; and no value in the detail (§13.4).
            refused.StatusCode.ShouldBe(StatusCode.InvalidArgument);
            refused.Status.Detail.ShouldNotContain(AToken);
        }
    }

    [Fact]
    public async Task A_sign_out_with_no_sequence_is_an_invalid_argument()
    {
        DevicesGrpc.DevicesClient client = Client(_authenticated, out GrpcChannel channel);
        using (channel)
        {
            RpcException refused = await Should.ThrowAsync<RpcException>(
                () => client.SignOutAsync(
                        new SignOutRequest { InstallationId = Guid.CreateVersion7().ToString(), Sequence = 0 },
                        Caller(),
                        cancellationToken: TestContext.Current.CancellationToken)
                    .ResponseAsync);

            refused.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        }
    }
}
