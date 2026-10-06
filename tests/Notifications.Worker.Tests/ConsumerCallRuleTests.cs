using Notifications.TestSupport;
using Common.TestSupport;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Mail;
using Notifications.Infrastructure.Mail;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>ADR-017's rule over this host's composition: a consumer's synchronous call is declared (§9.7).</summary>
public class ConsumerCallRuleTests(ConsumerCallRuleTests.ComposedFactory factory)
    : IClassFixture<ConsumerCallRuleTests.ComposedFactory>
{
    /// <summary>The host as <c>Program</c> composes it, captured before the base factory replaces anything.</summary>
    public sealed class ComposedFactory()
        : NotificationsWorkerFactory(HostSmokeTests.UnreachableSql, HostSmokeTests.UnreachableRabbit)
    {
        private IReadOnlyList<ServiceDescriptor> _composition = [];

        public IReadOnlyList<ServiceDescriptor> Composition
        {
            get
            {
                _ = Services;
                return _composition;
            }
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services => _composition = [.. services]);
            base.ConfigureWebHost(builder);
        }
    }

    private static readonly System.Reflection.Assembly Host = typeof(Program).Assembly;

    [Fact]
    public void Every_synchronous_call_a_consumer_reaches_is_declared()
    {
        // ADR-052's contact read is a worker's, so no consumer spends ADR-017's exception.
        ConsumerCallRule.Offenders(factory.Composition, Host, []).ShouldBeEmpty();
    }

    [Fact]
    public void The_rule_above_is_looking_at_this_hosts_consumers_and_clients()
    {
        // The floor: an offender list is as green over an empty composition.
        ConsumerCallRule.Consumers(factory.Composition, Host).ShouldNotBeEmpty();

        // Named, so a client this host gains is seen here before a consumer can reach it.
        Names(ConsumerCallRule.Clients(factory.Composition, Host)).ShouldBe(
            ["CachingTokenClient", "HttpClient", "IContactSource", "SmtpMailChannel"]);
    }

    public sealed record Probe;

    public sealed class Mailing(IMailChannel mail) : IConsumer<Probe>
    {
        public IMailChannel Mail { get; } = mail;

        public Task Consume(ConsumeContext<Probe> context) => Task.CompletedTask;
    }

    public interface IOpaque;

    public sealed class Concealed : IOpaque;

    public sealed class Hidden(IOpaque opaque) : IConsumer<Probe>
    {
        public IOpaque Opaque { get; } = opaque;

        public Task Consume(ConsumeContext<Probe> context) => Task.CompletedTask;
    }

    public sealed class SelfMailing : IConsumer<Probe>
    {
        public Task Consume(ConsumeContext<Probe> context)
        {
            using MailKit.Net.Smtp.SmtpClient client = new();

            return Task.CompletedTask;
        }
    }

    [Fact]
    public void A_consumer_building_its_own_smtp_client_is_caught()
    {
        IServiceCollection services = new ServiceCollection();
        services.AddScoped<SelfMailing>();

        ConsumerCallRule.Offenders(services, typeof(ConsumerCallRuleTests).Assembly, [])
            .ShouldHaveSingleItem()
            .ShouldContain("SelfMailing reaches SelfMailing (SelfMailing holds one itself)");
    }

    [Fact]
    public void A_consumer_reaching_the_smtp_channel_is_caught()
    {
        IServiceCollection services = new ServiceCollection();
        services.AddScoped<Mailing>();
        services.Add(factory.Composition.Single(d => d.ServiceType == typeof(IMailChannel)));

        ConsumerCallRule.Offenders(services, typeof(ConsumerCallRuleTests).Assembly, [])
            .ShouldHaveSingleItem()
            .ShouldContain("Mailing reaches SmtpMailChannel");
    }

    [Fact]
    public void A_consumer_reaching_an_untyped_factory_is_refused_rather_than_passed()
    {
        IServiceCollection services = new ServiceCollection();
        services.AddScoped<Hidden>();
        services.AddScoped(typeof(IOpaque), _ => new Concealed());

        ConsumerCallRule.Offenders(services, typeof(ConsumerCallRuleTests).Assembly, [])
            .ShouldHaveSingleItem()
            .ShouldContain("IOpaque is built by a factory returning object");
    }

    private static string[] Names(IEnumerable<Type> types) => [.. types.Select(type => type.Name)];
}
