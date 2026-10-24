namespace Common.Assistant;

/// <summary>
/// What the caller's text and every tool result pass before the core sends them to a provider (AI assistant design
/// §10). The host supplies it, because only the host knows its secrets; per caller, so a host can redact per caller.
/// </summary>
public interface IRedactor
{
    string Redact(string text, Caller caller);
}
