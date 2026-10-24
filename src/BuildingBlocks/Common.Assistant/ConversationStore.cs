using System.Security.Cryptography;

namespace Common.Assistant;

/// <summary>
/// The open conversations, in memory only and keyed by caller (AI assistant design §5.1, §5.2): a conversation is
/// visible to its caller alone, so another caller's id reads as unknown. Bounded per caller, in callers and in idle
/// time; a full store forgets its least recently used idle conversation, refusing only when all are running a turn.
/// </summary>
internal sealed class ConversationStore(AssistantLimits limits, TimeProvider time)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Dictionary<string, AssistantSession>> byCaller = new(StringComparer.Ordinal);

    public AssistantSession? Add(Caller caller, Func<string, AssistantSession> create)
    {
        lock (gate)
        {
            Sweep();

            if (!byCaller.TryGetValue(caller.Id, out Dictionary<string, AssistantSession>? own))
            {
                if (byCaller.Count >= limits.MaxCallers && !ForgetIdleCaller())
                {
                    return null;
                }

                own = new(StringComparer.Ordinal);
                byCaller[caller.Id] = own;
            }

            if (own.Count >= limits.MaxConversations && !ForgetIdle(own))
            {
                return null;
            }

            AssistantSession session = create(RandomNumberGenerator.GetHexString(32, lowercase: true));
            own[session.Id] = session;

            return session;
        }
    }

    public AssistantSession? Find(Caller caller, string id)
    {
        lock (gate)
        {
            Sweep();

            return byCaller.TryGetValue(caller.Id, out Dictionary<string, AssistantSession>? own)
                && own.TryGetValue(id, out AssistantSession? session)
                ? session
                : null;
        }
    }

    public bool Delete(Caller caller, string id)
    {
        AssistantSession? session;

        lock (gate)
        {
            if (!byCaller.TryGetValue(caller.Id, out Dictionary<string, AssistantSession>? own)
                || !own.Remove(id, out session))
            {
                return false;
            }

            if (own.Count == 0)
            {
                byCaller.Remove(caller.Id);
            }
        }

        session.Dispose();

        return true;
    }

    private void Sweep()
    {
        DateTimeOffset cutoff = time.GetUtcNow() - limits.IdleTimeout;

        foreach ((string callerId, Dictionary<string, AssistantSession> own) in byCaller.ToList())
        {
            foreach (AssistantSession stale in own.Values.Where(s => !s.Running && s.LastActive < cutoff).ToList())
            {
                own.Remove(stale.Id);
                stale.Dispose();
            }

            if (own.Count == 0)
            {
                byCaller.Remove(callerId);
            }
        }
    }

    private static bool ForgetIdle(Dictionary<string, AssistantSession> own)
    {
        if (own.Values.Where(s => !s.Running).MinBy(s => s.LastActive) is not { } oldest)
        {
            return false;
        }

        own.Remove(oldest.Id);
        oldest.Dispose();

        return true;
    }

    private bool ForgetIdleCaller()
    {
        KeyValuePair<string, Dictionary<string, AssistantSession>>[] idle =
            [.. byCaller.Where(c => c.Value.Values.All(s => !s.Running))];

        if (idle.Length == 0)
        {
            return false;
        }

        KeyValuePair<string, Dictionary<string, AssistantSession>> oldest =
            idle.MinBy(c => c.Value.Values.Select(s => s.LastActive).DefaultIfEmpty(DateTimeOffset.MinValue).Max());

        byCaller.Remove(oldest.Key);

        foreach (AssistantSession session in oldest.Value.Values)
        {
            session.Dispose();
        }

        return true;
    }
}
