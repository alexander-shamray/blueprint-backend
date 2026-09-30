using Shipping.TestSupport;
using Common.Infrastructure.Outbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>§9.4's refusal of a duplicate <c>FullName</c> at start, made eager by the map's validator.</summary>
public class MessageTypeMapValidatorTests
{
    // Five starts, because a run that loses the race below five times over
    // has something other than a race wrong with it.
    private const int StartAttempts = 5;

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

    /// <summary>Starts hosts naming one assembly twice until one reports its refusal, not a disposal race.</summary>
    private static async Task<Exception> RefusalAsync()
    {
        Exception? refusal = null;

        for (int attempt = 0; attempt < StartAttempts; attempt++)
        {
            // The same assembly twice, the realistic collision: a test host adding one production already named.
            using DuplicateTypeSourceFactory factory = new();

            refusal = (await Record.ExceptionAsync(() => factory.StartAsync()))
                .ShouldNotBeNull("a duplicate persisted name must stop the host, not the first message");

            if (refusal is not ObjectDisposedException { ObjectName: nameof(IServiceProvider) })
                return refusal;
        }

        throw new InvalidOperationException(
            "Every host started here reported a disposed provider, so none of them said why it " +
            "refused to start.",
            refusal);
    }

    private sealed class DuplicateTypeSourceFactory() : ShippingWorkerFactory(
        "Server=shipping-sql.invalid,1433;Database=Shipping;User Id=sa;" +
        "Password=not-a-real-password;Encrypt=False;Connect Timeout=1",
        "amqp://guest:guest@shipping-rabbit.invalid:5672")
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
    public static async Task StartAsync(this ShippingWorkerFactory factory)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        await Task.CompletedTask;
    }
}
