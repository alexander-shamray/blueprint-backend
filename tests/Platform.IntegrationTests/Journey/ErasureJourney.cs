using System.Net;
using System.Net.Http.Json;
using Common.Domain;
using Shouldly;
using Xunit;

namespace Platform.IntegrationTests.Journey;

/// <summary>A customer erased and nothing personal left in any store but the ones the register names (§11.7).</summary>
/// <remarks>
/// The proof is the scan after the erasure, not the holders' own suites, which cannot show the set complete (ADR-092).
/// The scan before it is the control: a search that finds nothing proves nothing until it finds something.
/// </remarks>
[Collection(nameof(JourneyCollection))]
public sealed class ErasureJourney(FirstJurisdictionWorld world)
{
    private const decimal UnitPrice = 19.90m;

    /// <summary>The holdings docs/personal-data.md calls lifetime only, and why each keeps the id (§11.7).</summary>
    private static readonly Dictionary<string, string> LifetimeOnly = new()
    {
        ["Ordering.ordering.OutboxMessages"] = "the id in the payloads of the order events, until the outbox window",
        ["Privacy.privacy.OutboxMessages"] = "the id in the PersonalDataDeleteRequested it broadcast (ADR-092)"
    };

    /// <summary>Each service's markers key a command by its caller, a customer's for <c>PlaceOrder</c>; lifetime only too.</summary>
    private const string MarkerTable = ".IdempotencyMarkers";

    /// <summary>What a customer who ordered and was delivered to is held in before anyone asks for erasure.</summary>
    private static readonly string[] HoldersOfACustomer =
    [
        "Ordering.ordering.Orders",
        "Payments.payments.PaymentOrders",
        "Shipping.shipping.DeliveryAddresses",
        "Notifications.notifications.ContactRecords",
        "Notifications.notifications.NotificationLog",
        "Bff.bff.Orders"
    ];

    [Fact]
    public async Task A_customer_is_erased_from_every_store_and_every_holder_says_so()
    {
        Guid product = await world.PublishProductAsync(UnitPrice);
        await world.StockAsync(product, 10);

        JourneyOrder order = await world.PlaceAsync(product, 1, UnitPrice);
        await Convergence.UntilAsync(
            async () => (await world.NoticesAsync(order)).Count(n => n.EndsWith(":Sent", StringComparison.Ordinal)) == 4 &&
                await world.AnyAsync(JourneyWorld.Bff, "SELECT 1 FROM bff.Orders WHERE CustomerId = @p0", order.Customer),
            Deadlines.Journey,
            "the order delivered, its four notices sent and the buyer's list holding it");

        string[] needles = [order.Customer.ToString("D"), order.Customer.ToString("N")];

        IReadOnlySet<string> before = await world.HoldingAsync(needles);
        HoldersOfACustomer.Except(before).ShouldBeEmpty("the scan must find a customer where one is held");

        Guid request = await RaiseAsync(order.Customer);
        await Convergence.UntilAsync(
            async () => await world.ScalarAsync<string?>(
                JourneyWorld.Privacy,
                "SELECT Status FROM privacy.ErasureRequests WHERE RequestId = @p0",
                request) == "Closed",
            Deadlines.Legs(4),
            "the five holders answering and Privacy closing the request");

        IReadOnlySet<string> after = await world.HoldingAsync(needles);
        string[] unexpected =
        [
            .. after.Where(table => !LifetimeOnly.ContainsKey(table) &&
                !table.EndsWith(MarkerTable, StringComparison.Ordinal))
        ];
        unexpected.ShouldBeEmpty("nothing but a holding the register names as lifetime only may still hold the customer");

        await AssertEveryHolderLeftItsAuditRowAsync(request, order.Customer);
        (await world.DeadLettersAsync()).ShouldBeEmpty("no message of the choreography was parked unanswered");
    }

    /// <summary>One audit row per holder, each carrying the hash and never the id, and the request closed on all five.</summary>
    private async Task AssertEveryHolderLeftItsAuditRowAsync(Guid request, Guid customer)
    {
        string hash = PersonalDataErasure.HashSubject(request, customer);

        foreach ((string database, string schema) in new[]
        {
            (JourneyWorld.Ordering, "ordering"),
            (JourneyWorld.Payments, "payments"),
            (JourneyWorld.Shipping, "shipping"),
            (JourneyWorld.Notifications, "notifications"),
            (JourneyWorld.Bff, "bff")
        })
        {
            (await world.ScalarAsync<string?>(
                database,
                $"SELECT SubjectHash FROM {schema}.PersonalDataErasures WHERE RequestId = @p0",
                request)).ShouldBe(hash, $"{database} left an audit row carrying the hash");
        }

        (await world.ScalarAsync<string?>(
            JourneyWorld.Privacy,
            "SELECT SubjectHash FROM privacy.ErasureRequests WHERE RequestId = @p0",
            request)).ShouldBe(hash);
        (await world.ScalarAsync<int>(
            JourneyWorld.Privacy,
            "SELECT COUNT(*) FROM privacy.ErasureRequests WHERE RequestId = @p0 AND SubjectId IS NULL",
            request)).ShouldBe(1, "the closed request no longer holds the customer's id");
        (await world.ColumnAsync(
            JourneyWorld.Privacy,
            "SELECT Responder FROM privacy.ErasureCompletions WHERE RequestId = @p0 AND Counted = 1",
            request)).ShouldBe(["bff", "notifications", "ordering", "payments", "shipping"], ignoreOrder: true);
    }

    /// <summary>A staff principal raising the request through Privacy's own route (§11.7).</summary>
    private async Task<Guid> RaiseAsync(Guid subject)
    {
        using HttpClient privacy = JourneyWorld.ClientFor(world.PrivacyHost, Guid.CreateVersion7(), "privacy:erase");
        HttpResponseMessage raised = await privacy.PostAsJsonAsync(
            "/v1/privacy/erasure-requests",
            new { subjectId = subject },
            TestContext.Current.CancellationToken);

        raised.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await raised.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken);
    }
}
