namespace Notifications.Application.Contacts;

/// <summary>The owner's answer about one customer (ADR-052).</summary>
public abstract record ContactLookup
{
    private ContactLookup()
    {
    }

    /// <summary>A mailbox, and a language tag where the realm holds one; its text names neither (§13.4).</summary>
    public sealed record Found(string Email, string? Locale) : ContactLookup
    {
        public override string ToString() => nameof(Found);
    }

    /// <summary>No such user, a disabled one, or one with no mailbox, collapsed at the adapter (ADR-052).</summary>
    public sealed record NoSuchCustomer : ContactLookup;
}
