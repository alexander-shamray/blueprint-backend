using Common.Domain;
using Privacy.Domain.ErasureRequests;
using Privacy.Domain.ErasureRequests.Events;
using Shouldly;
using Xunit;

namespace Privacy.Domain.Tests;

public class ErasureRequestLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Request = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Subject = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly TimeSpan Slo = TimeSpan.FromDays(30);

    private static ErasureRequest Raised(params string[] responders)
    {
        ErasureRequest request = ErasureRequest.Raise(
            Request,
            Subject,
            responders.Length == 0 ? ["ordering", "payments", "shipping"] : responders,
            Slo,
            Now);
        request.ClearDomainEvents();

        return request;
    }

    [Fact]
    public void The_request_closes_on_the_last_expected_holder_and_not_before()
    {
        ErasureRequest request = Raised();

        request.RecordCompletion("ordering", 2, Now.AddHours(1)).ShouldBe(CompletionOutcome.Counted);
        request.RecordCompletion("shipping", 0, Now.AddHours(2)).ShouldBe(CompletionOutcome.Counted);

        request.Status.ShouldBe(ErasureStatus.Open);
        request.Missing.ShouldBe(["payments"]);

        request.RecordCompletion("payments", 1, Now.AddHours(3)).ShouldBe(CompletionOutcome.Counted);

        request.Status.ShouldBe(ErasureStatus.Closed);
        request.Missing.ShouldBeEmpty();
        request.ClosedAt.ShouldBe(Now.AddHours(3));
    }

    [Fact]
    public void Closing_replaces_the_subjects_id_with_the_hash_the_holders_carry()
    {
        ErasureRequest request = Raised("ordering");

        request.RecordCompletion("ordering", 1, Now);

        request.SubjectId.ShouldBeNull("only an open or overdue request keeps the id (ADR-092)");
        request.SubjectHash.ShouldBe(PersonalDataErasure.HashSubject(Request, Subject));
    }

    [Fact]
    public void The_order_holders_answer_in_does_not_matter()
    {
        ErasureRequest request = Raised();

        foreach (string holder in new[] { "shipping", "payments", "ordering" })
            request.RecordCompletion(holder, 1, Now);

        request.Status.ShouldBe(ErasureStatus.Closed);
    }

    [Fact]
    public void A_name_outside_the_stored_set_is_recorded_flagged_and_never_counted()
    {
        ErasureRequest request = Raised("ordering", "payments");

        request.RecordCompletion("ordering", 1, Now).ShouldBe(CompletionOutcome.Counted);
        request.RecordCompletion("catalog", 4, Now).ShouldBe(CompletionOutcome.Unexpected);

        request.Status.ShouldBe(ErasureStatus.Open, "an unexpected name cannot stand in for an expected one");
        request.Missing.ShouldBe(["payments"]);
        ErasureCompletion stray = request.Completions.Single(c => c.Responder == "catalog");
        stray.Counted.ShouldBeFalse();
        stray.Count.ShouldBe(4);
    }

    [Fact]
    public void Nothing_but_unexpected_names_never_closes_a_request()
    {
        ErasureRequest request = Raised("ordering");

        request.RecordCompletion("catalog", 1, Now);
        request.RecordCompletion("inventory", 1, Now);

        request.Status.ShouldBe(ErasureStatus.Open);
    }

    [Fact]
    public void A_holder_answering_again_keeps_the_larger_count_and_the_latest_time()
    {
        ErasureRequest request = Raised("ordering", "payments");
        request.RecordCompletion("ordering", 5, Now);

        request.RecordCompletion("ordering", 0, Now.AddDays(1)).ShouldBe(CompletionOutcome.Repeated);

        ErasureCompletion heard = request.Completions.Single();
        heard.Count.ShouldBe(5, "a reissue that finds nothing left must not overwrite what the first pass removed");
        heard.ReceivedAt.ShouldBe(Now.AddDays(1));
        request.Status.ShouldBe(ErasureStatus.Open);
    }

    [Fact]
    public void A_late_answer_after_the_request_closed_is_ignored_and_changes_nothing()
    {
        ErasureRequest request = Raised("ordering");
        request.RecordCompletion("ordering", 1, Now);

        request.RecordCompletion("ordering", 9, Now.AddDays(1)).ShouldBe(CompletionOutcome.Ignored);
        request.RecordCompletion("catalog", 1, Now.AddDays(1)).ShouldBe(CompletionOutcome.Ignored);

        request.Completions.ShouldHaveSingleItem().Count.ShouldBe(1);
    }

    [Fact]
    public void A_completion_with_a_bad_name_or_a_negative_count_is_refused()
    {
        ErasureRequest request = Raised();

        Should.Throw<DomainException>(() => request.RecordCompletion("Ordering", 1, Now));
        Should.Throw<DomainException>(() => request.RecordCompletion("ordering", -1, Now));
    }

    [Fact]
    public void An_overdue_request_still_closes_when_the_missing_holder_answers_at_last()
    {
        ErasureRequest request = Raised("ordering", "payments");
        request.RecordCompletion("ordering", 1, Now);
        request.MarkOverdue(Now + Slo);

        request.RecordCompletion("payments", 2, Now + Slo + TimeSpan.FromDays(1));

        request.Status.ShouldBe(ErasureStatus.Closed);
        request.SubjectId.ShouldBeNull();
    }

    [Fact]
    public void An_open_request_becomes_overdue_at_its_due_time_and_not_before()
    {
        ErasureRequest request = Raised();

        request.MarkOverdue(request.DueAt - TimeSpan.FromTicks(1)).ShouldBeFalse();
        request.Status.ShouldBe(ErasureStatus.Open);

        request.MarkOverdue(request.DueAt).ShouldBeTrue();

        request.Status.ShouldBe(ErasureStatus.Overdue);
        request.OverdueAt.ShouldBe(request.DueAt);
        request.SubjectId.ShouldBe(Subject, "an overdue request keeps the id, because a reissue needs it (ADR-092)");
    }

    [Fact]
    public void An_overdue_or_closed_request_is_not_marked_overdue_again()
    {
        ErasureRequest overdue = Raised();
        overdue.MarkOverdue(overdue.DueAt);
        ErasureRequest closed = Raised("ordering");
        closed.RecordCompletion("ordering", 1, Now);

        overdue.MarkOverdue(overdue.DueAt.AddDays(1)).ShouldBeFalse();
        closed.MarkOverdue(closed.DueAt.AddDays(1)).ShouldBeFalse();

        overdue.OverdueAt.ShouldBe(overdue.DueAt);
        closed.Status.ShouldBe(ErasureStatus.Closed);
    }

    [Fact]
    public void A_reissue_reopens_an_overdue_request_with_a_fresh_time_and_asks_the_holders_again()
    {
        ErasureRequest request = Raised();
        request.RecordCompletion("ordering", 3, Now);
        request.MarkOverdue(request.DueAt);
        DateTimeOffset reissued = request.DueAt.AddDays(2);

        request.Reissue(Slo, reissued).ShouldBeTrue();

        request.Status.ShouldBe(ErasureStatus.Open);
        request.DueAt.ShouldBe(reissued + Slo);
        request.OverdueAt.ShouldBeNull();
        request.Reissues.ShouldBe(1);
        request.DomainEvents.ShouldHaveSingleItem()
            .ShouldBe(new ErasureRequestedDomainEvent(Request, Subject, reissued));
        request.Missing.ShouldBe(["payments", "shipping"], "an answer already counted stays counted");
    }

    [Fact]
    public void A_request_may_be_reissued_before_it_is_overdue()
    {
        ErasureRequest request = Raised();

        request.Reissue(Slo, Now.AddDays(1)).ShouldBeTrue();

        request.Reissues.ShouldBe(1);
        request.DueAt.ShouldBe(Now.AddDays(1) + Slo);
    }

    [Fact]
    public void A_closed_request_cannot_be_reissued_because_it_no_longer_holds_the_subject()
    {
        ErasureRequest request = Raised("ordering");
        request.RecordCompletion("ordering", 1, Now);
        request.ClearDomainEvents();

        request.Reissue(Slo, Now.AddDays(1)).ShouldBeFalse();

        request.DomainEvents.ShouldBeEmpty("nothing is asked again about a subject the service no longer knows");
        request.Reissues.ShouldBe(0);
    }

    [Fact]
    public void A_reissue_needs_a_positive_time_to_complete_in()
    {
        ErasureRequest request = Raised();

        Should.Throw<DomainException>(() => request.Reissue(TimeSpan.Zero, Now));
    }
}
