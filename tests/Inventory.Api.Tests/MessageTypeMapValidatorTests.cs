using Inventory.TestSupport;
using Common.Infrastructure.Outbox;
using Common.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Inventory.Api.Tests;

/// <summary>§9.4's refusal of a duplicate <c>FullName</c> at start, made eager by the map's validator.</summary>
public class MessageTypeMapValidatorTests
{
    [Fact]
    public async Task A_duplicate_persisted_name_stops_the_host_from_starting()
    {
        Exception refusal = await RefusalAsync();

        refusal.Message.ShouldContain("cannot distinguish");
    }

    [Fact]
    public async Task A_host_whose_types_are_distinct_starts()
    {
        // The other direction, so the test above cannot pass on an unrelated refusal to start.
        using HostSmokeTests.UnreachableInfrastructureFactory factory = new();

        await Should.NotThrowAsync(factory.StartAsync);
    }

    // The same assembly twice, the realistic collision: a test host adding one production already named.
    private static Task<Exception> RefusalAsync() =>
        StartRefusal.AskAsync(() => new DuplicateTypeSourceFactory(), factory => factory.StartAsync());

    private sealed class DuplicateTypeSourceFactory() : InventoryApiFactory(
        "Server=inventory-sql.invalid,1433;Database=Inventory;User Id=sa;" +
        "Password=not-a-real-password;Encrypt=False;Connect Timeout=1",
        "amqp://guest:guest@inventory-rabbit.invalid:5672")
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
    /// <summary>Starts the host and nothing else, where <c>CreateClient</c> would also make a request.</summary>
    public static async Task StartAsync(this InventoryApiFactory factory)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        await Task.CompletedTask;
    }
}
