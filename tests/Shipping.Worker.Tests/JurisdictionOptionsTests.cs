using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shipping.Infrastructure;
using Shipping.Infrastructure.Retention;
using Shipping.TestSupport;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>ADR-053 rule 1: a missing or impossible statutory window is §15.4's failure at start.</summary>
public sealed class JurisdictionOptionsTests
{
    private const string UnreachableSql =
        "Server=sql.invalid;Database=Shipping;User Id=x;Password=x;TrustServerCertificate=true";

    private const string UnreachableRabbit = "amqp://shipping-svc:x@rabbit.invalid:5672";

    private static readonly string[] Members = ["AddressRetention", "TrackingRetention"];

    [Theory]
    [InlineData("AddressRetention", "")]
    [InlineData("TrackingRetention", "")]
    [InlineData("AddressRetention", "00:00:00")]
    [InlineData("TrackingRetention", "00:00:00")]
    public void The_host_refuses_to_start_without_a_usable_window(string member, string value)
    {
        using ShippingWorkerFactory factory = new(
            UnreachableSql,
            UnreachableRabbit,
            addressRetention: string.Equals(member, "AddressRetention", StringComparison.Ordinal)
                ? value
                : ShippingWorkerFactory.InventedAddressRetention,
            trackingRetention: string.Equals(member, "TrackingRetention", StringComparison.Ordinal)
                ? value
                : ShippingWorkerFactory.InventedTrackingRetention);

        // The factory builds the host on first use, so the throw arrives here
        // rather than at construction.
        Should.Throw<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public void The_host_starts_with_the_invented_windows()
    {
        // The control for the theory above: the same unreachable hosts start,
        // so a refusal there is the window's and not the infrastructure's.
        using ShippingWorkerFactory factory = new(
            UnreachableSql,
            UnreachableRabbit,
            addressRetention: ShippingWorkerFactory.InventedAddressRetention,
            trackingRetention: ShippingWorkerFactory.InventedTrackingRetention);

        Should.NotThrow(() => factory.CreateClient());
    }

    [Theory]
    [InlineData("AddressRetention", "")]
    [InlineData("TrackingRetention", "")]
    [InlineData("AddressRetention", "00:00:00")]
    [InlineData("TrackingRetention", "3651.00:00:00")]
    public void Each_window_is_named_in_the_failure(string member, string value)
    {
        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(Members.Select(name => new KeyValuePair<string, string?>(
                $"{ShippingJurisdictionOptions.SectionName}:{name}",
                string.Equals(name, member, StringComparison.Ordinal) ? value : "11.00:00:00")))
            .Build());

        // The production validator over the same binding; the theory above still fails if the registration is dropped.
        services
            .AddOptions<ShippingJurisdictionOptions>()
            .BindConfiguration(ShippingJurisdictionOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<ShippingJurisdictionOptions>,
            AnnotatedOptionsValidator<ShippingJurisdictionOptions>>();

        using ServiceProvider provider = services.BuildServiceProvider();

        OptionsValidationException thrown = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        thrown.Message.ShouldContain(member);
    }

    [Fact]
    public void An_invented_jurisdiction_satisfies_both_windows()
    {
        // ADR-053 rule 2's control, without which the theories above could pass against a class nothing satisfies.
        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{ShippingJurisdictionOptions.SectionName}:AddressRetention"] =
                        ShippingWorkerFactory.InventedAddressRetention,
                    [$"{ShippingJurisdictionOptions.SectionName}:TrackingRetention"] =
                        ShippingWorkerFactory.InventedTrackingRetention
                })
            .Build());

        services
            .AddOptions<ShippingJurisdictionOptions>()
            .BindConfiguration(ShippingJurisdictionOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<ShippingJurisdictionOptions>,
            AnnotatedOptionsValidator<ShippingJurisdictionOptions>>();

        using ServiceProvider provider = services.BuildServiceProvider();

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());

        ShippingJurisdictionOptions bound =
            provider.GetRequiredService<IOptions<ShippingJurisdictionOptions>>().Value;

        bound.AddressRetention.ShouldBe(TimeSpan.Parse(
            ShippingWorkerFactory.InventedAddressRetention,
            System.Globalization.CultureInfo.InvariantCulture));
        bound.TrackingRetention.ShouldBe(TimeSpan.Parse(
            ShippingWorkerFactory.InventedTrackingRetention,
            System.Globalization.CultureInfo.InvariantCulture));
    }
}
