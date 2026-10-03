# The realm gate

**What §11's token obligations are, which realms are held to them, and what
this tree deliberately does not check.**

[ADR-042](../../docs/backend-architecture/adr/ADR-042-the-deployed-realm-is-checked-at-deploy-time.md)
is the decision; [§15.1](../../docs/backend-architecture/15-cicd-deployment.md)
lists this as the fifth `deploy/**` subtree CI exercises rather than deploys.
This file is that tree's operational reference, on
[`deploy/observability/README.md`](../observability/README.md)'s terms.

## The two subjects, at three moments

| | |
|---|---|
| `realm.yml`, `check` | `deploy/compose/keycloak/realm-export.json` — [§14.1](../../docs/backend-architecture/14-local-development.md)'s Compose realm, on every change to it, to `AuthenticationExtensions.cs` or to this tree |
| `deploy.yml` | the realm a deployment is about to be rolled onto — **derived from the chart**, fetched by `read_admin.py`, and judged before the rollout changes anything |
| `realm.yml`, `deployed` | the realm every deployed release points at, **between rollouts** — the same three calls, hourly and on `workflow_dispatch`, over every workload `deploy/canary/deployables/` describes ([ADR-043](../../docs/backend-architecture/adr/ADR-043-the-deployed-realm-is-checked-between-rollouts.md)) |

**One predicate judges both subjects**, because a Keycloak realm export and the
admin API's `RealmRepresentation` are the same document — the export is that
representation serialised, and the client list a full export carries under
`clients` is what `GET /admin/realms/{realm}/clients` answers. **And one
derivation names the deployed one at both moments**: the scheduled job derives
the realm out of each release's `identity.authority` exactly as the rollout
does, pins the origin from the same Environment variable, and the suite asserts
the three calls are there, in order, in both files.

```bash
py -3.12 -m unittest discover -s deploy/keycloak
py -3.12 deploy/keycloak/realm_check.py inputs
py -3.12 deploy/keycloak/realm_check.py check --kind local
```

## What it asserts

- `accessTokenLifespan` equals the `AccessTokenLifetime` `Common.Web` declares.
  **Read out of that declaration**, never restated —
  [ADR-040](../../docs/backend-architecture/adr/ADR-040-no-host-accepts-a-token-with-more-life-left-than-the-revocation-bound.md)
  made it the one place the number lives.
- No client carries an `access.token.lifespan` attribute disagreeing with it.
  Keycloak resolves the client attribute over the realm value, so a realm at
  300 with one client at 18000 issues five-hour tokens to that client.
- No client enables the implicit flow, which is what makes
  `accessTokenLifespanForImplicitFlow` unreachable and its silence honest.
- `web-app` carries `use.refresh.tokens` `"false"`
  ([ADR-034](../../docs/backend-architecture/adr/ADR-034-the-browser-holds-an-access-token-and-no-refresh-token.md)),
  and carries it — an absent attribute is the violation, because Keycloak's
  default is to issue refresh tokens on the standard flow.
- `web-app` enables the standard flow, without which the line above holds
  because the client mints nothing at all.
- `directAccessGrantsEnabled` is **off in a deployed realm and on in the local
  one**. That is the one obligation that inverts, which is why `--kind` is
  required and has no default: §11.2 documents the password grant as a local
  affordance a deployed realm turns off, and §14.1's documented login *is* that
  grant.
- **`web-app` and `mobile-app` each declare a browser origin.** Keycloak
  grants CORS to none by default, and a client that runs its token exchange
  from a page cannot read its own token response without one — silently,
  because the request is CORS-safelisted and nothing preflights. The entries
  are judged for shape and not for value: non-empty, no `*`, no `+` on a
  client whose redirect URIs imply no origin, and each remaining entry equal
  to the canonical origin a browser would send
  ([ADR-046](../../docs/backend-architecture/adr/ADR-046-each-client-declares-a-browser-origin-and-the-gate-asserts-the-shape.md)).
- **`mobile-app`'s own shape**, and the realm's refresh-token rotation behind
  it — cited rather than enumerated here, because
  [ADR-044](../../docs/backend-architecture/adr/ADR-044-the-native-client-holds-a-refresh-token-and-the-realm-rotates-it.md)
  argues each of them and `check_mobile_client` is the list.
