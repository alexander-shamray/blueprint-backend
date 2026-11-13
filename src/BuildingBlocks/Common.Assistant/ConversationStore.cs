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

    /// <summary>
    /// What the caller of <paramref name="keep"/> holds, after forgetting their least recently used idle conversations
    /// but that one while it is past <see cref="AssistantLimits.CallerHistoryChars"/>. A session calls it holding none
    /// of its own lock, since the store's lock is taken before a session's.
    /// </summary>
    public long Trim(AssistantSession keep)
    {
        lock (gate)
        {
            if (!byCaller.TryGetValue(keep.Caller.Id, out Dictionary<string, AssistantSession>? own))
            {
                return keep.Held;
            }

            long held = own.Values.Sum(s => s.Held);

            // A conversation at the budget alone is refused whatever else goes, so the others stay.
            if (keep.Held >= limits.CallerHistoryChars)
            {
                return Math.Max(held, keep.Held);
            }

            foreach (AssistantSession oldest in own.Values.Where(s => s != keep).OrderBy(s => s.LastActive).ToList())
            {
                if (held < limits.CallerHistoryChars)
                {
                    break;
                }

                if (oldest.TryForget())
                {
                    own.Remove(oldest.Id);
                    held -= oldest.Held;
                }
            }

            return own.ContainsKey(keep.Id) ? held : held + keep.Held;
        }
    }

    /// <summary>Forgets what is idle past the timeout, for a caller that opens and finds nothing to do it.</summary>
    public void SweepIdle()
    {
        lock (gate)
        {
            Sweep();
        }
    }

    private void Sweep()
    {
        DateTimeOffset cutoff = time.GetUtcNow() - limits.IdleTimeout;

        foreach ((string callerId, Dictionary<string, AssistantSession> own) in byCaller.ToList())
        {
            foreach (AssistantSession stale in own.Values.Where(s => s.LastActive < cutoff).ToList())
            {
                if (stale.TryForget())
                {
                    own.Remove(stale.Id);
                }
            }

            if (own.Count == 0)
            {
                byCaller.Remove(callerId);
            }
        }
    }

    // TryForget checks a session is idle and closes it in one hold of its lock, so a turn that starts between the
    // two is never cancelled by its own eviction.
    private static bool ForgetIdle(Dictionary<string, AssistantSession> own)
    {
        foreach (AssistantSession oldest in own.Values.OrderBy(s => s.LastActive).ToList())
        {
            if (oldest.TryForget())
            {
                own.Remove(oldest.Id);

                return true;
            }
        }

        return false;
    }

    private bool ForgetIdleCaller()
    {
        // Each conversation is forgotten through TryForget, so a turn started after the read above survives, and so
        // does its caller, though its idle conversations already forgotten stay forgotten: a narrow race, accepted.
        foreach (KeyValuePair<string, Dictionary<string, AssistantSession>> caller in byCaller
                     .Where(c => c.Value.Values.All(s => !s.Running))
                     .OrderBy(c => c.Value.Values.Max(s => (DateTimeOffset?)s.LastActive))
                     .ToList())
        {
            foreach (AssistantSession session in caller.Value.Values.ToList())
            {
                if (session.TryForget())
                {
                    caller.Value.Remove(session.Id);
                }
            }

            if (caller.Value.Count == 0)
            {
                byCaller.Remove(caller.Key);

                return true;
            }
        }

        return false;
    }
}
