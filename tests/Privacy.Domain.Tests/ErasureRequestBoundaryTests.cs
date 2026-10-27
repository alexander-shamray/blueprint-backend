using Privacy.Domain.ErasureRequests;
using Shouldly;
using Xunit;

namespace Privacy.Domain.Tests;

// Pins ErasureRequest's edges and wording.
public class ErasureRequestBoundaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Request = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Subject = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Operator = new("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly TimeSpan Slo = TimeSpan.FromDays(30);

    [Theory]
    [InlineData("az09")]
    [InlineData("z")]
    [InlineData("0")]
    [InlineData("9")]
    [InlineData("a-z")]
    public void Every_edge_of_the_closed_vocabulary_is_a_name(string name)
    {
        ErasureRequest.IsResponderName(name).ShouldBeTrue();
    }

    [Fact]
    public void A_reason_of_the_longest_length_is_accepted()
    {
        ErasureRequest request = ErasureRequest.Raise(Request, Subject, ["ordering"], Slo, Now);
        string reason = new('x', ErasureRequest.MaxReasonLength);

        request.AmendResponders(["bff"], [], reason, Operator, Now).ShouldBeFalse();

        request.Amendments.ShouldHaveSingleItem().Reason.ShouldBe(reason);
    }

    [Fact]
    public void A_set_joined_to_exactly_the_column_width_is_accepted_and_one_over_says_why()
    {
        string[] twelve = [.. Enumerable.Range(0, 12).Select(i => $"{(char)('a' + i)}{new string('x', 31)}")];
        string[] fits = [.. twelve, "mxxx"];
        string[] overflows = [.. twelve, "mxxxx"];

        string.Join(',', fits).Length.ShouldBe(ErasureRequest.MaxRespondersLength);
        string.Join(',', overflows).Length.ShouldBe(ErasureRequest.MaxRespondersLength + 1);

        ErasureRequest.WhyNotAResponderSet(fits).ShouldBeNull();
        ErasureRequest.WhyNotAResponderSet(overflows).ShouldNotBeNull().ShouldContain("characters");
    }

    [Fact]
    public void A_closed_request_says_that_it_has_closed()
    {
        ErasureRequest request = ErasureRequest.Raise(Request, Subject, ["ordering"], Slo, Now);
        request.RecordCompletion("ordering", 1, Now);

        request.WhyNotAnAmendment(["bff"], []).ShouldNotBeNull().ShouldContain("closed");
    }

    [Fact]
    public void A_refused_name_says_how_long_a_name_may_be()
    {
        ErasureRequest request = ErasureRequest.Raise(Request, Subject, ["ordering"], Slo, Now);

        request.WhyNotAnAmendment(["Bad"], []).ShouldNotBeNull().ShouldContain("at most 32 long");
        ErasureRequest.WhyNotAResponderSet(["Bad"]).ShouldNotBeNull().ShouldContain("at most 32 long");
    }

    [Fact]
    public void What_a_store_materialises_starts_empty()
    {
        Create<ErasureAmendment>().Added.ShouldBe("");
        Create<ErasureAmendment>().Removed.ShouldBe("");
        Create<ErasureAmendment>().Reason.ShouldBe("");
        Create<ErasureCompletion>().Responder.ShouldBe("");
        Create<ErasureRequest>().Responders.ShouldBe([""]);
    }

    private static T Create<T>() => (T)Activator.CreateInstance(typeof(T), nonPublic: true)!;
}
