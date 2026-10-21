using Common.Application;
using Common.Contracts.Ordering.V1;
using Common.Domain;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Intake;
using Notifications.Application.Privacy;
using Notifications.Infrastructure.Persistence;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>ADR-097: an order event for an erased customer is recorded and then deleted, whenever the erasure
/// ran.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ErasedSubjectIntakeTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset At = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly Guid _subject = Guid.CreateVersion7();
    private readonly Guid _bystander = Guid.CreateVersion7();

    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync();
        await fixture.ExecuteAsync("DELETE FROM notifications.PersonalDataErasures;");
    }

    public async ValueTask DisposeAsync() =>
        await fixture.ExecuteAsync("DELETE FROM notifications.PersonalDataErasures;");

    [Theory]
    [InlineData("Placed")]
    [InlineData("Confirmed")]
    [InlineData("Cancelled")]
    public async Task An_event_for_an_erased_customer_arriving_after_the_erasure_leaves_no_record_and_no_notice(
        string name)
    {
        Guid order = Guid.CreateVersion7();
        Guid bystandersOrder = Guid.CreateVersion7();

        // Before the order was placed: no age bounds the read, because the owner anonymises on its own clock.
        await RecordErasureAsync(fixture, _subject, At.AddDays(-1));

        await ApplyAsync(name, order, _subject);
        await ApplyAsync(name, bystandersOrder, _bystander);

        (await fixture.OrderRecordAsync(order)).ShouldBeNull();
        (await fixture.NotificationsAsync(order)).ShouldBeEmpty("a waiting notice would send the worker for a mailbox");
        (await fixture.OrderRecordAsync(bystandersOrder)).ShouldNotBeNull();
        (await fixture.NotificationsAsync(bystandersOrder)).ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("Placed")]
    [InlineData("Confirmed")]
    [InlineData("Cancelled")]
    public async Task An_order_placed_after_the_erasure_is_kept_with_its_notice_by_whichever_event_arrives(string name)
    {
        Guid order = Guid.CreateVersion7();
        Guid request = await RecordErasureAsync(fixture, _subject, At.AddDays(-1));

        await ApplyAsync(name, order, _subject, [request]);

        (await fixture.OrderRecordAsync(order)).ShouldNotBeNull();
        (await fixture.NotificationsAsync(order)).ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("Placed")]
    [InlineData("Confirmed")]
    [InlineData("Cancelled")]
    public async Task An_order_placed_after_one_erasure_is_ended_by_a_later_one(string name)
    {
        Guid order = Guid.CreateVersion7();
        Guid earlier = await RecordErasureAsync(fixture, _subject, At.AddDays(-2));
        await RecordErasureAsync(fixture, _subject, At.AddDays(-1));

        await ApplyAsync(name, order, _subject, [earlier]);

        (await fixture.OrderRecordAsync(order)).ShouldBeNull("the second request names the order the first did not");
        (await fixture.NotificationsAsync(order)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_request_that_closed_before_the_event_is_not_asked_and_the_order_is_kept()
    {
        Guid order = Guid.CreateVersion7();

        // Closed, so the owner had anonymised the subject before the event existed to carry them (ADR-139).
        await RecordErasureAsync(
            fixture,
            _subject,
            At.AddDays(-9),
            closedAt: At - PersonalDataErasure.ClockMargin - TimeSpan.FromHours(1));

        await ApplyAsync("Placed", order, _subject);

        (await fixture.OrderRecordAsync(order)).ShouldNotBeNull();
        (await fixture.NotificationsAsync(order)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_request_that_closed_inside_the_margin_before_the_event_still_ends_the_order()
    {
        Guid order = Guid.CreateVersion7();

        // The margin covers the owner's, Notifications' and Privacy's clocks disagreeing (ADR-139).
        await RecordErasureAsync(
            fixture,
            _subject,
            At.AddDays(-9),
            closedAt: At - PersonalDataErasure.ClockMargin + TimeSpan.FromHours(1));

        await ApplyAsync("Placed", order, _subject);

        (await fixture.OrderRecordAsync(order)).ShouldBeNull();
        (await fixture.NotificationsAsync(order)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_erasure_committing_between_the_record_and_the_check_still_ends_with_nothing_held()
    {
        Guid order = Guid.CreateVersion7();
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IServiceProvider services = scope.ServiceProvider;
        ErasureBetweenRecordAndCheck between = new(
            fixture,
            order,
            _subject,
            new SqlErasedSubjects(services.GetRequiredService<IDbConnectionFactory>()));
        OrderPlacedHandler handler = new(new ErasedIntake(services.GetRequiredService<IDispatcher>(), between));

        await handler.HandleAsync(
            OrderEvents.Placed(order, _subject, At),
            TestContext.Current.CancellationToken);

        between.RecordWasThereWhenAsked.ShouldBeTrue("the check has to come after the record, or this proves nothing");
        (await fixture.OrderRecordAsync(order)).ShouldBeNull();
        (await fixture.NotificationsAsync(order)).ShouldBeEmpty();
    }

    /// <summary>The audit row a holder writes; closed at <paramref name="closedAt"/> when Privacy's close has
    /// reached it, and open otherwise.</summary>
    internal static async Task<Guid> RecordErasureAsync(
        ServiceFixture fixture,
        Guid subject,
        DateTimeOffset erasedAt,
        DateTimeOffset? closedAt = null)
    {
        Guid request = Guid.CreateVersion7();

        // Two statements, since EF raw SQL cannot take a null parameter.
        if (closedAt is { } closed)
        {
            await fixture.ExecuteAsync(
                "INSERT INTO notifications.PersonalDataErasures (RequestId, SubjectHash, Count, ErasedAt, ClosedAt) " +
                "VALUES ({0}, {1}, 0, {2}, {3})",
                request,
                PersonalDataErasure.HashSubject(request, subject),
                erasedAt,
                closed);

            return request;
        }

        await fixture.ExecuteAsync(
            "INSERT INTO notifications.PersonalDataErasures (RequestId, SubjectHash, Count, ErasedAt) " +
            "VALUES ({0}, {1}, 0, {2})",
            request,
            PersonalDataErasure.HashSubject(request, subject),
            erasedAt);

        return request;
    }

    private async Task ApplyAsync(string name, Guid order, Guid customer, IReadOnlyList<Guid>? erasedUnder = null)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IServiceProvider services = scope.ServiceProvider;
        CancellationToken ct = TestContext.Current.CancellationToken;

        switch (name)
        {
            case "Placed":
                await Handler<OrderPlaced>(services)
                    .HandleAsync(OrderEvents.Placed(order, customer, At, erasedUnder), ct);
                break;
            case "Confirmed":
                await Handler<OrderConfirmed>(services)
                    .HandleAsync(OrderEvents.Confirmed(order, customer, At, erasedUnder), ct);
                break;
            default:
                await Handler<OrderCancelled>(services)
                    .HandleAsync(
                        OrderEvents.Cancelled(order, customer, At, CancelReasons.OutOfStock, null, erasedUnder),
                        ct);
                break;
        }
    }

    private static IIntegrationEventHandler<T> Handler<T>(IServiceProvider services)
        where T : class =>
        services.GetServices<IIntegrationEventHandler<T>>().Single();

    /// <summary>The audit row lands once the intake's unit has committed and before it asks, as a racing
    /// erasure's.</summary>
    private sealed class ErasureBetweenRecordAndCheck(
        ServiceFixture fixture,
        Guid order,
        Guid subject,
        IErasedSubjects real) : IErasedSubjects
    {
        public bool RecordWasThereWhenAsked { get; private set; }

        public async Task<bool> IsErasedAsync(
            Guid subjectId,
            IReadOnlyCollection<Guid> placedAfter,
            DateTimeOffset sourcedAt,
            CancellationToken ct)
        {
            RecordWasThereWhenAsked = await fixture.OrderRecordAsync(order) is not null;
            await RecordErasureAsync(fixture, subject, At);

            return await real.IsErasedAsync(subjectId, placedAfter, sourcedAt, ct);
        }
    }
}
