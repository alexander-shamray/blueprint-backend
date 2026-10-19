using Common.Application;
using Microsoft.Extensions.DependencyInjection;
using Shipping.Application.Addresses;
using Shipping.Application.Carrier;
using Shipping.Application.Privacy;
using Shipping.Domain.Shipments;
using Shipping.TestSupport;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>An erasure in flight against a worker saving an address, on a real engine's locks.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ErasureFenceTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly DeliveryAddress Address = new("1 High Street", null, "Almaty", "050000", "KZ");

    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    public ValueTask InitializeAsync() => new(fixture.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task An_address_saved_while_an_erasure_that_found_none_is_open_waits_for_its_commit()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Guid subject = Guid.CreateVersion7();
        OrderId order = new(Guid.CreateVersion7());
        TaskCompletionSource deleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource commit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // The erasure's DELETE matches nothing, as when the worker has not saved yet; the unit stays open after it.
        await using AsyncServiceScope erasing = fixture.Factory.Services.CreateAsyncScope();
        Task erasure = erasing.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(
            async inner =>
            {
                int count = await erasing.ServiceProvider.GetRequiredService<IShippingPersonalDataStore>()
                    .DeleteAddressesAsync(subject, inner);
                count.ShouldBe(0);
                deleted.SetResult();
                await commit.Task;

                return count;
            },
            ct);
        await deleted.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

        await using AsyncServiceScope saving = fixture.Factory.Services.CreateAsyncScope();
        Task save = saving.ServiceProvider.GetRequiredService<IDeliveryAddressStore>().SaveAsync(order, subject, Address, Now, ct);

        await Should.ThrowAsync<TimeoutException>(
            () => save.WaitAsync(TimeSpan.FromSeconds(3), ct),
            "an insert the erasure's range lock does not hold back lands before the audit row, and the worker books it");

        commit.SetResult();
        await erasure.WaitAsync(TimeSpan.FromSeconds(30), ct);
        await save.WaitAsync(TimeSpan.FromSeconds(30), ct);
    }
}
