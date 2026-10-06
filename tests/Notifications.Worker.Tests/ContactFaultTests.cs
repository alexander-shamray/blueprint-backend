using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Contacts;
using Notifications.Infrastructure.Contacts;
using Notifications.TestSupport;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The contact read's transient answers, a host each, since the breaker they fill opens.</summary>
public sealed class ContactFaultTests : IAsyncLifetime
{
    private WireMockServer _keycloak = null!;

    private ContactSourceTests.PatientFactory _factory = null!;

    private readonly Guid _customer = Guid.CreateVersion7();

    // A warmed stub and a patient host, so a cold first read is no fault; the stalled owner below meets the total.
    public async ValueTask InitializeAsync()
    {
        _keycloak = await ContactSourceTests.StartStubAsync();
        _factory = new ContactSourceTests.PatientFactory(_keycloak.Urls[0] + "/");
    }

    public ValueTask DisposeAsync()
    {
        _factory.Dispose();
        _keycloak.Stop();
        return ValueTask.CompletedTask;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Path => $"/admin/realms/{NotificationsWorkerFactory.LocalRealm}/users/{_customer:D}";

    private const string User = """{"enabled":true,"email":"aigerim@example.test"}""";

    private int Calls => _keycloak.LogEntries.Count(e => e.RequestMessage!.Path == Path);

    private async Task<ContactLookup> ReadAsync(NotificationsWorkerFactory? host = null)
    {
        await using AsyncServiceScope scope = (host ?? _factory).Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IContactSource>().GetAsync(_customer, Ct);
    }

    [Fact]
    public async Task A_server_fault_is_retried_inside_the_budget_then_thrown_uncounted()
    {
        using OutboundCount counted = OutboundCounter.ContactRefused(_factory.Services);
        _keycloak.Given(Request.Create().WithPath(Path).UsingGet()).RespondWith(Response.Create().WithStatusCode(503));

        await Should.ThrowAsync<HttpRequestException>(() => ReadAsync());

        Calls.ShouldBe(ContactHop.MaxRetryAttempts + 1);
        counted.Value.ShouldBe(0, "an outage is not a decision anybody took");
    }

    [Fact]
    public async Task A_fault_that_clears_is_retried_and_each_attempt_asks_for_a_token_again()
    {
        _keycloak
            .Given(Request.Create().WithPath(Path).UsingGet())
            .InScenario("flaky")
            .WillSetStateTo("recovered")
            .RespondWith(Response.Create().WithStatusCode(503));
        _keycloak
            .Given(Request.Create().WithPath(Path).UsingGet())
            .InScenario("flaky")
            .WhenStateIs("recovered")
            .RespondWith(
                Response
                    .Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(User));

        (await ReadAsync()).ShouldBeOfType<ContactLookup.Found>();

        Calls.ShouldBe(2);

        // The credential handler inside the pipeline runs once per attempt (§11.5).
        _keycloak.LogEntries
            .Select(e => e.RequestMessage!.Headers!["Authorization"][0])
            .Distinct()
            .Count()
            .ShouldBe(2);
    }

    [Fact]
    public async Task An_answer_larger_than_the_bound_is_an_attempt_the_pipeline_retries_and_then_a_fault()
    {
        _keycloak
            .Given(Request.Create().WithPath(Path).UsingGet())
            .RespondWith(
                Response
                    .Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("{\"enabled\":true,\"email\":\"" + new string('a', ContactHop.MaxAnswerBytes) + "\"}"));

        await Should.ThrowAsync<HttpRequestException>(() => ReadAsync());

        Calls.ShouldBe(ContactHop.MaxRetryAttempts + 1);
    }

    [Fact]
    public async Task A_stalled_owner_is_given_up_on_within_the_total_budget()
    {
        _keycloak
            .Given(Request.Create().WithPath(Path).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(User).WithDelay(TimeSpan.FromSeconds(10)));
        using OutboundCount counted = OutboundCounter.ContactRefused(_factory.Services);
        long started = Stopwatch.GetTimestamp();

        Exception thrown = await Should.ThrowAsync<Exception>(() => ReadAsync());

        thrown.ShouldNotBeOfType<ContactSourceRefusedException>();
        Stopwatch.GetElapsedTime(started).ShouldBeLessThan(ContactHop.TotalRequestTimeout + TimeSpan.FromSeconds(2));
        counted.Value.ShouldBe(0, "an outage is not a refusal");
    }

    [Fact]
    public async Task An_open_circuit_makes_no_call_at_all()
    {
        _keycloak.Given(Request.Create().WithPath(Path).UsingGet()).RespondWith(Response.Create().WithStatusCode(503));

        // The breaker sits inside the retry, so one read is MaxRetryAttempts + 1 attempts toward the throughput.
        while (Calls < ContactHop.CircuitBreakerMinimumThroughput)
            await Should.ThrowAsync<Exception>(() => ReadAsync());

        int before = Calls;

        await Should.ThrowAsync<Exception>(() => ReadAsync());

        Calls.ShouldBe(before, "once open, it refuses without a request leaving this process");
    }

    [Fact]
    public async Task An_unreachable_owner_throws_rather_than_answering()
    {
        using NotificationsWorkerFactory dead = new(Unreachable.Sql, Unreachable.Rabbit);

        // A slow NXDOMAIN can meet the timeout first, so the kind of fault is not asserted, only that it is one.
        Exception thrown = await Should.ThrowAsync<Exception>(() => ReadAsync(dead));

        thrown.ShouldNotBeOfType<ContactSourceRefusedException>();
    }
}
