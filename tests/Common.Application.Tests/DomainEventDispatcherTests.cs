using Common.Domain;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Common.Application.Tests;

/// <summary>§7.5's two lanes, asserted at the publisher, since the dispatcher runs no handler (ADR-018).</summary>
public class DomainEventDispatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Nothing_is_staged_when_no_event_was_raised()
    {
        using Harness harness = Harness.For();

        await harness.DispatchAsync();

        harness.Publisher.Staged.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_allow_listed_event_is_staged_on_the_broker_lane()
    {
        using Harness harness = Harness.For(new Mapped(Now));

        await harness.DispatchAsync();

        // The contract, not the domain event (§9.3).
        harness.Publisher.Staged
            .ShouldContain(s => s.Lane == OutboxLane.Broker && s.Message is MappedContract);
    }

    [Fact]
    public async Task An_event_with_a_registered_projection_is_staged_on_the_local_lane()
    {
        using Harness harness = Harness.For(new Mapped(Now));

        await harness.DispatchAsync();

        harness.Publisher.Staged
            .ShouldContain(s => s.Lane == OutboxLane.Local && s.Message is Mapped);
    }

    [Fact]
    public async Task An_event_with_no_projection_stages_no_local_row()
    {
        using Harness harness = Harness.For(new Unmapped(Now));

        await harness.DispatchAsync();

        // No row at all, since §9.4 throws on a row it finds no handler for.
        harness.Publisher.Staged.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_mapper_that_throws_fails_the_command()
    {
        using Harness harness = Harness.For(
            [new Mapped(Now)],
            services => services.AddSingleton<IIntegrationEventMapper>(
                new FakeIntegrationEventMapper { Throws = true }));

        // §9.3's table: a registered mapper that throws fails the command.
        await Should.ThrowAsync<InvalidOperationException>(harness.DispatchAsync);

        harness.Publisher.Staged.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_collector_is_asked_once()
    {
        FakeDomainEventCollector collector = new(new Mapped(Now));

        using Harness harness = Harness.For(
            [],
            services => services.AddSingleton<IDomainEventCollector>(collector));

        await harness.DispatchAsync();

        // Both lanes read one collection, because the real collector clears as it collects (§7.5).
        collector.Collections.ShouldBe(1);
    }

    /// <summary>A real container and a scope, since <see cref="IProjectionRegistry"/> asks one (§7.5).</summary>
    private sealed class Harness : IDisposable
    {
        private ServiceProvider _provider = null!;
        private IServiceScope _scope = null!;

        public FakeIntegrationEventPublisher Publisher { get; } = new();

        public static Harness For(params IDomainEvent[] events) => For(events, configure: null);

        public static Harness For(IDomainEvent[] events, Action<IServiceCollection>? configure)
        {
            Harness harness = new();

            harness._provider = TestContainer.Build(services =>
            {
                services.AddDomainEventDispatcher();
                services.AddSingleton<IDomainEventCollector>(new FakeDomainEventCollector(events));
                services.AddSingleton<IIntegrationEventMapper, FakeIntegrationEventMapper>();
                services.AddSingleton<IIntegrationEventPublisher>(harness.Publisher);

                // Last, because GetRequiredService takes a service type's last registration.
                configure?.Invoke(services);
            });

            harness._scope = harness._provider.CreateScope();
            return harness;
        }

        public Task DispatchAsync() =>
            _scope.ServiceProvider
                .GetRequiredService<IDomainEventDispatcher>()
                .DispatchAsync(TestContext.Current.CancellationToken);

        public void Dispose()
        {
            _scope.Dispose();
            _provider.Dispose();
        }
    }
}
