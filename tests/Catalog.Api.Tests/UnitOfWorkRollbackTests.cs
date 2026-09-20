using Catalog.Domain.Common;
using Catalog.Domain.Products;
using Catalog.Infrastructure.Persistence;
using Catalog.TestSupport;
using Common.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary>
/// What a rolled-back unit of work leaves behind, which is a question §6.3 did
/// not have to answer until §9.5's inbox filter became the second thing calling
/// <c>SaveChanges</c> on a consume scope.
/// </summary>
/// <remarks>
/// <b>Its own file, and outside the scaffold's template, because it needs a
/// tracked aggregate.</b> The claim is about entities the change tracker still
/// holds after a refusal, and <c>Product</c> is the only entity Catalog has —
/// so a service with no aggregate cannot make this assertion at all. It returns
/// with the first real slice, alongside the other suites the scaffold drops for
/// the same reason.
/// </remarks>
[Collection(nameof(IntegrationCollection))]
public class UnitOfWorkRollbackTests(ServiceFixture fixture)
{
    [Fact]
    public async Task A_rejected_command_leaves_nothing_tracked_for_a_later_save_to_commit()
    {
        // The rollback has to clear the change tracker as well as the
        // transaction. §6.3's behaviour declines to SaveChanges on a failed
        // Result, which covers the case where it is the only caller; §9.5's
        // inbox filter is a second one, running after the consumer returns and
        // saving unconditionally because it has its own row to write. Anything
        // a rejected handler left tracked would go with it, outside the
        // transaction that was just rolled back — a domain refusal committing
        // its own mutations, which is the single outcome the transaction
        // boundary exists to prevent. Written against the real registered
        // IUnitOfWork and the real context, the pair whose contract is at issue.
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        CatalogDbContext db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        Product product = Product.Publish(
            "Rejected desk",
            null,
            Money.Of(31.50m, "EUR"),
            DateTimeOffset.UtcNow);

        Result result = await unitOfWork.ExecuteAsync(
            token =>
            {
                db.Set<Product>().Add(product);

                // A domain refusal, which is an answer rather than a fault: the
                // handler ran, mutated, and decided no.
                return Task.FromResult(Result.Failure(
                    new Error("probe.refused", "the domain refused this", ErrorType.Rule)));
            },
            TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();

        // The second caller, standing in for the inbox filter: unconditional,
        // and with nothing of its own to write here.
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        int rows = await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM catalog.Products WHERE Id = {0}",
            product.Id.Value);

        rows.ShouldBe(0, "a rejected command's mutations must not survive a later SaveChanges");
    }
}
