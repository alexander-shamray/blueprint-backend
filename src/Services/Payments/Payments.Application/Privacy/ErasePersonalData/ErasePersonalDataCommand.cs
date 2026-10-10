using Common.Application;

namespace Payments.Application.Privacy.ErasePersonalData;

public sealed record ErasePersonalDataCommand(Guid RequestId, Guid SubjectId) : ICommand<Result>;
