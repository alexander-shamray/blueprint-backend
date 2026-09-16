# ADR-040 — No host accepts a token with more life left than the revocation bound

**Decision.** Every host that composes `AddCommonWebDefaults` refuses an
inbound access token carrying more than `AuthenticationExtensions.RevocationBound`
of remaining life — [ADR-033](ADR-033-revocation-is-bounded-by-the-token-lifetime-and-no-denylist-exists.md)'s
330 seconds, composed from the 300-second lifetime [§11.3](../11-identity-authorization.md)
states and the 30-second `ClockSkew` beside it. The number becomes a constant
in `Common.Web` because something now reads it, and
`RealmImportTests` reads the same constant instead of its own literal.

**This gates remaining life and does not enforce the issued lifetime, which is
the whole of what the title says and the reason it says it.** A five-hour token
is refused for four hours and fifty-four minutes and then admitted for its last
330 seconds — contained, not detected. So **[#157](https://github.com/alexander-shamray/blueprint-backend/issues/157)
stays open in full**: a deployed realm still owes `accessTokenLifespan` 300 and
no client-level override, and nothing here establishes that it has them. What
this record buys is that a realm which ignores that obligation can no longer
have any one of its tokens accepted for more than the bound. ADR-034's
refresh-token rule is untouched and stays an obligation on the same terms.

**Why.** ADR-033 and ADR-034 state platform-wide security guarantees whose
settings live in a realm. `RealmImportTests` pins them against
`deploy/compose/keycloak/realm-export.json` — [§14.1](../14-local-development.md)'s
Compose realm, the only one this repository owns. Every chart points at
`https://id.example.com/realms/commerce`, an externally provisioned authority
this repository holds no configuration for and runs no deploy-time check
against. So a deployed realm could issue five-hour access tokens while every
sentence in §11.2, §11.3, ADR-033 and ADR-034 still read as a platform
guarantee and the suite stayed green
([#157](https://github.com/alexander-shamray/blueprint-backend/issues/157)).

**A token is where the realm's answer is observable without credentials this
repository does not have.** The three shapes #157 named all wanted something
the platform lacks: a pipeline check needs admin credentials in CI, a startup
assertion reads a discovery document that does not expose token lifetimes at
all, and committing a production realm makes somebody's operational input into
this repository's artefact. The tokens themselves need none of that — whatever
the realm was configured to do, a token that reaches a host carries how long it
has left, and every host already validates one on every request.

**Remaining life against this host's clock, and not `exp - iat`.** The exact
form is sharper and was not taken. It needs `iat`, which RFC 7519 makes
optional, so an issuer omitting the claim would switch the control off by
omission — a control any subject can decline is not one. Reading it also means
naming a token type from a package this assembly does not pin, where `ValidTo`
is on `SecurityToken` itself and `exp` is already mandatory, because
`ValidateLifetime` refuses a token without one before the check runs.

**The cost of the inexact form is stated rather than hidden.** A host whose
clock lags the issuer's sees a fresh token as having more life left than it
has, so the ceiling is the **bound** — lifetime plus skew — and not the
lifetime. A realm at 330 seconds passes and one at 320 passes; five hours,
thirty minutes and six minutes do not, which is the class of misconfiguration
this closes and the class #157 describes.

**So the skew is spent twice, and the number that follows is 360 rather than
330.** A token admitted at the ceiling has `RevocationBound` left to live, and
`ValidateLifetime` then accepts it until `exp` plus `ClockSkew` — so the
longest window this control admits is the bound **plus** the skew. **That does
not move ADR-033's bound**, which is produced for a conforming realm by the
realm's 300 and this platform's 30 and against which this check never binds at
all. What the 360 bounds is a *non-conforming* token's acceptance, where the
alternative was hours.

**Capping the ceiling at `AccessTokenLifetime` would make the two equal and was
rejected**, because it is a knife-edge rather than a tighter bound. A host
whose clock lags the issuer's by δ reads a fresh 300-second token as having
300 + δ left, so **any** δ above zero refuses every token a correct realm
issues — and the 30 seconds that would have absorbed it is the term the cap
removes. There is no third value: the exact form is `exp - iat`, which the
paragraph above declines for a reason that has not changed.

**Consequences.**

- **A realm that violates the bound fails loudly at every host rather than
  quietly widening the revocation window.** This is the posture
  `AddJwtAuthentication` already takes for metadata over plain HTTP outside
  Development: a platform that accepts what it says it does not accept has a
  decorative guarantee. It is an availability cost taken deliberately — a
  misconfigured realm 401s everything instead of weakening a security property
  nobody can see — and the failure message names the number and the setting
  behind it.
- **300 becomes a constant in `Common.Web`, and the sentence that refused one
  is why.** `RealmImportTests` carried the literal and said outright that "a
  constant nothing reads would be a registration standing in for a control,
  which is the shape ADR-033 was written to withdraw". That was right while the
  number was only asserted against the shipped realm. The condition changed
  rather than the taste: something reads it now, so one declaration is what
  keeps the control and the realm assertion from disagreeing.
- **The enforced ceiling and the stated bound are two numbers, and both are
  real.** 330 is what a conforming realm produces and what ADR-033 promises;
  360 is the longest window this control will admit, because the skew is spent
  once on the ceiling and once by `ValidateLifetime`. Reporting only the
  smaller one would be the overclaim this record exists to remove, so
  `JwtAuthenticationTests` asserts the sum rather than restating the bound.
- **330 is composed in the control and written down in exactly one place.**
  `RevocationBound` is `AccessTokenLifetime + AllowedClockSkew`, and the skew is
  the same field `TokenValidationParameters.ClockSkew` is set from: a literal
  330 beside a 300 and a 30 is the arithmetic nobody redoes when one of them
  moves. The one place is `JwtAuthenticationTests`, which asserts the sum
  *equals* 330 — deliberately, because a composition nothing pins can be
  recomposed wrongly and still look composed. **"A literal nowhere" is the
  claim to avoid**: the control hard-codes nothing and the suite hard-codes it
  once, on purpose.
- **Neither of ADR-033's nor ADR-034's realm obligations is discharged, and
  saying otherwise was this record's first mistake.** A five-hour token is
  admitted in its final window, so the lifetime obligation is contained rather
  than checked; a refresh token passes between the browser and Keycloak and
  never reaches a service, so `use.refresh.tokens`, `standardFlowEnabled` and
  `directAccessGrantsEnabled` cannot be seen at all. Both remain obligations on
  whoever provisions a deployed realm — the division
  [§15.4](../15-cicd-deployment.md) draws for every Secret, undiminished — and
  #157 stays open for the whole of it.
- **The exact form is `exp - iat` with an absent `iat` failing closed, and it
  is deferred rather than refused.** That would enforce the issued lifetime,
  remove the double-spent skew, and need no clock of this platform's at all,
  since both terms come from the issuer. What it costs is a token type from a
  package `Common.Web` does not pin — so a licence-register row and a
  dependency decision — and a hard refusal of any issuer that omits a claim RFC
  7519 makes optional. Both are decisions rather than details, which is why
  they belong to the record that takes them and not to this one.
- **Nothing about the shipped realm changes**, and the two suites still agree:
  the realm sets 300, the control admits up to 330 of remaining life, and
  `RealmImportTests` reads the constant the control is built from. A realm
  edited to 400 fails that test, and its tokens would be refused for all but
  the last 330 seconds of each — which is one misconfiguration caught twice,
  and neither catch is the deploy-time check #157 asks for.

> **The deploy-time check this record's last consequence names as absent now
> exists — [ADR-042](ADR-042-the-deployed-realm-is-checked-at-deploy-time.md)
> — and nothing here has been edited.** The decision this record took is
> untouched and still binding: every host still refuses a token carrying more
> than the revocation bound of remaining life, the 360 is still the longest
> window the guard admits, and the reasons for declining `exp - iat` have not
> changed.
>
> **Two clauses of this record are named here, because a callout that moves a
> record without saying which sentence it moved is the TODO nothing
> re-checks.** The Decision says "**#157 stays open in full**" and the *Why*
> says the deployed authority is one this repository "runs no deploy-time check
> against". The first is closed and the second is false: the check exists and
> runs in `deploy.yml`'s rollout job. What is still true in both sentences is
> the obligation itself — a deployed realm owes `accessTokenLifespan` 300 and
> no client-level override, whoever provisions it.
>
> **What moved in the *Why* is one of its three refusals.** This record set
> aside a pipeline check because it "needs admin credentials in CI", and that
> was true of CI and is still true of CI. The check ADR-042 runs is not in CI:
> it is in the rollout job, under the `production` GitHub Environment, which is
> the mechanism [§15.4](../15-cicd-deployment.md) already relies on to scope a
> deployment's secrets. The other two refusals stand as written — a discovery
> document publishes no token lifetime, and committing a production realm makes
> somebody's operational input into this repository's artefact.
>
> **The consequence saying neither obligation is discharged is superseded, and
> the containment argument in it is not.** A five-hour token is still admitted
> in its final window and a refresh token is still invisible to every host, so
> what this record buys is unchanged and still worth having — it is what bounds
> a realm nobody has read yet, including one read at the last rollout and
> edited since. What is no longer true is that nothing reads a deployed realm.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
