using Common.Application;

namespace Shipping.Application.Privacy.ErasePersonalData;

/// <summary>Answers with how many records it anonymised, which the caller reports once the unit commits.</summary>
public sealed record ErasePersonalDataCommand(Guid RequestId, Guid SubjectId) : ICommand<Result<int>>;
