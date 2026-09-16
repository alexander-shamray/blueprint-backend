# Secrets

**How a secret gets from a vault into a running pod, how it is rotated, and
what the five places are that a new required key has to reach.**

This file is the operational half of
[§15.4](backend-architecture/15-cicd-deployment.md), exactly as
[`docs/testing.md`](testing.md) is the operational half of §12 — and it is
outside the blueprint tree on the same terms, so **§15.4 wins wherever the two
disagree.**

**The inventory is not repeated here.** Which keys exist, which are secrets,
which are conditional and which are required for one host only is §15.4's table,
and a second copy would be a second thing to reconcile. What follows is the
procedure that table implies.

## The one rule that decides the kind

**If the value contains a credential, it is a Secret.** Every connection string
in this platform does — SQL Server carries a login, RabbitMQ a user, and Redis
the per-service ACL user of [§8.1](backend-architecture/08-caching-redis.md).

A connection string in a ConfigMap is a password readable by anyone with
namespace read access. There is no "it is only staging".

**What the Kind buys is RBAC, not encryption, and the difference matters
because the wrong half is the one people remember.** A Secret is a separate
resource from a ConfigMap, so `get configmaps` and `get secrets` are separate
verbs and an ordinary read-only role grants only the first — that separation,
plus External Secrets keeping the value out of git entirely, is the reason for
the kind. A Secret's value is **base64, which is an encoding and not
encryption**, and Kubernetes stores it in etcd unencrypted unless an
`EncryptionConfiguration` is enabled on the API server.

**So encryption at rest is a cluster setting this platform depends on and does
not configure.** Confirm it is on before treating a Secret as protected
storage; §15.4 states the same thing from the specification side.

## The path a secret takes

```
External store (vault)
   ↓  ExternalSecret, reconciled by External Secrets Operator
Kubernetes Secret in the namespace
   ↓  secretKeyRef in the pod spec, rendered by the chart (§15.3)
Environment variable, double-underscore spelling
   ↓  .NET configuration binding
IConfiguration / IOptions<T>, validated at startup
```

Two consequences fall out of that chain and both have bitten:

- **The cluster never holds the source of truth.** A Secret edited by hand is
  reverted by the next reconcile, silently. Change the vault.
- **A `secretKeyRef` to a Secret that does not exist is a pod that never
  starts** — not a pod that starts without the value. §15.4 records this as the
  reason the two Redis rows are *conditional* rather than required: marking a
  key required against a solution where no host reads it does not over-supply,
  it stops the service. Since §8.5's behaviour landed the condition is met for
  Catalog and Ordering and unmet for the gateway and the BFF, so it is now a
  question a chart answers rather than one the platform answers once.

## Environment-variable spelling

.NET's double-underscore convention, so `ConnectionStrings__Ordering` binds to
`ConnectionStrings:Ordering`, and an indexed key is
`Ingress__TrustedNetworks__0`.

**Blank counts as missing, and nothing in the binding layer says so.** An
environment variable set to the empty string reaches configuration as `""`, not
null, so `GetRequiredSection` proves only that a section exists. Guard on the
bound values — this is a repository-wide lesson, not a secrets-specific one.

## Adding a required key

**A required setting is a deployment obligation**, and `ValidateOnStart` turns a
missing value into a refusal to boot. That is the right trade only if every
environment supplies it, so adding one means editing **five** places in the same
change:

| | |
|---|---|
| 1. Compose | the host's own unit under `deploy/compose/services/` (§14.1), with a working inline default |
| 2. The Aspire host | §14.2 — **not adopted**, so this is a line to write only if it ever is |
| 3. Helm values | `deploy/helm/<chart>/values.yaml` and the umbrella (§15.3) |
| 4. The inventory | §15.4's table — the row is what makes the obligation reviewable |
| 5. The integration-test fixture | §12.4 — and this one fails first |

