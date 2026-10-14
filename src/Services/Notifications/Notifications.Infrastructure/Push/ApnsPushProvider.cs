using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Notifications.Application.Push;
using OpenTelemetry;
using Polly;

namespace Notifications.Infrastructure.Push;

/// <summary>The one place that speaks APNs' provider API, for iOS.</summary>
/// <remarks>
/// The token is in the request's path, which a client span records whole, so the call runs with instrumentation
/// suppressed and its client logs nothing (ADR-117); a fault names the delivery and status, none of Apple's words.
/// </remarks>
internal sealed class ApnsPushProvider : IPushProvider
{
    /// <summary>The platform, and the provider tag every count carries.</summary>
    public const string Name = "apns";

    internal const string ClientName = "notifications-apns";

    private readonly ApnsOptions _options;
    private readonly IHttpClientFactory _clients;
    private readonly ApnsProviderTokens _tokens;
    private readonly PushMetrics _metrics;

    public ApnsPushProvider(
        IOptions<ApnsOptions> options,
        IHttpClientFactory clients,
        ApnsProviderTokens tokens,
        PushMetrics metrics,
        TimeProvider clock)
    {
        _options = options.Value;
        _clients = clients;
        _tokens = tokens;
        _metrics = metrics;
        Pipeline = new PushPipeline(Name, ApnsHop.Budget, metrics, clock);
    }

    public string Platform => Name;

    public PushPipeline Pipeline { get; }

    public async Task<PushResult> SendAsync(OutboundPush push, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(push);

        using IDisposable unrecorded = SuppressInstrumentationScope.Begin();

        try
        {
            return await Pipeline.Pipeline.ExecuteAsync(attempt => AttemptAsync(push, ct, attempt), ct);
        }
        // The pipeline's own refusals, an open circuit or a timeout, carry no word of Apple's.
        catch (ExecutionRejectedException e)
        {
            throw new PushUnavailableException(
                $"Delivery {push.DeliveryId} was not sent: {e.GetType().Name}.",
                PushFault.Transient,
                httpStatus: null);
        }
    }

    private async ValueTask<PushResult> AttemptAsync(
        OutboundPush push,
        CancellationToken caller,
        CancellationToken attempt)
    {
        HttpClient client = _clients.CreateClient(ClientName);
        Uri baseUrl = PushAttempts.Directory(_options.BaseUrl!);
        bool sending = false;

        try
        {
            // HTTP/2 exactly, which APNs requires; an http base URL exists in Development alone and takes HTTP/1.1.
            using HttpRequestMessage request = new(
                HttpMethod.Post,
                new Uri(baseUrl, $"3/device/{Uri.EscapeDataString(push.Token)}"))
            {
                Version = HttpVersion.Version20,
                VersionPolicy = baseUrl.Scheme == Uri.UriSchemeHttps
                    ? HttpVersionPolicy.RequestVersionExact
                    : HttpVersionPolicy.RequestVersionOrLower,
                Content = new StringContent(Payload(push).ToJsonString(), Encoding.UTF8, "application/json")
            };
            string providerToken = _tokens.Get();
            request.Headers.Authorization = new AuthenticationHeaderValue("bearer", providerToken);
            request.Headers.Add("apns-topic", push.AppId);
            request.Headers.Add("apns-push-type", "alert");
            request.Headers.Add("apns-priority", "10");
            request.Headers.Add("apns-collapse-id", push.CollapseKey);
            request.Headers.Add("apns-id", push.DeliveryId.ToString("D"));

            // Apple drops a push the platform would itself have given up on.
            request.Headers.Add(
                "apns-expiration",
                push.GiveUpAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

            sending = true;
            using HttpResponseMessage response = await client.SendAsync(request, attempt);
            return await AnswerAsync(push, providerToken, response, attempt);
        }
        // The caller's, or an attempt timeout before the request, which the pipeline converts, counts and may retry.
        catch (OperationCanceledException) when (caller.IsCancellationRequested || !sending)
        {
            throw;
        }
        // Classified and counted where the answer was read.
        catch (PushUnavailableException)
        {
            throw;
        }
        catch (Exception e)
        {
            PushFault cause = PushAttempts.Classify(e, sending);
            _metrics.Unavailable(Name, cause);
            throw new PushUnavailableException(
                $"Delivery {push.DeliveryId} met {PushAttempts.Named(e)} {(sending ? "sending" : "authorising")}.",
                cause,
                httpStatus: null);
        }
    }

    // The order and the template key are what the app builds its deep link from; nothing personal rides along.
    private static JsonObject Payload(OutboundPush push) => new()
    {
        ["aps"] = new JsonObject { ["alert"] = new JsonObject { ["title"] = push.Title, ["body"] = push.Body } },
        ["order"] = push.OrderId.ToString(),
        ["topic"] = push.TemplateKey
    };

    private async Task<PushResult> AnswerAsync(
        OutboundPush push,
        string providerToken,
        HttpResponseMessage response,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return new PushResult.Accepted();

        int status = (int)response.StatusCode;
        string? reason = await ReasonAsync(response, ct);

        PushRefusal? refusal = (status, reason) switch
        {
            (410, _) => PushRefusal.Unregistered,
            (400, "BadDeviceToken" or "DeviceTokenNotForTopic") => PushRefusal.InvalidToken,
            _ => null
        };

        if (refusal is { } refused)
        {
            _metrics.Refused(Name, refused);
            return new PushResult.Refused(refused);
        }

        // A 500 is no word that nothing was taken, and a push is not idempotent: §9.7 rule 4 retries none.
        PushFault cause = status switch
        {
            403 => PushFault.Credential,
            429 or 503 => PushFault.Transient,
            >= 500 => PushFault.Unconfirmed,
            _ => PushFault.Rejected
        };

        // Only an expired token is minted again early: Apple refuses renewals inside twenty minutes, and a wrong
        // key or id is not cured by one.
        if (status == 403 && reason == "ExpiredProviderToken")
            _tokens.Invalidate(providerToken);

        _metrics.Unavailable(Name, cause);
        throw new PushUnavailableException($"Delivery {push.DeliveryId} met APNs' {status}.", cause, status);
    }

    // APNs answers {"reason": "..."}; an answer that is not that shape decides by its status alone.
    private static async Task<string?> ReasonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using Stream body = await response.Content.ReadAsStreamAsync(ct);
            using JsonDocument answer = await JsonDocument.ParseAsync(body, cancellationToken: ct);

            return answer.RootElement.ValueKind == JsonValueKind.Object &&
                answer.RootElement.TryGetProperty("reason", out JsonElement reason) &&
                reason.ValueKind == JsonValueKind.String
                    ? reason.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
