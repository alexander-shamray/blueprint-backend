using System.Text.Encodings.Web;
using System.Text.Json;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Shipping.Application.Carrier;
using Shipping.Infrastructure.Carrier;
using Shipping.Infrastructure.Fulfilment;
using Shipping.TestSupport;
using Shouldly;
using WireMock.Server;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>Fulfilment cases ending in a carrier fault, each on its own host, as the breaker they fill opens.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class FulfilmentFaultTests : IAsyncLifetime
{
    /// <summary>Short of <c>CarrierHop.AttemptTimeout</c>, so the answer is journalled within the attempt.</summary>
    private static readonly TimeSpan StallPerAttempt = TimeSpan.FromSeconds(3);

    private readonly ServiceFixture _fixture;
    private readonly FulfilmentSteps _steps;
    private readonly WireMockServer _carrier = ServiceFixture.StartCarrier();
    private readonly ShippingWorkerFactory _host;

    public FulfilmentFaultTests(ServiceFixture fixture)
    {
        _fixture = fixture;
        _steps = new FulfilmentSteps(fixture);
        _host = fixture.NewWorkerHost(_carrier.Urls[0] + "/");
    }

    public ValueTask InitializeAsync() => new(_fixture.ResetAsync());

    public ValueTask DisposeAsync()
    {
        try
        {
            _host.Dispose();
        }
        finally
        {
            _carrier.Stop();
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_carrier_that_is_down_backs_the_row_off_and_keeps_it()
    {
        Guid order = await ConfirmAsync("SIM-DOWN");

        // From the engine's clock and before the pass, since the failure stamps NextAttemptAt from SYSDATETIMEOFFSET().
        DateTimeOffset before = await _steps.DatabaseNowAsync();

        (await PassAsync()).ShouldBe(0);

        (await _steps.StatusAsync(order)).ShouldBe("Pending");
        (await _steps.AttemptsAsync(order)).ShouldBe(1);
        (await _steps.NextAttemptAtAsync(order)).ShouldBeGreaterThanOrEqualTo(
            before.AddSeconds(5),
            "the dispatcher's ladder is 2^min(Attempts, 8) x 5 s, so the first backoff is at least five seconds");
        FulfilmentSteps.BookingCalls(_carrier).ShouldBe(
            CarrierHop.MaxRetryAttempts + 1,
            "the pipeline retries a 503 inside the one call");
    }

    [Fact]
    public async Task Two_passes_overlapping_claim_one_row_once()
    {
        Guid order = await ConfirmAsync("050000");

        // WireMock.Net journals a request once its response is produced, hence the stall short of the attempt timeout.
        using IDisposable stalled = ServiceFixture.CarrierAnswers(
            _carrier,
            FulfilmentSteps.BookingPath,
            503,
            method: "POST",
            delay: StallPerAttempt);

        Task<int> first = PassAsync();
        await FulfilmentSteps.WaitUntil(() => Task.FromResult(FulfilmentSteps.BookingCalls(_carrier) >= 1));
        (await PassAsync()).ShouldBe(0, "the second pass skipped a leased row");

        // Zero for the first pass too: the row's catch backs the fault off inside CarrierHop's total.
        (await first).ShouldBe(0);

        (await _steps.StatusAsync(order)).ShouldBe("Pending");
        (await _steps.AttemptsAsync(order)).ShouldBe(1, "one pass failed on the row, and the other never took it");
        FulfilmentSteps.BookingCalls(_carrier).ShouldBe(
            CarrierHop.MaxRetryAttempts + 1,
            "every request in the journal belongs to the first pass");
    }

    [Fact]
    public async Task No_log_line_holds_the_address()
    {
        // Two faults on one row, each logged with its exception: the owner's before the address, the carrier's after.
        Guid faulted = await _steps.ConfirmAsync(FulfilmentSteps.Kazakh with { PostalCode = "SIM-DOWN" });
        _fixture.Ordering.Fail(StatusCode.Unavailable);
        (await PassAsync()).ShouldBe(0, "the owner's outage fails the row before its address is read");

        await _steps.ConfirmAsync(FulfilmentSteps.Kazakh);
        (await PassAsync()).ShouldBe(1, "the row confirmed second is the one not backed off");

        await _steps.ClearBackoffAsync(faulted);
        (await PassAsync()).ShouldBe(0, "the carrier's fault fails the row with its address in hand");
        (await _steps.AttemptsAsync(faulted)).ShouldBe(2);

        // Raw and JSON-escaped, as the adapter's serialiser escapes non-ASCII; both hosts, as the collection's consumes
        // the events this one's passes follow.
        string[] parts = ["Абай", "пәтер", "Алматы"];
        string[] needles =
            [.. parts, .. parts.Select(p => JsonEncodedText.Encode(p, JavaScriptEncoder.Default).ToString())];
        string[] captured = [.. _fixture.CapturedLogs.Everything, .. _host.CapturedLogs.Everything];

        captured.ShouldNotContain(line => needles.Any(needle => line.Contains(needle, StringComparison.Ordinal)));
        captured.ShouldContain(
            line => line.Contains(FulfilmentSteps.BookingPath, StringComparison.Ordinal),
            "a capture that missed the booking the address travelled on would assert nothing");
        captured.ShouldContain(
            line => line.StartsWith("Grpc.Core.RpcException", StringComparison.Ordinal),
            "a capture that dropped the owner's exception would search none of it");
        captured.ShouldContain(
            line => line.StartsWith(typeof(CarrierUnavailableException).FullName!, StringComparison.Ordinal),
            "a capture that dropped the carrier's exception would search none of what held the address");
    }

    private Task<int> PassAsync() =>
        _host.Services
            .GetRequiredService<FulfilmentWorker>()
            .RunOnceAsync(TestContext.Current.CancellationToken);

    // The postal code is the only part a case varies, and it is what the simulator scripts.
    private Task<Guid> ConfirmAsync(string postalCode) =>
        _steps.ConfirmAsync(new DeliveryAddress("1 Abay Avenue", null, "Almaty", postalCode, "KZ"));
}
