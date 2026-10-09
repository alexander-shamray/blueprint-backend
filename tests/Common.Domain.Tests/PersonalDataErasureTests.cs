using Shouldly;
using Xunit;

namespace Common.Domain.Tests;

public class PersonalDataErasureTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Request = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Subject = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void Recording_an_erasure_keeps_the_hash_and_never_the_subject()
    {
        PersonalDataErasure erasure = PersonalDataErasure.Record(Request, Subject, 3, Now);

        erasure.Id.ShouldBe(Request);
        erasure.Count.ShouldBe(3);
        erasure.SubjectHash.ShouldNotContain(Subject.ToString("N"));
        erasure.SubjectHash.Length.ShouldBe(64);
    }

    [Fact]
    public void Recording_an_erasure_raises_one_event_for_the_request()
    {
        PersonalDataErasure erasure = PersonalDataErasure.Record(Request, Subject, 3, Now);

        erasure.DomainEvents
            .ShouldHaveSingleItem()
            .ShouldBe(new PersonalDataErasedDomainEvent(Request, 3, Now));
    }

    [Fact]
    public void Two_requests_for_one_subject_do_not_share_a_hash()
    {
        Guid other = new("cccccccc-cccc-cccc-cccc-cccccccccccc");

        PersonalDataErasure.HashSubject(Request, Subject)
            .ShouldNotBe(PersonalDataErasure.HashSubject(other, Subject));
    }

    [Fact]
    public void The_hash_is_the_same_for_the_same_request_and_subject()
    {
        PersonalDataErasure.HashSubject(Request, Subject)
            .ShouldBe(PersonalDataErasure.HashSubject(Request, Subject));
    }

    [Fact]
    public void An_erasure_with_nothing_to_erase_is_still_recorded()
    {
        PersonalDataErasure erasure = PersonalDataErasure.Record(Request, Subject, 0, Now);

        erasure.Count.ShouldBe(0);
        erasure.DomainEvents.ShouldHaveSingleItem();
    }

    [Fact]
    public void An_erasure_with_no_request_is_refused()
    {
        Should.Throw<DomainException>(() => PersonalDataErasure.Record(Guid.Empty, Subject, 1, Now));
    }

    [Fact]
    public void An_erasure_with_no_subject_is_refused()
    {
        Should.Throw<DomainException>(() => PersonalDataErasure.Record(Request, Guid.Empty, 1, Now));
    }

    [Fact]
    public void A_negative_count_is_refused()
    {
        Should.Throw<DomainException>(() => PersonalDataErasure.Record(Request, Subject, -1, Now));
    }

    [Fact]
    public void A_repeat_keeps_the_row_and_reports_again()
    {
        PersonalDataErasure erasure = PersonalDataErasure.Record(Request, Subject, 3, Now);
        erasure.ClearDomainEvents();

        erasure.Repeat(0, Now.AddHours(1));

        erasure.ErasedAt.ShouldBe(Now.AddHours(1));
        erasure.DomainEvents.ShouldHaveSingleItem()
            .ShouldBe(new PersonalDataErasedDomainEvent(Request, 0, Now.AddHours(1)));
    }

    [Fact]
    public void A_repeat_that_finds_nothing_keeps_what_the_first_pass_removed()
    {
        PersonalDataErasure erasure = PersonalDataErasure.Record(Request, Subject, 3, Now);

        erasure.Repeat(0, Now.AddHours(1));

        erasure.Count.ShouldBe(3);
    }

    [Fact]
    public void A_repeat_that_finds_more_raises_the_count()
    {
        PersonalDataErasure erasure = PersonalDataErasure.Record(Request, Subject, 3, Now);

        erasure.Repeat(5, Now.AddHours(1));

        erasure.Count.ShouldBe(5);
    }
}
