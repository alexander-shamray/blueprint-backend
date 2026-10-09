using System.Globalization;
using System.Text.Json;
using Notifications.Application.Rendering;
using Notifications.TestSupport;
using Shouldly;
using WireMock.Logging;
using Xunit;

namespace Platform.IntegrationTests.Journey;

/// <summary>An order fulfilled across six services and every notice it owes sent (§12.1).</summary>
/// <remarks>
/// Run under two deployments' answers that share no value (ADR-053): what differs between the runs is the proof
/// that no service holds a country, language, zone or currency it was not given.
/// </remarks>
public abstract class FulfilmentJourney(JourneyWorld world)
{
    private const decimal UnitPrice = 19.90m;
    private const int Quantity = 2;
    private const int Stocked = 10;

    [Fact]
    [Covers("Initial", "OrderPlaced")]
    [Covers("AwaitingStock", "StockReserved")]
    [Covers("AwaitingPayment", "PaymentAuthorised")]
    [Covers("AwaitingConfirmation", "OrderConfirmed")]
    [Covers("Confirmed", "ShipmentDispatched")]
    public async Task An_order_is_placed_fulfilled_delivered_and_told_under_the_deployments_own_answers()
    {
        Jurisdiction here = world.Jurisdiction;
        Guid product = await world.PublishProductAsync(UnitPrice);
        await world.StockAsync(product, Stocked);

        DateTimeOffset placedAfter = DateTimeOffset.UtcNow;
        JourneyOrder order = await world.PlaceAsync(product, Quantity, UnitPrice);
        await using OrderTrace trace = OrderTrace.Start(world, order, Stocked);

        await trace.UntilAsync(
            s => s is
            {
                OrderStatus: "Shipped",
                SagaState: OrderSnapshot.None,
                ShipmentStatus: "Delivered",
                Reserved: 0
            },
            Deadlines.Confirmed + Deadlines.Dispatched + Deadlines.Delivered,
            "the order shipped, its shipment delivered, its saga finished and its reservation consumed");

        OrderSnapshot done = trace.Latest!;
        done.Available.ShouldBe(Stocked - Quantity, "the two units left stock for good");
        done.PaymentStatus.ShouldBe("Authorised");
        trace.ShouldHaveBeenLegal();

        await Convergence.UntilAsync(
            async () => (await world.NoticesAsync(order)).Count(n => n.EndsWith(":Sent", StringComparison.Ordinal)) ==
                4,
            Deadlines.Notified,
            "the four notices the order owes sent");
        DateTimeOffset deliveredBefore = DateTimeOffset.UtcNow;

        (await world.NoticesAsync(order)).ShouldBe(
            [
                $"{TemplateKeys.OrderPlaced}:Sent",
                $"{TemplateKeys.OrderConfirmed}:Sent",
                $"{TemplateKeys.ShipmentDispatched}:Sent",
                $"{TemplateKeys.ShipmentDelivered}:Sent"
            ],
            ignoreOrder: true);

        AssertPaymentWasAskedOnce(order, here);
        AssertCarrierWasGivenTheAddress(order, here);
        await AssertTheCustomerWasToldAsync(order, here, placedAfter, deliveredBefore);
    }

    /// <summary>One authorisation, for the order's total in its currency's minor units, naming the customer.</summary>
    private void AssertPaymentWasAskedOnce(JourneyOrder order, Jurisdiction here)
    {
        ILogEntry[] asked =
        [
            .. world.Provider.LogEntries.Where(e =>
                e.RequestMessage?.Body?.Contains(order.Customer.ToString("D"), StringComparison.Ordinal) == true)
        ];

        asked.Length.ShouldBe(1, "one order is one charge, however many times a message was delivered");

        using JsonDocument body = JsonDocument.Parse(asked[0].RequestMessage!.Body!);
        body.RootElement.GetProperty("currency").GetString().ShouldBe(here.Currency);
        body.RootElement.GetProperty("amountMinor").GetInt64().ShouldBe((long)(order.Total * 100m));
    }

    /// <summary>The carrier was booked once, at the address the customer gave, which Ordering alone held.</summary>
    private void AssertCarrierWasGivenTheAddress(JourneyOrder order, Jurisdiction here)
    {
        ILogEntry[] booked =
        [
            .. world.Carrier.LogEntries.Where(e =>
                e.RequestMessage?.Body?.Contains(order.Customer.ToString("N"), StringComparison.Ordinal) == true)
        ];

        booked.Length.ShouldBe(1, "one shipment is one booking");

        using JsonDocument body = JsonDocument.Parse(booked[0].RequestMessage!.Body!);
        JsonElement address = body.RootElement.GetProperty("address");
        address.GetProperty("country").GetString().ShouldBe(here.Country);
        address.GetProperty("city").GetString().ShouldBe(here.City);
        address.GetProperty("postalCode").GetString().ShouldBe(here.PostalCode);
    }

    /// <summary>
    /// Four messages to the customer's mailbox, in the deployment's language, with the total as its currency's
    /// minor units write it and the date in the deployment's zone.
    /// </summary>
    private async Task AssertTheCustomerWasToldAsync(
        JourneyOrder order,
        Jurisdiction here,
        DateTimeOffset after,
        DateTimeOffset before)
    {
        IReadOnlyList<MailpitMessage> mail = await world.MailAsync(order);
        mail.Count.ShouldBe(4, "one message per notice, to the mailbox the contact source answered with");

        TemplateSet templates = TemplateSet.Embedded;
        foreach (string key in new[]
        {
            TemplateKeys.OrderPlaced,
            TemplateKeys.OrderConfirmed,
            TemplateKeys.ShipmentDispatched,
            TemplateKeys.ShipmentDelivered
        })
        {
            Template template = templates.Find(key, templates.CurrentVersion(key), here.Locale)!;
            mail.ShouldContain(
                m => m.Subject == template.Subject,
                $"the {key} notice is written in the customer's language ({here.Locale})");
        }

        CultureInfo culture = CultureInfo.GetCultureInfo(here.Locale);
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(here.TimeZone);
        string[] days =
        [
            .. new[] { after, before }
                .Select(at => TimeZoneInfo.ConvertTime(at, zone).ToString("D", culture))
                .Distinct()
        ];

        string total = order.Total.ToString("N2", culture) + " " + here.Currency;
        foreach (MailpitMessage message in mail)
        {
            message.Text.ShouldContain(order.Id.ToString("D"));
            days.ShouldContain(
                day => message.Text.Contains(day, StringComparison.Ordinal),
                $"the date is written in {here.TimeZone}, the zone the deployment named");
        }

        // The two notices that name an amount, which must agree: the order's total is one number.
        mail.Count(m => m.Text.Contains(total, StringComparison.Ordinal)).ShouldBe(
            2,
            $"the order placed and the order confirmed both say {total}");
    }
}

/// <summary>The journey under the first deployment's answers.</summary>
[Collection(nameof(JourneyCollection))]
public sealed class FirstJurisdictionFulfilment(FirstJurisdictionWorld world) : FulfilmentJourney(world);

/// <summary>The same journey under a second set, which shares no value with the first.</summary>
[Collection(nameof(SecondJurisdictionCollection))]
public sealed class SecondJurisdictionFulfilment(SecondJurisdictionWorld world) : FulfilmentJourney(world);
