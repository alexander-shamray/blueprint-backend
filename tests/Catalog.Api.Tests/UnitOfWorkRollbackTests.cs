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

/// <summary>A rolled-back unit of work leaves nothing tracked for §9.5's inbox filter to save.</summary>
[Collection(nameof(IntegrationCollection))]
public class UnitOfWorkRollbackTests(ServiceFixture fixture)
{
    [Fact]
    public async Task A_rejected_command_leaves_nothing_tracked_for_a_later_save_to_commit()
    {
        // §6.3's behaviour declines SaveChanges on a failure, but §9.5's inbox filter saves unconditionally after
        // the consumer returns, so the rollback has to clear the change tracker too.
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