**The fixture is the one people forget and the one that fails loudest.**
`WebApplicationFactory` builds the real host, so `ValidateOnStart` runs there
too: a missing key throws `OptionsValidationException` out of `InitializeAsync`
and takes down the whole suite before a single assertion runs.

It is also the one environment where the correct value is a **fake**. The
fixture must supply something that satisfies `[Required]` and is unmistakably
not a credential — a test that passes with a real secret in it is a test that
will one day be run against something real. The `.invalid` convention is the
same idea for hosts.

## Before adding an options type at all

§15.4 is blunt about this and it belongs here too, because the cheapest secret
to rotate is the one that does not exist.

**An options type needs at least one member that differs between
environments.** If every value would be the same in Compose, in the test fixture
and in production, it is not configuration — it is a constant that has been
given a deployment obligation and four places to be forgotten.
`Identity:Client` is the only options type in the solution, and it earns that by
holding a secret that must differ per environment.

## Rotation

**One** client secret reaches a running host — `Identity__Client__ClientSecret`,
for the BFF, the only host that calls a peer synchronously
([§9.7](backend-architecture/09-messaging.md),
[§11.5](backend-architecture/11-identity-authorization.md), ADR-017). Everything
else a *host* holds is a datastore credential.

**The fourth subsection below is not a host's, and that is why the sentence
above counts the ones that reach one.** Since ADR-042 this repository also holds
a Keycloak client secret that no pod ever reads — and a count of what the
platform holds is falsified by the next thing that holds one, where a count of
what reaches a host is a claim about a mechanism. `No_client_secret_is_committed`
at the foot of this file is the premise either way.

### A client secret

Keycloak supports two active secrets during a rotation; use that rather than a
flip.

1. Add the new secret in Keycloak, keeping the old one valid.
2. Update the vault entry.
3. Wait for External Secrets to reconcile, then restart the BFF's pods —
   configuration is read at startup, so a reconciled Secret does not reach a
   running process.
4. Confirm the BFF is authenticating: pricing calls to Catalog succeeding is the
   observable proof, since that hop is the only thing the credential is for.
5. Retire the old secret in Keycloak.

**Step 3 is the one that gets skipped**, and skipping it produces a rotation
that appears to work until the next unrelated restart.

### A database credential

The migrator and the runtime identity are **separate logins** by design
([§7.1](backend-architecture/07-persistence.md)) — the runtime one has no DDL
rights. Rotate them independently, and never merge them to simplify the
procedure: that split is the reason a compromised service cannot alter its own
schema.

The runtime identity is the higher-risk rotation because it is in use
continuously. Create the new login, grant it, update the vault, reconcile,
restart, verify readiness (`/health/ready` covers SQL —
[§13.5](backend-architecture/13-observability.md)), then drop the old login.

### A broker credential

`ConnectionStrings__RabbitMq`, and there is **one per service** since
[ADR-036](backend-architecture/adr/ADR-036-the-broker-has-a-per-service-identity.md)
— `catalog-rabbitmq` and `ordering-rabbitmq`, never a shared Secret. That is
the half of the rotation worth knowing first: rotating the broker is now N
rotations rather than one, and rotating *one* affects exactly one service.

A rotation that reaches the vault and not the pod presents as
[`runbooks/outbox-broker.md`](runbooks/outbox-broker.md) — the broker lane
stalls with authentication failures in the log — which is worth knowing
before rotating rather than during. **The permissions are not the vault's to
rotate**: they are declared in `deploy/compose/rabbitmq/definitions.json` for
the local broker, and are an obligation on whoever provisions a deployed one.

### A realm-check credential

`KEYCLOAK_CHECK_CLIENT_SECRET`, and it is the first credential in this
repository that **no pod ever reads**. It belongs to a workflow rather than to a
workload, and two jobs hold it. `deploy.yml`'s rollout job authenticates with
it, fetches the realm that deployment is about to be rolled onto, and hands
the document to `deploy/keycloak/realm_check.py` before the rollout changes
anything
([ADR-042](backend-architecture/adr/ADR-042-the-deployed-realm-is-checked-at-deploy-time.md)).
`realm.yml`'s `deployed` job authenticates with the same value, hourly and on
`workflow_dispatch`, under the same `production` Environment, and makes the
same three calls over every release the canary plan names — between rollouts,
which is the moment the rollout cannot be
([ADR-043](backend-architecture/adr/ADR-043-the-deployed-realm-is-checked-between-rollouts.md)).

