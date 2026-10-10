using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Privacy.Application.ErasureRequests;
using Privacy.Infrastructure;
using Shouldly;
using Xunit;

namespace Privacy.Api.Tests;

/// <summary>§15.4's promise that the host refuses to start on a holders-and-service-level it could not raise with.</summary>
public class PrivacyOptionsTests
{
    private static readonly TimeSpan Month = TimeSpan.FromDays(30);

    private static readonly string[] Holders = ["ordering", "payments", "shipping", "notifications", "bff"];

    private static ValidateOptionsResult Validate(string[] responders, TimeSpan slo) =>
        new PrivacyOptionsValidator().Validate(
            name: null,
            new PrivacyOptions { Responders = responders, CompletionSlo = slo });

    [Fact]
    public void The_five_holders_and_a_month_are_accepted()
    {
        Validate(Holders, Month).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void No_holders_are_refused_because_no_request_could_ever_close()
    {
        ValidateOptionsResult result = Validate([], Month);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldNotBeNull().ShouldContain("at least one holder");
    }

    [Theory]
    [InlineData("Ordering")]
    [InlineData("")]
    [InlineData("order,ing")]
    [InlineData("-ordering")]
    public void A_name_outside_the_shape_is_refused_and_named(string name)
    {
        ValidateOptionsResult result = Validate(["payments", name], Month);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldNotBeNull().ShouldContain($"'{name}'");
    }

    [Fact]
    public void A_holder_named_twice_is_refused()
    {
        Validate(["ordering", "payments", "ordering"], Month).FailureMessage.ShouldNotBeNull().ShouldContain("twice");
    }

    [Fact]
    public void A_set_wider_than_the_column_is_refused()
    {
        string[] wide = [.. Enumerable.Range(0, 13).Select(i => $"{(char)('a' + i)}{new string('x', 31)}")];

        Validate(wide, Month).FailureMessage.ShouldNotBeNull().ShouldContain("characters");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_service_level_that_is_missing_or_not_positive_is_refused(int days)
    {
        ValidateOptionsResult result = Validate(Holders, TimeSpan.FromDays(days));

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldNotBeNull().ShouldContain("CompletionSlo");
    }

    [Fact]
    public void Every_failure_is_reported_at_once_so_a_deployment_fixes_them_in_one_pass()
    {
        ValidateOptionsResult result = Validate([], TimeSpan.Zero);

        result.Failures.ShouldNotBeNull().Count().ShouldBe(2);
    }

    [Fact]
    public void A_host_with_an_impossible_service_level_does_not_start()
    {
        using WebApplicationFactory<Program> factory = new HostSmokeTests.AuthenticatedUnreachableFactory()
            .WithWebHostBuilder(web => web.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Privacy:CompletionSlo"] = "00:00:00" })));

        Exception refused = Should.Throw<Exception>(() => factory.CreateClient());

        refused.ToString().ShouldContain("CompletionSlo", Case.Sensitive, "ValidateOnStart names the setting");
    }

    [Fact]
    public void A_host_with_a_holder_that_is_not_a_name_does_not_start()
    {
        using WebApplicationFactory<Program> factory = new HostSmokeTests.AuthenticatedUnreachableFactory()
            .WithWebHostBuilder(web => web.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Privacy:Responders:0"] = "Not A Name" })));

        Exception refused = Should.Throw<Exception>(() => factory.CreateClient());

        refused.ToString().ShouldContain("Privacy:Responders", Case.Sensitive, "ValidateOnStart names the setting");
    }
}
