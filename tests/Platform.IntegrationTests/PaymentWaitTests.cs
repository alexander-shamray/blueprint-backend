using Ordering.Infrastructure.Messaging;
using Payments.Infrastructure.Messaging;
using Shouldly;
using Xunit;

namespace Platform.IntegrationTests;

/// <summary>
/// The one coupling between two services' timings, pinned where a suite holds
/// two services to each other: §3.2 requires Payments' wait for a missing
/// record to reach §9.6's payment timeout, so an order whose OrderPlaced never
/// arrives compensates on that timeout rather than paging first.
/// </summary>
public sealed class PaymentWaitTests
{
    [Fact]
    public void Payments_waits_at_least_as_long_as_the_saga_waits_for_it()
    {
        RedeliveryLadder.Total.ShouldBeGreaterThanOrEqualTo(OrderFulfilmentSaga.PaymentTimeoutDelay);
    }
}