**It is a service account of the realm being checked, with realm-read rights and
nothing else.** Explicitly *not* a cross-realm admin account: one of those would
hold rights over realms this check has no business reading, and what the check
needs is two reads — the realm representation and its client list.

**`view-clients` on `realm-management` is not optional, and the gate checks it
rather than assuming it.** Keycloak applies a list request's `max` to the
client-model stream and then drops the representations the caller may not see,
so an account short of that role produces a client list that is silently
incomplete and looks exactly like a complete one. `read_admin.py` reads the
roles out of its own access token and stops before asking for anything unless
`view-clients` or `realm-admin` is among them — a credential provisioned too
narrowly fails the deploy loudly instead of passing a realm nobody saw the end
of.

**`view-realm` is not one of them, and it reads as though it should be.** It is
a *non-composite* role in Keycloak's own model — §14.1's export shows it
granting nothing else, where `view-clients` composes `query-clients` and
`realm-admin` composes both — so an account holding it has no client visibility
at all. Provisioning this credential with `view-realm` and expecting it to work
is the plausible mistake here, and the gate refuses it by name rather than
letting it through to a silently short list. The token it
obtains can already see every client secret in the realm it does reach, which is
why `read_admin.py` refuses a base URL that is not `https`, and why widening the
grant costs more than widening it looks like it costs.

**The scheduled consumer is a second grant, argued here rather than assumed
to be covered by the first.** PR-36's row in
[`docs/pr-decision-log.md`](pr-decision-log.md) said that a second unattended
consumer — #176's scheduled run — would be a second grant to argue when it
arrived, and it has arrived. What it holds is the same service account, in
the same Environment, making the same two reads — the realm representation
and its client list — and it asks for no wider role: the schedule adds a
moment, not a right. What it changes is exposure. A credential the rollout
exercised once per dispatch is now exercised twenty-four times a day from a
runner, so a leaked or over-granted one is leaked or over-granted on every
one of those hours, and a token that could once read every client secret in
the realm at a rollout can now do so at seventeen minutes past any hour.
That is why the `view-clients`-only grant, and `read_admin.py`'s refusal of a
base URL that is not `https` or that answers with a redirect, matter more
under the schedule and not less — each was sized for a read that happened
rarely, and the same size now bounds a read that happens hourly. The consumer
is opted in by the repository variable `REALM_CHECK_SCHEDULED`, set to
`enabled` by whoever provisions the Environment; it is a *repository*
variable and not an Environment one because the job-level `if:` that reads
it is evaluated before the job enters `production`, so a variable scoped there
is not visible to it. Until it is set the job is skipped and this credential
has one consumer; once it is set, nothing in that job is optional.

**A credential no pod reads is a new category here, and it is why this one
reaches no row of §15.4's table.** That table is the inventory `ValidateOnStart`
enforces, and a key joins it when a *host's* code reads it — the five places
above are five places a service looks. Nothing binds this value, nothing
validates it at startup, and a row for it would put a key no host reads into the
inventory of keys every host must have, where the one mechanism that could
enforce it never runs. The obligation it does carry is real and sits on the
other side of the pipeline: without it the rollout refuses to start, which is
[§15.1](backend-architecture/15-cicd-deployment.md)'s checklist rather than a
pod's.

**It lives in the `production` GitHub Environment**, which is the mechanism
§15.4 already relies on to scope a deployment's secrets — two Environment
*variables*, `KEYCLOAK_CHECK_CLIENT_ID` and `KEYCLOAK_TRUSTED_ORIGIN`, and one
Environment *secret*, `KEYCLOAK_CHECK_CLIENT_SECRET`. A rotation that replaces
the client rather than its secret has the first and the last to move.

