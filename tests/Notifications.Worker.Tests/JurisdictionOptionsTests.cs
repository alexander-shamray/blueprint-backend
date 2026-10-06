using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Notifications.Application.Contacts;
using Notifications.Application.Rendering;
using Notifications.Infrastructure.Jurisdiction;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>ADR-053 rule 1: a jurisdiction the files, the runtime or ADR-052 cannot serve fails the start.</summary>
public sealed class JurisdictionOptionsTests
{
    private static readonly Dictionary<string, string?> Invented = new()
    {
        ["Jurisdiction:Languages:0"] = "kk",
        ["Jurisdiction:Languages:1"] = "en",
        ["Jurisdiction:TimeZone"] = NotificationsWorkerFactory.InventedTimeZone,
        ["Jurisdiction:LogRetention"] = NotificationsWorkerFactory.InventedLogRetention,
        ["Jurisdiction:ContactRetention"] = NotificationsWorkerFactory.InventedContactRetention,
        ["Jurisdiction:OrderRetention"] = NotificationsWorkerFactory.InventedOrderRetention
    };

    /// <summary>The production binding and validator over a configuration of the test's own.</summary>
    private static ServiceProvider Validating(Dictionary<string, string?> values)
    {
        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        services.AddSingleton(new ContactOptions());
        services
            .AddOptions<NotificationsJurisdictionOptions>()
            .BindConfiguration(NotificationsJurisdictionOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<NotificationsJurisdictionOptions>, NotificationsJurisdictionValidator>();

        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> With(string key, string? value)
    {
        Dictionary<string, string?> values = new(Invented) { [key] = value };
        if (value is null)
            values.Remove(key);

        return values;
    }

    [Fact]
    public void An_invented_jurisdiction_binds_its_language_set_in_its_own_order()
    {
        // ADR-053 rule 2's control, without which the refusals below could pass against a class nothing satisfies.
        using ServiceProvider provider = Validating(Invented);

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());

        NotificationsJurisdictionOptions bound =
            provider.GetRequiredService<IOptions<NotificationsJurisdictionOptions>>().Value;
        bound.Languages.ShouldBe(["kk", "en"]);
        bound.ContactRetention.ShouldBe(TimeSpan.FromDays(17));
    }

    [Theory]
    [InlineData("Jurisdiction:LogRetention", "", "LogRetention")]
    [InlineData("Jurisdiction:ContactRetention", "00:00:00", "ContactRetention")]
    [InlineData("Jurisdiction:OrderRetention", "3651.00:00:00", "OrderRetention")]
    [InlineData("Jurisdiction:TimeZone", null, "TimeZone")]
    [InlineData("Jurisdiction:TimeZone", "Mars/Olympus_Mons", "Mars/Olympus_Mons")]
    [InlineData("Jurisdiction:TimeZone", "Central Asia Standard Time", "Central Asia Standard Time")]
    [InlineData("Jurisdiction:Languages:1", "de", "Templates/order-placed.v1.de.txt")]
    [InlineData("Jurisdiction:Languages:1", "kk", "'kk' twice")]
    [InlineData("Jurisdiction:ContactRetention", "23:59:59", "StaleCeiling")]
    public void Each_refusal_names_what_to_fix(string key, string? value, string named)
    {
        using ServiceProvider provider = Validating(With(key, value));

        OptionsValidationException thrown = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        thrown.Message.ShouldContain(named);
    }

    [Fact]
    public void One_start_names_a_refused_annotation_and_a_refused_rule_together()
    {
        Dictionary<string, string?> values = With("Jurisdiction:LogRetention", "00:00:00");
        values["Jurisdiction:TimeZone"] = "Mars/Olympus_Mons";
        using ServiceProvider provider = Validating(values);

        OptionsValidationException thrown = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        thrown.Message.ShouldContain("LogRetention");
        thrown.Message.ShouldContain("Mars/Olympus_Mons");
    }

    [Fact]
    public void An_empty_language_set_is_refused()
    {
        Dictionary<string, string?> values = new(Invented);
        values.Remove("Jurisdiction:Languages:0");
        values.Remove("Jurisdiction:Languages:1");
        using ServiceProvider provider = Validating(values);

        Should
            .Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate())
            .Message.ShouldContain("Languages");
    }

    [Fact]
    public void A_contact_window_equal_to_the_stale_ceiling_passes()
    {
        // The floor is the ceiling itself: a row is served to the last instant ADR-052 allows, then deleted.
        using ServiceProvider provider = Validating(With("Jurisdiction:ContactRetention", "1.00:00:00"));

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void The_host_starts_with_the_invented_jurisdiction_and_renders_in_its_order()
    {
        // The control for the host-level refusals below: the same unreachable hosts start.
        using NotificationsWorkerFactory factory = new(Unreachable.Sql, Unreachable.Rabbit);

        Should.NotThrow(() => factory.CreateClient());

        factory.Services.GetRequiredService<TemplateRenderer>().Languages.ShouldBe(["kk", "en"]);
    }

    [Theory]
    [InlineData("LogRetention", "00:00:00")]
    [InlineData("ContactRetention", "23:59:59")]
    [InlineData("OrderRetention", "")]
    [InlineData("TimeZone", "Mars/Olympus_Mons")]
    public void The_host_refuses_to_start_on_a_window_or_zone_it_cannot_use(string member, string value)
    {
        using NotificationsWorkerFactory factory = new(
            Unreachable.Sql,
            Unreachable.Rabbit,
            timeZone: member == "TimeZone" ? value : NotificationsWorkerFactory.InventedTimeZone,
            logRetention: member == "LogRetention" ? value : NotificationsWorkerFactory.InventedLogRetention,
            contactRetention:
                member == "ContactRetention" ? value : NotificationsWorkerFactory.InventedContactRetention,
            orderRetention: member == "OrderRetention" ? value : NotificationsWorkerFactory.InventedOrderRetention);

        // The factory builds the host on first use, so the throw arrives here rather than at construction.
        Should.Throw<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public void The_host_refuses_to_start_on_a_language_the_templates_do_not_ship()
    {
        using NotificationsWorkerFactory factory = new(Unreachable.Sql, Unreachable.Rabbit, languages: ["kk", "de"]);

        Should.Throw<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public void The_host_refuses_to_start_on_an_empty_language_set()
    {
        using NotificationsWorkerFactory factory = new(Unreachable.Sql, Unreachable.Rabbit, languages: []);

        Should.Throw<Exception>(() => factory.CreateClient());
    }
}
