using System.Net.Http.Json;
using Catalog.Pricing.V1;
using Catalog.TestSupport;
using Grpc.Core;
using Grpc.Net.Client;
using Shouldly;
using Web.Bff.TestSupport;
using Xunit;
using PricingGrpc = Catalog.Pricing.V1.Pricing;

namespace Catalog.Api.Tests;

/// <summary>The provider's half of the consumer-driven contract in <see cref="PricingContract"/>.</summary>
/// <remarks>Linked in rather than referenced (ADR-023); what the contract does not ask for is not asserted.</remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class PricingContractVerificationTests(ServiceFixture fixture) : IAsyncLifetime
{
    private HttpClient _client = null!;
    private GrpcChannel _channel = null!;

    /// <summary>The interactions the contract says are answered.</summary>
    public static TheoryData<string> Answered => [.. PricingContract.Answered];

    /// <summary>The interactions the contract says are refused.</summary>
    public static TheoryData<string> Refused => [.. PricingContract.Refusals];

    public async ValueTask InitializeAsync()
    {
        _client = fixture.Factory.CreateClient();
        _channel = GrpcChannel.ForAddress(
            fixture.Factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = fixture.Factory.Server.CreateHandler() });

        await fixture.ResetAsync();
    }

    public ValueTask DisposeAsync()
    {
        _channel.Dispose();
        _client.Dispose();

        return ValueTask.CompletedTask;
    }

    [Theory]
    [MemberData(nameof(Answered))]
    public async Task Catalog_answers_what_the_contract_promises(string description)
    {
        PricingInteraction interaction = PricingContract.Named(description);
        IReadOnlyDictionary<string, Guid> published = await PublishAsync(interaction);

        GetPricesReply reply = await Pricing.GetPricesAsync(
            PricingContract.Request(interaction, published),
            Authenticated(),
            cancellationToken: TestContext.Current.CancellationToken);

        // The consumer's own tolerance applied to the provider's reply; nothing in this file decides what counts.
        PricingContract.Verify(interaction, published, reply);
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task Catalog_refuses_what_the_contract_says_it_refuses(string description)
    {
        PricingInteraction interaction = PricingContract.Named(description);
        IReadOnlyDictionary<string, Guid> published = await PublishAsync(interaction);
        PricingOutcome.Refusal refusal = (PricingOutcome.Refusal)interaction.Then;

        RpcException thrown = await Should.ThrowAsync<RpcException>(
            () => Pricing
                .GetPricesAsync(
                    PricingContract.Request(interaction, published),
                    Authenticated(),
                    cancellationToken: TestContext.Current.CancellationToken)
                .ResponseAsync);

        // The status, not merely a failure, since UpstreamExceptionHandler maps InvalidArgument to the caller's 400.
        thrown.StatusCode.ShouldBe(refusal.Status);
    }

    /// <summary>The BFF's service account (§11.3), since it is the BFF's contract being verified.</summary>
    private static Metadata Authenticated() =>
        [new Metadata.Entry(TestAuthHandler.UserHeader, "service-account-web-bff")];

    private PricingGrpc.PricingClient Pricing => new(_channel);

    /// <summary>Publishes each <c>Given</c> product through the endpoint, as a customer would.</summary>
    private async Task<IReadOnlyDictionary<string, Guid>> PublishAsync(PricingInteraction interaction)
    {
        Dictionary<string, Guid> published = [];

        foreach (ContractProduct product in interaction.Given)
        {
            HttpRequestMessage request = new(HttpMethod.Post, "/v1/catalog/products")
            {
                Content = JsonContent.Create(new
                {
                    // Fresh per product: the contract's Given block publishes
                    // several, and one shared CommandId would replay the first.
                    CommandId = Guid.CreateVersion7(),
                    product.Name,
                    ThumbnailUrl = (string?)null,
                    product.Amount,
                    product.Currency
                })
            };

            request.Headers.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
            request.Headers.Add(TestAuthHandler.PermissionsHeader, CatalogPermissions.Write);

            HttpResponseMessage response = await _client.SendAsync(
                request,
                TestContext.Current.CancellationToken);

            response.EnsureSuccessStatusCode();

            published.Add(
                product.Alias,
                (await response.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken))!);
        }

        return published;
    }
}
