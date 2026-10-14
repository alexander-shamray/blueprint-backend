using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Notifications.Application.Push;
using Polly;

namespace Notifications.Infrastructure.Push;

/// <summary>The one place that speaks FCM's HTTP v1 API, for Android and the web.</summary>
/// <remarks>
/// The token travels in the body alone and leaves this class in nothing else: a fault becomes a
/// <see cref="PushUnavailableException"/> naming the delivery and the status, with none of FCM's words (ADR-117).
/// </remarks>
internal sealed class FcmPushProvider : IPushProvider
{
    /// <summary>The platform, and the provider tag every count carries.</summary>
    public const string Name = "fcm";

    internal const string ClientName = "notifications-fcm";

    private readonly FcmOptions _options;
    private readonly IHttpClientFactory _clients;
    private readonly FcmAccessTokens _tokens;
    private readonly PushMetrics _metrics;

    public FcmPushProvider(
        IOptions<FcmOptions> options,
        IHttpClientFactory clients,
        FcmAccessTokens tokens,
        PushMetrics metrics,
        TimeProvider clock)
    {
        _options = options.Value;
        _clients = clients;
        _tokens = tokens;
        _metrics = metrics;
        Pipeline = new PushPipeline(Name, FcmHop.Budget, metrics, clock);
    }

    public string Platform => Name;

    public PushPipeline Pipeline { get; }

    public async Task<PushResult> SendAsync(OutboundPush push, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(push);

        try
        {
            return await Pipeline.Pipeline.ExecuteAsync(attempt => AttemptAsync(push, ct, attempt), ct);
        }
        // The pipeline's own refusals, an open circuit or a timeout, carry no word of FCM's.
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
        bool sending = false;

        try
        {
            string accessToken = await _tokens.GetAsync(client, attempt);

            using HttpRequestMessage request = new(
                HttpMethod.Post,
                new Uri(PushAttempts.Directory(_options.BaseUrl!), $"v1/projects/{_options.ProjectId}/messages:send"))
            {
                Content = new StringContent(Message(push).ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            sending = true;
            using HttpResponseMessage response = await client.SendAsync(request, attempt);
            return await AnswerAsync(push, accessToken, response, attempt);
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
    private static JsonObject Message(OutboundPush push) => new()
    {
        ["message"] = new JsonObject
        {
            ["token"] = push.Token,
            ["notification"] = new JsonObject { ["title"] = push.Title, ["body"] = push.Body },
            ["data"] = new JsonObject { ["order"] = push.OrderId.ToString(), ["topic"] = push.TemplateKey },
            ["android"] = new JsonObject { ["collapse_key"] = push.CollapseKey },
            ["webpush"] = new JsonObject { ["headers"] = new JsonObject { ["Topic"] = push.CollapseKey } }
        }
    };

    // FCM's errorCode decides, and the status only where none is given; INVALID_ARGUMENT is a fault, as FCM answers
    // it for a malformed message as well as a malformed token.
    private async Task<PushResult> AnswerAsync(
        OutboundPush push,
        string accessToken,
        HttpResponseMessage response,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return new PushResult.Accepted();

        int status = (int)response.StatusCode;
        string? code = await ErrorCodeAsync(response, ct);

        PushRefusal? refusal = code switch
        {
            "UNREGISTERED" => PushRefusal.Unregistered,
            "SENDER_ID_MISMATCH" => PushRefusal.InvalidToken,
            _ => null
        };

        if (refusal is { } reason)
        {
            _metrics.Refused(Name, reason);
            return new PushResult.Refused(reason);
        }

        PushFault cause = code switch
        {
            "INVALID_ARGUMENT" => PushFault.Rejected,
            "THIRD_PARTY_AUTH_ERROR" => PushFault.Credential,
            "QUOTA_EXCEEDED" or "UNAVAILABLE" => PushFault.Transient,

            // A 500 is no word that nothing was taken, and a push is not idempotent: §9.7 rule 4 retries none.
            "INTERNAL" => PushFault.Unconfirmed,
            _ => status switch
            {
                401 or 403 => PushFault.Credential,
                429 or 503 => PushFault.Transient,
                >= 500 => PushFault.Unconfirmed,
                _ => PushFault.Rejected
            }
        };

        if (status == 401)
            _tokens.Invalidate(accessToken);

        _metrics.Unavailable(Name, cause);
        throw new PushUnavailableException($"Delivery {push.DeliveryId} met FCM's {status}.", cause, status);
    }

    // error.details[] holds FcmError's errorCode; an answer that is not that shape decides by its status alone.
    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using Stream body = await response.Content.ReadAsStreamAsync(ct);
            using JsonDocument answer = await JsonDocument.ParseAsync(body, cancellationToken: ct);

            if (!answer.RootElement.TryGetProperty("error", out JsonElement error) ||
                !error.TryGetProperty("details", out JsonElement details) ||
                details.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement detail in details.EnumerateArray())
            {
                if (detail.ValueKind == JsonValueKind.Object &&
                    detail.TryGetProperty("errorCode", out JsonElement code) &&
                    code.ValueKind == JsonValueKind.String)
                {
                    return code.GetString();
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
