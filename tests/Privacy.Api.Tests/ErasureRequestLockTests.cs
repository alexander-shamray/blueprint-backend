using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Privacy.Application.ErasureRequests;
using Privacy.Infrastructure.Persistence;
using Privacy.TestSupport;
using Shouldly;
using Xunit;

namespace Privacy.Api.Tests;

/// <summary>The lock's timeout against a real engine: told from any other fault, and typed.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ErasureRequestLockTests(ServiceFixture fixture) : IAsyncLifetime
{
    public ValueTask InitializeAsync() => new(fixture.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_lock_held_past_the_wait_is_a_typed_fault_and_not_the_engines()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Guid requestId = Guid.CreateVersion7();

        await using AsyncServiceScope holder = fixture.Factory.Services.CreateAsyncScope();
        PrivacyDbContext holding = holder.ServiceProvider.GetRequiredService<PrivacyDbContext>();
        await using var held = await holding.Database.BeginTransactionAsync(ct);
        await new ErasureRequestRepository(holding).GetLockedAsync(requestId, ct);

        await using AsyncServiceScope waiter = fixture.Factory.Services.CreateAsyncScope();
        PrivacyDbContext waiting = waiter.ServiceProvider.GetRequiredService<PrivacyDbContext>();
        await using var waits = await waiting.Database.BeginTransactionAsync(ct);

        // Waits out the lock's ten seconds, which is the cost of proving the timeout rather than a stand-in for it.
        ErasureLockNotTakenException thrown = await Should.ThrowAsync<ErasureLockNotTakenException>(
            () => new ErasureRequestRepository(waiting).GetLockedAsync(requestId, ct));

        thrown.InnerException.ShouldBeOfType<SqlException>().Number
            .ShouldBe(ErasureRequestRepository.LockNotTakenNumber);
    }
}
