namespace Common.Infrastructure.Identity;

/// <summary>
/// The name of the configuration key a host read its authority from, so that
/// a refusal can say which key to fix. A registered value rather than a
/// constant here: §11.3's registration in <c>Common.Web</c> owns the name,
/// this building block sits below that assembly and may not reference it, and
/// <see cref="Outbox.OutboxTable"/>'s schema travels the same way (§9.4).
/// </summary>
public sealed record AuthorityKeyName(string Name);
