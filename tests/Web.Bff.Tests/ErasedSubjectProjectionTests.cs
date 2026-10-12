using Common.Application;
using Common.Contracts.Ordering.V1;
using Common.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Web.Bff.Orders;
using Web.Bff.Privacy;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>ADR-097: a write that names an erased customer is deleted again, whenever the erasure committed.</summary>
[Collection(nameof(BffIntegrationCollection))]
public sealed class ErasedSubjectProjectionTests(BffServiceFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private readonly Guid _subject = Guid.CreateVersion7();
    private readonly Guid _bystander = Guid.CreateVersion7();

    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync();
        await fixture.ExecuteAsync("DELETE FROM bff.PersonalDataErasures;");
    }

    public async ValueTask DisposeAsync() =>
        await fixture.ExecuteAsync("DELETE FROM bff.PersonalDataErasures;");

    [Theory]
    [InlineData("Placed")]
    [InlineData("Confirmed")]
    [InlineData("Cancelled")]
    public async Task An_event_for_an_erased_subject_arriving_after_the_erasure_leaves_no_row(string name)
    {
        Guid order = Guid.CreateVersion7();
        Guid bystandersOrder = Guid.CreateVersion7();

        // Before the order was placed: no age bounds the read, because the owner anonymises on its own clock.
        await RecordErasureAsync(_subject, At.AddDays(-1));

        await ApplyAsync(name, order, _subject);
        await ApplyAsync(name, bystandersOrder, _bystander);

        (await fixture.OrderAsync(order)).ShouldBeNull("the row was written, then deleted for the erased subject");
        (await fixture.LinesAsync(order)).ShouldBeEmpty("the lines go with their order, by the cascade");
        (await fixture.OrderAsync(bystandersOrder)).ShouldNotBeNull().CustomerId.ShouldBe(_bystander);
    }

    [Fact]
    public async Task An_erasure_committing_between_the_write_and_the_check_still_ends_with_no_row()
    {
        Guid order = Guid.CreateVersion7();
        ErasureBetweenWriteAndCheck between = new(fixture, order, _subject, new SqlErasedSubjects(Connections()));
        OrderProjection projection = new(Connections(), between, TimeProvider.System, NullLogger<OrderProjection>.Instance);

        await projection.HandleAsync(OrderEvents.Placed(order, _subject, At), TestContext.Current.CancellationToken);

        between.RowWasThereWhenAsked.ShouldBeTrue("the check has to come after the write, or this proves nothing");
        (await fixture.OrderAsync(order)).ShouldBeNull();
        (await fixture.LinesAsync(order)).ShouldBeEmpty();
    }

    private IDbConnectionFactory Connections() => fixture.Factory.Services.GetRequiredService<IDbConnectionFactory>();

    private static async Task RecordErasureAsync(BffServiceFixture fixture, Guid subject, DateTimeOffset erasedAt)
    {
        Guid request = Guid.CreateVersion7();
        await fixture.ExecuteAsync(
            "INSERT INTO bff.PersonalDataErasures (RequestId, SubjectHash, Count, ErasedAt) VALUES ({0}, {1}, 0, {2})",
            request,
            PersonalDataErasure.HashSubject(request, subject),
            erasedAt);
    }

    private Task RecordErasureAsync(Guid subject, DateTimeOffset erasedAt) =>
        RecordErasureAsync(fixture, subject, erasedAt);

    private async Task ApplyAsync(string name, Guid order, Guid customer)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IServiceProvider services = scope.ServiceProvider;
        CancellationToken ct = TestContext.Current.CancellationToken;

        switch (name)
        {
            case "Placed":
                await Handler<OrderPlaced>(services)
                    .HandleAsync(OrderEvents.Placed(order, customer, At), ct);
                break;
            case "Confirmed":
                await Handler<OrderConfirmed>(services)
                    .HandleAsync(OrderEvents.Confirmed(order, customer, At), ct);
                break;
            default:
                await Handler<OrderCancelled>(services)
                    .HandleAsync(
                        OrderEvents.Cancelled(order, customer, At, CancelReasons.OutOfStock, CancelOrigins.Workflow),
                        ct);
                break;
        }
    }

    private static IIntegrationEventHandler<T> Handler<T>(IServiceProvider services)
        where T : class =>
        services.GetServices<IIntegrationEventHandler<T>>().Single(h => h is OrderProjection);

    /// <summary>The audit row lands once the projection's write has committed and before it asks, as a racing erasure's.</summary>
    private sealed class ErasureBetweenWriteAndCheck(
        BffServiceFixture fixture,
        Guid order,
        Guid subject,
        IErasedSubjects real) : IErasedSubjects
    {
        public bool RowWasThereWhenAsked { get; private set; }

        public async Task<bool> IsErasedAsync(Guid subjectId, CancellationToken ct)
        {
            RowWasThereWhenAsked = await fixture.OrderAsync(order) is not null;
            await RecordErasureAsync(fixture, subject, At);

            return await real.IsErasedAsync(subjectId, ct);
        }
    }
}
