using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>A route switched from the mounted file takes effect on a running gateway, and a refusal changes nothing (ADR-118).</summary>
public sealed class RouteSwitchFileTests(StubDestination stub) : IClassFixture<StubDestination>
{
    private const string Suspendable = "privacy-erase";
    private const string Other = "catalog-public";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task A_route_switched_off_in_the_file_answers_503_and_back_on_forwards_again_without_a_restart()
    {
        using TempRouteFile file = new("{}");
        using FileFactory factory = new(stub.Address, file.Path);
        using HttpClient client = factory.CreateClient();

        (await StatusAsync(client, Suspendable)).ShouldBe(HttpStatusCode.NoContent);

        file.Write(Switch(Suspendable, on: true));
        await EventuallyAsync(client, Suspendable, HttpStatusCode.ServiceUnavailable);

        file.Write(Switch(Suspendable, on: false));
        await EventuallyAsync(client, Suspendable, HttpStatusCode.NoContent);
    }

    /// <summary>
    /// ADR-116's second half: a reload that sets the switch on a cluster ADR-104 keeps on is refused whole, so the
    /// route table before it stays, including a switch the same file turned off, and the next good file still applies.
    /// </summary>
    [Fact]
    public async Task A_refused_reload_keeps_the_configuration_before_it_and_the_next_good_one_still_applies()
    {
        using TempRouteFile file = new(Switch(Suspendable, on: true));
        using FileFactory factory = new(stub.Address, file.Path);
        using HttpClient client = factory.CreateClient();
        (await StatusAsync(client, Suspendable)).ShouldBe(HttpStatusCode.ServiceUnavailable);

        using ReloadSignal reloaded = new(factory.Services.GetRequiredService<IConfiguration>());

        // Turns the first route back on, which a partly applied reload would show, and switches a route that may not be.
        file.Write(Switch(Suspendable, on: false, Other, on2: true));
        await reloaded.WaitAsync(Wait);
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        (await StatusAsync(client, Suspendable)).ShouldBe(HttpStatusCode.ServiceUnavailable, "the refused reload applied");
        (await StatusAsync(client, Other)).ShouldBe(HttpStatusCode.NoContent, "the refused reload switched a route off");

        file.Write(Switch(Suspendable, on: false));
        await EventuallyAsync(client, Suspendable, HttpStatusCode.NoContent);
    }

    /// <summary>A misspelt id is a route with no match to YARP, so it is refused like any other bad reload (ADR-118).</summary>
    [Fact]
    public async Task A_name_that_is_no_route_is_refused_and_changes_nothing()
    {
        using TempRouteFile file = new(Switch(Suspendable, on: true));
        using FileFactory factory = new(stub.Address, file.Path);
        using HttpClient client = factory.CreateClient();
        using ReloadSignal reloaded = new(factory.Services.GetRequiredService<IConfiguration>());

        file.Write(Switch(Suspendable, on: false, "privacy-eras", on2: true));
        await reloaded.WaitAsync(Wait);
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        (await StatusAsync(client, Suspendable)).ShouldBe(HttpStatusCode.ServiceUnavailable, "the refused reload applied");
    }

    private static string Switch(string route, bool on, string? second = null, bool on2 = false)
    {
        Dictionary<string, object> routes = new() { [route] = Metadata(on) };

        if (second is not null)
            routes[second] = Metadata(on2);

        return JsonSerializer.Serialize(new { ReverseProxy = new { Routes = routes } });
    }

    private static object Metadata(bool on) =>
        new { Metadata = new Dictionary<string, string> { [SuspendedRoute.MetadataKey] = on ? "true" : "false" } };

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, string routeId)
    {
        RouteConfiguration route = ReadRoute(routeId);
        string path = route.MatchPath.Replace("{**catch-all}", "x", StringComparison.Ordinal);
        using HttpRequestMessage request = new(new HttpMethod(route.Methods is [string method, ..] ? method : "GET"), path);

        request.Headers.Add(TestAuthHandler.UserHeader, "018f4c2e");
        request.Headers.Add(TestAuthHandler.PermissionsHeader, route.AuthorizationPolicy ?? string.Empty);

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        return response.StatusCode;
    }

    private static async Task EventuallyAsync(HttpClient client, string routeId, HttpStatusCode expected)
    {
        DateTime until = DateTime.UtcNow + Wait;
        HttpStatusCode last = await StatusAsync(client, routeId);

        while (last != expected && DateTime.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
            last = await StatusAsync(client, routeId);
        }

        last.ShouldBe(expected, $"{routeId} did not follow the file within {Wait}");
    }

    private static RouteConfiguration ReadRoute(string id)
    {
        using GatewayFactory factory = new();

        return RouteConfiguration.ReadAll(factory.Services.GetRequiredService<IConfiguration>()).Single(r => r.Id == id);
    }

    private sealed class FileFactory(string destination, string path) : StubbedGatewayFactory(destination)
    {
        protected override IEnumerable<KeyValuePair<string, string>> AdditionalSettings =>
        [
            .. base.AdditionalSettings,
            new(RouteSwitchFile.PathKey, path)
        ];
    }

    private sealed class TempRouteFile : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("route-switch-").FullName;

        public TempRouteFile(string content)
        {
            Path = System.IO.Path.Combine(_directory, "routes.json");
            Write(content);
        }

        public string Path { get; }

        public void Write(string content) => File.WriteAllText(Path, content);

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    /// <summary>Completes when the configuration next reloads, which is when YARP has been asked to look again.</summary>
    private sealed class ReloadSignal : IDisposable
    {
        private readonly TaskCompletionSource _reloaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly IDisposable _registration;

        public ReloadSignal(IConfiguration configuration) =>
            _registration = Microsoft.Extensions.Primitives.ChangeToken.OnChange(
                configuration.GetReloadToken,
                () => _reloaded.TrySetResult());

        public Task WaitAsync(TimeSpan timeout) => _reloaded.Task.WaitAsync(timeout, TestContext.Current.CancellationToken);

        public void Dispose() => _registration.Dispose();
    }
}
