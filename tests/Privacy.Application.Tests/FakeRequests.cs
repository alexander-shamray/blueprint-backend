using Privacy.Application.ErasureRequests;
using Privacy.Domain.ErasureRequests;

namespace Privacy.Application.Tests;

/// <summary>An in-memory <see cref="IErasureRequestRepository"/>, holding what a test puts in it.</summary>
internal sealed class FakeRequests : IErasureRequestRepository
{
    private readonly Dictionary<Guid, ErasureRequest> _byId = [];

    public List<ErasureRequest> Added { get; } = [];

    public void Hold(ErasureRequest request) => _byId[request.Id] = request;

    public Task<ErasureRequest?> GetUnclosedForSubjectAsync(Guid subjectId, CancellationToken ct) =>
        Task.FromResult(_byId.Values.FirstOrDefault(r => r.SubjectId == subjectId));

    public Task<ErasureRequest?> GetLockedAsync(Guid requestId, CancellationToken ct) =>
        Task.FromResult(_byId.GetValueOrDefault(requestId));

    public void Add(ErasureRequest request)
    {
        Added.Add(request);
        Hold(request);
    }
}
