using Common.Application;

namespace Payments.Application.Privacy.ErasePersonalData;

/// <summary>Answers with how many order records it anonymised, which the caller reports once the unit commits.</summary>
public sealed record ErasePersonalDataCommand(Guid RequestId, Guid SubjectId) : ICommand<Result<int>>;