**The Environment's deployment branches are restricted to `main`, and that
restriction is the boundary — not the step in either workflow that refuses
another ref.** Both `deploy.yml`'s rollout and `realm.yml`'s scheduled job
run whatever copy of the gate the dispatched ref carries, with this
credential in hand, so a branch could delete the refusing step before
dispatching and read the realm with a weakened predicate, or send the secret
where its own copy of the origin check permits. A workflow cannot enforce a
rule against the ref it was loaded from. GitHub's *deployment branches*
setting on the `production` Environment can — under it a dispatch from any
other ref never enters the job — and provisioning the Environment includes
setting it. The in-workflow step stays as the guard against accident and as
the sentence that tells a person who dispatched from a branch why nothing
ran.

**`KEYCLOAK_TRUSTED_ORIGIN` is not a convenience, it is where this credential
may be sent.** The realm to check is *derived* from the release —
`realm_check.py authority` takes it out of the `identity.authority` that `helm
get values` answers and `-f stable-values.yaml` reinstalls — because a realm
named beside the chart rather than out of it is a check that can pass on a
realm nobody is deploying to. **Deriving the *origin* the same way would be
worse than the hole that closed**: the next step posts this client secret to
that host's token endpoint, and `identity.authority` lives in the cluster, so
an authority of `https://attacker.example/realms/x` would exfiltrate the
credential to whoever can edit a release. So the realm comes from the chart,
the origin comes from here, and the two are required to agree — a rollout onto
an identity provider this deployment does not name stops rather than
authenticating to it.

**`KEYCLOAK_BASE_URL` and `KEYCLOAK_REALM` are therefore not configured at
all.** `realm_check.py authority` writes both into `$GITHUB_ENV` once it has
checked the origin, so the value `read_admin.py` authenticates to is the one
this repository verified rather than one it was handed.

Keycloak's two-active-secrets affordance applies here exactly as it does to the
BFF's client, so the procedure is that one with no vault in it and a different
proof:

1. Add the new secret on the realm-check client, keeping the old one valid.
2. Update `KEYCLOAK_CHECK_CLIENT_SECRET` in the `production` Environment. There
   is no vault hop, no reconcile and no restart — nothing mounts this value, so
   the next workflow run reads it and no running process is holding the old one.
3. Run a rollout, or wait for the next one — or, cheaper, `workflow_dispatch`
   `realm.yml` from `main`, whose `deployed` job makes the same read and prints
   the same line. `read_admin.py` printing the realm and its client count is
   the proof, in the rollout's *Read the deployed realm* step or per release in
   the scheduled job's loop: that line is a read the admin API answers only to
   a working credential, against a realm the step before it derived from the
   chart.
4. Retire the old secret in Keycloak.

