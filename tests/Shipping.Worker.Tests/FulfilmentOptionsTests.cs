using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shipping.Infrastructure;
using Shipping.Infrastructure.Fulfilment;
using Shipping.TestSupport;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>ADR-052's give-up age is the deployment's, so a missing or impossible one fails start (§15.4).</summary>
public sealed class FulfilmentOptionsTests
{
    private const string UnreachableSql =
        "Server=sql.invalid;Database=Shipping;User Id=x;Password=x;TrustServerCertificate=true";

    private const string UnreachableRabbit = "amqp://shipping-svc:x@rabbit.invalid:5672";

    [Theory]
    [InlineData("")]
    [InlineData("00:00:00")]
    [InlineData("00:59:59")]
    public void The_host_refuses_to_start_without_a_usable_give_up_age(string value)
    {
        using ShippingWorkerFactory factory = new(UnreachableSql, UnreachableRabbit, giveUpAge: value);

        // The factory builds the host on first use, and a host refusing to
        // start races its disposal, so no exception type is asserted.
        Should.Throw<Exception>(() => factory.CreateClient());
    }

    [Theory]
    [InlineData("")]
    [InlineData("00:59:59")]
    [InlineData("3651.00:00:00")]
    [InlineData("90.00:00:00")]
    public void The_failure_names_the_give_up_age(string value)
    {
        using ServiceProvider provider = Bound(value);

        OptionsValidationException thrown = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        thrown.Message.ShouldContain(nameof(FulfilmentOptions.GiveUpAge));
    }

    [Fact]
    public void The_invented_age_is_accepted_and_bound()
    {
        // The control for the theories above, so they cannot pass against a class nothing satisfies.
        using ServiceProvider provider = Bound(ShippingWorkerFactory.InventedGiveUpAge);

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());

        provider.GetRequiredService<IOptions<FulfilmentOptions>>().Value.GiveUpAge.ShouldBe(
            TimeSpan.Parse(ShippingWorkerFactory.InventedGiveUpAge, System.Globalization.CultureInfo.InvariantCulture));
    }

    // The production validator over the same binding: the host theory above
    // is what still fails if AddShippingInfrastructure drops the registration.
    private static ServiceProvider Bound(string value)
    {
        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?> { [$"{FulfilmentOptions.SectionName}:GiveUpAge"] = value })
                .Build());

        services
            .AddOptions<FulfilmentOptions>()
            .BindConfiguration(FulfilmentOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<FulfilmentOptions>, AnnotatedOptionsValidator<FulfilmentOptions>>();

        return services.BuildServiceProvider();
    }
}
