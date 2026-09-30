using System.Reflection;
using Common.Application;
using Common.Contracts.Ordering.V1;
using Ordering.Application.Orders.FlagOrderForReview;
using Ordering.Infrastructure.Messaging;
using Shouldly;
using Xunit;

namespace Ordering.Application.Tests;

/// <summary>§9.4's wire-to-command boundary: <c>ReviewReasons</c> and the mapper's own accepted list agree.</summary>
public class CommandMapperTests
{
    private static readonly Guid Order = Guid.Parse("8b3a5c21-4d7e-4f19-8c62-3e5a7b9d1c04");

    /// <summary>Read off the class, so this suite checks the mapper rather than keeping a third copy.</summary>
    private static string[] DeclaredCodes()
    {
        string[] codes =
        [
            .. typeof(ReviewReasons)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f is { IsLiteral: true, IsInitOnly: false })
                .Select(f => (string)f.GetRawConstantValue()!)
        ];

        // Anti-vacuity, not a census; the agreement test below pins the exact set.
        codes.Length.ShouldBeGreaterThanOrEqualTo(3);

        return codes;
    }

    public static TheoryData<string> ReviewReasonCodes()
    {
        TheoryData<string> data = [];
        foreach (string code in DeclaredCodes())
            data.Add(code);

        return data;
    }

    [Theory]
    [MemberData(nameof(ReviewReasonCodes))]
    public void Every_declared_review_reason_is_accepted(string code)
    {
        FlagOrderForReviewMapper mapper = new();

        FlagOrderForReviewCommand mapped = mapper.Map(new FlagOrderForReview(Order, code));

        mapped.OrderId.ShouldBe(Order);
        mapped.Reason.ShouldBe(code);
    }

    [Fact]
    public void The_mapper_accepts_exactly_the_codes_the_vocabulary_declares()
    {
        // The theory above stays green while the mapper accepts a code the class no longer declares.
        FlagOrderForReviewMapper.Known.ShouldBe(DeclaredCodes(), ignoreOrder: true);
    }

    [Fact]
    public void A_code_the_vocabulary_does_not_declare_is_refused()
    {
        // Reason is half ordering.OrderReviews' key, so an unknown code would open an escalation nobody resolves.
        FlagOrderForReviewMapper mapper = new();

        Should.Throw<ContractMappingException>(
            () => mapper.Map(new FlagOrderForReview(Order, "cancelled_after_payent")));
    }
}
