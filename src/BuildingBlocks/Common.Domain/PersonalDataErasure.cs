using System.Security.Cryptography;
using System.Text;

namespace Common.Domain;

/// <summary>A holder's record that it erased a subject's data for one request, holding none of it (ADR-092).</summary>
/// <remarks>Keyed by the request, so a reissue finds its row and a second erasure succeeds silently (§11.7).</remarks>
public sealed class PersonalDataErasure : AggregateRoot<Guid>
{
    public string SubjectHash { get; private set; } = "";
    public int Count { get; private set; }
    public DateTimeOffset ErasedAt { get; private set; }

    private PersonalDataErasure() { }

    /// <summary>
    /// The SHA-256 of the request id's 32 lowercase hex digits then the subject's, as UTF-8, so two requests for one
    /// person do not link (ADR-092).
    /// </summary>
    public static string HashSubject(Guid requestId, Guid subjectId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{requestId:N}{subjectId:N}")));

    public static PersonalDataErasure Record(Guid requestId, Guid subjectId, int count, DateTimeOffset now)
    {
        if (requestId == Guid.Empty)
            throw new DomainException("An erasure belongs to a request.");

        if (subjectId == Guid.Empty)
            throw new DomainException("An erasure belongs to a subject.");

        RequireCount(count);

        PersonalDataErasure erasure = new()
        {
            Id = requestId,
            SubjectHash = HashSubject(requestId, subjectId),
            Count = count,
            ErasedAt = now
        };
        erasure.Raise(new PersonalDataErasedDomainEvent(requestId, count, now));
        return erasure;
    }

    /// <summary>A reissue erases again; the row keeps the most it ever removed, and the holder reports again.</summary>
    public void Repeat(int count, DateTimeOffset now)
    {
        RequireCount(count);

        // A reissue that finds nothing left must not overwrite the record of what the first pass removed.
        Count = Math.Max(Count, count);
        ErasedAt = now;
        Raise(new PersonalDataErasedDomainEvent(Id, count, now));
    }

    private static void RequireCount(int count)
    {
        if (count < 0)
            throw new DomainException("An erasure cannot have removed a negative number of records.");
    }
}

/// <summary>A holder finished erasing for a request; its mapper turns this into the completion (§9.3).</summary>
public sealed record PersonalDataErasedDomainEvent(Guid RequestId, int Count, DateTimeOffset OccurredAt)
    : IDomainEvent;
