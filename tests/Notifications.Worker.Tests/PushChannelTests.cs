using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Notifications.Application.Push;
using Notifications.Infrastructure.Push;
using Notifications.TestSupport;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Shouldly;
using WireMock;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>Both adapters over a real HTTP server loading the simulator's own mappings (§12.7).</summary>
public sealed class PushChannelTests : IClassFixture<PushSimulator>, IDisposable
{
    private readonly PushSimulator _simulator;
    private readonly ServiceProvider _provider;
    private readonly IPushChannel _channel;

    public PushChannelTests(PushSimulator simulator)
    {
        _simulator = simulator;
        _simulator.Reset();
        _provider = PushSettings.Registered(Environments.Development, PushSettings.Both(simulator.BaseUrl))
            .BuildServiceProvider();
        _channel = _provider.GetRequiredService<IPushChannel>();
    }

    public void Dispose() => _provider.Dispose();

    [Theory]
    [InlineData(FcmPushProvider.Name)]
    [InlineData(ApnsPushProvider.Name)]
    public async Task Any_other_token_is_accepted(string platform)
    {
        PushResult result = await _channel.SendAsync(PushSettings.To(platform, "device-1"), Ct);

        result.ShouldBeOfType<PushResult.Accepted>();
        Sends(platform).ShouldBe(1);
    }

    [Theory]
    [InlineData(FcmPushProvider.Name, "unregistered-1", PushRefusal.Unregistered)]
    [InlineData(ApnsPushProvider.Name, "unregistered-1", PushRefusal.Unregistered)]
    [InlineData(FcmPushProvider.Name, "invalid-1", PushRefusal.InvalidToken)]
    [InlineData(ApnsPushProvider.Name, "invalid-1", PushRefusal.InvalidToken)]
    public async Task A_refusal_is_an_answer_with_its_reason(string platform, string token, PushRefusal reason)
    {
        PushResult result = await _channel.SendAsync(PushSettings.To(platform, token), Ct);

        result.ShouldBe(new PushResult.Refused(reason));
        Sends(platform).ShouldBe(1, "a refusal is the provider's last word, so nothing repeats it");
    }

    [Theory]
    [InlineData(FcmPushProvider.Name, "rejected-1", PushFault.Rejected, 400, 1)]
    [InlineData(ApnsPushProvider.Name, "rejected-1", PushFault.Rejected, 400, 1)]
    [InlineData(FcmPushProvider.Name, "unavailable-1", PushFault.Transient, 503, 2)]
    [InlineData(ApnsPushProvider.Name, "unavailable-1", PushFault.Transient, 503, 2)]
    public async Task A_fault_throws_its_cause_and_status_and_only_a_transient_one_is_retried(
        string platform,
        string token,
        PushFault cause,
        int status,
        int requests)
    {
        PushUnavailableException thrown = await Should.ThrowAsync<PushUnavailableException>(() =>
            _channel.SendAsync(PushSettings.To(platform, token), Ct));

        thrown.Cause.ShouldBe(cause);
        thrown.HttpStatus.ShouldBe(status);
        thrown.InnerException.ShouldBeNull("a provider's own exception can quote the request");
        thrown.Message.ShouldNotContain(token, Case.Sensitive, "ADR-117: no exception's text holds a token");
        Sends(platform).ShouldBe(requests, "only a request the provider took nothing in is retried (§9.7)");
    }

    [Theory]
    [InlineData(FcmPushProvider.Name)]
    [InlineData(ApnsPushProvider.Name)]
    public async Task An_attempt_that_times_out_once_sent_is_unconfirmed_and_not_retried(string platform)
    {
        Stopwatch elapsed = Stopwatch.StartNew();

        PushUnavailableException thrown = await Should.ThrowAsync<PushUnavailableException>(() =>
            _channel.SendAsync(PushSettings.To(platform, "slow-1"), Ct));

        thrown.Cause.ShouldBe(PushFault.Unconfirmed);

        // A retry would hold a second attempt to its own timeout; the provider may have delivered it (§9.7).
        elapsed.Elapsed.ShouldBeLessThan(FcmHop.AttemptTimeout * 2, "a push once started is not retried");

        // WireMock logs a request when its delayed answer ends, which must land in this test and not the next.
        await HeldUntilLoggedAsync(platform);
        Sends(platform).ShouldBe(1);
    }

    [Theory]
    [InlineData(FcmPushProvider.Name)]
    [InlineData(ApnsPushProvider.Name)]
    public async Task A_500_is_unconfirmed_and_not_retried(string platform)
    {
        Answer(platform, "internal-1", 500, platform == FcmPushProvider.Name
            ? new { error = new { code = 500, details = new[] { new { errorCode = "INTERNAL" } } } }
            : new { reason = "InternalServerError" });

        PushUnavailableException thrown = await Should.ThrowAsync<PushUnavailableException>(() =>
            _channel.SendAsync(PushSettings.To(platform, "internal-1"), Ct));

        thrown.Cause.ShouldBe(PushFault.Unconfirmed, "a 500 says nothing about whether the push was taken");
        Sends(platform).ShouldBe(1, "§9.7 rule 4: a push is not idempotent, so only a 429 or a 503 is retried");
    }

