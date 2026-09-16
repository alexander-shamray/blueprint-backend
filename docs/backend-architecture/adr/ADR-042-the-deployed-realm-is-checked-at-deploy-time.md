# ADR-042 — The deployed realm is checked at deploy time

**Decision.** `deploy/keycloak/realm_check.py` asserts §11's token obligations
against a Keycloak **realm representation**, and two things run it. `realm.yml`
runs it over `deploy/compose/keycloak/realm-export.json` on every change to
that file, to `AuthenticationExtensions` or to the gate itself; `deploy.yml`'s
rollout job runs it over the realm a deployment actually points at, fetched
through the admin API by `read_admin.py`, **before the rollout changes
anything**. Which realm that is is **derived from the chart** — `helm get
values` answers the `identity.authority` the release is running with, and
`realm_check.py authority` splits it into the server root and the realm name.
A realm that disagrees fails the rollout rather than being rolled onto. What
is asserted is the table
[#157](https://github.com/alexander-shamray/blueprint-backend/issues/157)
drew: `accessTokenLifespan` equal to the `AccessTokenLifetime`
[ADR-040](ADR-040-no-host-accepts-a-token-with-more-life-left-than-the-revocation-bound.md)
made `Common.Web`'s one declaration of 300, no client-level
`access.token.lifespan` override, no client enabling the implicit flow,
`use.refresh.tokens` `"false"` on `web-app` with `standardFlowEnabled` `true`
beside it, and `directAccessGrantsEnabled` **off in a deployed realm and on in
the local one**. `ClockSkew` is not among them and cannot be: it is
`Common.Web`'s, pinned by `JwtAuthenticationTests`, and an operator told to
configure a realm `ClockSkew` would be looking for something that does not
exist.

**Why.** [ADR-033](ADR-033-revocation-is-bounded-by-the-token-lifetime-and-no-denylist-exists.md)
and [ADR-034](ADR-034-the-browser-holds-an-access-token-and-no-refresh-token.md)
state platform-wide security guarantees whose settings live in a realm, and
until this record the only realm anything read was §14.1's Compose export. Every
chart points at `https://id.example.com/realms/commerce`, so both guarantees
were verified in one realm and *stated* for the one that matters. ADR-040
narrowed what that could cost — a token carrying more than the revocation bound
of remaining life is refused at every host — and said in its own consequences
that the obligations were contained rather than checked.

**ADR-040 declined a pipeline check because one "needs admin credentials in
CI", and that objection is answered by *where* this sits rather than by having
found credentials.** CI holds none and still holds none: the job that reads a
live realm is the rollout job, under the `production` GitHub Environment, which
is the mechanism §15.4 already relies on to scope a deployment's secrets. The
credential is a service account **of the realm being checked**, with realm-read
rights and nothing else — a cross-realm admin account would hold rights over
realms this check has no business reading — and `docs/secrets.md` carries its
provisioning and rotation. The other two shapes are still refused for the
reasons ADR-040 gave: a discovery document publishes no token lifetime at all,
and committing a production realm makes somebody's operational input into this
repository's artefact.

**One predicate, two subjects, and the second one is what makes the first
trustworthy.** A realm export and the admin API's `RealmRepresentation` are the
same document — the export is that representation serialised, and the client
list a full export carries under `clients` is what
`GET /admin/realms/{realm}/clients` answers — so one piece of code judges both.
That is not a convenience. A deploy-time check nothing has ever executed is a
check nobody has established is looking at anything, and this platform has no
cluster to execute it against; running the deciding half against the local realm
on every change is what keeps the instrument honest in the meantime, and the
network half is on the other side of a file boundary so the deciding half has a
suite at all — `deploy/canary`'s split, adopted rather than re-invented.

**One obligation inverts between the two realms, and it is why the kind is an
argument with no default.** §11.2 documents `directAccessGrantsEnabled` as a
local affordance — the password grant a developer obtains a token with — and
says a deployed realm turns it off. So `RealmImportTests` asserts that flag
**true** and this gate asserts it **false** for a deployed realm, and the two
are only coherent if which realm is being judged is named rather than inferred.
A check that guessed would pass a production realm on the local realm's terms,
which is the failure it exists to prevent wearing the costume of the fix.

**The 300 is read out of `AuthenticationExtensions` rather than written into the
gate.** ADR-040 made that declaration the one place the number lives and argued
that a composition nothing pins can be recomposed wrongly and still look
composed; a literal in a Python file would be the same defect in a second
language. The gate stops rather than defaulting when it cannot find the
declaration — substituting 300 for a number it failed to read would make the
read decorative, which is the shape ADR-033 was written to withdraw.

**Consequences.**

- **#157 closes, and what closes it is the rollout step and not the CI one.**
  The issue's complaint is precise: this repository "holds no configuration for
  that realm and runs no deploy-time check against it". It now runs one, and
  the check is the same code that has been observed red — against a lifespan of
  18000, an issued refresh token, and the local realm judged as a deployed one.
  Every row of the issue's table is asserted except `ClockSkew`, which the issue
  itself excludes.
- **The realm checked is the realm installed, and the origin authenticated to
  is the one this deployment names. Those are two different decisions and
  taking either one the other way is a hole.** Naming the realm in the deploy
  environment beside a chart value naming another is a gate that passes on a
  compliant unrelated realm while every host it installs stays pointed at the
  non-compliant one — #157 closed in a description and nowhere else. So the
  realm is derived, by `realm_check.py authority`, out of the
  `identity.authority` that `-f stable-values.yaml` reinstalls two steps later.
  **Deriving the origin from the same value would be worse than that hole**:
  the fetch posts this deployment's client secret to the token endpoint under
  it, and `identity.authority` is held in the cluster, so a release edited to
  `https://attacker.example/realms/x` would send the credential there.
  `KEYCLOAK_TRUSTED_ORIGIN` comes from the deploy environment, the two are
  required to agree, and a rollout onto an identity provider this deployment
  does not name stops rather than authenticating to it. **And what was checked
  is what gets installed**: the authority is pinned as an explicit override on
  every `helm upgrade` in the job, because reading it from the running release
  and then upgrading to a checked-out chart are different questions — an
  authority that came from the old chart's default is replaced by the new
  chart's, and the realm checked a step earlier is not the realm the workload
  ends up on. **The names are
  declared once too**: `read_admin.py` imports them from `realm_check.py`, on
  `deploy/canary`'s import direction, because a writer and a reader spelling a
  variable separately agree until one is edited.
- **A rollout is the only moment a deployed realm is read, and a realm edited
  between rollouts is unobserved until the next one.** That is a narrower gap
  than the one this record closes and it is a real one:
  [#176](https://github.com/alexander-shamray/blueprint-backend/issues/176)
  carries it, because an accepted gap here gets an issue rather than a
  paragraph. Closing it means a scheduled run against an environment, which is
  a decision about operating this platform rather than about building it.
- **The deploy path has never reached a cluster, and this step inherits that
  exactly.** `deploy.yml` says so in its own header — there is no dev, staging
  or production environment, no kubeconfig and no registry — so what has been
  executed is the local half. Writing the step anyway is the same decision that
  workflow already took for the whole rollout, and it is **not** the fiction its
  header refuses: that header declines to write a cluster login for a provider
  nobody has chosen, where Keycloak is chosen in §11.1 and its admin API is not
  a guess.
- **It fails closed in every direction, and each refusal was a decision.** An
  absent environment variable stops the run naming every one that is missing; a
  base URL that is not `https` is refused, because the token this fetch carries
  can read every client secret in the realm; **a redirect is refused rather
  than followed**, because `urllib` copies a request's headers onto the
  redirected one and strips only the content ones, so an `Authorization` header
  would travel to any host a 302 named; **the client list is read in one
  request with a ceiling, a response *at* the ceiling is refused, and the
  account's grant is read out of its own token before the request is made** —
  Keycloak applies `max` to the client-model stream and then drops the
  representations the caller may not see, so a filtered list and a complete one
  are indistinguishable and every per-client obligation is satisfied by the
  clients nobody fetched. No ceiling can establish that; what can be
  established is the premise it rests on, so a run whose account holds neither
  `view-clients` nor `realm-admin` stops before it asks — and `view-realm` is
  not among them, because Keycloak defines it as a non-composite role that
  grants no client visibility at all, which §14.1's export is asserted against
  rather than restated;
  **whatever Helm's `strvals` parser reads as structure is refused in an
  authority**, because the derived value is passed to `--set-string` and a
  comma there makes one assignment into two — the tag preflight's lesson, one
  value over;
  **whitespace in an authority is refused**, because the two values derived
  from it become `NAME=value` lines in `$GITHUB_ENV` and a newline there starts
  a second assignment; an authority this gate cannot split stops it; a realm
  that cannot be read stops
  the rollout on the rule the Prometheus read beside it already follows; a realm
  document with no `clients` array is refused rather than passed, because every
  per-client obligation is vacuously true of an empty one; **the realm the gate
  holds is a projection of six named fields rather than the document it was
  handed**, so no credential is in it to leak into a message — a redaction was
  the first answer and it rested on a deny-list staying complete as Keycloak
  grows fields; a flag that is not a
  boolean is refused rather than compared, because every comparison here is an
  identity test and a string is neither; and a lifetime declaration this gate
  cannot anchor stops it rather than defaulting. **No count opens that
  sentence**, and the omission is deliberate: the list grew by two inside the
  pull request that wrote it.
- **`RealmImportTests` is unchanged and is not superseded.** It pins far more
  than these obligations — the audience mapper, the permission vocabulary, the
  two development logins — and it is the one suite that runs a real Keycloak.
  What this record adds is a second instrument with a different subject: the
  settings that must hold of **any** realm, in a form a deployment can run.
- **The gate's own caller is one of its declared inputs**, and that is not
  bookkeeping. `deploy.yml`'s rollout job runs on `workflow_dispatch` alone, so
  a change deleting or reordering the derive-fetch-judge steps would reach
  `main` with this workflow skipped and nothing else looking at them. It is
  declared, both triggers cover it, and a case asserts the three calls exist,
  run in that order, and run before the first command that changes the cluster.
- **The gate is a fifth `deploy/**` subtree exercised by CI rather than
  deployed**, and §15.1's count moves with it. It is also the fifth workflow to
  reach outside its own tree, and the fifth copy of the Helm tree's
  `SOURCE_INPUTS` — so it arrives owing the reads-direction self-check every
  copy of that pattern was found to owe. `realm_check.py inputs` asserts both
  that every path it reads is declared and that both of its workflow's
  triggers cover every declaration.
- **Nothing about either realm changes.** The Compose realm already held every
  obligation this gate asserts, which is why the gate had to be observed red
  against a mutated copy rather than trusted for passing.
- **Amended by
  [ADR-043](ADR-043-the-deployed-realm-is-checked-between-rollouts.md)**, which
  takes the third bullet's window: the realm is now also read on a schedule,
  and #176 closes. The bullet stands as written because it was true when it
  was written, and the record that moved it is ADR-043.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
