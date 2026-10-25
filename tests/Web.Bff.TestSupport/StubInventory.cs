using System.Collections.Concurrent;
using System.Net;
using Grpc.Core;
using Inventory.Stock.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Web.Bff.TestSupport;

/// <summary>A real gRPC server on a loopback port, standing in for Inventory on ADR-154's hop.</summary>
public sealed class StubInventory : IAsyncLifetime
{
    private readonly ConcurrentQueue<Call> _calls = new();
    private readonly ConcurrentQueue<GetAvailabilityReply> _replies = new();

    private WebApplication? _app;

    /// <summary>The address the BFF's gRPC client is pointed at.</summary>
    public Uri Address { get; private set; } = null!;

    /// <summary>Every call this stub has received, in order, recorded before it is answered.</summary>
    public IReadOnlyCollection<Call> Calls => _calls;

    /// <summary>Every reply this stub has actually sent, in order.</summary>
    public IReadOnlyCollection<GetAvailabilityReply> Replies => _replies;

    /// <summary>Whether each product is in stock; a product with no entry is one Inventory holds no row for.</summary>
    public ConcurrentDictionary<Guid, bool> InStock { get; } = new();

    /// <summary>Statuses to fail the next calls with, one per call, before answering normally.</summary>
    public ConcurrentQueue<StatusCode> FailNextWith { get; } = new();

    /// <summary>Echo each id in braces, another spelling of the same GUID.</summary>
    public bool BracedIds { get; set; }

    /// <summary>Ids to answer in every reply whether or not the request named them.</summary>
    public List<Guid> AlsoAnswerWith { get; } = [];

    /// <summary>Answer every product twice, which the contract forbids.</summary>
    public bool DuplicateEveryAnswer { get; set; }

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
        _app.MapGrpcService<StubStockService>();

        await _app.StartAsync();

        Address = new Uri(_app.Urls.Single());
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }

    /// <summary>Every reply sent so far as one, for a consumer that asks one product per call.</summary>
    /// <remarks>Here so a suite compiled beside Web.Bff never names the message, which both assemblies define.</remarks>
    public GetAvailabilityReply GatheredReply()
    {
        GetAvailabilityReply gathered = new();

        foreach (GetAvailabilityReply reply in _replies)
            gathered.Availability.AddRange(reply.Availability);

        return gathered;
    }

    /// <summary>Realises an interaction's <c>Given</c> state and answers each alias's published id.</summary>
    public IReadOnlyDictionary<string, Guid> Publish(StockInteraction interaction)
    {
        Dictionary<string, Guid> published = [];

        foreach (ContractStock stock in interaction.Given)
        {
            Guid id = Guid.CreateVersion7();

            InStock[id] = stock.Available > 0;
            published.Add(stock.Alias, id);
        }

        return published;
    }

    /// <summary>One call as it arrived, its headers included, since only the receiving end can see them.</summary>
    public sealed record Call(IReadOnlyList<string> ProductIds, string? Authorization, string? CorrelationId);

    private sealed class StubStockService(StubInventory stub) : Stock.StockBase
    {
        public override Task<GetAvailabilityReply> GetAvailability(
            GetAvailabilityRequest request,
            ServerCallContext context)
        {
            stub._calls.Enqueue(
                new Call(
                    [.. request.ProductId],
                    context.RequestHeaders.GetValue("authorization"),
                    context.RequestHeaders.GetValue("x-correlation-id")));

            if (stub.FailNextWith.TryDequeue(out StatusCode failure))
                throw new RpcException(new Status(failure, "stubbed failure"));

            // The defaults below model Inventory, as StockContract states; each opt-in override is a reply it forbids.
            GetAvailabilityReply reply = new();

            List<string> answering = [.. request.ProductId, .. stub.AlsoAnswerWith.Select(id => id.ToString())];

            if (stub.DuplicateEveryAnswer)
                answering = [.. answering, .. answering];

            foreach (string id in answering)
            {
                if (!Guid.TryParse(id, out Guid productId) || !stub.InStock.TryGetValue(productId, out bool inStock))
                    continue;

                reply.Availability.Add(
                    new ProductAvailability
                    {
                        ProductId = stub.BracedIds ? productId.ToString("B") : id,
                        InStock = inStock
                    });
            }

            stub._replies.Enqueue(reply);

            return Task.FromResult(reply);
        }
    }
}