    [Theory]
    [InlineData("ExpiredProviderToken", true)]
    [InlineData("InvalidProviderToken", false)]
    public async Task Only_an_expired_apns_token_is_minted_again_inside_its_lifetime(string reason, bool again)
    {
        Answer(ApnsPushProvider.Name, "refused-1", 403, new { reason });

        PushUnavailableException thrown = await Should.ThrowAsync<PushUnavailableException>(() =>
            _channel.SendAsync(PushSettings.To(ApnsPushProvider.Name, "refused-1"), Ct));
        await _channel.SendAsync(PushSettings.To(ApnsPushProvider.Name, "device-6"), Ct);

        thrown.Cause.ShouldBe(PushFault.Credential);
        string?[] bearers = [.. Requests(ApnsPushProvider.Name).Select(r => Header(r, "Authorization"))];
        bearers.Length.ShouldBe(2);
        (bearers[0] != bearers[1]).ShouldBe(again, "Apple refuses a renewal inside twenty minutes but for expiry");
    }

    [Fact]
    public async Task An_fcm_message_carries_the_collapse_key_and_the_deep_link_and_nothing_more()
    {
        OutboundPush push = PushSettings.To(FcmPushProvider.Name, "device-2");

        await _channel.SendAsync(push, Ct);

        IRequestMessage request = Requests(FcmPushProvider.Name).ShouldHaveSingleItem();
        request.Path.ShouldBe("/v1/projects/commerce-test/messages:send");
        Header(request, "Authorization").ShouldBe("Bearer simulated-access");

        using JsonDocument body = JsonDocument.Parse(request.Body!);
        JsonElement message = body.RootElement.GetProperty("message");
        message.GetProperty("token").GetString().ShouldBe("device-2");
        message.GetProperty("notification").GetProperty("title").GetString().ShouldBe(push.Title);
        message.GetProperty("data").GetProperty("order").GetString().ShouldBe(push.OrderId.ToString());
        message.GetProperty("data").GetProperty("topic").GetString().ShouldBe(push.TemplateKey);
        message.GetProperty("data").EnumerateObject().Count().ShouldBe(2, "no amount, address or name rides along");
        message.GetProperty("android").GetProperty("collapse_key").GetString().ShouldBe(push.CollapseKey);
        message.GetProperty("webpush").GetProperty("headers").GetProperty("Topic").GetString()
            .ShouldBe(push.CollapseKey);
    }

