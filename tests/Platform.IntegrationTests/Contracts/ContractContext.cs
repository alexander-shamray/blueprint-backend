namespace Platform.IntegrationTests.Contracts;

/// <summary>What one service's contracts add to the rules every service's share (§12.6).</summary>
/// <remarks>
/// A subclass sits in <c>Contracts/&lt;Service&gt;</c> beside that service's shape record, so retiring the service
/// deletes the folder (ADR-062). <see cref="ContractContextTests"/> holds the two sides to each other.
/// </remarks>
internal abstract class ContractContext
{
    /// <summary>The <c>&lt;Service&gt;</c> of <c>Common.Contracts.&lt;Service&gt;.V&lt;n&gt;</c> (§9.2).</summary>
    public abstract string Service { get; }

    /// <summary>One sample per contract, by hand, so a contract with none fails rather than skips (§12.6).</summary>
    public abstract IReadOnlyDictionary<Type, Func<object>> Samples { get; }

    /// <summary>Members added to a live contract, optional for that version's life (§9.2).</summary>
    public virtual string[] AdditiveMembers => [];

    /// <summary>The command roots the subject rule judges, from §3.2's Accepts columns (ADR-028).</summary>
    public virtual Type[] CommandRoots => [];

    /// <summary>The payloads a command reaches, which the subject rule judges with it (ADR-028).</summary>
    public virtual Type[] JudgedPayloads => [];

    /// <summary>The payloads only an event reaches, which the subject rule exempts with it (ADR-028).</summary>
    public virtual Type[] EventPayloads => [];

    /// <summary>Every member the judged commands may carry, so a new one is decided at a red build (ADR-028).</summary>
    public virtual (Type Contract, string Member)[] ApprovedCommandMembers => [];
}
