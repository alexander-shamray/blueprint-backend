using System.Net;
using Common.Assistant.Protocol;
using Common.Assistant.Providers;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Common.Assistant.Anthropic.Tests;

/// <summary>
/// Credential status (AI assistant design §8.2): the credential is the SDK's, and the adapter only reports.
/// </summary>
public sealed class StatusTests
{
    private const string Model =
        """{"id":"claude-opus-5-5","type":"model","display_name":"Claude Opus""" +
        """ 5.5","created_at":"2026-08-01T00:00:00Z"}""";

    private static AnthropicProvider Provider(FakeHttp http, TimeProvider time) =>
        new(http.Client(), AnthropicSettings.Default, time);

    [Fact]
    public async Task A_model_the_credential_can_read_is_available_and_names_the_model()
    {
        FakeHttp http = new FakeHttp().Json(Model);

        ProviderStatus status = await Provider(http, TimeProvider.System).StatusAsync(CancellationToken.None);

        status.ShouldBe(new ProviderStatus(true, "claude-opus-5-5", null));
        http.Requests.Single().Uri.AbsolutePath.ShouldBe("/v1/models/claude-opus-5-5");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_credential_the_api_refuses_is_no_credential(HttpStatusCode refusal)
    {
        FakeHttp http = new FakeHttp().Status(refusal);

        ProviderStatus status = await Provider(http, TimeProvider.System).StatusAsync(CancellationToken.None);

        status.ShouldBe(new ProviderStatus(false, "claude-opus-5-5", StatusReason.NoCredential));
    }

    [Fact]
    public async Task A_read_that_cannot_answer_is_unreachable_and_not_a_missing_credential()
    {
        FakeHttp http = new FakeHttp().Fails().Status(HttpStatusCode.BadGateway, "{}");
        AnthropicProvider provider = Provider(http, TimeProvider.System);

        (await provider.StatusAsync(CancellationToken.None)).Reason.ShouldBe(StatusReason.Unreachable);
    }

    [Fact]
    public async Task A_good_answer_is_trusted_for_minutes_and_a_bad_one_is_asked_again_sooner()
    {
        FakeTimeProvider time = new();
        FakeHttp http = new FakeHttp().Json(Model).Status(HttpStatusCode.Unauthorized).Json(Model);
        AnthropicProvider provider = Provider(http, time);

        (await provider.StatusAsync(CancellationToken.None)).Available.ShouldBeTrue();
        time.Advance(TimeSpan.FromMinutes(4));
        (await provider.StatusAsync(CancellationToken.None)).Available.ShouldBeTrue();
        http.Requests.Count.ShouldBe(1);

        // Past the five minutes it asks again, and a refusal is asked again after thirty seconds.
        time.Advance(TimeSpan.FromMinutes(2));
        (await provider.StatusAsync(CancellationToken.None)).Reason.ShouldBe(StatusReason.NoCredential);
        time.Advance(TimeSpan.FromSeconds(20));
        (await provider.StatusAsync(CancellationToken.None)).Reason.ShouldBe(StatusReason.NoCredential);
        http.Requests.Count.ShouldBe(2);

        time.Advance(TimeSpan.FromSeconds(20));
        (await provider.StatusAsync(CancellationToken.None)).Available.ShouldBeTrue();
        http.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Callers_that_arrive_while_a_read_is_out_share_it()
    {
        FakeHttp http = new FakeHttp().Json(Model);
        AnthropicProvider provider = Provider(http, TimeProvider.System);

        ProviderStatus[] all = await Task.WhenAll(
            Enumerable.Range(0, 5).Select(_ => provider.StatusAsync(CancellationToken.None).AsTask()));

        all.ShouldAllBe(s => s.Available);
        http.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_status_never_carries_the_credential()
    {
        FakeHttp http = new FakeHttp().Json(Model);

        ProviderStatus status = await Provider(http, TimeProvider.System).StatusAsync(CancellationToken.None);

        status.ToString().ShouldNotContain("sk-ant-test");
        Provider(http, TimeProvider.System).Id.ShouldBe("anthropic");
    }
}
