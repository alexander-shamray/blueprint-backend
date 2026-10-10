using Common.Domain;
using Privacy.Domain.ErasureRequests;
using Privacy.Domain.ErasureRequests.Events;
using Shouldly;
using Xunit;

namespace Privacy.Domain.Tests;

public class ErasureRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Request = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Subject = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly TimeSpan Slo = TimeSpan.FromDays(30);

    private static readonly string[] Holders = ["ordering", "payments", "shipping", "notifications", "bff"];

    [Fact]
    public void A_raised_request_is_open_and_remembers_who_must_answer_and_by_when()
    {
        ErasureRequest request = ErasureRequest.Raise(Request, Subject, Holders, Slo, Now);

        request.Id.ShouldBe(Request);
        request.SubjectId.ShouldBe(Subject);
        request.Status.ShouldBe(ErasureStatus.Open);
        request.RaisedAt.ShouldBe(Now);
        request.DueAt.ShouldBe(Now + Slo);
        request.Responders.ShouldBe(Holders, "the set is stored on the request, in the order it was given");
    }

    [Fact]
    public void Raising_a_request_raises_the_event_the_mapper_publishes()
    {
        ErasureRequest request = ErasureRequest.Raise(Request, Subject, Holders, Slo, Now);

        request.DomainEvents.ShouldHaveSingleItem().ShouldBe(new ErasureRequestedDomainEvent(Request, Subject, Now));
    }

    [Fact]
    public void A_later_change_to_the_list_the_caller_held_moves_nothing()
    {
        List<string> configured = [.. Holders];
        ErasureRequest request = ErasureRequest.Raise(Request, Subject, configured, Slo, Now);

        configured.Remove("shipping");

        request.Responders.ShouldBe(Holders, "a configuration change must not close a request on fewer answers");
    }

    [Fact]
    public void One_holder_is_enough_to_be_a_set()
    {
        ErasureRequest request = ErasureRequest.Raise(Request, Subject, ["ordering"], Slo, Now);

        request.Responders.ShouldBe(["ordering"]);
    }

    [Fact]
    public void A_request_without_an_id_or_a_subject_is_refused()
    {
        Should.Throw<DomainException>(() => ErasureRequest.Raise(Guid.Empty, Subject, Holders, Slo, Now));
        Should.Throw<DomainException>(() => ErasureRequest.Raise(Request, Guid.Empty, Holders, Slo, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_request_with_no_time_to_complete_in_is_refused(int days)
    {
        Should.Throw<DomainException>(
            () => ErasureRequest.Raise(Request, Subject, Holders, TimeSpan.FromDays(days), Now));
    }

    [Fact]
    public void A_request_nobody_is_asked_to_answer_is_refused()
    {
        Should.Throw<DomainException>(() => ErasureRequest.Raise(Request, Subject, [], Slo, Now));
    }

    [Fact]
    public void A_holder_named_twice_is_refused_because_it_would_be_counted_twice()
    {
        Should.Throw<DomainException>(
            () => ErasureRequest.Raise(Request, Subject, ["ordering", "payments", "ordering"], Slo, Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ordering")]
    [InlineData("order ing")]
    [InlineData("ordering,payments")]
    [InlineData("-ordering")]
    [InlineData("ordering_")]
    [InlineData("a23456789012345678901234567890123")]
    public void A_name_outside_the_closed_vocabulary_is_refused(string name)
    {
        Should.Throw<DomainException>(() => ErasureRequest.Raise(Request, Subject, [name], Slo, Now));
    }

    [Fact]
    public void The_most_names_the_column_holds_are_accepted_and_one_more_is_refused()
    {
        string[] fits = [.. Enumerable.Range(0, 12).Select(i => $"{(char)('a' + i)}{new string('x', 31)}")];
        string[] overflows = [.. fits, $"m{new string('x', 31)}"];

        string.Join(',', fits).Length.ShouldBeLessThanOrEqualTo(ErasureRequest.MaxRespondersLength);
        string.Join(',', overflows).Length.ShouldBeGreaterThan(ErasureRequest.MaxRespondersLength);

        ErasureRequest.Raise(Request, Subject, fits, Slo, Now).Responders.ShouldBe(fits);
        Should.Throw<DomainException>(() => ErasureRequest.Raise(Request, Subject, overflows, Slo, Now));
    }

    [Fact]
    public void A_fit_set_has_no_reason_to_be_refused_and_a_refused_one_names_the_holder()
    {
        ErasureRequest.WhyNotAResponderSet(Holders).ShouldBeNull();
        ErasureRequest.WhyNotAResponderSet(["ordering", "Payments"]).ShouldNotBeNull().ShouldContain("Payments");
        ErasureRequest.WhyNotAResponderSet(["ordering", "ordering"]).ShouldNotBeNull().ShouldContain("ordering");
    }

    [Fact]
    public void The_longest_name_the_column_holds_is_accepted()
    {
        string longest = new('a', ErasureRequest.MaxResponderLength);

        ErasureRequest request = ErasureRequest.Raise(Request, Subject, [longest], Slo, Now);

        request.Responders.ShouldBe([longest]);
    }
}
