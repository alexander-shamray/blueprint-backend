using Payments.TestSupport;
using Common.Infrastructure.Outbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Payments.Api.Tests;

/// <summary>
/// §9.4 promises that two staged types sharing a <c>FullName</c> fail the host
/// rather than the first message, and `MessageTypeMapValidator` is the only
/// thing that makes it true — the map is registered through a factory, and a
/// factory is lazy.
/// </summary>
/// <remarks>
/// Without this test, deleting that validator leaves the whole suite green:
/// nothing else resolves the map before the dispatcher claims a row, and the
/// dispatcher is removed in every fixture. The regression would then surface
/// on a background thread in a host that had been serving traffic — which is
/// the failure the validator exists to convert into a refusal to start.
/// <para>
/// No containers. The host never opens a connection, because the map is
/// resolved by a hosted service and the constructor throws before anything
/// else starts.
/// </para>
/// </remarks>
public class MessageTypeMapValidatorTests
{
    [Fact]
    public async Task A_duplicate_persisted_name_stops_the_host_from_starting()
    {
        // The same assembly twice, which is the realistic way two entries
        // collide — a test host adding one the production registration
        // already named. Every type in it then appears under one FullName.
        using DuplicateTypeSourceFactory factory = new();

        Exception? failure = await Record.ExceptionAsync(() => factory.StartAsync());

        failure
            .ShouldNotBeNull("a duplicate persisted name must stop the host, not the first message")
            .Message.ShouldContain("cannot distinguish");
    }

    [Fact]
    public async Task A_host_whose_types_are_distinct_starts()
    {
        // The other direction, so the test above cannot pass because the host
        // refuses to start for some unrelated reason. Unreachable
        // infrastructure is fine here: nothing dials it during start-up.
        using HostSmokeTests.UnreachableInfrastructureFactory factory = new();

        await Should.NotThrowAsync(factory.StartAsync);
    }

    private sealed class DuplicateTypeSourceFactory() : PaymentsApiFactory(
        "Server=payments-sql.invalid,1433;Database=Payments;User Id=sa;" +
        "Password=not-a-real-password;Encrypt=False;Connect Timeout=1",
        "amqp://guest:guest@payments-rabbit.invalid:5672")
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
                services
                    .Single(d => d.ServiceType == typeof(MessageTypeSource))
                    .ImplementationInstance
                    .ShouldBeOfType<MessageTypeSource>()
                    .Add(typeof(Common.Contracts.IIntegrationEvent).Assembly));
        }
    }
}

file static class FactoryExtensions
{
    /// <summary>
    /// Starts the host and nothing else. <c>CreateClient</c> would do it too,
    /// and would also make a request — this asserts about start-up alone.
    /// </summary>
    public static async Task StartAsync(this PaymentsApiFactory factory)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        await Task.CompletedTask;
    }
}
