using System.Collections.Concurrent;
using System.Net;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ordering.Delivery.V1;
using Xunit;

namespace Shipping.OrderingStub;

/// <summary>One address this stub will answer with.</summary>
public sealed record StubAddress(
    Guid CustomerId,
    string Line1,
    string? Line2,
    string City,
    string PostalCode,
    string Country);

/// <summary>
/// A real gRPC server on a loopback port, standing in for Ordering's <c>DeliveryAddresses.Get</c> (ADR-052).
/// </summary>
/// <remarks>HTTP/2 only, since a cleartext endpoint cannot serve HTTP/1.1 and h2c at once (§9.7).</remarks>
public sealed class StubOrdering : IAsyncLifetime
{
    private readonly ConcurrentQueue<StatusCode> _statuses = new();
    private readonly ConcurrentQueue<Guid> _calls = new();
    private readonly ConcurrentQueue<string> _tokens = new();

    private WebApplication? _app;

    /// <summary>The address the worker's gRPC client is pointed at.</summary>
    public Uri Address { get; private set; } = null!;

    /// <summary>Addresses this stub knows, by order id.</summary>
    public ConcurrentDictionary<Guid, StubAddress> Addresses { get; } = new();

    /// <summary>Every order id this stub has been asked for, in order.</summary>
    public IReadOnlyCollection<Guid> Calls => _calls;

    /// <summary>Every <c>Authorization</c> value this stub has been sent, in order.</summary>
    public IReadOnlyCollection<string> Tokens => _tokens;

    /// <summary>Statuses to fail the next calls with, one per call, before answering normally.</summary>
    public void Fail(params StatusCode[] statuses)
    {
        foreach (StatusCode status in statuses)
            _statuses.Enqueue(status);
    }

    /// <summary>How many of the next calls to abort instead of replying.</summary>
    /// <remarks>A transport fault the resilience handler retries, unlike a <see cref="Fail"/> status.</remarks>
    public int AbortNextCalls { get; set; }

    /// <summary>Back to knowing nothing, so a status one test queued cannot answer the next test's read.</summary>
    public void Reset()
    {
        Addresses.Clear();
        _statuses.Clear();
        _calls.Clear();
        _tokens.Clear();
        AbortNextCalls = 0;
    }

    public async ValueTask InitializeAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        builder.Logging.ClearProviders();
        // Listen rather than ListenLocalhost, which refuses port 0 because it opens two sockets.
        builder.WebHost.ConfigureKestrel(o =>
            o.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(this);

        _app = builder.Build();
        _app.MapGrpcService<Service>();

        await _app.StartAsync(TestContext.Current.CancellationToken);

        Address = new Uri(_app.Urls.Single());
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }

    private sealed class Service(StubOrdering stub) : DeliveryAddresses.DeliveryAddressesBase
    {
        public override Task<GetDeliveryAddressReply> Get(
            GetDeliveryAddressRequest request,
            ServerCallContext context)
        {
            stub._tokens.Enqueue(context.RequestHeaders.GetValue("authorization") ?? "");

            Guid order = Guid.Parse(request.OrderId);
            stub._calls.Enqueue(order);

            if (stub.AbortNextCalls > 0)
            {
                stub.AbortNextCalls--;
                context.GetHttpContext().Abort();

                throw new RpcException(new Status(StatusCode.Aborted, "connection aborted"));
            }

            if (stub._statuses.TryDequeue(out StatusCode status))
                throw new RpcException(new Status(status, "stubbed"));

            if (!stub.Addresses.TryGetValue(order, out StubAddress? address))
                throw new RpcException(new Status(StatusCode.NotFound, "No delivery address for that order."));

            return Task.FromResult(new GetDeliveryAddressReply
            {
                CustomerId = address.CustomerId.ToString(),
                Line1 = address.Line1,
                Line2 = address.Line2 ?? string.Empty,
                City = address.City,
                PostCode = address.PostalCode,
                Country = address.Country
            });
        }
    }
}
