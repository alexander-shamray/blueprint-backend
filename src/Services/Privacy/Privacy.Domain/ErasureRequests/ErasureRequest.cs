using Common.Domain;
using Privacy.Domain.ErasureRequests.Events;

namespace Privacy.Domain.ErasureRequests;

/// <summary>One data subject's request to be erased, and what the holders have answered (ADR-092).</summary>
/// <remarks>
/// The responder set is stored as it stood when the request was raised, so a configuration change moves no open
/// request; only an operator's recorded amendment does (ADR-136). The subject's id is personal data and stays until
/// the request closes and the hash replaces it (ADR-092).
/// </remarks>
public sealed class ErasureRequest : AggregateRoot<Guid>
{
    /// <summary>The widest a responder's name may be.</summary>
    public const int MaxResponderLength = 32;

    /// <summary>The width of the column the whole set is joined into, which the set may not outgrow.</summary>
    public const int MaxRespondersLength = 400;

    /// <summary>The longest reason an amendment may give.</summary>
    public const int MaxReasonLength = 400;

    private string _respondersCsv = "";
    private readonly List<ErasureCompletion> _completions = [];
    private readonly List<ErasureAmendment> _amendments = [];

    /// <summary>The subject, or null once the request has closed and only the hash remains.</summary>
    public Guid? SubjectId { get; private set; }

    public ErasureStatus Status { get; private set; }

    public DateTimeOffset RaisedAt { get; private set; }

    /// <summary>When the request becomes overdue if it is still open (ADR-092).</summary>
    public DateTimeOffset DueAt { get; private set; }

    /// <summary>The holders expected to answer: the set it was raised with, as any amendment left it.</summary>
    public IReadOnlyList<string> Responders => _respondersCsv.Split(',');

    /// <summary>Every answer heard, including any from a name outside the set, which is flagged and not counted.</summary>
    public IReadOnlyList<ErasureCompletion> Completions => _completions.AsReadOnly();

    /// <summary>Each change an operator made to the set, in order.</summary>
    public IReadOnlyList<ErasureAmendment> Amendments => _amendments.AsReadOnly();

