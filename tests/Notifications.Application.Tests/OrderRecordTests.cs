using Common.Contracts.Ordering.V1;
using Notifications.Application.Records;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

/// <summary>The order record's one move: a cancellation, recorded once and never cleared (ADR-049).</summary>
public class OrderRecordTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_record_names_its_customer_and_no_cancellation()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();

        OrderRecord record = OrderRecord.For(order, customer, Now);

        record.OrderId.ShouldBe(order);
        record.CustomerId.ShouldBe(customer);
        record.RecordedAt.ShouldBe(Now);
        record.CancelledAt.ShouldBeNull();
        record.CancelReason.ShouldBeNull();
        record.CancelOrigin.ShouldBeNull();
    }

    [Fact]
    public void A_cancellation_records_its_reason_origin_and_instant()
    {
        OrderRecord record = OrderRecord.For(Guid.CreateVersion7(), Guid.CreateVersion7(), Now);

        record.Cancel(CancelReasons.CustomerRequest, CancelOrigins.User, Now.AddMinutes(3)).ShouldBeTrue();

        record.CancelledAt.ShouldBe(Now.AddMinutes(3));
        record.CancelReason.ShouldBe(CancelReasons.CustomerRequest);
        record.CancelOrigin.ShouldBe(CancelOrigins.User);
    }

    [Fact]
    public void A_second_cancellation_keeps_the_first()
    {
        // ADR-049 reads the first: a later one, a republish or a redelivery past the inbox, must not rewrite it.
        OrderRecord record = OrderRecord.For(Guid.CreateVersion7(), Guid.CreateVersion7(), Now);
        record.Cancel(CancelReasons.PaymentDeclined, CancelOrigins.Workflow, Now);

        record.Cancel(CancelReasons.CustomerRequest, CancelOrigins.User, Now.AddHours(1)).ShouldBeFalse();

        record.CancelReason.ShouldBe(CancelReasons.PaymentDeclined);
        record.CancelOrigin.ShouldBe(CancelOrigins.Workflow);
        record.CancelledAt.ShouldBe(Now);
    }

    [Fact]
    public void A_cancellation_with_no_origin_is_recorded_as_an_older_publisher_sent_it()
    {
        OrderRecord record = OrderRecord.For(Guid.CreateVersion7(), Guid.CreateVersion7(), Now);

        record.Cancel(CancelReasons.PaymentTimeout, origin: null, Now).ShouldBeTrue();

        record.CancelOrigin.ShouldBeNull("ADR-049's reading of an absent origin is the send worker's");
    }

    [Fact]
    public void A_code_the_column_cannot_hold_is_the_caller_s_defect()
    {
        OrderRecord record = OrderRecord.For(Guid.CreateVersion7(), Guid.CreateVersion7(), Now);
        string tooLong = new('r', OrderRecordLimits.MaxCodeLength + 1);

        Should.Throw<ArgumentOutOfRangeException>(() => record.Cancel(tooLong, CancelOrigins.User, Now));
        Should.Throw<ArgumentOutOfRangeException>(() => record.Cancel(CancelReasons.OutOfStock, tooLong, Now));
        record.CancelledAt.ShouldBeNull();
    }
}