- **`shipping-worker`'s own shape**, as far as a client object reaches: one
  such client, confidential, service accounts on, no interactive flow, and
  `commerce-api` a default client scope and not an optional one — cited rather
  than enumerated here, because
  [ADR-052](../../docs/backend-architecture/adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)
  argues each of them and `check_worker_client` is the list. The grant itself
  is not among them: a service account's roles live on its user, which a
  deployed realm's client list does not carry and the projection drops from an
  export, so `check_worker_client` reaches the client object and the token
  client's own check is the other half (ADR-052).
- **`notifications-worker`'s own shape**, as far as a client object reaches:
  one such client, confidential, service accounts on, no interactive flow,
  `commerce-api` in neither scope list and `roles` in one — cited rather than
  enumerated here, because
  [ADR-052](../../docs/backend-architecture/adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)
  argues each of them and `check_contact_client` is the list. Its grant,
  `view-users` on `realm-management`, is out of reach for the reason the
  bullet above gives, and the worker's check on its own token is the other
  half.

## What it does not check

- **Whether a declared browser origin is the right one.** The shape is checkable
  from here and the value is not: what a packaged app's browser origin actually
  is comes out of `capacitor.config.ts` in the `blueprint-frontend`
  repository, which this one neither owns nor can read. A realm naming an
  origin the app does not send passes this gate and fails on the device. Only
  a packaged build reaching the stack establishes the two are the same, and
  that round trip belongs to the sibling repository rather than to a check
  this file could grow.
- **`ClockSkew`.** It is the other half of ADR-033's 330 and it is not a realm
  setting at all — it is `Common.Web`'s, pinned by `JwtAuthenticationTests`.
  An operator told to configure a realm `ClockSkew` would go looking for
  something that does not exist.
- **Everything else in the realm — and it does not merely decline to check
  it, it does not hold it.** What the gate judges is a projection of the keys
  `REALM_FIELDS`, `CLIENT_FIELDS` and `CLIENT_ATTRIBUTES` name, so the audience
  mapper, the permission vocabulary, the client scopes' own definitions and
  their mappers, the two development logins and every client secret are not in
  the object at all — a client's two scope lists are, for the obligations
  above that read them. Those belong
  to `tests/Common.Web.Tests/RealmImportTests.cs`, which is not superseded.
  The projection is also why no message here can leak a credential: there is
  none to leak.
- **A realm with more clients than the ceiling.** The client list is read in
  one request asking for far more than any realm this platform will have, and a
  response *at* that ceiling stops the run rather than being truncated.
- **Whether the answer was filtered — directly.** Keycloak applies `max` to the
  client-model stream and then drops the representations the caller may not
  see, so a filtered list and a complete one look the same and no ceiling or
  page boundary can tell them apart. What is checked instead is the premise:
  `read_admin.py` reads the roles out of its own token and stops before asking
  unless the account holds `view-clients` or `realm-admin`. The
  token is decoded and **not verified**, which is safe because nothing is
  authorised on it — the server issued it and the server enforces the roles;
  it is read to find out whether this account could see the whole realm.
  **`view-realm` is not accepted** and reads as though it should be: Keycloak
  defines it as a non-composite role granting nothing else, so it carries no
  client visibility. The suite asserts that against §14.1's export rather than
  restating it here.
- **A realm edited since the last scheduled run.** Between rollouts the realm
  is read on `realm.yml`'s schedule, so what bounds the window a drift is live
  in is that cadence and ADR-040's runtime guard together — nominally an hour
  of the first, and only as reliably as GitHub runs a schedule, which is best
  effort: a run can be delayed or dropped under load, without a red one. For
  the token lifetime the second gates *remaining* life rather than the issued
  one. A shorter cron is a one-line change; what would make the hour a bound
  is the monitor the next bullet names, and what the job cannot become is
  continuous — the section below says what a red run does.
- **Whether the schedule is still running.** A dropped run and the sixty-day
  suspension are the same silence at two scales: GitHub sheds or delays a
  scheduled run under load, and suspends the `schedule` trigger outright in a
  repository with no commit for sixty days, and neither leaves a red run — a
  stabilised service is exactly the repository with no commits and exactly the
  one the window is longest on. Nothing in this tree can observe its own
  absence; what closes both is a monitor outside GitHub asking whether this
  workflow ran in the last day, and that is an operating decision this file
  names rather than takes.
- **Which realm a *future* rollout will install.** The authority is read from
  the release as it stands, so a rollout that also changes `identity.authority`
  checks the realm it is leaving. Nothing here does that — `-f
  stable-values.yaml` carries the value forward — but a change that started
  moving authorities would owe this file a second look.
