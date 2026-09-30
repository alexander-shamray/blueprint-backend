namespace Common.Infrastructure.Identity;

/// <summary>The key a host read its authority from, registered since §11.3's <c>Common.Web</c> owns it.</summary>
public sealed record AuthorityKeyName(string Name);
