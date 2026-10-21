using Common.Application;
using Notifications.Application.Intake;
using Notifications.Application.Privacy;
using Notifications.Application.Privacy.EndErasedOrder;
using Notifications.Application.Rendering;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

/// <summary>ADR-097: the intake records first, then deletes again only for a customer the audit rows name.</summary>
public class ErasedIntakeTests
{
    private static readonly Guid Customer = Guid.CreateVersion7();
    private static readonly DateTimeOffset EventAt = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly RecordingDispatcher _dispatcher = new();

    [Fact]
    public async Task A_customer_the_audit_rows_name_has_what_was_just_recorded_ended()
    {
        RecordNotificationCommand command = Command(new OrderFact(Customer, Cancellation: null));

        await new ErasedIntake(_dispatcher, new Answers(erased: true))
            .RecordAsync(command, TestContext.Current.CancellationToken);

        _dispatcher.Sent.ShouldBe([command, new EndErasedOrderCommand(Customer, command.Parameters.OrderId)]);
    }

    [Fact]
    public async Task A_customer_nobody_erased_is_recorded_and_nothing_more()
    {
        RecordNotificationCommand command = Command(new OrderFact(Customer, Cancellation: null));

        await new ErasedIntake(_dispatcher, new Answers(erased: false))
            .RecordAsync(command, TestContext.Current.CancellationToken);

        _dispatcher.Sent.ShouldHaveSingleItem().ShouldBe(command);
    }

    [Fact]
    public async Task The_requests_the_order_was_placed_under_are_handed_to_the_check()
    {
        Answers answers = new(erased: false);
        Guid request = Guid.CreateVersion7();
        RecordNotificationCommand command = Command(new OrderFact(Customer, Cancellation: null, [request]));

        await new ErasedIntake(_dispatcher, answers).RecordAsync(command, TestContext.Current.CancellationToken);

        answers.PlacedAfter.ShouldHaveSingleItem().ShouldBe([request]);
    }

    [Fact]
    public async Task An_event_from_an_older_publisher_hands_the_check_no_requests()
    {
        Answers answers = new(erased: false);
        RecordNotificationCommand command = Command(new OrderFact(Customer, Cancellation: null));

        await new ErasedIntake(_dispatcher, answers).RecordAsync(command, TestContext.Current.CancellationToken);

        answers.PlacedAfter.ShouldHaveSingleItem().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_check_is_bounded_by_the_events_own_instant_and_not_the_time_it_is_asked()
    {
        Answers answers = new(erased: false);
        RecordNotificationCommand command = Command(new OrderFact(Customer, Cancellation: null));

        await new ErasedIntake(_dispatcher, answers).RecordAsync(command, TestContext.Current.CancellationToken);

        answers.SourcedAt.ShouldHaveSingleItem()
            .ShouldBe(command.Parameters.OccurredAt, "the data left the owner then (ADR-139)");
    }

    [Fact]
    public async Task An_event_that_names_no_customer_is_not_asked_about()
    {
        Answers answers = new(erased: true);
        RecordNotificationCommand command = Command(order: null);

        await new ErasedIntake(_dispatcher, answers).RecordAsync(command, TestContext.Current.CancellationToken);

        _dispatcher.Sent.ShouldHaveSingleItem().ShouldBe(command);
        answers.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_check_follows_the_record_so_an_erasure_between_them_is_seen()
    {
        Answers answers = new(erased: true);
        RecordNotificationCommand command = Command(new OrderFact(Customer, Cancellation: null));
        answers.OnAsked = () => _dispatcher.Sent.ShouldBe([command], "the record has to commit before the check");

        await new ErasedIntake(_dispatcher, answers).RecordAsync(command, TestContext.Current.CancellationToken);

        answers.Asked.ShouldBe([Customer]);
    }

    private static RecordNotificationCommand Command(OrderFact? order) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            TemplateKeys.OrderPlaced,
            new NotificationParameters { OrderId = Guid.CreateVersion7(), OccurredAt = EventAt },
            order);

    private sealed class Answers(bool erased) : IErasedSubjects
    {
        public List<Guid> Asked { get; } = [];

        public List<IReadOnlyCollection<Guid>> PlacedAfter { get; } = [];

        public List<DateTimeOffset> SourcedAt { get; } = [];

        public Action? OnAsked { get; set; }

        public Task<bool> IsErasedAsync(
            Guid subjectId,
            IReadOnlyCollection<Guid> placedAfter,
            DateTimeOffset sourcedAt,
            CancellationToken ct)
        {
            Asked.Add(subjectId);
            PlacedAfter.Add(placedAfter);
            SourcedAt.Add(sourcedAt);
            OnAsked?.Invoke();

            return Task.FromResult(erased);
        }
    }

    private sealed class RecordingDispatcher : IDispatcher
    {
        public List<object> Sent { get; } = [];

        public Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken ct = default)
        {
            Sent.Add(command);
            return Task.FromResult((TResult)(object)Result.Success());
        }

        public Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
