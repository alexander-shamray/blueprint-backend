using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
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

        // The holder is a bare connection: the production retry strategy refuses a transaction started outside it.
        await using SqlConnection holder = new(fixture.ConnectionString);
        await holder.OpenAsync(ct);
        await using SqlTransaction held = holder.BeginTransaction();
        await using (SqlCommand take = holder.CreateCommand())
        {
            take.Transaction = held;
            take.CommandText =
                "EXEC sp_getapplock @Resource = @r, @LockMode = 'Exclusive', " +
                "@LockOwner = 'Transaction', @LockTimeout = 0";
            take.Parameters.AddWithValue("@r", $"privacy.erasure.request.{requestId:N}");
            await take.ExecuteNonQueryAsync(ct);
        }

        await using AsyncServiceScope waiter = fixture.Factory.Services.CreateAsyncScope();
        PrivacyDbContext waiting = waiter.ServiceProvider.GetRequiredService<PrivacyDbContext>();

        // Waits out the lock's ten seconds, which is the cost of proving the timeout rather than a stand-in for it.
        ErasureLockNotTakenException thrown = await Should.ThrowAsync<ErasureLockNotTakenException>(
            () => waiting.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var tx = await waiting.Database.BeginTransactionAsync(ct);
                await new ErasureRequestRepository(waiting).GetLockedAsync(requestId, ct);
            }));

        thrown.InnerException.ShouldBeOfType<SqlException>().Number
            .ShouldBe(ErasureRequestRepository.LockNotTakenNumber);
    }
}
