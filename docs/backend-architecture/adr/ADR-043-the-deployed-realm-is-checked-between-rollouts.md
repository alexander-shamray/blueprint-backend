# ADR-043 — The deployed realm is checked between rollouts

**Decision.** `realm.yml` gains a second job, `deployed`, on a `schedule`
trigger and on `workflow_dispatch`, under the `production` GitHub Environment.
Nominally every hour it runs the rollout's own three calls — `realm_check.py
authority` over `helm get values --all` for the release, `read_admin.py` to
fetch the realm that names, and `realm_check.py check --kind deployed` to
judge it — for **every workload the canary plan lists**, read out of
`canary.py workloads` rather than restated. *Nominally*, because a `schedule`
is a cadence GitHub runs as best effort and not a bound: a run can be delayed
or dropped under load, without a red one, and the consequences below say what
would make the hour a bound. A red judgement **files a tracker issue**, labelled
`security` and `critical`, or comments on the open one, and
`deploy/keycloak/README.md` carries the procedure it points at. The job is
opted in by a repository variable, `REALM_CHECK_SCHEDULED`, set to `enabled` by
whoever provisions the Environment; until it is set the job is skipped, and
once it is set nothing in it is optional. The suite asserts the schedule, the
guard, the Environment, the three calls in order, the per-release loop, the
refusal of a dispatch from any ref but `main`, and that only the judgement's
own failure files anything.

**Why.**
[ADR-042](ADR-042-the-deployed-realm-is-checked-at-deploy-time.md) reads a
deployed realm at one moment, and the moment is a rollout. A realm edited
afterwards — `accessTokenLifespan` raised, a client-level override added,
`use.refresh.tokens` flipped, the password grant turned back on — is
unobserved until somebody deploys again, which on a weekly service is a week
and on a stabilised one is unbounded
([#176](https://github.com/alexander-shamray/blueprint-backend/issues/176)).
The settings this platform's security guarantees rest on are exactly the ones
an operator reaches for under pressure, and the reach that would matter most
was the one nothing here would see.

**#176 named three things a fix owed and each is a decision rather than a
line.** A trigger that is not a rollout: `schedule`, on `realm.yml`, because
the subject is the realm gate's and `deploy.yml`'s concurrency, its inputs and
its 150-minute budget are all a rollout's. What that trigger buys is a
cadence and not a bound — GitHub runs a `schedule` as best effort, and a run
it sheds or delays under load leaves no red one — so the window is *nominally*
an hour, and the consequences below say what a bound would take. A statement
of what a red run
*does*: an issue, because §13.6's rule is that an alert with no procedure is a
page nobody can act on, and a notification is read once where an issue is open
until somebody closes it — a drift that persists across the hours needs the
second, and the next run comments rather than filing again so the tracker holds
one issue per drift and not one per hour. **It is not a runbook**, because
`docs/runbooks/` is one file per Prometheus alert and `check.py` fails the
build on a runbook with no alert behind it; nothing in §13.6 fires this, so
the procedure lives beside the gate. And the environment the credential
belongs to: the same one, with the same account, and `docs/secrets.md` argues
the second unattended consumer as a second grant rather than assuming the
first covers it.

**The realm is derived and the origin is pinned, at this moment exactly as at
the rollout, and for the same two reasons.** A realm named beside the chart is
a check that passes on a realm nobody deployed to, and an origin taken from the
cluster hands whoever can edit a release the address this job posts a client
secret to. Between rollouts the question widens from *the workload being
rolled* to *any deployed workload*, so the loop is over the canary plan's
workloads — `deploy.yml`'s `options:` is a dispatch menu, and a list restated
in this workflow would agree with the plan until a fifth workload joined one
and not the other.

**The opt-in is not the skip this repository refuses, and the difference is
what the guard reads.** `deploy.yml` runs on `workflow_dispatch` alone because
a rollout on `push` would fail on every merge for want of a cluster and train
everyone to ignore a red pipeline; an hourly job in a repository with no
environment would do the same thing twenty-four times a day. The skip refused
elsewhere — a Docker daemon that did not answer, a `helm status` that failed
for an expired credential — reads a **runtime failure** as absence. This guard
reads a **configuration**: a repository variable nobody has set means no
Environment has been provisioned and there is no realm to be silent about. It
is a repository variable and not an Environment one because a job-level `if`
is evaluated before the job enters its Environment, so a variable scoped to
`production` is not visible to it.

**Consequences.**

- **#176 closes, and what closes it is the cadence — not a continuous
  observation, and not a guaranteed one.** The window a drift is live in is
  now bounded by the schedule and by ADR-040's runtime guard together:
  nominally an hour of the first, and only as reliably as GitHub runs a
  schedule, which is best effort and says so in the workflow's own comment.
  For the token lifetime the second gates *remaining* life, so the first is
  what bounds the issued one. A shorter cron is a one-line change; what would
  make the hour a bound is the monitor the next bullet names. What the job
  cannot become is continuous, and the README says so beside what a red run
  does.
- **The residual is the schedule's own silence, and it is stated rather than
  filed.** A dropped run and the sixty-day suspension are the same silence at
  two scales: GitHub sheds or delays a scheduled run under load, and suspends
  the `schedule` trigger outright in a repository with no commit for sixty
  days, and neither leaves a red run — a stabilised service is exactly the
  repository with no commits and exactly the one the window is longest on.
  Nothing in this repository can observe its own absence; what closes both,
  and what would turn the nominal hour above into a bound, is a monitor
  outside GitHub asking whether the workflow ran in the last day, which is an
  operating decision the README names and does not take. It is
  not an issue because the platform has no environment to schedule against,
  which was #176's own reason for being filed rather than fixed — and this
  record does not repeat that: it builds the mechanism and states the one
  thing the mechanism cannot see.
- **`critical` is decided rather than hedged, and the first step of the
  procedure is to read the log and relabel.** The job cannot tell a realm that
  drifted from a realm it could not read — an expired credential and a raised
  lifespan are both red — and the rule that decides is the rollout's: a realm
  nobody can see holds no guarantee anybody can state. An issue relabelled
  down after a read of the log costs less than a five-hour token lifetime in
  production filed as something to look at next week.
- **The first write permission any workflow here holds, and it is
  job-scoped.** `issues: write` sits on the `deployed` job alone; the `check`
  job and every other workflow keep `contents: read`. The closure gate reads a
  pull request and changes nothing; this job changes the tracker and nothing
  else.
- **The concurrency key carries the event.** A scheduled run and a pushed gate
  run both carry `refs/heads/main`, and under a ref-only key each cancelled the
  other — a merge during the hourly read cancelled the read, and the read
  cancelled the gate over the merge. Neither is a duplicate of the other; the
  thing `cancel-in-progress` is for is two runs of the same subject.
- **The derivation is assigned before it is read, and never `eval`ed.**
  `realm_check.py authority` prints two `NAME=value` lines for `$GITHUB_ENV`;
  inside a loop there is no `$GITHUB_ENV` to append to, and `eval "$(cmd)"`
  swallows a failed cmd under `set -e` — the rollout's own lesson, at the
  `plan` step — as does a process substitution. So the output is captured into
  a variable first, and each line is exported as a literal with
  `export "$NAME=$VALUE"`, which no shell re-parses. The gate's refusal of
  whitespace in either value is what keeps that safe, and this is what keeps
  the refusal load-bearing.
- **A dispatch from a branch is refused before checkout**, on `deploy.yml`'s
  reasoning: a schedule always runs `main`'s copy of the file, but
  `workflow_dispatch` lets the caller pick a ref, and this job authenticates
  to the production realm with the production credential using whatever copy
  of `read_admin.py` that ref carries. An unmerged branch could read the realm
  with a weakened predicate and report it compliant, or send the credential
  where its own copy of the origin check permits.
- **The deployed half has still never run.** The job's first command is a
  `helm get values` that needs the cluster login `deploy.yml` states it is
  missing, and it inherits that exactly. What has been executed is the local
  half and the suite over both workflows' text; a scheduled check nothing has
  ever executed is a check nobody has established is looking at anything, and
  the suite's subject-tests over the workflow are what stand in until an
  environment exists.
- **The `check` job runs only on the two triggers that carry a diff.** A
  schedule has no diff to gate, and the `deployed` job runs the suite itself
  before judging anything — the copy that decides is the copy that must have
  been observed red — so running `check` on a schedule as well would be the
  same command twice in one run.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
