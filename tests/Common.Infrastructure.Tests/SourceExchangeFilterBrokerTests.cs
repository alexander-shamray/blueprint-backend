using System.Collections.Concurrent;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary><see cref="SourceExchangeFilter{T}"/> on a real broker, where a delivery names its exchange.</summary>
/// <remarks>
/// Each test names its own exchanges under the <c>payments-</c> prefix, the one <c>payments-svc</c> may bind. The
/// delayed routes need ADR-021's plugin, which this stock image lacks, so Ordering's and Payments' suites hold them.
/// </remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class SourceExchangeFilterBrokerTests(RabbitFixture fixture)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    public sealed record Granted(Guid Id);

    public sealed record Forged(Guid Id);

    public sealed class GrantedConsumer : IConsumer<Granted>
    {
        public static readonly ConcurrentBag<Guid> Seen = [];

        public Task Consume(ConsumeContext<Granted> context)
        {
            Seen.Add(context.Message.Id);
            return Task.CompletedTask;
        }
    }

    public sealed class ForgedConsumer : IConsumer<Forged>
    {
        public static readonly ConcurrentBag<Guid> Seen = [];

        public Task Consume(ConsumeContext<Forged> context)
        {
            Seen.Add(context.Message.Id);
            return Task.CompletedTask;
        }
    }

    // The skipped queue read without the filter, which is the operator's view of a refused delivery.
    public sealed class SkippedConsumer : IConsumer<Forged>
    {
        public static readonly ConcurrentBag<Guid> Seen = [];

        public Task Consume(ConsumeContext<Forged> context)
        {
            Seen.Add(context.Message.Id);
            return Task.CompletedTask;
        }
    }

    private sealed record Names(string Queue, string Granted, string Forged)
    {
        public static Names Fresh()
        {
            string run = Guid.NewGuid().ToString("N");
            return new(
                $"payments-source-check-{run}",
                $"payments-source-granted-{run}",
                $"payments-source-forged-{run}");
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(Budget);
        while (!condition())
            await Task.Delay(25, timeout.Token);
    }

    private ServiceProvider Build(Names names)
    {
        Uri broker = new(fixture.BrokerAddress);
        ServiceCollection services = new();
        services.AddLogging();
        services.AddMassTransit(x =>
        {
            x.AddConsumer<GrantedConsumer>();
            x.AddConsumer<ForgedConsumer>();
            x.AddConsumer<SkippedConsumer>();
            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(broker);

                cfg.Message<Granted>(m => m.SetEntityName(names.Granted));
                cfg.Message<Forged>(m => m.SetEntityName(names.Forged));

                // Two publishers' types on one queue, the shape ADR-191 found on the saga's and on four others.
                cfg.ReceiveEndpoint(
                    names.Queue,
                    e =>
                    {
                        e.UseSourceExchangeCheck(context);
                        e.ConfigureConsumer<GrantedConsumer>(context);
                        e.ConfigureConsumer<ForgedConsumer>(context);
                    });

                cfg.ReceiveEndpoint(
                    names.Queue + "_skipped",
                    e =>
                    {
                        e.ConfigureConsumeTopology = false;
                        e.ConfigureConsumer<SkippedConsumer>(context);
                    });
            });
        });

        return services.BuildServiceProvider(validateScopes: true);
    }

    private async Task Run(Names names, Func<IBus, Task> body)
    {
        await using ServiceProvider provider = Build(names);
        IBusControl bus = provider.GetRequiredService<IBusControl>();
        await bus.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Until(() => bus.CheckHealth().Status == BusHealthStatus.Healthy);
            await body(bus);
        }
        finally
        {
            await bus.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_type_named_on_another_types_exchange_is_refused_and_parked_in_skipped()
    {
        // ADR-191's forgery: an exchange the sender may write, bound to a queue consuming a type it may not.
        Names names = Names.Fresh();
        await Run(names, async bus =>
        {
            Guid forged = Guid.CreateVersion7();
            Guid genuine = Guid.CreateVersion7();

            ISendEndpoint grantedExchange = await bus.GetSendEndpoint(new Uri($"exchange:{names.Granted}"));
            await grantedExchange.Send(new Forged(forged), TestContext.Current.CancellationToken);
            await bus.Publish(new Forged(genuine), TestContext.Current.CancellationToken);

            // Parked means decided, so the consumer's bag is read once both have arrived somewhere.
            await Until(() => SkippedConsumer.Seen.Contains(forged) && ForgedConsumer.Seen.Contains(genuine));
            ForgedConsumer.Seen.ShouldNotContain(
                forged,
                "the envelope chose the consumer, and the exchange it came through granted no such type");
        });
    }

    [Fact]
    public async Task A_type_published_through_its_own_exchange_is_consumed()
    {
        Names names = Names.Fresh();
        await Run(names, async bus =>
        {
            Guid id = Guid.CreateVersion7();
            await bus.Publish(new Granted(id), TestContext.Current.CancellationToken);

            await Until(() => GrantedConsumer.Seen.Contains(id));
        });
    }

    [Fact]
    public async Task A_send_to_the_endpoints_queue_is_consumed_whatever_its_type()
    {
        // A command's route, and the dead-letter tool's replay, which publishes to the endpoint's exchange.
        Names names = Names.Fresh();
        await Run(names, async bus =>
        {
            Guid id = Guid.CreateVersion7();
            ISendEndpoint queue = await bus.GetSendEndpoint(new Uri($"queue:{names.Queue}"));
            await queue.Send(new Forged(id), TestContext.Current.CancellationToken);

            await Until(() => ForgedConsumer.Seen.Contains(id));
        });
    }
}
