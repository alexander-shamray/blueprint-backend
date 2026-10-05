using Common.Infrastructure.Idempotency;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>The store a service with no Redis registers: it never claims or holds, so ADR-039's purge runs.</summary>
public sealed class NoClaimsIdempotencyStoreTests
{
    private readonly NoClaimsIdempotencyStore _store = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UnheldAsync_reports_every_key_it_is_given()
    {
        string[] keys = ["a", "b"];

        IReadOnlyCollection<string> unheld = await _store.UnheldAsync(keys, Ct);

        unheld.ShouldBe(keys, ignoreOrder: true);
    }

    [Fact]
    public async Task Every_claim_operation_throws_rather_than_pretending_to_hold_a_key()
    {
        await Should.ThrowAsync<InvalidOperationException>(() => _store.TryClaimAsync("k", TimeSpan.FromHours(1), Ct));
        await Should.ThrowAsync<InvalidOperationException>(() => _store.GetAsync("k", Ct));
        await Should.ThrowAsync<InvalidOperationException>(() => _store.CompleteAsync("k", "c", "{}", Ct));
        await Should.ThrowAsync<InvalidOperationException>(() => _store.ReleaseAsync("k", "c", Ct));
    }
}
