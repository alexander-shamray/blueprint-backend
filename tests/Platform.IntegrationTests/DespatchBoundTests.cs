using System.Globalization;
using System.Text.RegularExpressions;
using Ordering.Infrastructure.Messaging;
using Shipping.Infrastructure.Observability;
using Shouldly;
using Xunit;

namespace Platform.IntegrationTests;

/// <summary>
/// §9.6's despatch wait is the one collection bound the platform has decided, and Shipping sizes its own values
/// against it without reading it (§4.2). Each is held equal here, in the suite that holds both services.
/// </summary>
public sealed partial class DespatchBoundTests
{
    [Fact]
    public void The_unscanned_shipment_age_is_the_sagas_despatch_wait()
    {
        ShipmentStats.FirstScanAge.ShouldBe(OrderFulfilmentSaga.DespatchTimeoutDelay);
    }

    [Fact]
    public void The_chart_defaults_the_fulfilment_give_up_age_to_the_sagas_despatch_wait()
    {
        // FulfilmentOptions takes the age from configuration, so the chart's default is the only value there is.
        var values = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "shipping-values.yaml"));
        var match = GiveUpAge().Match(values);

        match.Success.ShouldBeTrue("the chart's fulfilment.giveUpAge was not found");
        TimeSpan.Parse(match.Groups["age"].Value, CultureInfo.InvariantCulture)
            .ShouldBe(OrderFulfilmentSaga.DespatchTimeoutDelay);
    }

    // A quoted TimeSpan under the top-level fulfilment: key, which no other block in the file shares.
    [GeneratedRegex("""^fulfilment:\r?\n(?:[ ]+.*\r?\n)*?[ ]+giveUpAge:\s*"(?<age>[^"]+)"\s*$""", RegexOptions.Multiline)]
    private static partial Regex GiveUpAge();
}
