using System.Data;
using Common.Application;
using Common.Infrastructure.Idempotency;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using Common.Infrastructure.Outbox;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>§9.5's purge composes a statement per registered table, so a service with no outbox resolves it.</summary>
public class RetentionPurgeServiceTests
{
    [Fact]
    public void It_resolves_for_a_service_that_registers_no_outbox_table()
    {
        // ValidateOnBuild, as every host builds: the outbox is the one table a pure consumer does not have (§4.1).
        ServiceCollection services = Registrations(new List<string>(), withOutbox: false);

        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        provider.GetRequiredService<RetentionPurgeService>().ShouldNotBeNull();
    }

    [Fact]
    public async Task It_composes_no_outbox_statement_and_reports_no_outbox_rows_without_an_outbox_table()
    {
        List<string> statements = [];
        using ServiceProvider provider = Registrations(statements, withOutbox: false).BuildServiceProvider();

        (int outbox, int inbox, int idempotency) =
            await provider.GetRequiredService<RetentionPurgeService>().PurgeAsync(CancellationToken.None);

        outbox.ShouldBe(0);
        inbox.ShouldBe(0);
        idempotency.ShouldBe(0);
        statements.ShouldNotBeEmpty();
        statements.ShouldNotContain(sql => sql.Contains("ProcessedAt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task It_still_purges_the_outbox_when_the_service_registers_the_table()
    {
        List<string> statements = [];
        using ServiceProvider provider = Registrations(statements, withOutbox: true).BuildServiceProvider();

        await provider.GetRequiredService<RetentionPurgeService>().PurgeAsync(CancellationToken.None);

        statements.ShouldContain(sql => sql.Contains("[probe].", StringComparison.Ordinal)
            && sql.Contains("ProcessedAt", StringComparison.Ordinal));
    }

    private static ServiceCollection Registrations(List<string> statements, bool withOutbox)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new InboxTable("probe"));
        services.AddSingleton(new IdempotencyMarkerTable("probe"));
        services.AddSingleton(Substitute.For<IIdempotencyStore>());
        services.AddSingleton(new RetentionPolicy());
        services.AddSingleton<RetentionPurgeService>();
        services.AddSingleton(RecordingFactory(statements));

        if (withOutbox)
            services.AddSingleton(new OutboxTable("probe"));

        return services;
    }

    // Each pass gets a connection that records its statements and affects no rows.
    private static IDbConnectionFactory RecordingFactory(List<string> statements)
    {
        IDbConnectionFactory factory = Substitute.For<IDbConnectionFactory>();
        factory.Create().Returns(_ => new RecordingDbConnection(statements));
        return factory;
    }
}
