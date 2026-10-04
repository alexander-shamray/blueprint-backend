using Common.Infrastructure.Inbox;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>§9.5's purge over the BFF's inbox, with no outbox and no markers to compose (ADR-051).</summary>
[Collection(nameof(BffIntegrationCollection))]
public sealed class RetentionPurgeTests(BffServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_pass_purges_the_aged_inbox_row_and_reports_no_outbox_and_no_markers()
    {
        // Far from the window on either side, so the test is about the predicate, not a boundary.
        await fixture.StageInboxAsync(
            new InboxMessage(Guid.CreateVersion7(), "bff-probe", DateTimeOffset.UtcNow.AddDays(-30)),
            new InboxMessage(Guid.CreateVersion7(), "bff-probe", DateTimeOffset.UtcNow.AddDays(-1)));

        (int outbox, int inbox, int idempotency) = await fixture.PurgeRetentionAsync();

        outbox.ShouldBe(0);
        inbox.ShouldBe(1);
        idempotency.ShouldBe(0);
        (await fixture.InboxAsync()).Count.ShouldBe(1);
    }
}
