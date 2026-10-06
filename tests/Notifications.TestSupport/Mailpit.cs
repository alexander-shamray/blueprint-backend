using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Notifications.TestSupport;

/// <summary>The relay, in a container, read back through its HTTP API (§12.7).</summary>
/// <remarks>The tag is the Compose unit's, held equal by a test, so a person watches this sink (§14.1).</remarks>
public sealed class Mailpit : IAsyncDisposable
{
    public const string Image = "axllent/mailpit:v1.31.3";

    public const int SmtpPort = 1025;

    public const int ApiPort = 8025;

    /// <summary>The self-signed certificate's name, so its refusal is the trust's and not the name's.</summary>
    public const string CertificateName = "localhost";

    /// <summary>How long a message may take to appear; a deadline, not a sleep.</summary>
    private static readonly TimeSpan ArrivalDeadline = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IContainer _container;
    private HttpClient? _api;

    private Mailpit(IContainer container) => _container = container;

    /// <summary>Plain SMTP with the Chaos API on, as Compose runs it.</summary>
    public static Mailpit Plain() => new(Builder().Build());

    /// <summary>Plain SMTP on a host port the caller chose, which a stop and a start keep (§14.1).</summary>
    public static Mailpit PlainOn(int smtpHostPort) => new(Builder(smtpHostPort).Build());

    /// <summary>STARTTLS under a certificate Mailpit signs itself, which no trust store holds.</summary>
    public static Mailpit SelfSigned() =>
        new(
            Builder()
                .WithEnvironment("MP_SMTP_TLS_CERT", $"sans:{CertificateName}")
                .WithEnvironment("MP_SMTP_TLS_KEY", $"sans:{CertificateName}")
                .Build());

    public string Host => _container.Hostname;

    public int Port => _container.GetMappedPublicPort(SmtpPort);

    private HttpClient Api => _api ?? throw new InvalidOperationException("Mailpit has not been started.");

    public async Task StartAsync(CancellationToken ct)
    {
        await _container.StartAsync(ct);

        // A restarted container maps its API afresh, so the client is rebuilt on every start.
        _api?.Dispose();
        _api = new HttpClient
        {
            BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(ApiPort)}/api/v1/")
        };
    }

    /// <summary>Stops the relay as an outage would; <see cref="StartAsync"/> brings it back.</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        _api?.Dispose();
        _api = null;
        await _container.StopAsync(ct);
    }

    /// <summary>No messages and no Chaos trigger, so a test sees only what it sent and what it staged.</summary>
    public async Task ResetAsync(CancellationToken ct)
    {
        using HttpResponseMessage cleared = await Api.DeleteAsync("messages", ct);
        cleared.EnsureSuccessStatusCode();
        await ChaosAsync(new { }, ct);
    }

    /// <summary>Every recipient answered with <paramref name="code"/>, as a relay refusing them would.</summary>
    public Task RefuseRecipientsAsync(int code, CancellationToken ct) =>
        ChaosAsync(new { Recipient = new { ErrorCode = code, Probability = 100 } }, ct);

    /// <summary>The sender answered with <paramref name="code"/>, as a relay refusing this deployment would.</summary>
    public Task RefuseSendersAsync(int code, CancellationToken ct) =>
        ChaosAsync(new { Sender = new { ErrorCode = code, Probability = 100 } }, ct);

    public async Task<IReadOnlyList<MailpitSummary>> MessagesAsync(CancellationToken ct)
    {
        MailpitPage? page = await Api.GetFromJsonAsync<MailpitPage>("messages", Json, ct);
        return page?.Messages ?? throw new InvalidOperationException("Mailpit answered its message list with no body.");
    }

    /// <summary>The one message the sink holds, waited for, read whole.</summary>
    public async Task<MailpitMessage> SingleAsync(CancellationToken ct)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + ArrivalDeadline;

        while (DateTimeOffset.UtcNow < deadline)
        {
            IReadOnlyList<MailpitSummary> messages = await MessagesAsync(ct);
            if (messages.Count > 1)
                throw new InvalidOperationException($"Mailpit holds {messages.Count} messages where one was sent.");

            if (messages.Count == 1)
            {
                return await Api.GetFromJsonAsync<MailpitMessage>(
                    $"message/{Uri.EscapeDataString(messages[0].Id)}",
                    Json,
                    ct) ??
                    throw new InvalidOperationException("Mailpit answered a message read with no body.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }

        throw new TimeoutException($"No message reached Mailpit within {ArrivalDeadline}.");
    }

    /// <summary>The sink's messages once it holds <paramref name="count"/>, waited for; more is a failure.</summary>
    public async Task<IReadOnlyList<MailpitSummary>> WaitForAsync(int count, CancellationToken ct)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + ArrivalDeadline;

        while (DateTimeOffset.UtcNow < deadline)
        {
            IReadOnlyList<MailpitSummary> messages = await MessagesAsync(ct);
            if (messages.Count > count)
            {
                throw new InvalidOperationException(
                    $"Mailpit holds {messages.Count} messages where {count} were sent.");
            }

            if (messages.Count == count)
                return messages;

            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }

        throw new TimeoutException($"{count} message(s) did not reach Mailpit within {ArrivalDeadline}.");
    }

    /// <summary>One message, read whole.</summary>
    public async Task<MailpitMessage> MessageAsync(string id, CancellationToken ct) =>
        await Api.GetFromJsonAsync<MailpitMessage>($"message/{Uri.EscapeDataString(id)}", Json, ct) ??
        throw new InvalidOperationException("Mailpit answered a message read with no body.");

    /// <summary>A message's headers as the relay received them, by name in any case.</summary>
    public async Task<IReadOnlyDictionary<string, string[]>> HeadersAsync(string id, CancellationToken ct)
    {
        Dictionary<string, string[]> headers =
            await Api.GetFromJsonAsync<Dictionary<string, string[]>>(
                $"message/{Uri.EscapeDataString(id)}/headers",
                Json,
                ct) ??
            throw new InvalidOperationException("Mailpit answered a header read with no body.");

        return new Dictionary<string, string[]>(headers, StringComparer.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        _api?.Dispose();
        await _container.DisposeAsync();
    }

    private async Task ChaosAsync(object triggers, CancellationToken ct)
    {
        using HttpResponseMessage response = await Api.PutAsJsonAsync("chaos", triggers, ct);
        response.EnsureSuccessStatusCode();
    }

    private static ContainerBuilder Builder(int? smtpHostPort = null) =>
        (smtpHostPort is { } port
                ? new ContainerBuilder().WithPortBinding(port, SmtpPort)
                : new ContainerBuilder().WithPortBinding(SmtpPort, assignRandomHostPort: true))
            .WithImage(Image)
            .WithPortBinding(ApiPort, assignRandomHostPort: true)
            .WithEnvironment("MP_ENABLE_CHAOS", "true")
            .WithWaitStrategy(
                Wait
                    .ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(r => r.ForPort((ushort)ApiPort).ForPath("/readyz")));

    private sealed record MailpitPage(int Total, IReadOnlyList<MailpitSummary> Messages);
}

public sealed record MailpitAddress(string Name, string Address);

public sealed record MailpitSummary(string Id, string MessageId, string Subject, IReadOnlyList<MailpitAddress> To);

public sealed record MailpitMessage(
    string Id,
    string MessageId,
    string Subject,
    string Text,
    MailpitAddress From,
    IReadOnlyList<MailpitAddress> To);
