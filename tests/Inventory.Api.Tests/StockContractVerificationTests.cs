using Grpc.Core;
using Grpc.Net.Client;
using Inventory.Stock.V1;
using Inventory.TestSupport;
using Web.Bff.TestSupport;
using Xunit;
using StockGrpc = Inventory.Stock.V1.Stock;

namespace Inventory.Api.Tests;

/// <summary>The provider's half of the consumer-driven contract in <see cref="StockContract"/>.</summary>
/// <remarks>Linked in rather than referenced (ADR-023); what the contract does not ask for is not asserted.</remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class StockContractVerificationTests(ServiceFixture fixture) : IAsyncLifetime
{
    private GrpcChannel _channel = null!;

    /// <summary>The interactions the contract holds.</summary>
    public static TheoryData<string> Answered => [.. StockContract.Descriptions];

    public async ValueTask InitializeAsync()
    {
        _channel = GrpcChannel.ForAddress(
            fixture.Factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = fixture.Factory.Server.CreateHandler() });

        await fixture.ResetAsync();
    }

    public ValueTask DisposeAsync()
    {
        _channel.Dispose();

        return ValueTask.CompletedTask;
    }

    [Theory]
    [MemberData(nameof(Answered))]
    public async Task Inventory_answers_what_the_contract_promises(string description)
    {
        StockInteraction interaction = StockContract.Named(description);
        IReadOnlyDictionary<string, Guid> published = await PublishAsync(interaction);

        GetAvailabilityReply reply = await Stock.GetAvailabilityAsync(
            StockContract.Request(interaction, published),
            Authenticated(),
            cancellationToken: TestContext.Current.CancellationToken);

        // The consumer's own tolerance applied to the provider's reply; nothing in this file decides what counts.
        StockContract.Verify(interaction, published, reply);
    }

    /// <summary>The BFF's service account (§11.3), since it is the BFF's contract being verified.</summary>
    private static Metadata Authenticated() =>
        [new Metadata.Entry(TestAuthHandler.UserHeader, "service-account-web-bff")];

    private StockGrpc.StockClient Stock => new(_channel);

    /// <summary>Seeds each <c>Given</c> row by SQL, since the admin PUT sets a count and cannot choose Reserved.</summary>
    private async Task<IReadOnlyDictionary<string, Guid>> PublishAsync(StockInteraction interaction)
    {
        Dictionary<string, Guid> published = [];

        foreach (ContractStock stock in interaction.Given)
        {
            Guid id = Guid.CreateVersion7();

            // Reserved above zero on every row, so a flag read off the gross count would fail the contract.
            await fixture.ExecuteAsync(
                "INSERT INTO inventory.StockItems (ProductId, Available, Reserved, UpdatedAt) " +
                "VALUES ({0}, {1}, 3, SYSDATETIMEOFFSET())",
                id,
                stock.Available);

            published.Add(stock.Alias, id);
        }

        return published;
    }
}