    [Fact]
    public async Task An_apns_push_names_its_app_its_delivery_and_its_give_up_instant()
    {
        OutboundPush push = PushSettings.To(ApnsPushProvider.Name, "device-3");

        await _channel.SendAsync(push, Ct);

        IRequestMessage request = Requests(ApnsPushProvider.Name).ShouldHaveSingleItem();
        request.Path.ShouldBe("/3/device/device-3");
        Header(request, "apns-topic").ShouldBe(PushSettings.ApnsApp);
        Header(request, "apns-push-type").ShouldBe("alert");
        Header(request, "apns-priority").ShouldBe("10");
        Header(request, "apns-collapse-id").ShouldBe(push.CollapseKey);
        Header(request, "apns-id").ShouldBe(push.DeliveryId.ToString("D"));
        Header(request, "apns-expiration")
            .ShouldBe(push.GiveUpAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        Header(request, "Authorization").ShouldStartWith("bearer ");
    }

    [Fact]
    public async Task One_providers_open_breaker_parks_its_platform_and_not_the_other()
    {
        IPushProvider apns = _provider.GetServices<IPushProvider>().Single(p => p.Platform == ApnsPushProvider.Name);

        // Each send is two faulted attempts, so half the minimum throughput, rounded up, fills a window past it.
        for (int i = 0; i < (ApnsHop.CircuitBreakerMinimumThroughput + 1) / 2; i++)
        {
            await Should.ThrowAsync<PushUnavailableException>(() =>
                _channel.SendAsync(PushSettings.To(ApnsPushProvider.Name, $"unavailable-{i}"), Ct));
        }

        apns.Pipeline.IsOpen.ShouldBeTrue();
        int before = Sends(ApnsPushProvider.Name);

        PushUnavailableException parked = await Should.ThrowAsync<PushUnavailableException>(() =>
            _channel.SendAsync(PushSettings.To(ApnsPushProvider.Name, "device-4"), Ct));

        parked.Cause.ShouldBe(PushFault.Transient);
        Sends(ApnsPushProvider.Name).ShouldBe(before, "an open breaker sends nothing");
        (await _channel.SendAsync(PushSettings.To(FcmPushProvider.Name, "device-4"), Ct))
            .ShouldBeOfType<PushResult.Accepted>("FCM's platform still sends");
    }

    [Fact]
    public async Task No_log_line_holds_a_token()
    {
        using CapturedLogs logs = new();
        using ServiceProvider provider = PushSettings
            .Registered(Environments.Development, PushSettings.Both(_simulator.BaseUrl), logs: logs)
            .BuildServiceProvider();
        IPushChannel channel = provider.GetRequiredService<IPushChannel>();

        foreach (string platform in new[] { FcmPushProvider.Name, ApnsPushProvider.Name })
        {
            await channel.SendAsync(PushSettings.To(platform, "device-logged"), Ct);
            await channel.SendAsync(PushSettings.To(platform, "unregistered-logged"), Ct);
            await Should.ThrowAsync<PushUnavailableException>(() =>
                channel.SendAsync(PushSettings.To(platform, "rejected-logged"), Ct));
        }

        // The control: a client the factory logs as it does by default writes the request's address, token and all.
        HttpClient ordinary = provider.GetRequiredService<IHttpClientFactory>().CreateClient("control");
        using (await ordinary.PostAsync(new Uri($"{_simulator.BaseUrl}3/device/device-control"), null, Ct))
        {
        }

        logs.Everything.ShouldContain(line => line.Contains("device-control"));
        logs.Everything.ShouldAllBe(line => !line.Contains("-logged"), "ADR-117: a token is never a log attribute");
    }

    [Fact]
    public async Task No_exported_span_holds_an_apns_token()
    {
        List<Activity> exported = [];
        using (TracerProvider tracing = Sdk.CreateTracerProviderBuilder()
                   .AddHttpClientInstrumentation()
                   .AddInMemoryExporter(exported)
                   .Build())
        {
            await _channel.SendAsync(PushSettings.To(FcmPushProvider.Name, "device-fcm-span"), Ct);
            await _channel.SendAsync(PushSettings.To(ApnsPushProvider.Name, "device-apns-span"), Ct);
            tracing.ForceFlush();
        }

        // The control: FCM's call, whose token is in its body alone, is traced, so the suppression is APNs' own.
        exported.ShouldContain(a => Tags(a).Any(t => t.Contains("messages:send")));
        exported.ShouldAllBe(a => !Tags(a).Any(t => t.Contains("device-apns-span") || t.Contains("/3/device/")));
    }

    [Fact]
    public async Task Fcms_access_token_is_exchanged_once_and_again_only_before_it_expires()
    {
        FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
        using ServiceProvider provider = PushSettings
            .Registered(Environments.Development, PushSettings.Both(_simulator.BaseUrl), clock)
            .BuildServiceProvider();
        IPushChannel channel = provider.GetRequiredService<IPushChannel>();

        await channel.SendAsync(PushSettings.To(FcmPushProvider.Name, "device-5"), Ct);
        await channel.SendAsync(PushSettings.To(FcmPushProvider.Name, "device-5"), Ct);
        Exchanges().ShouldBe(1);

        // The simulator answers an hour, so the renewal falls due the margin before it.
        clock.Advance(TimeSpan.FromHours(1) - FcmAccessTokens.RenewalMargin);
        await channel.SendAsync(PushSettings.To(FcmPushProvider.Name, "device-5"), Ct);
        Exchanges().ShouldBe(2);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IEnumerable<string> Tags(Activity activity) =>
        activity.TagObjects.Select(t => $"{t.Key}={t.Value}").Append(activity.DisplayName);

    private static string? Header(IRequestMessage request, string name) =>
        request.Headers?
            .FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase))
            .Value?
            .FirstOrDefault();

    private IRequestMessage[] Requests(string platform) =>
    [
        .. _simulator.Server.LogEntries
            .Select(e => e.RequestMessage!)
            .Where(r => platform == FcmPushProvider.Name
                ? r.Path.EndsWith("/messages:send", StringComparison.Ordinal)
                : r.Path.StartsWith("/3/device/", StringComparison.Ordinal))
    ];

    private int Sends(string platform) => Requests(platform).Length;

    // One answer the simulator's files do not script, ahead of them, for this test alone.
    private void Answer(string platform, string token, int status, object body)
    {
        IRequestBuilder request = platform == FcmPushProvider.Name
            ? Request.Create()
                .WithPath(new RegexMatcher("^/v1/projects/[^/]+/messages:send$"))
                .WithBody(new RegexMatcher($"\"token\":\"{token}\""))
                .UsingPost()
            : Request.Create().WithPath($"/3/device/{token}").UsingPost();

        _simulator.Server
            .Given(request)
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(status).WithBodyAsJson(body));
    }

    private async Task HeldUntilLoggedAsync(string platform)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow.AddSeconds(15);

        while (Sends(platform) == 0 && DateTimeOffset.UtcNow < until)
            await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
    }

    private int Exchanges() => _simulator.Server.LogEntries.Count(e => e.RequestMessage!.Path == "/token");
}
