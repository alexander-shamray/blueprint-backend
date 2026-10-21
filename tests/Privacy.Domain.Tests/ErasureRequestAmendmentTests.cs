using Common.Domain;
using Privacy.Domain.ErasureRequests;
using Privacy.Domain.ErasureRequests.Events;
using Shouldly;
using Xunit;

namespace Privacy.Domain.Tests;

public class ErasureRequestAmendmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Request = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Subject = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Operator = new("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly TimeSpan Slo = TimeSpan.FromDays(30);

    private const string Reason = "shiping is a misspelling of shipping in privacy.responders";

    [Fact]
    public void Removing_the_only_holder_still_missing_closes_the_request_and_lets_the_subject_go()
    {
        ErasureRequest request = Raise("ordering", "ghost");
        request.RecordCompletion("ordering", 2, Now);

        bool closed = request.AmendResponders([], ["ghost"], Reason, Operator, Now.AddDays(31));

        closed.ShouldBeTrue();
        request.Status.ShouldBe(ErasureStatus.Closed);
        request.ClosedAt.ShouldBe(Now.AddDays(31));
        request.SubjectId.ShouldBeNull();
        request.SubjectHash.ShouldBe(PersonalDataErasure.HashSubject(Request, Subject));
        request.Responders.ShouldBe(["ordering"]);
    }

    [Fact]
    public void A_close_that_dropped_a_holder_before_it_answered_tells_the_holders_nothing()
    {
        // The dropped holder may still give the subject out, so the holders keep asking the request (ADR-139).
        ErasureRequest request = Raise("ordering", "ghost");
        request.RecordCompletion("ordering", 2, Now);

        request.AmendResponders([], ["ghost"], Reason, Operator, Now.AddDays(31)).ShouldBeTrue();

        request.DomainEvents.OfType<ErasureRequestClosedDomainEvent>().ShouldBeEmpty();
    }

    [Fact]
    public void A_last_answer_after_an_earlier_amendment_dropped_an_unanswered_holder_tells_the_holders_nothing()
    {
        ErasureRequest request = Raise("ordering", "ghost");
        request.AmendResponders([], ["ghost"], Reason, Operator, Now).ShouldBeFalse();

        request.RecordCompletion("ordering", 2, Now.AddHours(1));

        request.Status.ShouldBe(ErasureStatus.Closed);
        request.DomainEvents.OfType<ErasureRequestClosedDomainEvent>().ShouldBeEmpty();
    }

    [Fact]
    public void A_dropped_holder_that_answers_before_the_close_lets_the_close_be_published()
    {
        // Its answer says it erased, which is all a published close claims of it (ADR-139).
        ErasureRequest request = Raise("ordering", "shipping");
        request.AmendResponders([], ["shipping"], Reason, Operator, Now).ShouldBeFalse();
        request.RecordCompletion("shipping", 1, Now.AddMinutes(5)).ShouldBe(CompletionOutcome.Unexpected);

        request.RecordCompletion("ordering", 2, Now.AddHours(1));

        request.DomainEvents.OfType<ErasureRequestClosedDomainEvent>().ShouldHaveSingleItem();
    }

    [Fact]
    public void A_close_whose_dropped_holder_had_answered_tells_the_holders_when_it_closed()
    {
        ErasureRequest request = Raise("ordering", "shipping");
        request.RecordCompletion("shipping", 1, Now);
        request.AmendResponders([], ["shipping"], Reason, Operator, Now).ShouldBeFalse();

        request.RecordCompletion("ordering", 2, Now.AddHours(1));

        request.DomainEvents.OfType<ErasureRequestClosedDomainEvent>().ShouldHaveSingleItem()
            .OccurredAt.ShouldBe(Now.AddHours(1));
    }

    [Fact]
    public void Adding_a_holder_whose_answer_was_already_heard_counts_that_answer()
    {
        ErasureRequest request = Raise("ordering", "shiping");
        request.RecordCompletion("ordering", 2, Now);
        request.RecordCompletion("shipping", 1, Now).ShouldBe(CompletionOutcome.Unexpected);

        bool closed = request.AmendResponders(["shipping"], ["shiping"], Reason, Operator, Now);

        closed.ShouldBeTrue("the corrected name had answered, so nothing is missing");
        request.Completions.Single(c => c.Responder == "shipping").Counted.ShouldBeTrue();
    }

    [Fact]
    public void Adding_a_holder_that_has_not_answered_leaves_the_request_waiting_for_it()
    {
        ErasureRequest request = Raise("ordering");
        request.MarkOverdue(Now + Slo + TimeSpan.FromSeconds(1)).ShouldBeTrue();

        bool closed = request.AmendResponders(["bff"], [], Reason, Operator, Now + Slo + TimeSpan.FromDays(1));

        closed.ShouldBeFalse();
        request.Status.ShouldBe(ErasureStatus.Overdue, "a reissue, not the amendment, gives it a fresh time");
        request.Missing.ShouldBe(["ordering", "bff"]);
        request.SubjectId.ShouldBe(Subject);
    }

    [Fact]
    public void Removing_a_holder_that_answered_stops_its_answer_counting()
    {
        ErasureRequest request = Raise("ordering", "payments", "bff");
        request.RecordCompletion("payments", 1, Now);

        request.AmendResponders([], ["payments"], Reason, Operator, Now);

        request.Completions.Single().Counted.ShouldBeFalse("a name outside the set is recorded and never counted");
        request.Missing.ShouldBe(["ordering", "bff"]);
    }

    [Fact]
    public void An_amendment_is_recorded_with_who_made_it_when_and_why()
    {
        ErasureRequest request = Raise("ordering", "shiping");

        request.AmendResponders(["shipping"], ["shiping"], Reason, Operator, Now);
        request.AmendResponders(["bff"], [], "the BFF was left out", Operator, Now.AddHours(1));

        request.Amendments.Count.ShouldBe(2);
        ErasureAmendment first = request.Amendments[0];
        first.Sequence.ShouldBe(1);
        first.Added.ShouldBe("shipping");
        first.Removed.ShouldBe("shiping");
        first.Reason.ShouldBe(Reason);
        first.AmendedBy.ShouldBe(Operator);
        first.AmendedAt.ShouldBe(Now);
        request.Amendments[1].Sequence.ShouldBe(2);
        request.Amendments[1].Removed.ShouldBe("");
    }

    [Fact]
    public void A_closed_request_cannot_be_amended()
    {
        ErasureRequest request = Raise("ordering");
        request.RecordCompletion("ordering", 1, Now);

        request.WhyNotAnAmendment(["bff"], []).ShouldNotBeNull();
        Should.Throw<DomainException>(() => request.AmendResponders(["bff"], [], Reason, Operator, Now));
    }

    [Theory]
    [InlineData("ordering", "", "already in the set")]
    [InlineData("", "bff", "not in the set")]
    [InlineData("", "ordering,payments", "at least one holder")]
    [InlineData("", "", "adds or removes")]
    [InlineData("bff", "bff", "named twice")]
    [InlineData("Not-A-Name", "", "is not a lower-case name")]
    public void An_amendment_the_set_cannot_take_is_refused_with_why(string add, string remove, string why)
    {
        ErasureRequest request = Raise("ordering", "payments");

        request.WhyNotAnAmendment(Split(add), Split(remove)).ShouldNotBeNull().ShouldContain(why);
        Should.Throw<DomainException>(() => request.AmendResponders(Split(add), Split(remove), Reason, Operator, Now));
        request.Responders.ShouldBe(["ordering", "payments"], "a refused amendment changes nothing");
        request.Amendments.ShouldBeEmpty();
    }

    [Fact]
    public void An_amendment_without_a_reason_or_an_operator_is_refused()
    {
        ErasureRequest request = Raise("ordering");

        Should.Throw<DomainException>(() => request.AmendResponders(["bff"], [], " ", Operator, Now));
        string tooLong = new('x', ErasureRequest.MaxReasonLength + 1);

        Should.Throw<DomainException>(() => request.AmendResponders(["bff"], [], tooLong, Operator, Now));
        Should.Throw<DomainException>(() => request.AmendResponders(["bff"], [], Reason, Guid.Empty, Now));
    }

    private static ErasureRequest Raise(params string[] responders) =>
        ErasureRequest.Raise(Request, Subject, responders, Slo, Now);

    private static string[] Split(string names) => names.Length == 0 ? [] : names.Split(',');
}
