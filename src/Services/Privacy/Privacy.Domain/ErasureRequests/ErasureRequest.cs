using Common.Domain;
using Privacy.Domain.ErasureRequests.Events;

namespace Privacy.Domain.ErasureRequests;

/// <summary>One data subject's request to be erased, and what the holders have answered (ADR-092).</summary>
/// <remarks>
/// The responder set is stored as it stood when the request was raised, so a later change moves no open request.
/// The subject's id is personal data and stays until the request closes and the hash replaces it (ADR-092).
/// </remarks>
public sealed class ErasureRequest : AggregateRoot<Guid>
{
    /// <summary>The widest a responder's name may be.</summary>
    public const int MaxResponderLength = 32;

    /// <summary>The width of the column the whole set is joined into, which the set may not outgrow.</summary>
    public const int MaxRespondersLength = 400;

    private string _respondersCsv = "";

    /// <summary>The subject, or null once the request has closed and only the hash remains.</summary>
    public Guid? SubjectId { get; private set; }

    public ErasureStatus Status { get; private set; }

    public DateTimeOffset RaisedAt { get; private set; }

    /// <summary>When the request becomes overdue if it is still open (ADR-092).</summary>
    public DateTimeOffset DueAt { get; private set; }

    /// <summary>The holders that were expected to answer when the request was raised.</summary>
    public IReadOnlyList<string> Responders => _respondersCsv.Split(',');

    // EF Core materialisation only (§5.4).
    private ErasureRequest() { }

    public static ErasureRequest Raise(
        Guid requestId,
        Guid subjectId,
        IReadOnlyCollection<string> responders,
        TimeSpan completionSlo,
        DateTimeOffset now)
    {
        if (requestId == Guid.Empty)
            throw new DomainException("An erasure request needs an id.");

        if (subjectId == Guid.Empty)
            throw new DomainException("An erasure request needs a subject.");

        if (completionSlo <= TimeSpan.Zero)
            throw new DomainException("An erasure request needs a positive time to complete in.");

        string? unfit = WhyNotAResponderSet(responders);
        if (unfit is not null)
            throw new DomainException(unfit);

        ErasureRequest request = new()
        {
            Id = requestId,
            SubjectId = subjectId,
            Status = ErasureStatus.Open,
            RaisedAt = now,
            DueAt = now + completionSlo,
            _respondersCsv = string.Join(',', responders)
        };
        request.Raise(new ErasureRequestedDomainEvent(requestId, subjectId, now));
        return request;
    }

    /// <summary>
    /// Why a set cannot be a request's, or null when it can. The aggregate and the options both ask, so the host
    /// refuses at start what the first request would refuse.
    /// </summary>
    public static string? WhyNotAResponderSet(IReadOnlyCollection<string> responders)
    {
        if (responders.Count == 0)
            return "An erasure request needs at least one holder to answer it.";

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string responder in responders)
        {
            if (!IsResponderName(responder))
            {
                return $"'{responder}' is not a lower-case name of letters, digits and hyphens, " +
                    $"at most {MaxResponderLength} long, not starting with a hyphen.";
            }

            if (!seen.Add(responder))
                return $"'{responder}' is named twice.";
        }

        return string.Join(',', responders).Length > MaxRespondersLength
            ? $"The holders together are over {MaxRespondersLength} characters, the width the set is stored in."
            : null;
    }

    /// <summary>A name never holds a comma, which is what lets the set be stored as one delimited column.</summary>
    public static bool IsResponderName(string name)
    {
        if (name.Length is 0 or > MaxResponderLength || name[0] == '-')
            return false;

        foreach (char c in name)
        {
            if (c is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-')
                return false;
        }

        return true;
    }
}
