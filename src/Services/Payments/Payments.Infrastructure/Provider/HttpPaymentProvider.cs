using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Payments.Application;
using Payments.Application.Provider;
using Polly;

namespace Payments.Infrastructure.Provider;

/// <summary>
/// The one place that knows the provider's wire format (§3.2's anti-corruption
/// layer). Everything it returns is the port's vocabulary.
/// </summary>
internal sealed class HttpPaymentProvider(HttpClient http) : IPaymentProvider
{
    private const string KeyHeader = "Idempotency-Key";

    private sealed record AuthoriseBody(long AmountMinor, string Currency, Guid PayerId);

    private sealed record AuthoriseAnswer(string Status, string? Reference, string? Code);

    private sealed record VoidAnswer(string Status);

    public async Task<AuthorisationResult> AuthoriseAsync(AuthorisationRequest request, CancellationToken ct)
    {
        using HttpRequestMessage message = new(HttpMethod.Post, "v1/authorisations")
        {
            Content = JsonContent.Create(new AuthoriseBody(ToMinor(request.Amount), request.Currency, request.PayerId))
        };
        message.Headers.Add(KeyHeader, request.IdempotencyKey);

        using HttpResponseMessage response = await SendAsync(message, ct);

        // Only the two answers the wire format defines carry a body worth
        // reading; anything else is a provider this adapter does not
        // understand, which is a fault rather than a verdict.
        if (response.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.PaymentRequired))
        {
            throw new PaymentProviderUnavailableException(
                $"The provider answered an authorisation with {(int)response.StatusCode}.");
        }

        AuthoriseAnswer? answer;
        try
        {
            answer = await response.Content.ReadFromJsonAsync<AuthoriseAnswer>(ct);
        }
        catch (JsonException e)
        {
            throw new PaymentProviderUnavailableException(
                "The provider answered an authorisation with no JSON body.", e);
        }

        // The body must agree with its status, and each verdict must carry what
        // it is a verdict about. A contradiction is a provider this adapter does
        // not understand — a fault, never an authorisation or a decline.
        // Longer than ProviderLimits is refused here rather than at the insert:
        // a verdict that cannot be recorded would leave money authorised with
        // no PaymentAuthorised committed for it.
        if (response.StatusCode == HttpStatusCode.PaymentRequired)
        {
            return answer is { Status: "declined", Code: { } code } && Recordable(code, ProviderLimits.MaxReasonLength)
                ? new AuthorisationResult.Declined(code)
                : throw new PaymentProviderUnavailableException(
                    "The provider declined with a body that is not a decline.");
        }

        return answer is { Status: "approved", Reference: { } reference }
               && Recordable(reference, ProviderLimits.MaxReferenceLength)
            ? new AuthorisationResult.Authorised(reference)
            : throw new PaymentProviderUnavailableException(
                "The provider approved with a body that is not an approval.");
    }

    // What PaymentIntent's factories accept: blank is refused there, after the
    // money has moved, so it is refused here before a verdict exists.
    private static bool Recordable(string value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength;

    public async Task VoidAsync(VoidRequest request, CancellationToken ct)
    {
        using HttpRequestMessage message = new(
            HttpMethod.Post, $"v1/authorisations/{Uri.EscapeDataString(request.Reference)}/void");
        message.Headers.Add(KeyHeader, request.IdempotencyKey);

        using HttpResponseMessage response = await SendAsync(message, ct);

        // 200 with "voided" and nothing else: the wire format defines that pair
        // as the void having happened. A 202 is a void still pending, and a 200
        // saying anything else is a provider this adapter does not understand;
        // either would record a Refund and PaymentRefunded before money moved.
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new PaymentProviderUnavailableException(
                $"The provider answered a void with {(int)response.StatusCode}.");
        }

        VoidAnswer? answer;
        try
        {
            answer = await response.Content.ReadFromJsonAsync<VoidAnswer>(ct);
        }
        catch (JsonException e)
        {
            throw new PaymentProviderUnavailableException("The provider answered a void with no JSON body.", e);
        }

        if (answer is not { Status: "voided" })
        {
            throw new PaymentProviderUnavailableException(
                "The provider answered a void with a body that is not a void.");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
    {
        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(message, ct);
        }
        // ExecutionRejectedException is every refusal the pipeline makes on its
        // own account: a timeout, an open circuit, the concurrency limiter.
        catch (Exception e) when (e is HttpRequestException or ExecutionRejectedException
                                      || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new PaymentProviderUnavailableException("The provider did not answer within the budget.", e);
        }

        // A 409 is the provider refusing a key reused with different figures:
        // the same key and different money is a defect, and no retry fixes it.
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

    // The factor PaymentAmounts.MinorUnitPlaces implies, derived rather than
    // written, so the mapper's refusal and this conversion cannot disagree
    // about how many places a payment has.
    private static readonly decimal MinorUnitFactor =
        Enumerable.Repeat(10m, PaymentAmounts.MinorUnitPlaces).Aggregate(1m, (factor, ten) => factor * ten);

    // A figure with more places than minor units is not a payment this
    // platform can state.
    private static long ToMinor(decimal amount)
    {
        decimal minor = amount * MinorUnitFactor;

        if (minor != decimal.Truncate(minor))
            throw new PaymentMismatchException($"An amount of {amount} has more precision than minor units.");

        return decimal.ToInt64(minor);
    }
}
