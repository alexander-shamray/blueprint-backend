using Ordering.Infrastructure.Messaging;
using Payments.Infrastructure.Messaging;
using Shouldly;
using Xunit;

namespace Platform.IntegrationTests;

/// <summary>
/// §3.2's coupling of two services' timings, read in the suite that holds both: Payments waits for a missing
/// record at least as long as §9.6's payment timeout.
/// </summary>
public sealed class PaymentWaitTests
{
    [Fact]
    public void Payments_waits_at_least_as_long_as_the_saga_waits_for_it()
    {
        RedeliveryLadder.Total.ShouldBeGreaterThanOrEqualTo(OrderFulfilmentSaga.PaymentTimeoutDelay);
    }
}