**Rotating this credential cannot break a running service**, because nothing
reads it at run time. What a botched rotation produces is a **refused
rollout** — `read_admin.py` stops rather than reporting a realm nobody read —
and, since ADR-043, a **red scheduled run**, which files an issue on the
tracker labelled `security` and `critical`, or comments on the one already
open. Both point in the correct direction for this failure. Fix the
credential; never "fix" a red deploy or a red scheduled run by deleting the
check.
Deleting it converts a rollout that could not see its own realm into one that
never looked, which is the state
[#157](https://github.com/alexander-shamray/blueprint-backend/issues/157) was
filed about — and disabling the schedule to quiet the issue is the same
conversion, arranged by hand.

## Local development is a deliberate exception

§14.1's Compose file carries real-looking values, and they are **not** a defect
to be tidied away:

| | |
|---|---|
| SQL Server | `${SQL_PASSWORD:-Local_Dev_Pa55w0rd!}` |
| BFF client secret | `${BFF_CLIENT_SECRET:-local-dev-secret}` |
| Keycloak admin | `admin` / `admin` |
| RabbitMQ | `catalog-svc` / `local-dev-catalog`, `ordering-svc` / `local-dev-ordering` |

These defaults are what make `docker compose up` work with no prior setup, and
**the environment variable in front of each is the seam** that keeps them out of
anything deployed. `deploy/compose/.env.example` documents the overrides.

**Two of those four rows carry no variable in front of them, and the sentence
above is about the other two.** RabbitMQ's per-service credentials are imported
into the image from `deploy/compose/rabbitmq/definitions.json`, so a `${…}`
would front one half of a pair while the broker still expected the compiled-in
password (ADR-036) — rotating them locally is an edit to that file and a
`docker compose down -v`. Keycloak's `admin` / `admin` is the bootstrap admin of
a container the same command recreates, and **it is not the credential the realm
check uses**: `read_admin.py` refuses a base URL that is not `https` and has no
local subject at all, because the local realm is checked from its file rather
than through a running Keycloak. The two never meet, and a reader who has just
met the realm-check service account should not have to infer that from a
silence.

Note how the connection strings nest — `${CATALOG_CONNECTION:-…Password=${SQL_PASSWORD:-…}…}`
— so overriding the password alone keeps every connection string correct. That
is why `.env.example` leaves the per-service connection variables commented out:
uncommenting one freezes the password inside it and it quietly stops following.

## What must never be committed

No production connection string, key or certificate path — in a sample, in a
test, or in source.

> **There is a secret scan in CI, and what it does not do is worth knowing
> before you rely on it.** §15.1 puts "SCA + secret scan" ahead of the build
> and ahead of the path-filter fork, and argues why: neither half needs a
> build, and scanning downstream of one is scanning that a build failure skips.
> Both halves now run in the `licence-gate` job, the secret scan first
> (`.github/secret-scan/`).
>
> **It reads the working tree, not the history.** A credential committed and
> then removed is still in the object store and is still compromised, and this
> gate will not find it — which is why the rotate-then-rewrite rule below is
> the procedure rather than a formality.
>
> **It is a pattern scanner and not an oracle.** Twelve rules, each named and
> each with its own tests, over key blocks, provider-token shapes and
> credential-shaped assignments. A high-entropy string that looks like nothing
> in particular passes, and so does a credential written in a shape no rule
> describes. The list of rules is the list of things it can find.
>
> **Every exception is a line under `.github/secret-scan/allowed/` naming a
> path, a rule and a fingerprint, with a reason** — never a glob and never an
> inline pragma, on the same argument `Directory.Build.props` makes about
> suppressions. That directory holds one file per tree, each declaring the
> prefix it may suppress, so an entry goes to the file covering its path. An
> entry matching nothing **fails the build**, so a suppression whose finding
> has gone is reported rather than left standing. That is why the
> local-development defaults below are enumerated there once per site:
> rotating one becomes a reconciliation those files list.
>
> **A person is no longer the only thing that writes to it**, and that is worth
> knowing before you read a block you did not add. §4.5's scaffold runs this
> scanner over the service it has just rendered and appends one entry per
> finding under its own paths (#161) — because the equivalent literals for a
> hand-built service were added by hand the day that service landed, and a
> rendered one could otherwise not be committed at all. The entries carry the
> fingerprints the scanner reported rather than any the scaffold computed: a
> digest worked out independently would be a second implementation of which
> substring each rule matches, and being wrong at it produces an entry matching
> nothing — which is the stale-entry failure above, arriving from the tool that
> exists to prevent it.
>
> **A documented control that does not exist is worse than an absent one**,
> and this callout said exactly that while there was no scanner (#119). It
> stays here in that form because the sentence is now about the gate's limits
> rather than its absence, and those are the half a reader relies on without
> checking.

`No_client_secret_is_committed` is an assertion in the test suite, and its
premise is worth restating because it has already been falsified once: it was
correct until a second caller existed. A rule about who holds a credential is
falsified by the next host that holds one.

If a secret is committed, rotate it first and rewrite history second. The commit
is public the moment it is pushed, and a force-push does not un-fetch it.