    /// <summary>The hash the holders' audit rows carry, which replaces the subject's id when the request closes.</summary>
    public string? SubjectHash { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public DateTimeOffset? OverdueAt { get; private set; }

    /// <summary>How many times an operator has reissued the request (ADR-092).</summary>
    public int Reissues { get; private set; }

    /// <summary>The expected holders that have not yet been counted.</summary>
    public IReadOnlyList<string> Missing
    {
        get
        {
            List<string> missing = [];
            foreach (string responder in Responders)
            {
                if (!IsCounted(responder))
                    missing.Add(responder);
            }

            return missing;
        }
    }

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
    /// Records a holder's answer, closing the request when every expected holder has been counted.
    /// </summary>
    /// <remarks>A closed request ignores a late answer, since the subject is no longer here to match it to (ADR-092).</remarks>
    public CompletionOutcome RecordCompletion(string responder, int count, DateTimeOffset now)
    {
        if (!IsResponderName(responder))
            throw new DomainException("A completion names a holder by a valid name.");

        if (count < 0)
            throw new DomainException("A holder cannot have erased a negative number of records.");

        if (Status == ErasureStatus.Closed)
            return CompletionOutcome.Ignored;

        bool expected = IsExpected(responder);

        foreach (ErasureCompletion heard in _completions)
        {
            if (heard.Responder == responder)
            {
                heard.Repeat(count, now);
                return expected ? CompletionOutcome.Repeated : CompletionOutcome.Unexpected;
            }
        }

        _completions.Add(new ErasureCompletion(responder, count, expected, now));

        if (!expected)
            return CompletionOutcome.Unexpected;

        if (Missing.Count == 0)
            Close(now);

        return CompletionOutcome.Counted;
    }

    /// <summary>Moves an open request that has outlived its due time to overdue; false when it has not.</summary>
    public bool MarkOverdue(DateTimeOffset now)
    {
        if (Status != ErasureStatus.Open || now < DueAt)
            return false;

        Status = ErasureStatus.Overdue;
        OverdueAt = now;
        return true;
    }

    /// <summary>
    /// Asks the holders again under the same request, with a fresh time to answer in. False for a closed request,
    /// which no longer holds the subject to ask about (ADR-092).
    /// </summary>
    public bool Reissue(TimeSpan completionSlo, DateTimeOffset now)
    {
        if (completionSlo <= TimeSpan.Zero)
            throw new DomainException("A reissue needs a positive time to complete in.");

        if (Status == ErasureStatus.Closed || SubjectId is not { } subject)
            return false;

        Status = ErasureStatus.Open;
        DueAt = now + completionSlo;
        OverdueAt = null;
        Reissues++;
        Raise(new ErasureRequestedDomainEvent(Id, subject, now));
        return true;
    }

    /// <summary>
    /// Changes the set of a request that has not closed, records who did it and why, and closes the request when
    /// every holder left in the set has been counted; true when it closed. Publishes nothing: a holder the change
    /// adds is asked by a reissue (ADR-136).
    /// </summary>
    public bool AmendResponders(
        IReadOnlyCollection<string> add,
        IReadOnlyCollection<string> remove,
        string reason,
        Guid amendedBy,
        DateTimeOffset now)
    {
        string? unfit = WhyNotAnAmendment(add, remove);
        if (unfit is not null)
            throw new DomainException(unfit);

        if (string.IsNullOrWhiteSpace(reason) || reason.Length > MaxReasonLength)
            throw new DomainException($"An amendment gives a reason of at most {MaxReasonLength} characters.");

        if (amendedBy == Guid.Empty)
            throw new DomainException("An amendment names the operator who made it.");

        _respondersCsv = string.Join(',', AmendedSet(add, remove));

        foreach (ErasureCompletion heard in _completions)
            heard.Recount(IsExpected(heard.Responder));

        _amendments.Add(new ErasureAmendment(_amendments.Count + 1, add, remove, reason, amendedBy, now));

        if (Missing.Count > 0)
            return false;

        Close(now);
        return true;
    }

    /// <summary>Why the set cannot take this change, or null when it can.</summary>
    public string? WhyNotAnAmendment(IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove)
    {
        if (Status == ErasureStatus.Closed)
            return "The request has closed, so its set no longer decides anything.";

        if (add.Count == 0 && remove.Count == 0)
            return "An amendment adds or removes at least one holder.";

        List<string> named = new(add);
        named.AddRange(remove);

        foreach (string name in named)
        {
            if (!IsResponderName(name))
            {
                return $"'{name}' is not a lower-case name of letters, digits and hyphens, " +
                    $"at most {MaxResponderLength} long, not starting with a hyphen.";
            }
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string name in named)
        {
            if (!seen.Add(name))
                return $"'{name}' is named twice.";
        }

        foreach (string name in add)
        {
            if (IsExpected(name))
                return $"'{name}' is already in the set.";
        }

        foreach (string name in remove)
        {
            if (!IsExpected(name))
                return $"'{name}' is not in the set.";
        }

        return WhyNotAResponderSet(AmendedSet(add, remove));
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

    // The set in its current order, less what goes, then what arrives in the order given.
    private List<string> AmendedSet(IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove)
    {
        HashSet<string> going = new(remove, StringComparer.Ordinal);
        List<string> set = [];

        foreach (string responder in Responders)
        {
            if (!going.Contains(responder))
                set.Add(responder);
        }

        set.AddRange(add);
        return set;
    }

    private bool IsExpected(string responder)
    {
        foreach (string expected in Responders)
        {
            if (expected == responder)
                return true;
        }

        return false;
    }

    private bool IsCounted(string responder)
    {
        foreach (ErasureCompletion heard in _completions)
        {
            if (heard.Counted && heard.Responder == responder)
                return true;
        }

        return false;
    }

    // The id goes and its hash stays, so what the closed row proves is that the holders answered, not for whom.
    private void Close(DateTimeOffset now)
    {
        SubjectHash = PersonalDataErasure.HashSubject(Id, SubjectId!.Value);
        SubjectId = null;
        Status = ErasureStatus.Closed;
        ClosedAt = now;
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
