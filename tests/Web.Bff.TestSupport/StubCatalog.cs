using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Catalog.Pricing.V1;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Web.Bff.TestSupport;

/// <summary>A real gRPC server on a loopback port, standing in for Catalog on §9.7's hop.</summary>
public sealed class StubCatalog : IAsyncLifetime
{
    private readonly ConcurrentQueue<Call> _calls = new();
    private readonly ConcurrentQueue<GetPricesReply> _replies = new();

    private WebApplication? _app;

    /// <summary>The address the BFF's gRPC client is pointed at.</summary>
    public Uri Address { get; private set; } = null!;

    /// <summary>Every call this stub has received, in order, recorded before it is answered.</summary>
    public IReadOnlyCollection<Call> Calls => _calls;

    /// <summary>Every reply this stub has actually sent, in order.</summary>
    public IReadOnlyCollection<GetPricesReply> Replies => _replies;

    /// <summary>Prices by product id, each in its own currency as Catalog stores them.</summary>
    public ConcurrentDictionary<Guid, (string Name, decimal Amount, string Currency)> Prices { get; } = new();

    /// <summary>Statuses to fail the next calls with, one per call, before answering normally.</summary>
    public ConcurrentQueue<StatusCode> FailNextWith { get; } = new();

    /// <summary>An amount string to answer with instead of formatting <see cref="Prices"/>.</summary>
    public string? RawAmount { get; set; }

    /// <summary>A currency to answer with instead of the stored one.</summary>
    public string? RawCurrency { get; set; }

    /// <summary>Ids to price in every reply whether or not the request named them.</summary>
    public List<Guid> AlsoAnswerWith { get; } = [];

    /// <summary>Answer every price twice, which the contract forbids.</summary>
    public bool DuplicateEveryPrice { get; set; }

    /// <summary>Calls to answer by aborting the connection, the transport fault §9.7's retry can see.</summary>
    public int AbortNextCalls { get; set; }

    /// <summary>How long each call hangs before answering, so the pipeline's own timeout fires.</summary>
    public TimeSpan HangFor { get; set; }

    public async ValueTask InitializeAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        // Listen, as ListenLocalhost refuses port 0; Http2 alone, as a cleartext default refuses gRPC's HTTP/2.
        builder.WebHost.ConfigureKestrel(o =>
            o.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));

        builder.Services.AddGrpc();
        builder.Services.AddSingleton(this);

        _app = builder.Build();
        _app.MapGrpcService<StubPricingService>();

        await _app.StartAsync();

        string url = _app.Urls.Single();
        Address = new Uri(url);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }

    /// <summary>Realises an interaction's <c>Given</c> state and answers each alias's published id.</summary>
    public IReadOnlyDictionary<string, Guid> Publish(PricingInteraction interaction)
    {
        Dictionary<string, Guid> published = [];

        foreach (ContractProduct product in interaction.Given)
        {
            // Version 7, as Catalog mints them, which keeps these clear of PricingContract.UnknownId.
            Guid id = Guid.CreateVersion7();

            Prices[id] = (product.Name, product.Amount, product.Currency);
            published.Add(product.Alias, id);
        }

        return published;
    }

    /// <summary>One call as it arrived, its headers included, since only the receiving end can see them.</summary>
    public sealed record Call(
        IReadOnlyList<string> ProductIds,
        string Currency,
        string? Authorization,
        string? CorrelationId);

    private sealed class StubPricingService(StubCatalog stub) : Pricing.PricingBase
    {
        public override async Task<GetPricesReply> GetPrices(
            GetPricesRequest request,
            ServerCallContext context)
        {
            stub._calls.Enqueue(
                new Call(
                    [.. request.ProductId],
                    request.Currency,
                    context.RequestHeaders.GetValue("authorization"),
                    // Lower-cased, as Metadata is an ordinal list and HTTP/2 names are lower-case on the wire.
                    context.RequestHeaders.GetValue("x-correlation-id")));

            if (stub.HangFor > TimeSpan.Zero)
                await Task.Delay(stub.HangFor, context.CancellationToken);

            if (stub.AbortNextCalls > 0)
            {
                stub.AbortNextCalls--;
                context.GetHttpContext().Abort();

                throw new RpcException(new Status(StatusCode.Aborted, "connection aborted"));
            }

            if (stub.FailNextWith.TryDequeue(out StatusCode failure))
                throw new RpcException(new Status(failure, "stubbed failure"));

            // The defaults below model Catalog, as PricingContract states; each opt-in override is a reply it forbids.

            // GetPricesValidator's ceiling.
            if (request.ProductId.Count > PricingContract.MaxProductIds)
            {
                throw new RpcException(
                    new Status(
                        StatusCode.InvalidArgument,
                        $"A request may name at most {PricingContract.MaxProductIds} products."));
            }

            GetPricesReply reply = new();

            List<string> answering = [.. request.ProductId, .. stub.AlsoAnswerWith.Select(id => id.ToString())];

            if (stub.DuplicateEveryPrice)
                answering = [.. answering, .. answering];

            foreach (string id in answering)
            {
                if (!Guid.TryParse(id, out Guid productId) ||
                    !stub.Prices.TryGetValue(
                        productId,
                        out (string Name, decimal Amount, string Currency) price))
                {
                    continue;
                }

                // OrdinalIgnoreCase, as Catalog upper-cases both sides, so "gbp" prices what "GBP" does.
                if (!string.Equals(price.Currency, request.Currency, StringComparison.OrdinalIgnoreCase))
                    continue;

                reply.Price.Add(new ProductPrice
                {
                    ProductId = id,
                    Name = price.Name,
                    // "F4", as the column is decimal(19,4) (§7.2) and 49.99 leaves Catalog as "49.9900".
                    Amount = stub.RawAmount ?? price.Amount.ToString("F4", CultureInfo.InvariantCulture),
                    // The stored spelling, as Catalog answers a "gbp" request "GBP".
                    Currency = stub.RawCurrency ?? price.Currency
                });
            }

            stub._replies.Enqueue(reply);

            return reply;
        }
    }
}
