using Common.Application;
using Microsoft.Extensions.DependencyInjection;
using Payments.TestSupport;
using Shouldly;
using Xunit;

namespace Payments.Api.Tests;

/// <summary>
/// §2: Payments' own <c>IIdempotencyStore</c> never claims and never holds,
/// so ADR-039's purge can still resolve one without Redis. Resolved from the
/// factory's services rather than named directly — the type is internal to
/// Payments.Infrastructure, and this project carries no reference to it.
/// </summary>
public sealed class NoClaimsIdempotencyStoreTests
{
    private static PaymentsApiFactory Factory() =>
        new(
            "Server=sql.invalid;Database=Payments;User Id=x;Password=x;TrustServerCertificate=true",
            "amqp://payments-svc:x@rabbit.invalid:5672");

    [Fact]
    public async Task UnheldAsync_reports_every_key_it_is_given()
    {
        using PaymentsApiFactory factory = Factory();
        IIdempotencyStore store = factory.Services.GetRequiredService<IIdempotencyStore>();

        string[] keys = ["a", "b"];

        IReadOnlyCollection<string> unheld = await store.UnheldAsync(keys, TestContext.Current.CancellationToken);

        unheld.ShouldBe(keys, ignoreOrder: true);
    }

    [Fact]
    public async Task A_claim_attempt_throws()
    {
        using PaymentsApiFactory factory = Factory();
        IIdempotencyStore store = factory.Services.GetRequiredService<IIdempotencyStore>();

        await Should.ThrowAsync<InvalidOperationException>(
            () => store.TryClaimAsync("k", TimeSpan.FromHours(1), TestContext.Current.CancellationToken));
    }
}