- **Anything about a cluster.** Neither deployed moment has ever run, because
  `deploy.yml` has never reached a cluster and says so in its own header, and
  the scheduled job's first command is a `helm get values` that needs the same
  missing login. What has been executed is the local half — which is exactly
  why the local half exists: a deploy-time check nothing has ever run is a
  check nobody has established is looking at anything.

## What a red scheduled run means

`realm.yml`'s `deployed` job files an issue when its judgement goes red, or
comments on the open one — titled *The deployed realm no longer holds section
11's token obligations, or could not be read*, labelled `security` and
`critical`, linking the run. It is an issue and not a runbook because
`docs/runbooks/` is one file per Prometheus alert and its gate fails on a
runbook with no alert behind it; nothing in §13.6 fires this, so the procedure
is here.

1. **Read the run log first, and decide which of the two it was.** The job
   cannot tell a drifted realm from a realm it could not read — an expired
   credential and a raised lifespan are both red — and it labels for the
   worse case on the rollout's rule that a realm nobody can see holds no
   guarantee anybody can state. Each workload is its own log group; the
   `realm-gate:` lines name the field and the client, and a `read_admin.py`
   refusal names what it could not do.
2. **A drift is corrected in the realm, never in the gate.** The obligations
   are the list above and every one of them is a setting ADR-033 or ADR-034
   rests on. Deleting or loosening the check to clear the issue converts a
   realm that was seen to be wrong into one nobody looks at, which is the
   state #157 was filed about.
3. **A credential failure is [`docs/secrets.md`](../../docs/secrets.md)'s
   rotation procedure**, and until it is done the next rollout is refused as
   well — the same credential, the same refusal.
4. **Close the issue on a green `workflow_dispatch` of `realm.yml`**, from
   `main`, and on nothing weaker. A run that goes green because the schedule
   was disabled is the sixty-day silence above, arranged by hand.

**Nothing deploys onto the drift meanwhile.** `deploy.yml` runs the same
judgement before any rollout changes anything, so what the issue tracks is the
window in which the realm is live as it stands — and the token lifetime's half
of that window is already bounded at every host by ADR-040.

**The job is opted in, and the opt-in is not the skip this repository
refuses.** `deploy.yml` runs on `workflow_dispatch` alone because a rollout on
`push` would go red on every merge for want of a cluster; an hourly job would
do that twenty-four times a day. The skip refused elsewhere reads a *runtime*
failure as absence; this guard reads a *configuration*: the repository
variable `REALM_CHECK_SCHEDULED` is set to `enabled` by whoever provisions the
`production` Environment, and until then there is no realm for the job to be
silent about. Once it is set nothing below it is optional. A repository
variable rather than an Environment one, because a job-level `if:` is
evaluated before the job enters its Environment.

## The credential

`read_admin.py` needs four values and stops naming every one that is missing,
and they arrive from two different places on purpose. **Two callers hold
them**: the rollout, once per dispatch, and the scheduled job, once an hour —
the second consumer [`docs/secrets.md`](../../docs/secrets.md) argues as a
second grant, and it is the same account with the same two reads.

**Two are configured.** `KEYCLOAK_CHECK_CLIENT_ID` and
`KEYCLOAK_CHECK_CLIENT_SECRET` come from the `production` GitHub Environment;
[`docs/secrets.md`](../../docs/secrets.md) carries how the service account is
provisioned and rotated, and why it is the one credential here that reaches no
row of §15.4's table.

**Two are derived.** `KEYCLOAK_BASE_URL` (the **server root**, not the realm's
issuer URL) and `KEYCLOAK_REALM` are written into `$GITHUB_ENV` by
`realm_check.py authority` in the rollout, and exported per release inside the
scheduled job's loop, out of the `identity.authority` the release is running
with. A realm named beside the chart rather than out of it is a check that can
pass on a realm nobody is deploying to.

**And one pins where the credential may be sent.** `KEYCLOAK_TRUSTED_ORIGIN` is
a third Environment variable, and it is what keeps the derivation above from
being worse than the hole it closed: `identity.authority` lives in the cluster,
so deriving the *origin* from it would let whoever can edit a release have this
job post its client secret to a host of their choosing. The origin is pinned
here, the realm is derived there, and `authority` refuses a release whose
authority names any other origin.

A base URL that is not `https` is refused: the bearer token that fetch obtains
can read every client secret in the realm. So is a redirect — `urllib` carries
an `Authorization` header onto a redirected request, and the `https` check says
nothing about where a 302 leads.
