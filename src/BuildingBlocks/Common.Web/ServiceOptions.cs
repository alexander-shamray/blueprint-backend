namespace Common.Web;

/// <summary>
/// §15.4's static constants — the tier of settings that are genuinely not
/// configuration, and are therefore not bound, not validated and not
/// deployable. §15.4's test is whether a member would differ between
/// Compose, the fixture and production; nothing here does, and
/// <c>ServiceIdentityOptions</c> is a type that passes it (§11.5).
/// </summary>
/// <remarks>
/// It caps a hierarchy that is the platform's and not one host's (§9.7).
/// </remarks>
public static class ServiceOptions
{
    /// <summary>
    /// The ceiling §9.7's timeout hierarchy asserts against — a service's total
    /// budget for one operation, which every outbound client total must sit
    /// below.
    /// </summary>
    /// <remarks>
    /// Twenty seconds — the middle of §9.7's 10–30 s band, and **not** its top,
    /// which is the correction PR-19 needed. The band's job is to sit under the
    /// gateway's 30–60 s and over the 5 s outbound total; at 30 it does not,
    /// because a gateway taking its own band's floor is also at 30 and the two
    /// TIE. §9.7's invariant is a strict decrease and
    /// <c>ResilienceHierarchyTests</c> says so in as many words — "an ordering
    /// has no ties" — so the value contradicted the rule its own test asserts.
    /// A compile-time invariant, not a deployable value.
    /// <para>
    /// <b>Nothing enforces it at runtime, and that is worth stating where the
    /// value lives.</b> No host registers request-timeout middleware, so this
    /// is the ceiling the outbound budget is <i>checked against</i> rather than
    /// a timeout any request will hit — which is exactly what §9.7 asks for
    /// ("the ordering is the invariant … what to assert in a
    /// configuration-validation test"), and is less than the word "timeout"
    /// suggests on its own.
    /// </para>
    /// <para>
    /// Making the tier real means request-timeout middleware in every host, and
    /// a 504 in §10.5's table to go with it — a platform decision about the
    /// error contract rather than something the BFF may take alone, which is
    /// why PR-19 named the gap instead of closing it.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(20);
}
