using Common.Application;
using Common.Domain;
using Payments.Application.Orders;

namespace Payments.Application.Privacy.ErasePersonalData;

/// <summary>Anonymises the subject's payer id on every order record and writes the audit row (ADR-092).</summary>
/// <remarks>A request already seen repeats rather than fails, so a reissue reports again (§11.7).</remarks>
public sealed class ErasePersonalDataHandler(
    IPaymentOrderStore orders,
    IPersonalDataErasureRepository erasures,
    IErasureReporter reporter,
    TimeProvider clock)
    : ICommandHandler<ErasePersonalDataCommand, Result>
{
    public async Task<Result> HandleAsync(ErasePersonalDataCommand command, CancellationToken ct)
    {
        int count = await orders.AnonymiseCustomerAsync(command.SubjectId, ct);
        DateTimeOffset now = clock.GetUtcNow();

        PersonalDataErasure? seen = await erasures.GetAsync(command.RequestId, ct);
        if (seen is null)
            erasures.Add(PersonalDataErasure.Record(command.RequestId, command.SubjectId, count, now));
        else
            seen.Repeat(count, now);

        // Held by the endpoint until the unit has committed, so nothing is reported for a rolled-back erasure.
        await reporter.ReportAsync(command.RequestId, count, ct);

        return Result.Success();
    }
}
