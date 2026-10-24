namespace Common.Assistant;

/// <summary>
/// Who a conversation belongs to (AI assistant design §5.2): an opaque id and a display name the host supplies from its
/// own authentication. The core authenticates nothing and stores no credential; it only keys conversations, limits and
/// tool calls by caller.
/// </summary>
public sealed record Caller(string Id, string DisplayName);
