using System.Net.Http.Json;
using Web.Bff.Endpoints;

namespace Web.Bff.Tests;

/// <summary>The suite's quote calls, their bodies built from <see cref="QuoteRequest"/>, not raw JSON.</summary>
internal static class QuoteCalls
{
    internal const string Path = "/v1/checkout/quote";

    /// <summary>The raw reply, for the tests whose subject is the status.</summary>
    internal static Task<HttpResponseMessage> PostQuote(
        this HttpClient client,
        string currency,
        CancellationToken ct,
        params (Guid ProductId, int Quantity)[] lines) =>
        client.PostQuote(
            new QuoteRequest(currency, [.. lines.Select(line => new QuoteRequestLine(line.ProductId, line.Quantity))]),
            ct);

    /// <summary>The same, for a request the tuple form cannot express.</summary>
    internal static Task<HttpResponseMessage> PostQuote(
        this HttpClient client,
        QuoteRequest request,
        CancellationToken ct) =>
        client.PostAsJsonAsync(Path, request, ct);

    /// <summary>The quote, failing on a non-success status rather than handing the assertions a null.</summary>
    internal static async Task<QuoteResponse?> Quote(
        this HttpClient client,
        string currency,
        CancellationToken ct,
        params (Guid ProductId, int Quantity)[] lines)
    {
        using HttpResponseMessage response = await client.PostQuote(currency, ct, lines);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<QuoteResponse>(ct);
    }
}
