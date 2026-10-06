using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Common.Contracts.Payments.V1;
using Payments.Application;
using Payments.Application.Provider;
using Polly;

namespace Payments.Infrastructure.Provider;

/// <summary>The one place that knows the provider's wire format (§3.1's anti-corruption layer).</summary>
internal sealed class HttpPaymentProvider(HttpClient http, ProviderMetrics metrics) : IPaymentProvider
{
    private const string KeyHeader = "Idempotency-Key";

    private sealed record AuthoriseBody(long AmountMinor, string Currency, Guid PayerId);

    private sealed record AuthoriseAnswer(string Status, string? Reference, string? Code);

    private sealed record VoidAnswer(string Status);

    public async Task<AuthorisationResult> AuthoriseAsync(AuthorisationRequest request, CancellationToken ct)
    {
        long amountMinor = ToMinor(request.Amount, request.Currency);

        using HttpRequestMessage message = new(HttpMethod.Post, "v1/authorisations")
        {
            Content = JsonContent.Create(new AuthoriseBody(amountMinor, request.Currency, request.PayerId))
        };
        message.Headers.Add(KeyHeader, request.IdempotencyKey);

        using HttpResponseMessage response = await SendAsync(message, ct);

        // Any status the wire format does not define is a fault, not a verdict.
        if (response.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.PaymentRequired))
            throw Unavailable($"The provider answered an authorisation with {(int)response.StatusCode}.");

        AuthoriseAnswer? answer;
        try
        {
            answer = await response.Content.ReadFromJsonAsync<AuthoriseAnswer>(ct);
        }
        catch (JsonException e)
        {
            throw Unavailable("The provider answered an authorisation with no JSON body.", e);
        }

        // A body that contradicts its status is a fault. An over-long value is refused here, not at the insert,
        // where it would leave money authorised with no PaymentAuthorised committed for it.
        if (response.StatusCode == HttpStatusCode.PaymentRequired)
        {
            return answer is { Status: "declined", Code: { } code } && Recordable(code, ProviderLimits.MaxReasonLength)
                ? new AuthorisationResult.Declined(code)
                : throw Unavailable("The provider declined with a body that is not a decline.");
        }

        return answer is { Status: "approved", Reference: { } reference } &&
            Recordable(reference, PaymentLimits.MaxReferenceLength)
            ? new AuthorisationResult.Authorised(reference)
            : throw Unavailable("The provider approved with a body that is not an approval.");
    }

    // A blank reference or reason records nothing, so it is refused before a verdict exists.
    private static bool Recordable(string value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength;

    public async Task VoidAsync(VoidRequest request, CancellationToken ct)
    {
        using HttpRequestMessage message = new(
            HttpMethod.Post,
            $"v1/authorisations/{Uri.EscapeDataString(request.Reference)}/void");
        message.Headers.Add(KeyHeader, request.IdempotencyKey);

        using HttpResponseMessage response = await SendAsync(message, ct);

        // Only 200 with "voided" means the void happened; anything else would record a Refund before money moved.
        if (response.StatusCode != HttpStatusCode.OK)
            throw Unavailable($"The provider answered a void with {(int)response.StatusCode}.");

        VoidAnswer? answer;
        try
        {
            answer = await response.Content.ReadFromJsonAsync<VoidAnswer>(ct);
        }
        catch (JsonException e)
        {
            throw Unavailable("The provider answered a void with no JSON body.", e);
        }

        if (answer is not { Status: "voided" })
            throw Unavailable("The provider answered a void with a body that is not a void.");
    }

    // Counts an answer the pipeline passed but the adapter cannot read; the pipeline counts its own failures.
    private PaymentProviderUnavailableException Unavailable(string message, Exception? inner = null)
    {
        metrics.Unavailable();
        return inner is null ? new(message) : new(message, inner);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
    {
        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(message, ct);
        }
        // ExecutionRejectedException is the pipeline's own refusal: a timeout, an open circuit, the limiter.
        catch (Exception e) when (e is HttpRequestException or ExecutionRejectedException ||
            (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new PaymentProviderUnavailableException("The provider did not answer within the budget.", e);
        }

        // A 409 is a key reused with different figures: a defect no retry fixes (§9.8).
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            response.Dispose();
            throw new PaymentMismatchException("The provider refused a reused idempotency key with different figures.");
        }

        if (ProviderAttemptCounter.IsTransient(response.StatusCode))
        {
            HttpStatusCode status = response.StatusCode;
            response.Dispose();
            throw new PaymentProviderUnavailableException($"The provider answered {(int)status} after every retry.");
        }

        return response;
    }

    // PaymentAmounts', so this and the mapper's refusal cannot disagree.
    private static long ToMinor(decimal amount, string currency) =>
        PaymentAmounts.ToMinorUnits(amount, currency) ??
        throw new PaymentMismatchException($"An amount of {amount} {currency} is no whole number of minor units.");
}
