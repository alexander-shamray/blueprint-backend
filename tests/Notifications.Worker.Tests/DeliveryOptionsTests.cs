using System.Globalization;
using Common.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Notifications.Infrastructure.Delivery;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>ADR-052's give-up age is the deployment's, so a missing, impossible or unguarded one fails start.</summary>
public sealed class DeliveryOptionsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("00:00:00")]
    [InlineData("00:59:59")]
    [InlineData("7.00:00:01")]
    public void The_host_refuses_to_start_without_a_usable_give_up_age(string value)
    {
        // The last is past RetentionPolicy's default InboxWindow, so it proves the host checks the registered one.
        using NotificationsWorkerFactory factory = new(Unreachable.Sql, Unreachable.Rabbit, giveUpAge: value);

        // The factory builds the host on first use, and a host refusing to start races its disposal.
        Should.Throw<Exception>(() => factory.CreateClient());
    }

    [Theory]
    [InlineData("")]
    [InlineData("00:59:59")]
    [InlineData("3651.00:00:00")]
    public void The_failure_names_the_give_up_age(string value)
    {
        using ServiceProvider provider = Bound(value, new RetentionPolicy());

        Should
            .Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate())
            .Message.ShouldContain(nameof(DeliveryOptions.GiveUpAge));
    }

    [Fact]
    public void An_inbox_window_shorter_than_the_give_up_age_is_refused_and_both_are_named()
    {
        using ServiceProvider provider =
            Bound("1.00:00:00", new RetentionPolicy { InboxWindow = TimeSpan.FromHours(23) });

        string message = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate()).Message;

        message.ShouldContain(nameof(RetentionPolicy.InboxWindow));
        message.ShouldContain(nameof(DeliveryOptions.GiveUpAge));
    }

    [Fact]
    public void An_inbox_window_equal_to_the_give_up_age_passes()
    {
        using ServiceProvider provider =
            Bound("1.00:00:00", new RetentionPolicy { InboxWindow = TimeSpan.FromDays(1) });

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void The_day_a_deployment_is_given_fits_the_registered_inbox_window()
    {
        // Compose's value and the chart's default, against the policy the host registers.
        using ServiceProvider provider = Bound("1.00:00:00", new RetentionPolicy());

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void The_invented_age_is_accepted_and_bound()
    {
        // The control for the refusals above, so they cannot pass against a class nothing satisfies.
        using ServiceProvider provider = Bound(NotificationsWorkerFactory.InventedGiveUpAge, new RetentionPolicy());

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());

        provider
            .GetRequiredService<IOptions<DeliveryOptions>>().Value.GiveUpAge
            .ShouldBe(TimeSpan.Parse(NotificationsWorkerFactory.InventedGiveUpAge, CultureInfo.InvariantCulture));
    }

    // The production binding and validator; the host theory above is what still fails if the registration goes.
    private static ServiceProvider Bound(string value, RetentionPolicy retention)
    {
        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?> { [$"{DeliveryOptions.SectionName}:GiveUpAge"] = value })
                .Build());
        services.AddSingleton(retention);

        services
            .AddOptions<DeliveryOptions>()
            .BindConfiguration(DeliveryOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DeliveryOptions>, DeliveryOptionsValidator>();

        return services.BuildServiceProvider();
    }
}
