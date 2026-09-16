# ADR-034 — The browser holds an access token and no refresh token

**Decision.** [§11.2](../11-identity-authorization.md)'s browser client obtains a
short-lived access token through authorization code with PKCE and is issued
**no refresh token**. Continuity comes from silent renewal against the
authorization endpoint, bounded by the SSO session, rather than from a refresh
token held on the origin. The realm enforces it: the `web-app` client carries
`use.refresh.tokens: "false"`, and `RealmImportTests` pins that it does.

**Why.** A refresh token delivered to the browser must live somewhere script on
the origin can read. One XSS, or one malicious transitive dependency in the
bundle, then yields persistent account takeover that outlives the session and
survives a password change — and with no revocation path
([ADR-033](ADR-033-revocation-is-bounded-by-the-token-lifetime-and-no-denylist-exists.md))
it survives until the refresh token's own expiry. The realm's settings made
that concrete rather than theoretical: `revokeRefreshToken: false` and
`refreshTokenMaxReuse: 0` against an `ssoSessionMaxLifespan` of 36,000 seconds
meant a browser-held refresh token was reusable, never rotated, for up to ten
hours. Removing it collapses the exposure to ADR-033's 330 seconds.

**Terminating the flow in `Web.Bff` is the stronger answer and is not this
one.** Holding the tokens server-side behind a `HttpOnly; Secure; SameSite`
cookie is what the BFF pattern exists for, and the component is already in the
solution. It is not taken here because it is not one decision: it needs an OIDC
handler and a cookie stack in `Web.Bff`, antiforgery on every state-changing
route because cookie auth reintroduces CSRF, a realm change turning `web-bff`
from a service account into a standard-flow relying party, and a change to the
gateway's `web-bff` route, which today requires a bearer the browser would no
longer carry. That is an Appendix C row, and this decision is deliberately the
smaller one that can land now. §11.5's statement that the BFF's only credential
is its own client secret stays correct under this decision and would not survive
that one.

> **`use.refresh.tokens` is set in the local realm and in no other**, on
> exactly ADR-033's terms: `RealmImportTests` pins the Compose realm, every
> chart points at an externally provisioned authority, and nothing in this
> repository validates that one. A deployed realm that leaves the attribute at
> its default issues the browser a refresh token while this record says it does
> not. What is decided here is that the browser must not hold one; enforcing it
> where the realm is actually provisioned is an operational obligation this
> repository states and cannot check.

**Consequences.** The browser re-authenticates against the authorization
endpoint every 300 seconds — the token's own lifetime, which is the renewal
clock rather than ADR-033's 330-second acceptance bound; where the SSO session
has ended, the user sees a login. The residual is stated rather than closed: an
XSS still yields an access token a service will accept for up to 330 seconds,
and §11 says so instead of implying otherwise. Local development is
unaffected — the realm keeps
`directAccessGrantsEnabled` on `web-app` so a developer can obtain a token with
`curl`, which §14.1 documents as a local affordance and §11.2 now names rather
than leaving to a realm-file description.

> **This record's own gap is *not* closed by
> [ADR-040](ADR-040-no-host-accepts-a-token-with-more-life-left-than-the-revocation-bound.md),
> and saying so is the point of this note.** ADR-033's lifetime became
> enforceable at every host because a token carries how long it has left. A
> refresh token carries nothing to a host: it passes between the browser and
> Keycloak and never reaches a service, so `use.refresh.tokens`,
> `standardFlowEnabled` and `directAccessGrantsEnabled` remain obligations on
> whoever provisions a deployed realm, exactly as the callout above states.
> [#157](https://github.com/alexander-shamray/blueprint-backend/issues/157)
> stays open for this half. A reader who has just seen ADR-033's amendment
> should not carry it across to this record, which is why the two notes are
> written the opposite way round.

> **The obligation this record states is checked at deploy time by
> [ADR-042](ADR-042-the-deployed-realm-is-checked-at-deploy-time.md), and what
> that supersedes is the *first* callout above rather than the ADR-040 note
> beside it.** The first callout ends "enforcing it where the realm is actually
> provisioned is an operational obligation this repository states and cannot
> check", and the last four words are the ones that moved: `deploy.yml`'s
> rollout job reads `use.refresh.tokens` off the realm it is about to roll onto
> and refuses a realm that does not carry it. Everything before those four
> words stands — this repository still provisions no realm and owns only the
> Compose one.
>
> **The ADR-040 note is untouched, and its argument is still exactly right.** A
> refresh token never reaches a service, so no runtime control can observe one
> and ADR-040 does not discharge this record's gap. ADR-042 does not observe
> one either — it reads the realm's configuration rather than a token, which is
> the one place `use.refresh.tokens`, `standardFlowEnabled` and
> `directAccessGrantsEnabled` are visible at all.
>
> **So the two notes stay written the opposite way round, and now for a second
> reason.** A reader who has seen ADR-033's amendment should still not carry it
> across; what they may carry across is the *later* record, which reaches this
> obligation and ADR-033's alike because both are settings in one document.
> [#157](https://github.com/alexander-shamray/blueprint-backend/issues/157)
> closes for this half as well, and what is left is narrower and filed:
> a realm edited between rollouts is unobserved until the next one
> ([#176](https://github.com/alexander-shamray/blueprint-backend/issues/176)).
> That one is closed in turn by
> [ADR-043](ADR-043-the-deployed-realm-is-checked-between-rollouts.md), which
> reads the deployed realm on a schedule as well, so the edit is seen at the
> next scheduled run — nominally within the hour, and only as reliably as
> GitHub runs a schedule — rather than at the next rollout.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
