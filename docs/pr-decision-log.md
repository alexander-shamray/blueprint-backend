# PR decision log

**What each landed PR from PR-08 onward decided, and why those decisions bind
what comes after.**

> **This log is closed at PR-37.** No entry is added after it and no entry
> below is edited. The record of what a pull request decided is, from here,
> the commit bodies and the pull request body that landed it, and a rule that
> moves is an ADR — [`change-locality.md`](change-locality.md) is the
> contract that says so, and its plan is what froze this file. The entries
> below stay as the record of how PR-08 to PR-37 were reasoned through,
> verbatim, and each reads as what was true on the day it was written.

**PR-01 to PR-07 have no entries, and never did.** The practice of recording a
PR's findings began with PR-08's persistence layer; the seven before it landed
the foundation, the building blocks and Catalog's skeleton without one. What
PR-07 established — §4.2's architecture gates, observed red before they were
trusted — is stated in `CLAUDE.md`'s phase section rather than here, because it
is a live rule and not a historical decision.

This file is the long half of `CLAUDE.md`'s *Which phase are you in* section.
It was extracted from it when that section reached 1,132 lines — every PR from
PR-08 on appended its findings and nothing was ever consolidated, so a file
loaded into
every session's context was carrying a changelog nobody needed in order to act.

**The entries below are verbatim in their arguments**, because the arguments
are the whole value and a summary of an argument is how a rule gets "corrected"
back. Four kinds of edit were made on the way out, and naming them is cheaper
than a claim that does not survive a grep: each block gained a `## PR-NN`
heading; **self-references were rebased**, since a sentence saying "the rule at
the top of this file" pointed at a section that did not travel and would
otherwise now point at nothing; where a block stated a *live* fact that
`CLAUDE.md` also carries, it now points there instead of holding a second copy
that drifts; and one 136-column line was rewrapped. Not one argument was
shortened.

A few lines still run one to nine columns past 80, each ending in a backticked
identifier too long to break. They came across as they were, and the original
carried them the same way.

**That sentence used to carry a count, and the count was wrong.** It said five
where six matched its own description — the sixth arrived with a later entry,
as the next one will, because nothing recomputes it and the sentence is not
where anybody looks. The predicate is checkable and the number was not, so the
number is gone rather than corrected: `CLAUDE.md` makes the same argument about
its own line count one file over, and a figure this file states about itself
invalidates with the next PR that appends to it.

**It is guidance, not specification.** Where an entry disagrees with the
blueprint, the blueprint wins — the same rule `docs/superpowers/` carries — and
the disagreement is a bug report against one of them rather than a defect to
tidy away. `CLAUDE.md`'s own *one rule that matters* governs it: a claim
changed here is changed everywhere it appears.

**It sits outside `docs/backend-architecture/` deliberately**, beside
`docs/roadmap.md` and for the same reason. It is not a chapter, so it is in no
index, carries no nav footer, and is in the scope of neither
`/validate-blueprint` nor `/check-links` — both of which glob the blueprint
directory. Nothing structural will catch its drift; the *one rule* is what
carries it, exactly as it carries `CLAUDE.md` itself.

**Newest first.** PR-37 is at the top and PR-08 at the bottom, because each PR
appended its block above its neighbours while the log was open.

**It does not rewrite another PR's argument, and since the freeze it does not
reconcile another PR's live claims either — which reverses a rule this
paragraph used to state, so the reversal is argued rather than silent.** An
entry records how a PR was reasoned through, so re-arguing it later destroys
the only thing the entry is for. While the log was open, a *number* restated
inside an entry was treated differently — as a claim about the system that the
one rule bound like any other — and PR-10's block is the worked example: it
states the compose smoke's timeout and says in place that the figure "is
restated here, which is what makes it a claim to reconcile rather than a fact
to read", and PR-17, PR-18 and PR-19 each raised it there. That tour is the
cost [`change-locality.md`](change-locality.md) withdraws: a value has one
owner, and a restatement met in a closed log is dated by the heading above it
rather than corrected. A reader who needs the current figure reads the code.

---

## PR-37 — the moment the rollout cannot be

**#176 was PR-36's own residual, and this row pays it, which is PR-32's
kind.** ADR-042 reads a deployed realm at one moment and the moment is a
rollout — so a realm edited afterwards, `accessTokenLifespan` raised, a
client-level override added, `use.refresh.tokens` flipped, the password grant
turned back on, is unobserved until somebody deploys again. On a weekly
service that is a week; on a stabilised one it is unbounded, and the four
settings ADR-033 and ADR-034 rest on are exactly the ones an operator reaches
for under pressure. PR-36's *What is owed* named the window and filed it
rather than closing it, because closing it needed a decision about what a red
scheduled run *does*, and that is an operating decision rather than a build
one.

**The rule that moved is ADR-042's third consequence: that a rollout is the
only moment a deployed realm is read.** It stands as written, because it was
true when it was written, and
[ADR-043](backend-architecture/adr/ADR-043-the-deployed-realm-is-checked-between-rollouts.md)
amends it rather than rewriting it: `realm.yml` gains a `deployed` job that
runs the rollout's own three calls, nominally hourly, over every release the
canary plan names. Nothing about the predicate changed and nothing about
either realm changed; what changed is how often the question is asked, and of
how many releases — and *nominally* is load-bearing, because a `schedule` is a
cadence GitHub runs as best effort and not a bound, which the residual under
*What is owed* carries.

### The three things the issue said a fix owed

**A trigger that is not a rollout, and it sits on `realm.yml` rather than on
`deploy.yml`.** The subject is the realm gate's — the same `realm_check.py`,
the same `read_admin.py`, the same suite run before either — and everything
about `deploy.yml` is a rollout's: its concurrency group, its dispatch inputs
and its 150-minute budget. A `schedule:` on that file would have inherited all
three for a job that reads and never waits on a hook, a rung or a dwell. The
cron is `17 * * * *`, off the hour because GitHub sheds top-of-the-hour
schedules under load, and a dropped run is a silence rather than a red one —
the failure §13.6 spends a callout on.

**A statement of what a red run does, and it is an issue.** Not a runbook:
`docs/runbooks/` is one file per Prometheus alert, `deploy/observability/`'s
`check.py` fails the build on a runbook with no alert behind it, and nothing
in §13.6 fires this — so the procedure lives beside the gate, in
`deploy/keycloak/README.md` under *What a red scheduled run means*, and the
issue points at it. Not a notification either: a notification is read once
and an issue is open until somebody closes it, which is the property a drift
that persists across the hours needs. The next red run comments on the open
issue rather than filing a second, found by its own title, so the tracker
holds one issue per drift and not one per hour — and only the `judge` step's
failure files anything, so a dispatch refused for coming from a branch is not
a drift. `issues: write` is the first write permission any workflow here
holds; it is scoped to the `deployed` job alone, and the `check` job keeps
`contents: read` with everything else.

**The environment the credential belongs to, and it is the same one.** The
scheduled job reads with the account the rollout reads with —
`KEYCLOAK_CHECK_CLIENT_ID` and `KEYCLOAK_CHECK_CLIENT_SECRET` from the
`production` Environment, the same two reads, the same `view-clients` role
checked rather than assumed. PR-36's row said a second unattended consumer
was a second grant to argue rather than one the first covers, and
`docs/secrets.md` argues it as one, beside the first grant; the README's *The
credential* section names both callers.

### The opt-in is not the skip this repository refuses

**The `deployed` job runs only when a repository variable,
`REALM_CHECK_SCHEDULED`, is `enabled`, and what separates that from the skip
refused everywhere else is what the guard reads.** `deploy.yml` runs on
`workflow_dispatch` alone because a rollout on `push` would go red on every
merge for want of a cluster and train everyone to ignore a red pipeline; an
hourly job in a repository with no environment would do that twenty-four
times a day. The skip refused since PR-08 — a Docker daemon that did not
answer, a `helm status` that failed on an expired credential — reads a
*runtime failure* as absence. This guard reads a *configuration*: a variable
nobody has set means no Environment has been provisioned and there is no
realm to be silent about. Once it is set nothing in the job is optional — an
absent credential, a release the gate cannot read, a realm that cannot be
fetched and a realm that disagrees are all red, on the rollout's own rule.

**A repository variable and not an Environment one, and the reason is the
harness's.** A job-level `if:` is evaluated before the job enters its
Environment, so a variable scoped to `production` is invisible to it. The
three values the job uses are Environment-scoped exactly as the rollout's
are; only the opt-in sits one level up, and its name is declared once, in
`realm_check.py`, so the README and the workflow are asserted to agree on it.

### Every release, the rollout's derivation, and two lessons it had taught

**The set of releases is the canary plan's, read out of `canary.py
workloads`.** The rollout judges the workload it is rolling; between rollouts
the question is whether *any* deployed workload points at a realm that has
drifted, so the job loops. A list restated in the workflow would agree with
`canary.json` until a fifth workload joined one and not the other — which is
`deploy.yml`'s `options:` one artefact over, and that one is a dispatch menu
rather than a subject. The subcommand prints one name per line for a shell
loop, and its own test pins that it lists the plan and not its comments.

**Inside the loop the derivation is assigned, then exported per line — never
`eval`ed and never process-substituted.** `realm_check.py authority` prints
two `NAME=value` lines for `$GITHUB_ENV`, and inside a loop there is no
`$GITHUB_ENV` to append to. `eval "$(cmd)"` swallows a failed cmd under
`set -e` — the substitution yields an empty string and `eval ""` returns
success — which is the rollout's own lesson at its `plan` step, and
`while read; done < <(cmd)` swallows it the same way. So the output is
captured into a variable first, where `set -e` sees the gate refuse, and each
line is exported as a literal with `export "$NAME=$VALUE"`, which no shell
re-parses. The gate's refusal of whitespace in either value is what makes
that safe, and this is what keeps that refusal load-bearing rather than
decorative.

**The concurrency key gained the event name, because a schedule run and a
push run share `refs/heads/main`.** Under the ref-only key each cancelled the
other: a merge to `main` during the hourly read cancelled the read, and the
read cancelled the gate over the merge. Neither is a duplicate of the other —
they have different subjects — and what `cancel-in-progress` is for is two
runs of the *same* subject, which the event name is what distinguishes.

**A dispatch from a branch is refused before checkout, on `deploy.yml`'s
reasoning.** A schedule always runs `main`'s copy of the file, but
`workflow_dispatch` lets the caller pick a ref, and this job authenticates to
the production realm with the production credential using whatever copy of
`read_admin.py` and `realm_check.py` that ref carries. An unmerged branch
could read the realm with a weakened predicate and report it compliant, or
send the credential where its own copy of the origin check permits. It is a
step rather than a job-level `if`, so somebody who dispatched it on purpose is
told why it did not run.

### `critical` is decided rather than hedged

**The job cannot tell a realm that drifted from a realm it could not read** —
an expired credential and a raised lifespan are both red — and the label is
chosen for the worse case on the rollout's rule that a realm nobody can see
holds no guarantee anybody can state. So the procedure's first step is to read
the run log, decide which of the two it was, and relabel: an issue relabelled
down after a read of the log costs less than a five-hour token lifetime in
production filed as something to look at next week. The second step is that a
drift is corrected in the realm and never in the gate, and the last is that
the issue closes on a green `workflow_dispatch` from `main` and on nothing
weaker — a run that goes green because the schedule was disabled is the
sixty-day silence below, arranged by hand.

### The suite's subject is whether anything still asks the gate

**`TheRolloutStillCallsThisGate` and `TheScheduleStillCallsThisGate` assert
the same three calls in the same order, out of one list.** `REALM_CALLS` —
derive, fetch, judge — is the invariant both files share, so it is declared
once and `realm_call_positions` reads it for both. `commands_of` strips the
comment lines from either workflow before anything searches it, because both
explain each call in a comment beside it and a raw search would find
`realm_check.py authority` whether or not anything ran it — the fourth
matcher in this tree found matching the prose about itself, and the third
artefact to need stripping. The schedule's class then slices the `deployed`
job out of the file, so a property of it cannot be satisfied by the `check`
job above — both run the suite, and only one may go on to read a realm — and
asserts the shape the workflow's own comments argue: the schedule and the
dispatch declared, the `check` job on a diff only, the guard's variable and
its two events, the Environment, the refusal before checkout, the loop over
`canary.py workloads`, the assign-then-export with no `eval` and no `< <(`,
the concurrency key, and that only the `judge` step's failure files.

### What is owed

- **The schedule's own silence, and it is stated rather than filed.** A
  dropped run and the sixty-day suspension are the same silence at two
  scales: GitHub sheds or delays a scheduled run under load, and suspends the
  `schedule` trigger outright in a repository with no commit for sixty days,
  and neither leaves a red run — and a stabilised service is exactly the
  repository with no commits and exactly the one #176 said the window is
  longest on. Nothing in this repository can observe its own absence, which
  is why the hour above is nominal. What closes both, and what would make
  the hour a bound, is a monitor outside GitHub asking whether `realm.yml`
  ran in the last day, which is an operating decision the README names and
  does not take. It is not an issue
  because the platform has no environment to schedule against, which was
  #176's own reason for being filed rather than fixed — and this row does not
  repeat that: the mechanism is built, and the one thing it cannot see is
  written down beside it.
- **The deployed half has still never run, and neither moment has.** The
  job's first command is a `helm get values` that needs the cluster login
  `deploy.yml` states it is missing, and it inherits that exactly. What has
  been executed is the local half and the suite over both workflows' text; a
  scheduled check nothing has ever executed is a check nobody has established
  is looking at anything, and the subject-tests above are what stand in until
  an environment exists.

### The harness half, in the same pull request

**One pull request is one row, and this one carried three harness issues
beside the realm's.** They share nothing with ADR-043 except the branch, and
they are recorded here rather than in Appendix C because the harness is not
the blueprint's subject. Each is a grant question, and each was settled by a
measurement rather than by reading.

**#149 and #75's last item closed by one shape — adjudicate in a step that
cannot write, apply from a record — and the boundary the shape promised
turned out to be discipline, for a measured reason.** A `review-adjudicator`
profile with `Read`, `Grep` and `Glob` reads the review and returns
structured rows; the sweeps verify by a second read-only dispatch under a
verdict contract; and `gh-issue-create.sh` leaves `gh issue create` with no
free parameter. The first form of `/review-grok` also put
`Read(suggestions.md)` in its `disallowed-tools`, so the writing step would
be refused the review by the harness. A nested-session probe showed what
that reaches: a command's deny list propagates to the subagents it spawns,
so the adjudicator's own reads were refused and it returned
`unreadable-review`; and a path deny reaches a `Bash` command naming the
path, so the size preflight was refused beside it. The harness offers no
deny that reaches the parent and not the child. The entry is gone, the
command says the review is readable to the writing step and that the split
holds because it does not read it, and the three machinery trees refused to
`Edit` are the half the harness holds.

**The same probing measured the `Bash(...)` form of `disallowed-tools`,
which the boundaries inventory had named as owed since #23.** A throwaway
command carrying `Bash(git diff:*)` in both keys had the diff refused with
the harness's own text while a `wc` in the same session ran, which
separates a rejected pattern from a command that failed to load exactly.
Both sweeps now deny `git push origin`, its `-u` form and the raw
`gh issue create` by name — the fix their own residual paragraphs had
refused to write unmeasured.

**#17's two Docker-testable items were built on evidence, and the third is
a procedure.** The uid collision was reproduced against the base image's own
accounts, the tempting `useradd --non-unique` was built and rejected because
`id` then answers `nobody`, and the reuse-or-rename fix was built at four id
pairs. Egress is an internal network and a CONNECT-only proxy baked into the
reviewer image; the grok CLI's honouring of `HTTPS_PROXY` was measured, and
the network is brought up before the credential probes because those are
model calls too. What was not measured is stated in the boundaries
inventory: an authenticated call through the proxy, which the classifier
refused to stage, and SELinux, which needs an enforcing host. The plan under
`docs/superpowers/plans/2026-09-02-sandbox-hardening.md` is the measurement
record, verbatim.

## PR-36 — where a check sits decided what it could hold

**#157 had been open since PR #156 and had been narrowed twice without being
closed.** ADR-033 composes a 330-second revocation bound out of a realm setting
and a `Common.Web` one; ADR-034 gives the browser no refresh token; §11.2
documents the password grant as a local affordance a deployed realm turns off.
All four settings live in a Keycloak realm, and the only realm anything read
was `deploy/compose/keycloak/realm-export.json` — §14.1's Compose export, which
`RealmImportTests` parses. Every chart points at
`https://id.example.com/realms/commerce`. So four security guarantees read as
platform-wide and were verified in the one realm that is not deployed anywhere.

**The issue itself listed three shapes and said none was obviously right, which
is why it stayed an issue.** ADR-040 then refused all three in one sentence — a
pipeline check needs admin credentials in CI, a startup assertion reads a
discovery document that publishes no token lifetime at all, and committing a
production realm makes somebody's operational input into this repository's
artefact — and built the containment instead: no host accepts a token with more
than the bound of remaining life. That was worth having and it is untouched.
It is also not what the issue asked for, and ADR-040 said so.

**Two of those three refusals survived this PR and the third turned out to be a
statement about a job rather than about the platform.** "Admin credentials in
CI" is true of CI. It says nothing about `deploy.yml`'s rollout job, which runs
under the `production` GitHub Environment — the mechanism §15.4 already relies
on to scope a deployment's secrets, and the same one that supplies
`PROMETHEUS_URL` to the canary two steps later. The obstacle was never the
credential; it was reading "a pipeline check" as one thing when a pipeline has
two halves with different rights.

### One predicate, two subjects, and the second is what makes the first
trustworthy

**A Keycloak realm export and the admin API's `RealmRepresentation` are the
same document.** The export is that representation serialised, and the client
list a full export carries under `clients` is what
`GET /admin/realms/{realm}/clients` answers. So `realm_check.py` judges a
`dict` and does not know, or care, which of the two produced it;
`read_admin.py` fetches and never interprets, on `deploy/canary`'s split, which
is what lets the deciding half have a suite at all.

**That is not a convenience, it is the answer to a question this repository
asks of every deploy-time artefact.** `deploy.yml` has never reached a cluster
and says so in its own header, so a check written only for the deploy path
would be a check nobody has ever executed — the same standing as the alert
rules no Prometheus loads and the charts nothing installs, except worse,
because those at least have gates over their text. Running the deciding half
against the Compose realm on every change to it is what keeps the instrument
honest until an environment exists, and it is why `realm.yml` is not
redundant with `RealmImportTests`.

**The two instruments are not duplicates and the sharpest proof is that they
disagree.** `RealmImportTests` asserts `directAccessGrantsEnabled` **true**,
because §14.1's documented login is a password grant and a local realm without
one makes the README's `curl` a lie. The deploy-time check asserts it
**false**, because §11.2 says a deployed realm turns it off. Both are right
about their own realm. That is the whole reason `--kind` is a required argument
with no default: a check that guessed which realm it had would pass a
production realm on the local realm's terms, which is this failure wearing the
costume of its own fix.

### The number is read, and the check meant to prove it was matching nothing

**ADR-040 had just made `AccessTokenLifetime` the one place the 300 lives**, on
the argument that a composition nothing pins can be recomposed wrongly and
still look composed. A literal `300` in a Python gate would have been that
defect again in a second language, so `read_access_token_lifetime` matches the
declaration out of `AuthenticationExtensions.cs` and raises rather than
defaulting when it finds zero or two of them — substituting the number it
failed to read is what would make the read decorative.

**The cost of that is a third reader depending on the declaration's textual
shape**, and it is stated rather than hidden: a refactor of that line is now a
change to what every realm owes. The mitigating test is the one whose subject
is the read — it asserts against the real file that the parser still finds a
declaration, so the way this gate stops covering its newest surface is a loud
failure rather than a silent one.

**The reads-direction self-check shipped broken in its first form here, and
only its own test found it.** The pattern is the Helm tree's — declare
`SOURCE_INPUTS` beside the reads — and the reads direction on top of it is what
every copy was found to owe after `canary.py` declared two paths and opened
three. This gate is the fifth copy. The path
literal pattern allowed a dot only at the *start* of a segment, so
`Common.Web/` never matched, so the one read this gate most depends on was
invisible to the check that exists to declare it, **and the check reported a
pass**. Nothing about reading the code says so; what said so was
`test_an_undeclared_read_is_caught`, which empties `SOURCE_INPUTS` and demands
a failure naming the lifetime source. A check that cannot be made to fail is
not passing.

### What the gate refuses to judge, and why each refusal is a decision

**A realm document with no `clients` array is refused rather than passed.**
Every per-client obligation — no lifespan override, no implicit flow, the
browser's refresh-token attribute — is vacuously true of an empty realm, and a
truncated or wrong-shaped document is exactly what a misconfigured fetch
produces. **An absent `use.refresh.tokens` is a violation, not a silence**,
because Keycloak's default is to issue refresh tokens on the standard flow: a
missing attribute means ADR-034 is violated, and reading it as unspecified
would make the one setting that record rests on optional. **`standardFlowEnabled`
is asserted beside it**, because a client that mints no token issues no refresh
token either and would satisfy the check for the wrong reason. **A non-`https`
admin URL stops the run**, because the bearer token that fetch carries can read
every client secret in the realm. **A missing environment variable names every
missing one**, not the first, so an operator wiring this up learns what it
needs in one run.

**`ClockSkew` is excluded and the exclusion is written down.** It is the other
half of ADR-033's 330 and it is not a realm setting at all — it is
`Common.Web`'s, pinned by `JwtAuthenticationTests` — so an operator told to
configure a realm `ClockSkew` would go looking for something that does not
exist. #157's own table says this in its last row; the gate says it too,
because the table is not what a reader of the gate has open.

### What is owed

- **The window between rollouts, and it is filed.** A realm is read when a
  deployment reads it, so an edit made after a rollout is unobserved until the
  next one — on a stabilised service, indefinitely
  ([#176](https://github.com/alexander-shamray/blueprint-backend/issues/176)).
  Closing it needs a `schedule:` trigger, an environment to point it at, and a
  decision about what a red scheduled run *does*, which is an operating
  decision rather than a build one.
- **The deploy half has never run, and neither has anything else in that job.**
  `deploy.yml` has no cluster, no kubeconfig and no registry, and its header
  says so. What this PR adds inherits that exactly. It is not the fiction that
  header refuses — that one declines to write a cluster login for a provider
  nobody has chosen, where Keycloak is chosen in §11.1 and its admin API is not
  a guess — but "the local half is what has been executed" is the honest
  sentence and it is in ADR-042's consequences.
- **The credential is the first in this repository that no pod ever reads**, so
  it is in `docs/secrets.md` and not in §15.4's table. That table is the
  inventory `ValidateOnStart` enforces and a key joins it when a host's code
  reads it; a workflow credential is a new category and had to be argued rather
  than slipped in. A second unattended consumer — #176's scheduled run — is a
  second grant to argue when it arrives.
- **`BROWSER_CLIENT = "web-app"` is a name restated from §11.2 and from
  `RealmImportTests`.** The alternative is inferring which client is the
  browser from its flags, and every flag that would identify it is one of the
  settings under test — a subject derived from the predicate passes vacuously
  the moment the predicate is what has gone wrong. Admitted rather than
  avoided, and the non-vacuity check beside it (exactly one `web-app`) is what
  makes the restatement safe.

## PR-35 — the identity that was a stand-in, and the constraint that replaced it

**PR-34 filed the issue this PR closes, in its own "What is owed" section,
which is now the third time running.** ADR-039 stopped the purge deciding a
marker's fate by comparing two windows and made it ask the store that owns the
claim — and the split that bought cost the atomicity a single statement had for
free, so the delete had to *name* the rows the select returned instead of
describing them. `(Key, CommittedAt)` was that name, and the same decision said
in its own consequences what the name was worth: it identifies a write **by
construction and not by constraint**. Nothing enforces uniqueness on a
`datetimeoffset(7)`.

**So the ABA the join closes was re-entered through the identity itself.** A
key names a *command*, not a row: past §8.5's guarantee a retry can claim the
key again, commit, and write a fresh marker under a key an earlier pass already
selected. If the database's clock is set to the exact 100-nanosecond tick that
row carries — a row already at least `IdempotencyWindow` old — the stale delete
matches the replacement and removes it with a live claim behind it, and the
retry after that runs a committed command a third time. Silently, which is the
part that earns a PR.

### A fifth clock fault, and why it was filed rather than fixed in place

**The four before it are bounded by a magnitude and this one is not.** Each
predicate ADR-039 tried failed to a drift of sufficient size in a direction — a
two-clock comparison, a forward step, a backward step, a step and its
correction — and each was closed at the source rather than bounded, which is
where the "no third margin" rule comes from. This one needs an exact
*coincidence*. There is no quantity to put a margin on, and a smaller
probability of a different fault is not a smaller instance of the same one.

**That distinction is also why it did not hold up PR-34.** A residual whose
fix is a schema change to every service is a row of its own, not a paragraph in
a pull request about something else — the same reasoning ADR-038 used when it
filed #171 rather than folding the fix in. What made this one cheap to defer
and expensive to forget is that the exposure is silent: nothing fails, nothing
logs, and a command runs twice.

### A rowversion answers all five by reading no clock

**The column is SQL Server's own database-wide counter**, unique and monotonic,
generated by the engine, and — for a row nothing ever updates — carried
unchanged for the life of that row. It is the identity the join wanted, where
the timestamp was standing in for one. `CommittedAt` is not dropped and its
index is untouched: it still ages the candidate `SELECT`, which is ADR-038's
half and is unaffected. **The column stops saying which row is which and goes
on saying which rows have served their window** — one job removed, not one
column replaced.

**It is a shadow property, and that was the one design decision worth
arguing.** Nothing in C# reads the column through EF: the only reader is the
purge's own SQL, over Dapper, which never consults the model. A CLR property
would be a mutable array on a public type that exists to be ignored, and it
would invite exactly the reads the column must not have. What a shadow property
costs is discoverability, and that is paid twice — the name is a `const` on the
entity, so the mapping and both statements agree by construction rather than by
three literals, and a per-service test reads the model back.

**Each service declares it, beside the schema and for the same reason.** The
entity is a building block and knows no schema; the mapping is the service's.
That symmetry is what makes the scaffold's job clear, and it is also the
failure mode: a service that maps the table and omits the column fails its own
purge with `Invalid column name 'RowVersion'` on the first pass. Loud, and in
the right direction — which is why §4.5's scaffold carries the migration as a
seventh template rather than leaving it to a service author.

### The test could be written from this side, where ADR-039's could not

**No suite owns the container's clock, so the coincidence cannot be staged as a
clock movement — but it can be staged as its effect.** The state a collision
produces is a replacement carrying the *same* `CommittedAt` as the row a pass
selected, landing between that pass's `SELECT` and its `DELETE`. Both halves are
reachable: the fixture rewrites the row preserving the timestamp with a
variable, and the pass's own call to `UnheldAsync` is the window, so a
decorator that mutates the table while answering lands the replacement in it
deterministically — no fake clock, no paused thread, no second connection
racing the first.

**Preserving the timestamp is the whole of the test and is the part easiest to
get wrong.** A replacement stamped at a fresh instant is caught by the old
`(Key, CommittedAt)` pair, so a test that let the column move would pass
against the very defect it is aimed at. It was **observed red** against the
previous identity — `idempotency should be 0 but was 1`, the pass deleting the
replacement — and green against this one.

**A second test per service has the mapping as its subject rather than the
behaviour**, because the behavioural test would go on passing if the column
quietly became a plain `binary(8)`: two rows in one test still differ in a
value nobody updates. What must be true is that the *database* generates it,
and that is read from the EF model and from `sys.columns` rather than restated
— a claim written in a test and a claim written in a configuration agree until
one of them is edited.

### What is owed

- **Nothing, on this mechanism.** ADR-039 ended with one open item besides
  this — the claim expiring under a running handler
  ([#127](https://github.com/alexander-shamray/blueprint-backend/issues/127))
  — it is §8.5's, it is unchanged, and it is now the only one. Three
  consecutive rows have each closed the residual the row above named; this is
  the first with none to hand on.
- **ADR-039's cost sentence was wrong in two details, and both are corrected in
  its supersession callout.** It said a `rowversion` costs "a sixth template
  migration in §4.5's scaffold" when §4.5 already shipped six, so it is the
  seventh; and "an Appendix D row", which turned out to be two — the entity's
  and `RetentionPurgeService`'s. An ADR is superseded and never rewritten, so
  the correction goes where the supersession does — **in the callout, not in
  ADR-041**, which is a distinction worth keeping because a reader arriving at
  ADR-039 meets the callout and may never open the record after it.
- **The generator's `new byte[0]` is a recurring edit, not a one-off.**
  `dotnet ef migrations add` renders a zero-length array for the inert
  `defaultValue`, ADR-019's analyser policy refuses it as CA1825, and the
  migration does not compile until it reads `Array.Empty<byte>()`. Both
  migration files say so in place, because the next person to regenerate one
  will meet the same failure and the file is where they will be looking.
- **`Catalog.Api.Tests.DatabaseSmokeTests` still counts its migrations with a
  literal**, and this PR moved it again — `ShouldBe(7)` to `ShouldBe(8)`, with
  the eighth name beside it, and the scaffold's patch needle for that block
  edited in the same change. Ordering's derives the count from its `expected`
  list and needed only the new name. PR-33's entry noted the asymmetry and it
  is still there; the literal is a second place to edit that says nothing the
  named rows do not.

## PR-34 — the term no margin could bound, and the question that replaced it

**PR-33 filed the issue this PR closes, in its own "What is owed" section,
which is now the second time running.** ADR-038 removed two of the three terms
that made the marker's expiry a guess and was explicit that the third was
untouched: Redis counts the claim's window, SQL Server counts the marker's, and
nothing couples the rates. A forward step of the database's clock — an NTP
correction, a host migration, a resumed snapshot — carries the purge's cutoff
past a marker whose claim is still live, the claim then expires into a table
that has already forgotten the commit, and the next retry runs a committed
command a second time.

**What made it worth a PR rather than a paragraph is where the exposure was
worst.** The margin absorbing a step was the handler's runtime plus whatever
the configured window exceeded the floor by — six days on the shipped defaults,
and nothing at all at the floor. The floor is the value §8.5 and this log both
describe as the supported way to buy back the late retry ADR-037 costs. **The
one configuration this platform recommends narrowing towards was the one with
no margin left**, which is not a residual to document a third time.

### A margin was refused for the third time, and then the comparison was

ADR-037 added `MarkerLeadAllowance` to bound two terms. ADR-038 removed both at
the source, retired the allowance, and wrote down why a number would not do:
*a margin that stands in for an unbounded quantity is a guess with a number on
it*. A clock step is bounded by nothing this repository can assert, so the same
sentence disposes of any figure this PR could have written.

What was left was to notice what the arithmetic was *for*. **The marker
outlives the claim** is a statement about two instants that two different
servers produce. **The claim is gone** is a question, and the store holding the
claim is the only thing that can answer it. `IIdempotencyStore` gains
`UnheldAsync`, `RetentionPurgeService` selects candidates by age and then asks,
and no clock is read on either side of the decision.

**Age still selects and no longer deletes, which is the sentence to carry.**
The `DATEADD(second, -@WindowSeconds, SYSDATETIMEOFFSET())` cutoff ADR-038
introduced is unchanged and still the database's own clock — it decides which
markers have served their window. What it stopped deciding is whether they may
go.

### Three things the shape cost, each stated rather than absorbed

**The pass is two statements, so it can be blocked at the head — and the first
rule for that was wrong.** It stopped on any batch it could not delete
*entirely*, on the premise that such a batch comes back unchanged. It does not:
`TOP` refills the deleted slots with the next-oldest candidates, so one held key
at the head ended a pass after roughly one batch and left the rest of the table
for an hour later. The rule is the store's answer now — stop when it releases
nothing — and a test stages a held key mid-backlog and asserts the pass gets
past it. **Stopping on nothing *deleted* was the intermediate spelling and was
also wrong**, for a reason the first one hid: with three replicas, another
purger may delete the selected rows first, so a zero is concurrent progress
rather than a batch nobody may touch, and stopping on it hands the backlog to
the next pass or to nobody.

**The delete is by row, which meets a limit belonging to another layer.** Each
row costs two parameters — its key and the `CommittedAt` that identifies the
write — and SQL Server refuses a statement carrying more than 2,100, where the
default `BatchSize` is 5,000. Chunking at 900 keeps `BatchSize` meaning rows
considered per batch instead of being quietly capped by a number nobody set.

**The purge now depends on Redis, and the failure directions differ.** A
service with markers and no claim store fails to resolve at startup, which is
loud. An unreachable store mid-pass throws, reaches the existing catch, and
leaves every marker in place — and only markers, since the outbox and the
inbox are deleted before that call and those deletions stand. Markers
accumulate, which is the outbox's
failure mode and not a lost guarantee.

### The floor survives and makes a smaller claim

`RetentionPolicy.IdempotencyWindow` still may not go below
`IdempotencyRetention.MarkerFloor`, and the refusal message had to be rewritten
because its stated reason became false. A shorter window no longer re-opens the
duplicate — the purge waits for the claim regardless — it simply asks for a
guarantee shorter than the claim already gives, which is a setting that cannot
do what it says. **What the floor bounds now is how long §8.5's guarantee lasts
rather than whether it holds**, which is the distinction it could not draw
while it was the thing making the ordering true.

### The test could not be written from the clock's side

No suite here owns the database container's clock, so a forward step cannot be
staged. What can be staged is the state a step produces: a marker thirty days
old with a live claim behind it. **The companion test is what makes the pair
mean anything** — it releases the claim and watches the same row go, so a purge
that had simply stopped deleting markers fails rather than passing both. That
is `CLAUDE.md`'s most-repeated failure in the shape a regression test takes it,
and the reason the first test alone would have been worse than none.

### Containing #157's lifetime half, because one pull request is one row

ADR-033's 330-second revocation bound and ADR-034's no-refresh-token rule were
enforced by one file test over §14.1's Compose realm. Every chart points at an
externally provisioned authority this repository holds no configuration for, so
both read as platform guarantees and were true locally.

**A token is where a realm's answer is observable without credentials nobody
here has.** Every host already validates one on every request, and a token
carries how long it has left, so `AddJwtAuthentication` now refuses one
carrying more than `AuthenticationExtensions.RevocationBound`. The three shapes
#157 named all wanted something the platform lacks — admin credentials in CI, a
discovery document that does not expose token lifetimes, or somebody's realm
turned into this repository's artefact.

**The inexact form was chosen deliberately and the choice is argued in
ADR-040.** `exp - iat` is sharper and needs `iat`, which RFC 7519 makes
optional — so an issuer omitting the claim would either switch the control off
or have to be refused outright, and both are decisions rather than details.
Measuring remaining life against this host's clock instead makes the ceiling
the *bound* rather than the lifetime. A realm at 320 or 330 seconds passes;
five hours does not.

**It cost two review rounds to say what that actually enforces, and both
corrections are the same shape.** The first: the ceiling is 330 and
`ValidateLifetime` then adds its own 30, so the longest window admitted is 360.
The second, and the larger: gating *remaining* life is not enforcing the
*issued* lifetime at all. A five-hour token is refused for four hours and
fifty-four minutes and then admitted for its last 330 seconds — **contained,
not detected**. So the record's first title said the lifetime was enforced and
was renamed to say what the control does, and **#157 stays open in full**
rather than half-closed.

**`RealmImportTests` said why it held a literal, and the condition it named is
what changed.** "A constant nothing reads would be a registration standing in
for a control, which is the shape ADR-033 was written to withdraw" — correct
while the number was only ever asserted against the shipped realm. Something
reads it now, so `AccessTokenLifetime` is declared once and that suite reads it.
**The taste did not change; the condition did**, and an edit that moved the
constant without saying which would read as the reverse.

### What is owed

- **#157 is not closed at all, and the first draft of this entry said it was
  half closed.** ADR-034's half cannot be closed this way — a refresh token
  passes between the browser and Keycloak and never reaches a service — and
  ADR-033's half is contained rather than checked, because a token past its
  ceiling becomes admissible again as it approaches expiry. A deployed realm
  still owes `accessTokenLifespan` 300 and no client-level override, and
  nothing here establishes that it has them.
- **The exact form is owed and its price is known.** `exp - iat` with an absent
  `iat` failing closed enforces the issued lifetime, removes the double-spent
  skew and needs no clock of this platform's, since both terms are the
  issuer's. It costs a package pin this assembly does not carry, a
  licence-register row, and a hard refusal of any issuer omitting a claim RFC
  7519 makes optional — a dependency decision and a compatibility decision,
  which belong to the record that takes them.
- **A misconfigured realm is now an outage rather than a weakened bound.** That
  is the posture the authority guard already takes for metadata over plain
  HTTP, and it is a real availability cost taken on purpose. Nothing here
  measures how a cluster behaves when it is paid.
- **#127 is unchanged and is now the only residual of the three.** A handler
  outrunning its claim reaches §6.3's stamp after the claim has gone, for
  reasons no clock is involved in. No value of the retention floor reaches it.
- **The race between the `SELECT` and the `DELETE` is stated in ADR-039 and not
  closed.** A retry can re-claim a key between the two. It is not a
  regression — one `DELETE` had the same window — and both attempts are already
  past `Window` from the original commit, where §8.5's guarantee ends by
  design.
- **A second race in that window WAS a regression, and this entry carried "not
  a regression" over it for four review rounds.** A key names a command and not
  a row, so a retry past the guarantee can *commit* under a key this pass has
  already selected — and with three purge replicas, a second purger's key-only
  delete removes that replacement: a row inside its window with a live claim
  behind it. The statement this split replaced matched on age and could not.
  **The lesson is the one this repository keeps paying for**: a refactor that
  splits an atomic statement has to be checked against what the atomicity was
  buying, and "the same race existed before" was true of one half and hid the
  other.
- **The first fix for it was wrong in a way worth keeping**, because the
  reasoning is more instructive than the defect. Repeating the age cutoff in
  the `DELETE` restores exactly what the single statement matched on — and it
  re-reads `SYSDATETIMEOFFSET()` to do it, so a forward step of the database's
  clock between the two statements makes the replacement look old enough and
  the stale delete takes it regardless. **An age against a moving clock cannot
  guard an ABA**, in a pull request whose entire subject is that the clock
  moves: the fix assumed away the fault the change exists to survive. The
  delete **names the row rather than describing it**, and it took three failed
  predicates to get there. A bound on the newest selected `CommittedAt`
  survives a forward step and falls to a backward one; the age cutoff is the
  mirror; and the two together fall to a backward step followed by a
  correction, which puts the replacement below the bound and past a re-read
  cutoff at once. **An arbitrary clock cannot be out-predicated**, and a fourth
  attempt would have been the guess ADR-037 and ADR-038 exist to refuse. The
  statement now joins the `(Key, CommittedAt)` pairs the `SELECT` returned: the
  key names the command, the timestamp names the write, and a replacement is a
  different write — **by construction rather than by constraint**, which is the
  qualification the twelfth round added and the ninth had already filed as
  [#173](https://github.com/alexander-shamray/blueprint-backend/issues/173).
  Nothing enforces uniqueness on a `datetimeoffset(7)`, so a replacement
  stamped at the selected row's exact tick would match; that needs a clock set
  to an exact historical instant rather than drifted by a magnitude, and a
  `rowversion` is what closes it.
- **Three rounds spent on one defect is the entry's own lesson.** Each fix was
  a smaller predicate over the same moving quantity, and each review found the
  movement it did not cover. The shape that ended it was giving up on
  describing the row and identifying it — which is what the atomic statement
  had been doing implicitly all along. **A `rowversion` says the same thing and is owed rather than
  declined** — #173. It costs a column, a migration per service, a scaffold
  change and an Appendix D row; what it buys is the uniqueness the pair has by
  construction and not by constraint. The first draft of this entry called it
  declined, which was the reading before the residual was named.
- **Two review rounds were needed to reach that**, and neither the defect nor
  the wrong fix could have been caught by the suite: the interleaving needs
  three purgers, a retry and a clock step, and nothing here stages one. What
  the tests do cover is that a version-bounded delete still deletes — the
  1,001-row chunking case runs through the same statement.

## PR-33 — the two terms an allowance stood in for, and what closed them

**#167 and #168 were filed by the PR that created them, which is the part
worth reading first.** PR-32 put a correctness property at a boundary set by a
retention number and then discovered it could not make the two numbers alone
decide it: the rule it needed — the marker outlives the claim — is a statement
about two *instants*, and the two instants were produced by different events on
different clocks. It shipped a five-minute `MarkerLeadAllowance` on the floor,
covering both, and said in three places that the allowance bounded the terms
rather than removing them. This PR removes them.

**An allowance that stands in for an unbounded quantity is a guess with a
number on it.** Both terms were unbounded in principle. The lag between
stamping the marker and re-arming the claim is the commit's tail, which a
process suspension or a stalled connection stretches arbitrarily; the skew
between two pods' clocks is bounded by NTP working, which is not a property
this repository can assert. Five minutes covered every ordinary value of each
and the shipped default carried six days of margin regardless — and nothing
detected either term exceeding it, in a mechanism whose whole purpose is that a
write happens once and whose failure is silent.

### The first term was the lag, and it was not the clock's fault

§6.3 stamps `CommittedAt` inside the transaction, before `SaveChangesAsync`;
§8.5's `CompleteAsync` runs only after `next()` has returned, which is after
the commit. Re-arming there started the claim's window at the *commit* — later
than the stamp — so Redis outlived the marker by exactly that lag with
perfectly synchronised clocks.

`CompleteAsync` now writes `SET … KEEPTTL` and takes no retention at all. The
claim's window runs from `TryClaimAsync`, which precedes the stamp on the same
thread in the same dispatch, so the ordering is a consequence of the code's
shape rather than of any duration.

**The parameter went with the behaviour, and that was the decision rather than
a tidy-up.** A signature still taking a retention it no longer uses invites the
next implementation to use it, and the next implementation is where a
re-armed entry would come back with nothing to catch it.

**What it costs is a change to what §8.5 promises, and PR-32's own issue said
so before this PR existed.** An outcome now stays replayable for the remainder
of the window the claim opened rather than for a full retention starting at the
commit, so a command that ran for an hour has spent an hour of its own replay
window. That is a real loss and it is priced against the guarantee it buys —
*at most one commit per key while the marker survives* — which is the one the
mechanism exists for. It is why #168 was filed rather than folded into PR-32:
changing a stated guarantee is a decision, and a decision taken inside a PR
about something else is a decision nobody reviewed.

### The second was one row aged by two clocks

`CommittedAt` was stamped from the writing pod's `TimeProvider` and the purge
cutoff computed on whichever pod ran the purge, across §15.3's three replicas.
The column now carries a `SYSDATETIMEOFFSET()` default and
`RetentionPurgeService` computes the marker's cutoff as
`DATEADD(second, -@WindowSeconds, SYSDATETIMEOFFSET())`, so both ends of the
comparison belong to the server that owns the row.

**Only the marker moves, and the asymmetry is argued rather than left to be
tidied.** §9.5 states, twice and with a reason, that the purge reads its cutoff
from `TimeProvider` and not from `SYSDATETIMEOFFSET()`, because a test host
substitutes the clock. That reason is worth more where the window is
housekeeping and worth less than a single clock where the window *is* the
guarantee — so the marker is now the stated exception and the other two are
unchanged. Nothing was traded to get it: the two `RetentionPurgeTests` suites
stage rows at explicit ages against the real clock, not a substituted one,
which was checked rather than assumed.

**Both halves are needed and neither is sufficient**, which is why they landed
together. A default with an application-computed cutoff still compares two
clocks; a SQL cutoff against an application-stamped column does the same.

### Two tests had to be inverted, and that is the interesting cost

`RedisIdempotencyStoreTests.A_claim_carries_the_retention_as_a_time_to_live`
asserted that completing **re-armed** the TTL, and it was a *good* test: an
earlier version asserted only that the completed TTL was above zero, which a
freshly claimed key satisfies whether the write inherited the expiry or
replaced it, and a review round had already shortened the key first precisely
so the assertion could fail. `RetentionPolicyTests` asserted that a window
equal to the claim's was **refused**, and it too carried the argument for why
in place.

**A test that pins a design correctly is the thing you have to edit when the
design changes, and the edit is where the design change gets hidden.** Both
were rewritten to assert the opposite with the history kept in the comment, and
the second now also asserts that `MarkerFloor` equals `Window` — so a later
reader who reinstates an allowance fails a test whose subject is the
relationship, not one whose subject is a number.

The floor's own history is worth carrying for the same reason: `RetentionPolicy`
first admitted equality, a review round established that it should not and
added the allowance, and this PR admits it again. Somebody who does not know
why the allowance existed will re-add it, so ADR-038 and both XML doc blocks
name the two terms and say what closed each.

### #166 — an assertion that was two claims

`InboxFilterTests.A_redelivered_message_is_dropped_and_its_consumer_runs_once`
failed once in CI with two inbox rows where it asserts one, and the filter was
blameless in the very run that failed: the line above it, asserting the
consumer ran once, passed. `ServiceFixture.InboxAsync()` returns every row in
the schema, so the assertion claimed both *the filter suppressed the duplicate*
and *no other row exists anywhere* — and only the first is the test's subject.

The fix is `docs/lessons.md`'s own entry applied where it had not reached:
fence on the message's own id, in the helper every test already calls. A scoped
`InboxAsync(Guid)` joins the unscoped one, which stays for the assertions whose
subject really is the table. **The waits were scoped as well as the
assertions**, and that half is not cosmetic: an unscoped `Eventually(…,
expected: 1)` returns on a leaked row, so the second publish then races the
first delivery's commit and the filter has nothing to read.

**The scoped read got a test whose subject is the scoping**, observed red
against a predicate that ignored its argument. Without it a read matching
nothing would make every assertion above it pass vacuously, which is this
repository's most-repeated failure arriving inside the fix for it.

### #164 — a predicate over the rendered block, not a list of names

`new_service.py RabbitMq` rendered a Compose block carrying
`ConnectionStrings__RabbitMq` twice: the service's own §7.1 key renames onto
§14.1's broker key, one of the two values is discarded by whatever parses the
file, and which one survives is the parser's choice. Nothing refused it — the
rename was correct, no template token survived, and the YAML stayed valid.

The issue offered a list of reserved names beside the SQL and Windows lists.
**A list is the shape that goes stale**, and it would have gone stale the first
time a sixth `ConnectionStrings__*` key joined the template's block. What
landed instead reads the environment keys back off the block the script has
just rendered and refuses a duplicate, so the check is a property of the file
rather than of anybody's memory of it. `RedisCache` and `RedisCoordination`
collide the same way and were measured, not assumed.

Scoping the check per `environment:` mapping rather than over the whole block
was load-bearing: `build:`'s nested pair and `depends_on:`'s entries sit at the
same indent, and a flat set matched all of them.

### What is owed

- **The two windows are still counted by two servers' clocks, and the
  reviewer found the overclaim rather than the code.** Nothing couples Redis's
  expiry rate to SQL Server's, so a forward step of the database's clock
  relative to Redis's can carry the purge past the marker while the claim is
  live; what absorbs it is the handler's runtime plus whatever the window
  exceeds the floor by, which is six days on the shipped defaults and nothing
  at the floor. The prose said the ordering held "by construction" and meant
  the two *start* events; it now says which. Reinstating an allowance was
  rejected — five minutes never bounded a clock step either, so a number there
  repeats in a third term the mistake this PR removes from two — and the fix is
  one time source for both deadlines, filed as
  [#171](https://github.com/alexander-shamray/blueprint-backend/issues/171).
- **A second assumption came out of the review, and it has nothing to do with
  clocks.** The expiry ordering also wants the marker to reach the database
  inside the claim's own window — and the deadline is later than "the handler
  finished", which is how this entry first put it: §6.3 stamps after the
  handler returns, after §7.5's dispatch and §2.3's count, and the row does not
  exist until `SaveChangesAsync` sends it. A claim expiring anywhere before
  that commit leaves the marker written with no claim in front of it. That is
  §8.5's long-standing overrun residual (#127) reaching a conclusion nobody had
  connected it to, and it holds with every clock in the
  platform correct.
- **A backward step of the server's clock is *not* a third one**, and this
  bullet said it was. Both ends of the purge predicate read the clock that
  moves, so a marker stamped after a backward step is aged against a cutoff on
  the same shifted timeline and expires on schedule, while rows stamped before
  it merely survive longer — the direction the floor exists to produce. Only
  the forward correction that follows is a hazard, and that is #171's step
  rather than a second issue. The claim was written into ADR-038, the pull
  request's description and here; a reviewer disproved it in all three, and it
  is corrected rather than softened because a documented failure mode that
  cannot occur costs the next reader the time to rule it out.
- **§8.5's remaining residual is unchanged.** Nothing
  bounds the claim's retention against a handler's runtime, so past the claim's
  expiry a successor may claim the key and both attempts run; the claim token
  keeps the loser from corrupting the winner's entry (#127) and no more.
- **`Catalog.Api.Tests.DatabaseSmokeTests` still counts its migrations with a
  literal.** #126 retired that shape in Ordering — `expected.Length` beside a
  named list, so a new migration is one edit rather than two — and this PR
  added the seventh entry to Catalog's literal `ShouldBe(7)` beside its seven
  named rows because converting it is a change to a file §4.5's scaffold
  patches by exact text. It is the same defect #126 named, still standing one
  service over.

## The controls that were only ever written down (#45, #46, #49, #139)

Three security claims and one style rule, and the shape they share is that
**each was stated somewhere and enforced nowhere**. A denylist that no code
reads. A refresh token the chapter drew arriving at a browser it should never
have reached. An erasure rule contradicted by the platform's most widely
consumed contract. A continuation rule the corpus disagreed with in one file.
None of the four was found by a test going red, because in each case the thing
that would have gone red is what was missing.

**The tell they share is that reading either side alone leaves you satisfied.**
§11.3 said outright that no deny list exists; ADR-006, §8.1 and `RedisKeys`
said one did. §11.7 named an `OrderConfirmed` carrying personal data as the
forbidden case; §9.1 shipped exactly that and argued for it. In both, the
inconsistency was invisible from either end and only a reader holding both at
once could see it — which is the one rule's whole subject, arriving three times
in one branch.

### The keyspace nobody read (#46)

`RedisKeys.Denylist` existed. §8.1 gave `{service}:denylist:` the strictest
eviction policy in the platform and argued for it on the grounds that a
revoked-token entry must never be silently evicted. ADR-006 recorded the
denylist among Redis's decided uses. Tests pinned the key's shape. **Nothing
ever wrote or read it**, and §11.3 said so in a sentence nobody reconciled with
the other three.

[ADR-033](backend-architecture/adr/ADR-033-revocation-is-bounded-by-the-token-lifetime-and-no-denylist-exists.md)
withdraws the claim rather than building the consumer, and the four
measurements behind that choice are in the record. The one worth repeating
here: **two of the four hosts have no Redis at all**, and one of them is the
gateway — the edge every external request enters. A `JwtBearerEvents` handler
resolves per request, so `ValidateOnBuild` cannot see the gap; the gateway
would have thrown on its first authenticated request, and the obvious remedy of
making the lookup optional would have meant the edge silently never checking.
That is the fail-open shape, reached by trying to close a hole.

**The access-token lifetime was the other half and was never stated at all.**
The realm ran `accessTokenLifespan: 300` and no chapter said so, which left the
exposure window a realm default nobody had chosen. §11.3 now states it
normatively and `RealmImportTests` pins the realm against it. **The coincidence
that nearly hid it**: §11.3 already reasoned about "five minutes" — but that
was `ClockSkew`'s default, a different quantity that happens to share a number,
and the section read as though the lifetime were covered because a five appeared
in it.

**A plausible cause you can see beats an actual cause you have not looked
for.** This log already carries that lesson from the HybridCache meter, where a
registration stood in for a signal. The denylist is the same failure one level
up: not a name registered against nothing, but a *decision record* standing in
for a mechanism. Asking "is revocation handled" returned three yeses and one
no, and the three were louder because they were the ones written like answers.

### The token that outlived the session (#49)

§11.2's flow delivered a refresh token to the browser. The realm was not merely
permitting that, it was configured for it: `revokeRefreshToken` off,
`refreshTokenMaxReuse` zero, `ssoSessionMaxLifespan` ten hours — so the token
was reusable, never rotated, for as long as the session lived, and with no
revocation path (#46, above) nothing could cut it short.

**#49's cheap option was unreachable until #46 was answered, which is why they
landed together.** "SPA-held tokens, stated honestly" needs a stated lifetime
and a stated revocation posture; without those, the residual it records is
unbounded and the honesty is decorative. Ranking the two issues independently
would have priced #49 wrong — this repository's own rule that two open
questions can decide each other, arriving for the third time.

**The fix was verified before it was written, and both directions were
measured.** `use.refresh.tokens` is documented as a client attribute; what was
not established is that it does anything for this client, on this realm, on the
Keycloak version §14.1 pins. Run against `quay.io/keycloak/keycloak:26.0` with
this realm file: unmodified, `web-app` is issued a `refresh_token` with
`refresh_expires_in` 1800; with the attribute, there is no `refresh_token` key
at all and `refresh_expires_in` is 0. **The control was the same client, not a
different one** — a first pass compared `web-app` against `admin-cli`, which
would have established only that the two clients differ.

**What was deliberately not done.** Terminating the flow in `Web.Bff` is the
stronger answer and is
[ADR-034](backend-architecture/adr/ADR-034-the-browser-holds-an-access-token-and-no-refresh-token.md)'s
stated runner-up: an OIDC handler, a cookie stack, antiforgery on every
state-changing route, a realm change and a gateway route change. §11.5's
account of the BFF holding exactly one credential is correct today and would
not survive it. That is an Appendix C row, not an edit.

### The address that could not be erased (#45)

`OrderConfirmed` carried a `ShippingAddressV1`. §9.1 argued it in on
"fat enough" grounds; §11.7 named that exact contract as the counter-example of
what must never happen. The contradiction is what made it worth filing, because
a reviewer reading either chapter alone concluded the rule held.

**What settled it is that every escape route is one-way.** The payload is
serialised into `ordering.OutboxMessages`, whose purge deletes only rows with
`ProcessedAt IS NOT NULL` — deliberately, so §13.6's alert can see abandoned
rows — so an abandoned row keeps the address indefinitely and a test guarantees
it. It sits in the broker, for which no chapter sets a retention bound. It is
copied into whatever each consumer persists from it — not the inbox, which
holds a message id, an endpoint and a time and no payload — and §3.2 gives the
event to Notifications,
which has no use for an address. §13.4's redactor matches key names and none
covers an address — and `SensitiveKeysTests` held a **green test asserting
`ShippingAddress` must not be redacted**, so the platform did not merely fail to
protect the field, it pinned that it exported it.

**Amending §11.7 instead was refused, and the reasoning generalises.** Defining
an erasure procedure for event-borne personal data looks free because it touches
no code. It is not: it needs an erasure-triggered outbox purge that cannot reuse
the retention purge whose predicate is load-bearing, a consumer-side obligation
binding services that do not exist, and a broker retention bound nothing sets.
It would also have to reverse §11.7's own sentence — "which is not practically
possible" — into a procedure nobody can run. **That converts an honest
contradiction into a dishonest resolution**, which is worse than the
contradiction, because the contradiction is at least visible to anyone holding
both chapters.

**Nothing is deployed, which is the only reason this was cheap — and that is
not the same as the field having had no consumer.**
[ADR-035](backend-architecture/adr/ADR-035-an-integration-event-carries-identifiers-not-personal-data.md)
is the record §9.2's second condition demands. Its first condition was **not**
met, and three separate places said it was before review caught the last of
them: Shipping and Notifications are unbuilt, so no independently scheduled
service could be stranded, but §9.6's saga binds `Event<OrderConfirmed>` and a
bound consumer deserialises the whole payload however little its transition
reads. "The saga reads only the `OrderId`" is true and answers a different
question — it is about what the transition uses, where the removal is about
what the deserialiser requires.

**What made it safe is that no cluster has ever run this platform**, so there
is no old replica to hand a reduced payload to. Every hour Shipping does not
exist is an hour this removal stays free; the same edit after Shipping ships is
a version bump with a consumer on the other side, and the same edit after the
*first deployment* owes ADR-026's no-overlap cutover or a two-release
retirement even without one.

**Three copies of one wrong sentence, corrected in three different rounds, is
the finding rather than the sentence.** The claim was written once and restated
in §9.2's callout, in ADR-035's own argument and here; each round's fix reached
the copy that had been quoted at it and left the others standing. A reconciled
claim is only reconciled where somebody greps, and the grep has to be for the
*claim* rather than for the site under discussion.

**The domain event keeps its `Address`, and the asymmetry is the decision.**
`OrderConfirmedDomainEvent` never crosses a service boundary and
`ordering.Orders` legitimately stores the address of the order it describes —
§11.7 governs what travels, not what a service holds about its own data.
**A comment written for that asymmetry had to be corrected before it shipped**:
a first draft explained that the domain event "travels the Local lane, in
process, to a projection reading a table". `DomainEventDispatcher` stages on
the Local lane only where `projections.HasHandler`, and Ordering registers no
domain-event handler at all — so the mechanism named was one nothing runs. The
claim was plausible, adjacent to real code, and would have shipped as the
file's own explanation of itself.

### The rule the corpus disagreed with (#139)

`docs/style-guide.md` puts a broken fluent chain's continuations at head + 4.
`OrderFulfilmentSagaTests.cs` used head + 8 for one shape — and held nine sites
at +4 in the same file, so it was neither the repository's form nor its own.
§12.5's sample followed the minority form, which is the half that mattered:
the chapter is the specification, so a service built from it inherits the
contradiction.

**The count had grown between filing and fixing** — 22 sites became 34 — which
is the ordinary fate of a style issue left open, and the reason the issue argued
for a sweep rather than a drive-by. What made the sweep safe was that the two
populations were separable by column: every +8 site sat at indent 20 with its
head at 12, and every already-correct site at 16. That was checked before a
line moved, because a transformation keyed on the wrong predicate would have
dedented nine correct sites into being wrong.

**No new rule, and that is the point.** #139 is the one issue in this branch
where the specification was already right and only the corpus was wrong, so the
fix is a sweep and a chapter edit with nothing to record beyond having done it.

### What this branch measured about itself

**The suite total went *down* for the first time.** Withdrawing the denylist
took `RedisKeys.Denylist` and the case pinning its shape with it, so
`Common.Infrastructure.Tests` reads seventy on the fast side where it read
seventy-one, and `docs/testing.md`'s retake series — which had only ever grown —
now runs in both directions. That is recorded there rather than only here,
because a figure that has only ever grown trains the next reader to check
whether it is *behind* rather than whether it is *wrong*.

**Both new realm facts were checked against the wrong answer before being
trusted.** Flipping `accessTokenLifespan` to 900 and `use.refresh.tokens` to
`true`, **rebuilding**, and observing two failures is what establishes they are
gates rather than green decoration — and the rebuild is load-bearing, since the
realm file is copied to the test output and `--no-build` would have run the
stale copy and reported a pass. This log already carries that lesson from a
counterfactual that did not rebuild; it applies to a content file exactly as it
does to an assembly.

## The mechanism a word stood in for (#128, #137, #62)

Three issues, and each is a word that had been read as a mechanism for long
enough that nobody re-read it. **Outbox** on §9.6's receive endpoint meant a
process-local buffer that flushed *after* the commit it was supposed to
survive. **27 pull requests** was a total for a document that had grown a
section past it. **Development-only** in §14.3 was an instruction to whoever
writes the seeder, on the one container this platform runs in production
holding §7.1's DDL identity. None of the three was wrong when it was written;
what makes them one shape is that each kept reading correctly after the thing
behind it had stopped being true, so the only way to find any of them was to
run something.

### The buffer that was called an outbox (#128)

`OrderFulfilmentSaga` persisted its instance through
`EntityFrameworkRepository` and sent its commands through `UseInMemoryOutbox`,
which releases its buffer after the inner pipeline returns — after the
repository has committed. A crash in that window left the order in
`AwaitingPayment` with stock reserved, no `AuthorisePayment` sent and **no
`PaymentTimeout` to rescue it**, because the schedule sat in the same buffer.
[ADR-032](backend-architecture/adr/ADR-032-the-sagas-outbox-is-masstransits-in-the-sagas-own-transaction.md)
carries the decision, the exception it takes to §9.3 and the two costs it
accepts, and none of that is restated here. What follows is what building it
found.

**The issue named the wrong API, and the misreading had travelled by
citation.** `UseBusOutbox()` is a **bus-side** option on
`IEntityFrameworkOutboxConfigurator`: it intercepts `IPublishEndpoint` and
`ISendEndpointProvider` *outside* a consume context, which is the API request
path §9.4's application outbox already owns, and it does nothing whatever for a
receive endpoint. The endpoint-side call is
`UseEntityFrameworkOutbox<OrderingDbContext>(context)`, and it needs
`AddEntityFrameworkOutbox<OrderingDbContext>` at the bus for a store to write
to. This log had carried the wrong name forward three times — twice as the API
and once as the assumption that §9.3's dismissed alternative was merely dearer
— and all three are reconciled in place rather than rewritten, on the rule the
header states: an argument is left alone, a claim about the system is not.
**Nobody had run it.** A name that is plausible, cited and never executed is
indistinguishable from a name that works, which is the same property the rest
of this entry keeps meeting.

**The alternative §9.3 dismissed is unavailable, not merely dearer, and that is
the finding rather than the fix.** §9.3 said routing saga output through §9.4's
outbox "would add a second staging hop with no additional guarantee" — a claim
about cost, and true only while the in-memory outbox was believed to persist
anything. A saga timeout is a **scheduled** message and the delay is a
transport feature
([ADR-021](backend-architecture/adr/ADR-021-saga-timeouts-are-scheduled-by-the-broker.md)),
so no dispatcher of ours can replay a delay it never held. An application
outbox would have carried `AuthorisePayment` and not `PaymentTimeout`: it
closes half the window and leaves the half with **no bound at all**, which is
worse than the shape it replaces because a stuck order with a timeout on it
eventually announces itself. A sentence ranking an option by price is a
sentence nobody re-checks for whether the option exists.

**Both counterfactuals were measured, and they fail in different halves.**
Reverting the endpoint line alone leaves both new registration tests green and
turns the integration test red; removing `AddEntityFrameworkOutbox` alone turns
the registration test red. Neither half implies the other, which is the whole
argument for there being two tests rather than one:
`The_saga_has_a_transactional_outbox_rather_than_an_in_memory_one` in
`tests/Ordering.Api.Tests/MessagingRegistrationTests.cs` reads the container,
and `The_sagas_sends_are_committed_with_its_instance_rather_than_buffered` in
`tests/Ordering.Api.Tests/OrderFulfilmentSagaEndpointTests.cs` reads
`ordering.InboxState` against a real broker and a real SQL Server, because a
receive endpoint's filters cannot be read back out of a `ServiceCollection` at
all.

**What that integration test asserts is not that a message arrived.** Every
other test in that class already proves delivery, and every one of them passed
before this change — delivery is what the in-memory outbox also does, right up
until the process dies. The observable difference is *where the messages were*
between the commit and the send, and MassTransit records exactly that:
`LastSequenceNumber IS NOT NULL` on the inbox row is stamped once the messages
staged beside it have gone. It is paired with the same query minus that
predicate, as an anti-vacuity control — without it, a `WHERE` matching nothing
reads exactly like a delivery that has not happened yet, and the test fails for
the wrong reason after thirty seconds of waiting.

**A registration added a background writer, and a background writer is a new
participant in every lock the fixture takes.** `AddEntityFrameworkOutbox` also
registers a hosted `InboxCleanupService<OrderingDbContext>` that prunes
`ordering.InboxState` on a timer for as long as the host is up. Respawn's
`ResetAsync` deletes every row in the `ordering` schema in its own dependency
order, so two multi-table deletes run concurrently over the same tables and SQL
Server picks a victim — error **1205**, surfacing out of
`OrderingCommandEndpointTests.InitializeAsync`, *a test with nothing to do with
sagas*. It presented as a flake and passed on re-run, which is how it would
have gone on presenting; capturing the exception is what named it.
**The first answer was a bounded retry on 1205 in `ResetAsync`. Review found a
better argument, the branch acted on it, and the better argument was wrong
about the mechanism.** `OrderingApiFactory` already takes the outbox dispatcher
and the retention purge out of the test host, on the ground that a background
writer racing an assertion makes "the pass never happened" and "the pass spared
the row" the same green — so the cleanup service belonged on that list, and
the file's own precedent was an argument nobody had reached for. The branch
removed the writer **and deleted the retry**, on a sentence that read well:
*there is no second deleter to race*.

**It reproduced with the cleanup service gone, on the second of six runs.** A
deadlock needs two transactions taking locks in opposing order, not two
deleters. What Respawn races is whatever consume transaction is still
committing when the next test resets — and ADR-032 made the saga's longer,
multi-table and `Serializable`, so this decision widened a hazard the fixture
already documented as one it cannot close. The removal stands on the precedent,
which is enough on its own; the retry is back, and both comments now say which
of the two does which job.

**That is the third time on this branch that a claim was reasoned rather than
run, and the only one whose reasoning was the branch's own rather than
inherited.** The API name came from an issue and so did the signal; this came
from a sentence written while deleting the code that contradicted it. Six runs
cost twelve minutes. Ignoring MassTransit's tables in the reset was the third option
and stays refused — it leaves rows standing between tests and makes the next
whole-table assertion over them wrong for a reason nobody would find.

**The registration this most wanted to assert was said not to be public, and
that was a failed `using` directive written up as a property of the library.**
`IOutboxContextFactory<OrderingDbContext>` is what the filter resolves, it is
public, and it lives in `MassTransit.Middleware` rather than the
`MassTransit.Middleware.Outbox` the first attempt guessed at. Compiling it is
what settled it. **The correction is not cosmetic**: the subject that claim
settled for, `InboxCleanupService<OrderingDbContext>`, is registered behind a
flag `o.DisableInboxCleanupService()` clears, so the gate would have gone red on
a change leaving the outbox entirely intact — a gate keyed on the wrong thing,
which is this repository's most-repeated failure wearing its opposite face. The
negative test beside it, that
`IBusOutboxNotification` is **not** registered, pins a decision rather than
catching a defect, and it is not left on its own: a negative assertion passes
when the name it looks for stops existing, so the positive test is its control
against a MassTransit bump renaming either type.

**The window was observable, and a draft of this branch's ADR said it was
silent.**
[#128](https://github.com/alexander-shamray/blueprint-backend/issues/128)'s
own body describes
[#117](https://github.com/alexander-shamray/blueprint-backend/issues/117) as
having replaced the fault with an `Ignore()` and a log line. #117 tried that and
then **removed** it: what shipped keeps MassTransit's default and enumerates the
legitimate arrivals one at a time, and `Ignore(StockReserved)` is declared
`During(Compensating)` alone — so the redelivery this window produces, a
`StockReserved` landing in `AwaitingPayment`, is genuinely unhandled. It faults,
spends §9.8's five retries and reaches the error queue
[§13.6](backend-architecture/13-observability.md) pages on, which
`A_redelivered_event_faults_rather_than_being_absorbed_silently` in
`tests/Ordering.Application.Tests/OrderFulfilmentSagaTests.cs` has asserted
since #117 landed. **Reading the issue instead of the state machine is how it
got in** — the same failure as the wrong API name above, arriving through a
second door: a claim that was plausible, that had travelled by quotation, and
that nobody had run anything to check. ADR-032 carries the correction and it is
not restated here. What matters for what comes after is that the finding
**relocates the case rather than weakening it**: a page is not a repair, and
nothing in the error queue recreates the `AuthorisePayment` that was never sent
or the `PaymentTimeout` that would have rescued the order, so what the decision
buys is that there is nothing left to reconstruct by hand.

**`OnUnhandledEvent`'s enumeration did not come back to being a catch-all, and
the reason is arithmetic rather than caution.** #117 removed a catch-all
because three arrivals reached it and one answer could not be right for all
three. ADR-032 deletes the middle one — the pre-flush crash that left an
instance advanced and its commands unsent — which leaves two. A callback
answering two cases identically is still only ever as right as its worse one,
and the worse one here is the misroute, which is a configuration fault worth
six retries and an error-queue message. **The case that got cheaper is the one
the catch-all was already right about**, so nothing about closing the window
made the enumeration less load-bearing.

### The rows the plan had grown past (#137)

`docs/roadmap.md` opened by saying Appendix C "sequences the work into 27 pull
requests", and the appendix's *After the plan* section already held two rows
past that number before this branch added a third. Further down, the ADR-019
paragraph priced the analyser policy's per-pull-request cost as "24 pull
requests wide", which works out only as PR-02 … PR-25 and was already false
against the Scope row's own PR-27. **Both counts are gone rather than
corrected**, which is the fix this repository reaches for every time a restated
total rots: what a reader can check is that the rows below are Appendix C's
rows, title for title and phase for phase, and that check needs no numeral in
front of it. A figure true of one part of a document and false of the document
is worse than none.

**C.3 called its own graph an omission, and it is a rule.** The prose under the
mermaid block said the missing *After the plan* nodes were "an omission rather
than a claim about their dependencies" and filed itself as this issue. Drawing
them buys nothing that can be acted on: a row lands in that section only
because the plan was already complete, so every pull request it depends on has
already been delivered — PR-28's three, PR-29's one, PR-30's one — and a node
whose predecessors are all landed can neither free a branch to start nor warn
that one is blocked. That is structural rather than a coincidence of today's
three rows. The graph now says it is the plan and stops at PR-27 **by rule**,
and #137 is closed by stating the rule rather than by adding the edges the
issue proposed.

**The roadmap's *After the plan* section carries no estimate, and the omission
is the decision.** Every number in that file was quoted before any code
existed, against a specification that was already finished; not one of the
three rows was priced that way, since two were rowed after they landed and the
third is rowed by the pull request doing the work. A figure there would be
either invented — which the *Basis* section's own terms forbid, an invented day
being no argument about relative size — or an actual restated as a forecast,
which makes the column mean two things depending on which row is read. PR-26 is
the near case and does not supply the missing rule: its four days stayed out of
the total, and they are legitimate for the one reason these rows cannot borrow,
which is that it was priced before it was taken. **The days were real and this
file cannot say how many**, which is a different claim from the work having
been free.

**One claim in that file had been falsified and nothing had looked.** The
domain-risk section credited PR-12 with narrowing the largest item on the page
because §8's Redis helpers were "shared mechanism in `Common.Infrastructure`,
wired to no service at all" — PR-28 wired them into both Catalog and Ordering,
and Appendix C's own PR-28 row names that wiring as a deliverable. **The seven
days do not move**, which is why the sentence is reworded rather than deleted:
what makes PR-12 domain-proof is that its helpers name nothing a domain owns,
not that nothing had called them. Being wired into two services it knows
nothing about demonstrates the property rather than costing it, and the state
claim was never the argument — it was a convenient way of gesturing at one,
which is precisely the kind of sentence that goes stale without anybody
noticing the argument survived.

### The gate that was a sentence, and the guard that never opens (#62)

§14.3 said seeding "runs from the migrator container, is idempotent, and is
development-only", specified no mechanism for the last of those, and told an
implementer where to put the seeder. §15.3 runs that same migrator image as
Helm's `pre-install,pre-upgrade` hook on every release, holding the DDL
identity §7.1 gives it and nothing else in the platform holds. A seeder written
where the paragraph pointed, with no gate, writes demo rows into a production
database on the first `helm upgrade` — **idempotently**, so nothing fails, no
hook goes red, and the deploy log says only that a migration ran. §14.3 is now
that gate, and §7.4 and §15.3 carry the lines that make it one.

**The guard the issue asked for would have been fail-closed and permanently
closed, and that is measured rather than reasoned.** `Ordering.Migrator` builds
its host with `Host.CreateApplicationBuilder`
(`src/Services/Ordering/Ordering.Migrator/MigratorHost.cs`), and a generic host
binds its environment from **`DOTNET_ENVIRONMENT`**, not
`ASPNETCORE_ENVIRONMENT`. With `ASPNETCORE_ENVIRONMENT=Development` set and
`DOTNET_ENVIRONMENT` unset, `EnvironmentName` is `Production` and
`IsDevelopment()` is false — so the obvious guard never opens **anywhere**, and
what a developer then debugs is the seeder, because the gate is the silent half
and the seeder is the visible one. §14.1's two migrator services set no
environment name at all today, which is why the chapter puts
`DOTNET_ENVIRONMENT: Development` beside their connection strings in the same
change as the guard rather than leaving it for whoever next runs
`docker compose up`.

**Two further measurements shaped the guard, and both are about the blank
value.** `Configuration.GetValue<bool>` **throws** on an empty string rather
than answering false — a variable set to `""` reaches configuration as `""` and
not null — so a stray key would fail the pre-upgrade hook and block a release;
the guard reads the raw string and `bool.TryParse`s it, which answers the same
way for `""`, for null, for `1` and for `yes`. And `!IsProduction()` is **not**
the same test as `IsDevelopment()`: with the variable set to the empty string
`EnvironmentName` is `""`, and `IsDevelopment()` and `IsProduction()` are both
false, so the negation admits exactly the value a templating mistake produces.
Of the two spellings of one condition, the one that reads as more permissive is
the one that fails open on the input nobody types deliberately.

**The chart half is what makes turning it on a diff rather than a variable.**
`deploy/helm/common/templates/_migration-job.tpl` renders exactly one `env`
entry — §7.1's migrator connection string — so `Seed__Enabled` has no route
into that container at all. Enabling seeding in a cluster would take a new
entry in the library chart's template *and* a values file, both lines somebody
reviews, rather than an export onto a namespace. §15.3 states that no chart
carries a seed key **as a statement rather than an omission**, on that
section's own rule that a key joins a chart when a host's code reads one — and
no `*.Migrator` holds a seeder to switch on, which is the last thing worth
recording here: nothing seeds today, so §14.3 is a specification and not a
description. A mechanism written after the seeder exists is a mechanism argued
against code somebody has already shipped.

### What review found, and why all of it was the same defect

Three reviews ran over this branch after it was opened — Copilot's, and two
read-only passes of my own, one on the code and one on the claims. Between them
they found eleven things, and it is worth recording that **not one was a
disagreement about design**. Every one was a sentence that had never been
executed.

**The API name.** Covered above, and it is the cheapest instance: four sites,
months, no compiler.

**The signal**, argued in full under #128 above and not repeated here. The
short form: the window was observable, a draft said it was silent, and the
draft had read the issue rather than the state machine.

**The reason for a mapping.** Four documents said
`AddTransactionalOutboxEntities()` was used because
`ApplyConfigurationsFromAssembly` cannot reach an entity type declared in
another assembly. It can — the scan selects on where the *configuration* lives,
not the entity — so a local `IEntityTypeConfiguration<InboxState>` would be
found and applied. The conclusion survived and the reason did not: MassTransit
owns those mappings and its own queries read them, so a configuration of ours
would be a second definition of a schema the library has to agree with.

**The visibility of a type.** A test comment asserted that
`IOutboxContextFactory<OrderingDbContext>` "is not public at the 8.5.3 pin, so
it cannot be named from a test assembly". It is public, in
`MassTransit.Middleware`, and the claim was a failed `using` directive written
up as a property of the library. **That one had teeth**: the subject it settled
for instead, `InboxCleanupService<T>`, is registered behind a flag that
`o.DisableInboxCleanupService()` clears — so the gate would have gone red on a
change leaving the outbox entirely intact, which is this repository's own
gate-coverage failure wearing the opposite face.

**The isolation level, and this is the one that was a defect rather than a
sentence.** `ConcurrencyMode.Pessimistic` bought serialisation of two events
racing for one `CorrelationId`, and it bought it through a transaction the saga
repository opened itself at `Serializable`. Under this decision the repository
no longer opens it — joining the outbox's is the whole mechanism — so the level
in force became the outbox's, and MassTransit defaults that to
`RepeatableRead`. Measured: the option read `RepeatableRead`. The row-lock hint
does not cover it, because the case the mode exists for is two deliveries both
finding *no row*, where only a key-range lock helps. Recoverable — one loses on
the primary key and the endpoint retries — but a fault where there was none, on
the exact property the mode was chosen for. One line fixes it and a test pins
it.

**The retention claim, and the leak under it.** Five sites said an
`InboxCleanupService` prunes "MassTransit's tables". The package's own
documentation scopes it to `InboxState`; `OutboxMessage` drains on delivery and
`OutboxState` is touched by nothing at all here. Reading further gave the
sharper finding: the cleanup deletes rows whose `Delivered` is set, so a
transition that commits and then exhausts its retries leaves that row and its
staged messages permanently — **the one path this decision exists to make
survivable is also the one that leaks**. It is stated in ADR-032 rather than
closed, with the error-queue alarm named as what stands in front of it.

**The fixture**, argued under #128 above. The short form: the branch answered a
race with a retry, review pointed at a better-looking answer, the branch took
it and deleted the retry — and the race was not the one either of them had
named. Both changes are in; only one of them was the fix.

**What the reviews did not find is worth as much.** Both central mechanical
claims held under a decompiler: the outbox filter really does enlist the saga
repository's writes in one transaction, and `ScheduleSend` really is captured —
the scheduler's payload is replaced with one built over the outbox context, so
the delay is staged as a message property and restored on delivery. Those were
the two claims that, had either been false, would have made this decision
worthless rather than merely mis-argued. **Neither was checkable by reading the
code in this repository**, which is the argument for a review pass that goes
into the package rather than around it.

## The claims a gate never checked (#22, #50, #61, #64, #72, #119, #127)

Seven issues, and the shape they share is narrower than the last batch's. Those
were controls argued in a comment and enforced by nothing. These are controls
that **exist, run, and go green over a subject narrower than the one they are
read as covering** — a licence gate reading one file out of thirty-five, a
scaffold suite that renders text and never compiles it, a suppression branch
with no signal, a claim that could not say whose it was. Every one of them
passes today and would pass tomorrow with the defect present, which is the
property that makes a green result worth nothing.

**The exception is the compose binding, and it is here because it is the same
sentence read the other way.** `deploy/compose/README.md` addresses every
service as `http://localhost:…` and the file published all thirteen mappings on
`0.0.0.0`. Nothing was wrong with the documented workflow; what was wrong was
that the document described a narrower exposure than the file delivered, and
the deliberate development credentials underneath — `sa`, two passwordless
Redis instances, `guest`/`guest`, Keycloak's `admin` — are exactly what makes
the interface the control. The fix is one prefix per mapping and takes nothing
away from `localhost`.

### The scaffold was the file that broke, and that is the interesting half

Prefixing the mappings broke `tools/new-service` in two places, in opposite
directions. The substitution regex stopped matching and the scaffold
hard-failed — loud, found by running it. The **collision check** stopped
matching too, and that one is silent: a port already published read as free, so
the next service would have been scaffolded onto a taken one. Only the first
was ever going to be found by running the script, which is why the second is
worth recording.

The repair took a decision worth naming. The published-port pattern now
**requires** the loopback prefix of the template rather than copying whatever
Catalog happens to carry. Reading the prefix off Catalog would make the
scaffold agree with Catalog by construction, so removing the bind there would
publish every service scaffolded afterwards on every interface, silently — *a
gate that follows its subject cannot catch its subject regressing*. Anchored,
the same removal is a scaffold that refuses to run and says why.

### A claim names the work; only a token names the attempt (#127)

`RedisIdempotencyStore.CompleteAsync` and `ReleaseAsync` wrote and deleted
unconditionally, and every claim wrote the same `InProgressMarker` — so neither
write could tell *this* attempt's claim from a successor's, and an attempt that
outlived its own retention overwrote or deleted a live one.
`RedisDistributedLock` sits in the same folder, on the same connection, and
releases through a token-checked Lua script whose comment states this hazard
verbatim. **The asymmetry was the sharper half of the finding**: a reader who
has read the lock assumes the store does the same, so the hole sat behind a
pattern that read as applied.

`TryClaimAsync` now returns a claim token and both writes take it, comparing
and acting in one script for the reason the lock's comment already gave — a
check and an act that are two operations are two operations the claim can
expire between.

**A smaller fix was available and is worse than none**, which is why it was
refused: making the writes conditional on the value still being
`InProgressMarker` closes the payload-overwrite half only, because both
attempts write the *same* marker. It would read as a fix while leaving
claim-versus-claim open.

**What the token closes is corruption, not the overrun.** Nothing bounds the
retention against a handler's runtime; the behaviour passes 24 hours, so no
shipped path reaches this, and nothing in the port's contract stops a caller
passing seconds. Past the expiry a successor may claim and both attempts run —
the loser now fails to write rather than writing over the winner, and the store
logs the refusal. **The test drives a one-second retention**, because the
shipped 24 hours cannot be observed and a test that cannot reach the case is a
test about something else.

**The encoding chose itself against a hash.** The value is `{claim}:{state}`,
one string, because the claim has to be a single atomic write against a key
that may not exist: `SET NX` with a TTL is one operation, where `HSETNX` plus
`EXPIRE` is two and a claim dying between them is a key with no expiry at all.
The token is 32 hex characters and carries no separator, so the split is
unambiguous from the left however many colons a JSON payload holds on the
right.

**An entry carrying no token is read by the test the store used before the
token existed** — the marker means in progress, anything else is a recorded
outcome — because a claim written by the previous release is still inside its
retention when this one starts serving.

**The first shape of that branch reported the whole untokened class as in
progress, and the sentence defending it was where the defect lived.** It read
*both answers decline the duplicate commit, and only this one declines to
invent an owner*, which is true and beside the point: **a replay is not a
commit**. A completed pre-token entry read as in-progress answers 409 to a
retry of work that succeeded, for the rest of the retention — and then lets
the command run a second time once the key expires. During a rolling deploy,
which is the only window the branch exists for, that breaks both halves of
what §8.5 promises at once. Found by the external review, on the second round,
against prose that had already been through one.

The half that survives is the marker test itself, and it survives for the
reason it always held: the marker is deliberately not valid JSON, so no
serialised payload can spell it. The write side needed no matching case —
both scripts compare a token these values do not carry, so they no-op rather
than clobber.

**The double had to change with the port, and that is the load-bearing half of
the test work.** `RecordingIdempotencyStore` compared nothing, so every
behaviour test would have passed whether or not the behaviour carried its token
through — the shipped defect wearing a test's clothes. Two new cases assert the
write carries the token the claim returned, which is the claim no assertion
about *which call happened* can make.

**The grant that was explained by one caller now has two.** §8.1 grants
`+eval` and gave the lock's token-checked release as the reason — `EVAL` is
`@scripting`, which none of the data categories include, and under the shorter
grant that line used to print, every release threw and the lock stood until its
TTL. The store's move to Lua does not change the grant; it changes what a
reader may conclude from the explanation, which is **this repository's own
lesson that a premise about who calls a thing is falsified by the next caller**
arriving inside the change that falsifies it. Both are named now, and the store
has the lock's test: the §8.1 user provisioned live, the real type driven
through it.

That test needed the re-claim to say anything. `ReleaseAsync` swallows a
`RedisException` by design, so a missing grant there does not throw — it leaves
the claim standing for its whole retention and writes a log line. *It did not
throw* is exactly the assertion that would have passed against the defect, so
the claim is made from the other side: claim, release, claim again.

### An invisible drop path is why the attack had no signal (#64)

§9.5's inbox suppressed a duplicate with a bare `return;` — no log, no metric,
no error queue — so the one path on which this platform loses a message on
purpose was the only path with no signal anywhere in §13. The counter and the
debug line do not make a suppression distinguishable from a redelivery *inside
the filter*: both are an id already recorded, and nothing there can tell them
apart. What they buy is that the class is measurable at all, and a suppression
rate that does not match the redelivery rate is the thing worth looking at.

**The `MessageId` is on the log line and never on the counter.** It is
caller-chosen and unbounded, which is both the reason the finding exists and
the reason it cannot be a series. The two tags are a type name and a queue
name, closed sets fixed at registration (§9.8).

**§9.5 gained the trust dependency rather than a mitigation.** The key this
suppresses on is chosen by whoever published the message — §9.1 makes the
envelope id and the transport header one GUID — so the inbox is only as
trustworthy as the set of principals that may publish to the endpoint. #44's
per-service broker credential is a prerequisite for reading this mechanism as a
guarantee, not an improvement to it, and recording the publisher's identity
alongside the id needs that credential to exist first.

> **The test is over the instrument, not over the drop.** Every other test in
> that file passes with the counter deleted — they assert the consumer ran
> once and the row was written, which is true of a silent drop and of a
> counted one alike. A `MeterListener` filtered to this suite's own endpoint
> is what makes the signal load-bearing; the endpoint tag is doing real work
> there, because a listener is process-wide where an xUnit collection is not.

### Three spellings of a package the licence gate never read (#50)

The gate read `PackageVersion` elements in `Directory.Packages.props` and no
project file at all, so `GlobalPackageReference`, a `PackageReference` carrying
`Version` or `VersionOverride`, and `ManagePackageVersionsCentrally` set to
`false` each restored an uncleared package while the gate printed *"Every one
registered, licence-cleared"*. All three are ordinary MSBuild rather than
anything exotic, and none of them puts a `PackageVersion` element anywhere the
pin reader looks.

**The scan parses each file rather than grepping it**, because `Web.Bff.csproj`
already carries multi-line `PackageReference` elements with child elements — a
line pattern reads that shape as two unrelated lines and sees nothing. Element
names are matched namespace-insensitively for the same class of reason: an
`xmlns` on `<Project>` is an attribute that switches a tag-name match off in
silence.

**And it reads `.props` and `.targets`, not `.csproj` alone.** A
`PackageReference` carrying a `Version` is legal in `Directory.Build.props`,
where it reaches every project at once — so a scan that stopped at the projects
would have closed the narrow spelling of this defect and left the wider
spelling of the same thing standing. That is this repository's most-repeated
failure arriving *inside the check written to close it*, which is why the
suffix list is a constant with the argument beside it and a test whose subject
is what the glob found.

**The empty subject is a finding.** A glob matching nothing reports exactly
what a repository with no fault reports, and from inside the gate those two
results are the same.

**The licence-part rule was reversed, and it reverses a documented decision.**
`allowed-licences.txt` said a dual-licensed package passes if *either* half is
allowed, "because a dual licence is a choice the consumer makes" — true of a
disjunction, and the gate cannot tell a disjunctive `/` from a conjunctive one.
Under *any*, the register clears a forbidden licence by pairing it with an
allowed one. It now requires **every** part, and reports a part the spelling
map cannot name with a message of its own, because the repairs differ: one is a
decision about the allow-list and the other is a spelling nobody has named.
Verified against the register before taking it — every multi-part cell today
has all parts allowed, so the reversal breaks nothing and the argument is what
changed.

### A suite that renders text and never compiles it (#72)

`tools/new-service`'s suite renders a service and inspects the resulting text,
and the CI job that runs it installs no SDK. That is the property the job was
designed for — §15.1's argument for putting the licence gate ahead of the build
is the same one — so the fix is a **second job**, not a step added to the first.
Anything the scaffold *removes* is invisible to a text check: PR-14 wrote a
dispatcher test over `OutboxRows.Broker`, which leaves with the first contract,
and rendered a service that did not compile with every test green.

`Yankee` at 5199, never `Ordering`. The issue proposed `Ordering` on the
grounds that it is the real next service and sorts after `Common`; PR-18 made
Ordering real, so the create now refuses the taken name and port — and the
cleanup that follows the render deletes the service. A probe cannot quietly
become a service later.

**Build, do not run.** The failure class is compile-time, and running the
rendered tests needs SQL and RabbitMQ containers for very little extra signal.
The *test* project rather than the service, because it references the whole
rendered service transitively and the half a text check cannot see is in the
tests.

---

## The comments that were not mechanisms (#35, #36, #37, #38, #39, #41, #68)

Seven issues, one shape. Each names a control the platform already had, and in
every case the control was **argued in a comment and not enforced by
anything** — a redaction list whose exceptions were reasoned about in prose, a
readiness predicate whose fail-open was documented in the file that fails open,
an authorization posture that held because every endpoint group had
remembered a line. Most of them were not live exposures on the day they were
filed, and that is the property they share rather than a mitigation: a rule
nothing mechanises survives exactly as long as the next person reads the
comment. The exceptions are the two below that were one defect — and the
exception is what the rest of this entry is about, because the comment that
made them look safe is the same kind of comment as all the others.

**The one that was a live exposure had been argued as safe in writing.** §13.4's
redactor documented that it does not read log scopes, and justified it: the
platform opens exactly two, and neither can carry a secret, because
`UseCorrelationId`'s `CorrelationId` "is a trace ID or a GUID". It is not. That
is the *fallback* branch; a client-supplied `X-Correlation-Id` was adopted
verbatim, unbounded and unconstrained in charset, and pushed into the one
channel the processor was documented as not inspecting. The comment named the
channel correctly and was wrong about what travels down it — which is why #36
and #37 are one fix and were filed as two.

**A processor could not have closed it, and finding that out chose the layer.**
The obvious repair is to walk `record.ForEachScope(...)` and apply the same key
test — it is what the issue proposed first. Measured against OpenTelemetry 1.17
rather than assumed: `LogRecord` exposes `ForEachScope` and **no settable scope
provider**, so a `BaseProcessor<LogRecord>` can read a scope and has no way to
rewrite or suppress one. That repair would have let the type *notice* a secret
it could not remove. The fix sits one layer lower — `LoggerFactory` takes an
`IExternalScopeProvider` from the container and hands the same instance to every
provider, so a wrapper registered there redacts scopes on the way out and covers
the ones EF Core and MassTransit open as well as the platform's own two. **The
API surface decided the design, and reading it cost one probe.**

> **A test over the mechanism, not over the fix.** `RedactingScopeProviderTests`
> asserts that the logger factory actually hands providers the registered
> instance. Every other test in that file passes against a wrapper nothing ever
> calls — constructor selection is the assumption the whole fix rests on, and it
> is the one thing no other test would notice losing.

**The first implementation of that wrapper was silently wrong in the only case
that matters, and its own tests were green.** `Redact` matched
`IReadOnlyList<KeyValuePair<string, object?>>`, which is what MEL's
`FormattedLogValues` is — and `BeginScope(new Dictionary<,>)`, which is what
both of the platform's scopes actually use, produces a `Dictionary`, which is
not an `IReadOnlyList`. So every scope this platform opens fell through
unredacted while the unit tests over the list shape passed. It was caught
because one test drove the real call rather than the interface the
implementation had picked. **Match the shape the caller produces, not the one
the framework's own type happens to implement.**

**#35 is the other half, and the argument for a value check is that no test can
be written for a term nobody thought of.** The list grew from eight terms to the
vocabulary this codebase actually uses — `ConnectionString` most of all, which
no term in the old list was a substring of, and which four call sites throw
naming. A list is still only as good as the imagination behind it, so two
**value** shapes are recognised whatever the key is called: a connection string
carrying `Password=` inline, and a JWT. Deliberately not an entropy test — a
high-entropy string is an id as often as a credential, and redacting every id
empties the records §13.1 says an incident is triaged by. `{OriginalFormat}` is
exempt from the value check, because a template is written by an author rather
than bound from data, and it is the fallback the message rewrite depends on.

> **`pin` is not on the list, and the omission is the price of substring
> matching.** `Shipping` contains it. The list is pinned by a test naming the
> innocent keys as well as the guilty ones, because a vocabulary that only ever
> grows is one nobody has established is still selective.

**#41 was filed as an exposure and was a missing mechanism, which is a different
fix.** Every endpoint group in the solution already carried
`RequireAuthorization()`; what was absent was anything that would notice the
next one not carrying it. `SetFallbackPolicy` in `AddCommonWebDefaults` is the
answer rather than §11.4's proposed `EndpointDataSource` test, and the reason is
§4.5: a test lives in one service's project and reaches the services the
scaffold has already rendered, where a fallback in the building block reaches
every host that will ever compose it. Both are kept, and
[ADR-030](backend-architecture/adr/ADR-030-authorization-is-deny-by-default-in-the-building-block.md)
records why neither replaces the other.

**It reaches three endpoints nobody wrote, and each had to be decided rather
than discovered.** Routing's 405 short-circuit endpoint carries no authorization
metadata, so an anonymous wrong-method request is now challenged before the
method is considered — and an authenticated one still gets 405, which is what
makes the pair assertable. `MapOpenApi()` carries none either, so the document
enumerating every route and schema now requires a caller. That one cost a
second factory in two test projects: a 401 assertion alone is satisfied by a
host that has stopped serving the document at all, so the suite needed a caller
who gets through. **A negative assertion about an endpoint is also an assertion
that the endpoint exists, and only the positive half carries it.**

**The third is the unmatched route, and it is the widest of them.** A path that
matched nothing is evaluated against the fallback too, so an anonymous request
for a URL this platform does not serve answers 401 where it answered 404 — every
path, rather than one endpoint. Found by review rather than by design, then
measured and accepted: `HostSmokeTests` pins both halves, an authenticated
caller still gets the 404, and a 404 is exactly the disclosure §11.4's ownership
rule already refuses one resource at a time. **The count in the paragraph above
read two for as long as nobody asked what else carries no metadata**, which is
this entry's own subject arriving in the entry — a rule reversed everywhere it
is stated or nowhere, and an endpoint inventory is where the "everywhere" is
easiest to stop one short.

**#39 arrived asking for three things and two of them were already decided.**
Rate limiting and the request-body ceiling are the gateway's, explicitly, in
§10.1 and §4.2 — the issue was filed before PR-17 and PR-27 landed them, and
re-deciding a recorded decision because an issue restates the question is how a
platform acquires two answers. The third ask had no owner at all: the blueprint
discussed response security headers as neither done, nor deferred, nor out of
scope. `nosniff` is now the service's, in a building block every host composes,
and
[ADR-031](backend-architecture/adr/ADR-031-the-service-owns-nosniff-the-ingress-owns-hsts.md)
argues the omissions — HSTS belongs to the only component that terminates TLS,
and framing and script policies govern a document this platform does not serve.

> **Where a response header is written from is the whole of its correctness.**
> `UseExceptionHandler` clears the response before writing §10.5's problem body,
> so a header assigned on the way in is absent from exactly the 500 on which a
> caller-supplied value is most likely to be reflected. The middleware registers
> an `OnStarting` callback instead. The 200 path passes either way, which is why
> the test that matters drives the 500.

**#68's fix is a parameter, and the parameter is the point.** An empty predicate
set is a passing predicate set, so a host with no `ready`-tagged check answered
`/health/ready` with 200 without having verified anything — and §15.1 removes
the smoke
stage *by name* on the grounds that this probe already gates the rollout. The
gateway and the BFF genuinely own no *readiness* dependency — the BFF calls
Catalog and the gateway proxies four services, and neither hop gates
readiness — so the guard cannot simply
throw: `ownsNoReadinessDependencies: true` makes those two hosts state their
case at the call site, and every other host fails to start. §13.5 already
carried the rule in prose — a host with a connection string has a readiness
check — and this is that sentence given a compiler.

**The guard tests the set and not any member of it, which is the honest bound
and was found by review rather than stated up front.** `AnyReadinessCheck` asks
whether *one* registration carries the tag, so Catalog and Ordering, which
register three each, keep starting after any two are deleted. What it catches is
the readiness set going missing whole: a host wired up with none, or one whose
Infrastructure registration stopped being called. The narrower case needs a
per-service count, and nothing generates one for a service §4.5's scaffold has
rendered — the same reason the guard travels with the building block rather than
living in a test. **An `Any` over a set proves the set is non-empty and says
nothing about which member is in it**, and the sentence that shipped first — *a
service that loses its `AddSqlServer(...)` therefore fails at startup* —
promised the second while the code implemented the first. It is the
gate-coverage lesson at one remove: not a gate that stopped covering a surface,
but one whose subject was narrower than the sentence selling it.

**#38 is the small one and the only one outside `Common.Web`.** `ThumbnailUrl`
was bounded for length and for nothing else, in a validator where every other
field is argued down to the regex anchor. It is now an absolute `http` or
`https` URI. Not an allow-list of image hosts, which is the stronger form: §14.1
has no image host and §4.1 plans no media service, so the list would be empty or
invented.

**What this cost elsewhere.** The gateway's `catalog-public` route gained
`"AuthorizationPolicy": "anonymous"`, which reverses a rule §10.2 stated three
times — that naming no policy was the only correct way to declare a route
public. That reversal is reconciled in the chapter, in Appendix C's rows, in
the compose README, in the k6 script, in the source comments that named the
route, in the test that pinned it, and in this log's own PR-16 entry, because
the claim had been copied to all of them. **A rule is reversed everywhere it is
stated or nowhere** — and this sentence carried a count of four, then seven,
each time corrected by somebody counting again. The number is gone rather than
corrected a third time: the places are checkable and the tally never was, which
is the argument `docs/style-guide.md` makes about the blueprint's callout
totals.

---

## The cancellation the saga could not see (#123, #143, #141, #109)

Three of these are one defect at three distances. A cancellation reaches
§9.6's saga on `OrderCancelled`, and everything the machine does about one
starts there — so every interval in which that event has *not* arrived is an
interval in which the saga acts as though nothing happened. #123 is the
interval before the instance exists, #143 the interval while it exists and is
holding a different event, and #141 the question of who releases the stock
while both are running.

**`Reason` is not the discriminator, and the branch that nearly shipped is
worth recording before the one that did.** #123's own issue offered it as the
cheapest option: the saga's echoes carry `out_of_stock`, `stock_timeout`,
`payment_declined` and `payment_timeout`, so `customer_request` is the
customer's. It is false, the chapter already said so, and the argument had to
be re-derived anyway because the issue outranked it in the reading order.
§11.4's endpoint parses the whole five-code `CancellationReasons` map, so a
caller may cancel with `payment_declined`; and the saga forwards whatever
reason it recorded, so its own compensation carries `customer_request`
whenever a customer's cancellation is what started it. **A field that a caller
chooses cannot answer a question about provenance**, and the test that pins
this pairs `out_of_stock` with a user origin precisely because a
`Reason`-based branch passes every test that only ever pairs
`customer_request` with a customer.

**What closed it was the route the chapter named first — an added
discriminator — and the enum for it already existed one layer out.**
`CommandOrigin` has distinguished §11.4's endpoint from the saga's broker
command since #63, for the ownership check; it simply never travelled onto the
event. So the chain is `CommandOrigin` → `CancellationOrigin` (domain) →
`OrderCancelled.Origin` (a `CancelOrigins` code), written as a literal at the
handler and never bound from a request, which is what keeps it from being a
value a caller can claim. §9.2 makes a new **optional** field additive, so
there is no V2.

**The absent case is the half that had to be got right, and both readings are
defensible until you price them.** A rolling deploy has instances publishing
before they populate the field. Faulting on absent closes #123 immediately and
files an error-queue entry for every ordinary cancellation for the length of
every deploy — a certainty. Discarding on absent holds the pre-#123 behaviour
across the deploy and leaves the race open for that window — a possibility.
**A guaranteed incident is worse than a bounded exposure**, so absent
discards rather than being read as an origin. A test pins it so it cannot be
tidied away as an oversight.

**And the exposure is not bounded by the deploy, which this entry said and
the review corrected.** It called the tolerance §15.5's expand phase with a
contract phase owed — a later release making `Origin` required. That
tightening is a breaking change inside V1: a payload predating the field has
no bound on when it can arrive, because `error-queue.md` keeps a message
until somebody handles it and a replay can reintroduce one at any time, so
requiring the member would fail deserialisation before the branch above
could read the absent value. §9.2 sends that to a V2. **An additive member
stays optional for the life of its contract version**, and the promised
phase was a scheduled breakage in the clothes of rigour — which is why it
survived several review rounds: it was the tidier claim.

**Faulting buys a second thing nobody asked for, and it is not noise.** A
cancellation arriving after the saga finalised down a `FlagOrderForReview`
branch also faults. The order is already in front of a person, and "the
customer then cancelled it" is the next thing that person needs. The exception
raised is the `SagaException` `Fault()` would have raised — deliberately, so
`error-queue.md` works both arrivals through one procedure rather than two.

**#143 is ADR-025's rule applied to a fact rather than to a join.** That ADR
says what is outstanding is recorded on the instance and a state name can
carry one such answer, not the rest. What is recorded here is not an
obligation but a piece of knowledge: a `StockReleased` arriving in a state
that sent no `ReleaseStock` **proves** a cancellation reached Inventory, and
the four states that absorb one now set `CancellationObserved` instead of
discarding it, and every forward transition in those states asks — named
rather than counted, because the first draft of this sentence said four and
`AwaitingConfirmation`'s `OrderConfirmed` was the fifth.

**The money row is the one that justifies the column.** `AwaitingStock` +
`StockReserved` sent an `AuthorisePayment` for an order already being
cancelled; it now withholds it and waits where its own `OrderCancelled` branch
can still be reached. **The deliberate absence there is the `Unschedule`**:
`StockTimeout` stays armed, because an instance that withholds a forward step
and never gets its cancellation needs a bound, and this state already has one.
The same shape governs `AwaitingPayment`, where `PaymentTimeout` is left armed
on the observed branch for the same reason.

**The two terminal rows lose the instance rather than the money, and that is
the sharper failure.** `ShipmentDispatched` in `AwaitingConfirmation` and in
`Confirmed` finalises, so a cancellation in flight then correlates to nothing:
no review row, and — before #123 — no fault either. Both branches now raise
`cancelled_after_confirmation` before finalising. **`MarkOrderShipped` still
goes on both, and the aggregate refuses it.** The flag is set only by a
`StockReleased` Inventory published off an `OrderCancelled` staged in the
transaction that set the order `Cancelled` (ADR-029), so on this path the
order is already cancelled and `MarkOrderShippedHandler` answers
`order.not_shippable`. It is sent anyway because §5.4 gives the aggregate the
transition and a state machine does not get to predict its answer from a
flag — an inference one ordering premise away from leaving a shipment
unrecorded with nothing saying so.

**The justification this paragraph carried was that withholding would leave
the aggregate claiming `Confirmed`, and it is false on the one path the flag
names.** It survived the review round that named it, in this file and in the
test recording the same send, because the fix was applied to the two sites
the finding cited rather than to a grep for the sentence — which is this
repository's own rule about resuming from the list and not from the error,
met in the round after the one that states it.

**#141 could not be decided on its own terms, and finding that out is the
entry's most reusable half.** It asked whether Inventory should decline to
release for an order it knows reached `Confirmed`, and ranked three sketches
by cost — calling "`ReleaseStock` becomes the only trigger" the cleanest and
largest. Answering #143 deletes that option: the second producer is the *only*
evidence a cancellation gives the saga, so removing it reopens every race
#143's guards close. It is also a safety net, since with the saga as sole
author the stock comes back only if an instance exists to send the command,
and §9.6 finalises down several branches before any despatch. ADR-029 records
keeping it. **Two open questions were being weighed independently and one
decided the other** — the same shape #125 met, and the second time is what
makes it a rule: before ranking options by cost, ask whether each survives its
neighbours being settled.

**What the restraint was always for.** `Confirmed` sends no `ReleaseStock`
because a reservation being picked is not one Inventory can be told to drop on
a state machine's word. That argument is about the *command* and survives
intact; it was never an argument about the reservation surviving, and the
picked-parcel gap stays open as Inventory's to close.

**#109 rode along because it shares a file and a test with the rest.**
`Order.Cancel` refuses `Shipped` **or** `Delivered` and its own message
interpolates the real status; `CancelOrderHandler` catches the
`DomainException` and discards that text for `OrderErrors.AlreadyShipped`,
which named only the first. So the accurate sentence was thrown and the
inaccurate one served. The description broadened and **the code did not** —
`order.already_shipped` is a dimension value on §9.8's dashboard, and a second
code would silently halve every series built on it. The producer had no test
at all: every occurrence under `tests/` was a sample string, so §10.5's 422
was unproven. `Delivered` is arranged directly in that test, because
`OrderStatus` declares it and no transition reaches it — the guard is written
for a status the aggregate cannot get to on its own, which is what makes
arranging it the only way to hold the guard to anything.

**#128 was in scope and is not in this PR, for a reason the log already
carried.** The saga's in-memory outbox is a dual write, and the fix —
`AddEntityFrameworkOutbox` with `UseBusOutbox` — means running MassTransit's
transactional outbox alongside §9.4's hand-rolled one. §9.3 forbids a second
outbox table set in as many words. That is a §9 decision about owning two
outboxes, it wants an ADR, and PR-21's entry said so before this branch
existed. Nothing here makes it less true.

**It has since been taken, and the API named twice above is the wrong one** —
reconciled here rather than rewritten, because what the entry got right is the
part worth keeping: it wanted an ADR, and
[ADR-032](backend-architecture/adr/ADR-032-the-sagas-outbox-is-masstransits-in-the-sagas-own-transaction.md)
is it. `UseBusOutbox()` is a bus-side option and does not touch a receive
endpoint at all; what closes #128 is `UseEntityFrameworkOutbox<T>(context)` on
the saga's endpoint, with `AddEntityFrameworkOutbox<T>` behind it. The
misreading survived three sites in two entries because nobody had run it.

**Every guard was observed red before it was trusted.** Six of the seven new
saga tests were run against a machine patched back to its previous behaviour
— the missing-instance branch returning unconditionally and every
`CancellationObserved` guard forced to its unguarded side — and all six
failed. **The seventh is deliberately not in that set**, and which one it is
says what the branch changed: the test that an `OrderCancelled` published
before `Origin` existed is still discarded passes against both machines,
because holding that behaviour unchanged across a rolling deploy is the
whole of what it asserts. The first attempt at
that measurement was **invalid and looked fine**: the patch script normalised
the file's line endings, the build failed IDE0055, and the run executed a
stale assembly and reported six passes. A counterfactual that does not rebuild
is a claim about a run nobody performed, wearing the evidence of one.

---

## The subject that crossed a boundary nothing checked (#63)

§11.4's subject rule — *a subject identifier is bound from the principal, never
from the request* — carried an exclusion for the message path, and the
exclusion was honest: a command arriving over the broker has no principal, so
there is nothing for `ICurrentUser` to answer with. What the chapter said next
is what this PR came back for. It said the question of what the message side
*should* do was open, and left `AuthorisePayment` naming the customer whose
instrument Payments would charge.

**An open question in a specification is a decision taken by default.** For as
long as the callout stood, the default was "carry the subject as a field", and
it was reachable from a request body: #54 closed the HTTP end of that chain and
#43 closed the ownership check behind it, but the value they made trustworthy
was re-emitted onto `OrderPlaced`, copied to the saga instance, and sent on to
Payments as an ordinary message field with nothing left that could check it.
Two of the chain's four links were closed and the path was still open, exactly
as #63 predicted at filing.

**The fix is a word, and finding the word was most of the work.** The message
path cannot *bind* — there is no principal — so the rule there is
**re-derive**: the service that owns the decision resolves the subject from its
own record, built from an event whose subject was bound from a principal.
`OrderPlaced` already carries such a value. Payments consumes it, keeps its own
record of who an order belongs to, and looks the payer up when the command
arrives. That is ADR-028, and §3.2's Payments row gained `OrderPlaced` to make
the precondition a contract rather than an implementation note.

**The precedent is one service over, and picking the right one took a review
round.** The first draft cited ADR-027 everywhere — Ordering resolving product
*names* from a projection it owns — and then §3.2 described that ADR as being
about *prices*, which it is not: ADR-027 is careful that
`ordering.ProductPrices` has never carried a name, and the two tables are
distinct on purpose. One precedent, three sites, two different readings.

The closer analogue is the **price** projection and not the name one, which is
what made the slip easy to miss: §6.4's `PlaceOrder` reads
`ordering.ProductPrices` behind an `IProductPriceReader` documented as *never a
remote call*, so a handler needing another service's fact **on the deciding
path** looks it up locally. That is Payments' shape exactly — write path, at
the moment of decision. ADR-027 is the same mechanism on the read path, for
names. All three sites now say so, and say which is which. Same mechanism,
different purchase — a synchronous hop avoided there, an unverifiable assertion
removed here.

**Why the contract narrowed instead of emptying, which is the question a
reviewer asks first.** `Amount` and `Currency` stayed. The line between them
and the subject is **instruction versus authority**: the amount and the
currency are what to do, the sender decides them, and Payments may refuse a
mismatch against its record as a consistency check between two parties who both
have a view. The subject is on whose behalf, and that is the deciding service's
to derive. A money-movement command carries its instruction and never its
authority — the reusable half of this PR, and what decides the next such
contract without re-running the argument.

**That is the second formulation, and the first was falsified by this change's
own design.** It said the line was *whether the receiver can disagree* — a
field the receiver can check is a claim, one it cannot check is an assertion —
and Copilot pointed out that Payments' record holds the payer as well as the
total, so a supplied `CustomerId` would be exactly as checkable as the amount.
Checkability separates none of the three. **The rule was refuted by the
paragraph two above it**, which is where the record's contents are specified.

The replacement is stronger rather than a retreat. A transported authority is a
second source for a decision that must have exactly one; the check that would
catch a mismatched subject is a check somebody has to remember to perform; and
a redundant authority-bearing field is precisely the one a later call site
reads *instead of* deriving — cheaper, identical in the happy case, wrong
exactly when it matters. Removing the field removes the possibility rather than
guarding against it.

**Worth recording because the falsified version was the sentence this PR was
proudest of**, restated in six places and offered as guidance for future
contracts. A rule that generalises is the most expensive kind to get wrong, and
the thing that caught it was reading the rule against the design in the same
document rather than against the case that motivated it.

**The reviewer's best finding was a rule the branch broke without noticing:
§9.2 requires a version bump to remove a field.** `AuthorisePayment` lost
`CustomerId` from `Payments.V1` in place, and §9.2's standing remedy is a `V2`
with both published for a deprecation window. The branch had an argument for
the exception — Payments does not exist, so `V1` has no consumer — and had not
written it anywhere, which is exactly the one-rule failure: a reader greps §9.2,
finds "removing a field requires a new version", and finds this change doing
otherwise with nothing to say why.

**Writing it down turned up the sharper half, which the original argument had
missed.** The exception is not merely that the version bump is *unnecessary*
here; for this class of change it is *wrong*. A `V2` alongside `V1` keeps the
version carrying the subject published and consumable for the whole window —
so the standard remedy would re-arm the defect the change exists to remove.
"Unnecessary" and "counterproductive" are different claims, and only the second
justifies putting an exception into a chapter rather than a footnote in a PR.

So §9.2 gained the exception as a **rule** with two required conditions — no
consumer in `Platform.slnx`, and an ADR recording it so "there was no consumer"
is checkable later — plus the note that it expires the moment a consumer
exists. That is the same window §9.1 already describes from the other side:
`ShippingAddressV1` was four fields short for as long as nothing populated it,
and the PR that becomes a contract's first producer is the last one that can
fix its shape for free.

**The saga instance lost its `CustomerId` too, and that is the structural half
rather than tidying.** The field had exactly one reader — the send this PR
removed. Left in place, all it could still do is offer itself to the next
transition that wants a customer, which is how the subject finds its way back
onto a message a release later. Removing it means no command the machine sends
carries a subject. **Not "every command names an order and nothing else"**,
which is how this was first written and is false of three of them —
`ReserveStock` carries its lines, `AuthorisePayment` its amount and currency,
`CancelOrder` its reason. Grok caught it, and the correction is worth keeping
because the wrong version reads as the stronger claim: what these commands have
in common is an absence, not a shape, and a rule stated as a shape invites the
next reader to defend a uniformity that was never true. Ordering is not short
of the value either way: `ordering.Orders` owns it, bound at the endpoint.

**Removing the property is one release; removing the column is two, and the
first draft got that wrong.** `dotnet ef migrations add` scaffolded a
`DROP COLUMN` and warned about data loss, which is the visible half. The
invisible half is §7.4 and §15.5: migrations run ahead of the deploy, the
previous release keeps serving beside the new one, and *that* release's saga
writes this column on every `OrderPlaced`. A drop would have failed those
inserts for the length of the ladder and left a rollback with no column at all.
So the column is mapped as a **shadow property** — unreachable from the
instance, so the control still holds — with a database default, and the drop is
owed to a release where nothing writes it.

**The default's shape is decided by the old build reading the new build's
rows.** Rolling forward, the new build's `INSERT` omits the column and SQL
Server supplies the default; a nullable column would serve equally well. The
old build materialises a non-nullable `Guid` from rows the new build wrote —
and a nullable column throws there rather than reading empty. `NOT NULL` with a
default is the one shape that survives both. The value is `Guid.Empty` on
`AddSagaPaymentVerdictJoin`'s terms one release back: the conservative choice
rather than merely a legal one, because it is **nobody**, where any other
default would name a real subject that was never that order's.

**Copilot found that "the old build" was written throughout as a rollback, and
it is mostly not one.** §15.5's canary runs both releases at once over the same
queues, so the ordinary ladder produces the case: a new pod creates the
instance with the column defaulted, an old pod takes the next event for that
correlation, reads `Guid.Empty`, and sends its four-field `AuthorisePayment`
naming nobody. Reachable on every deploy rather than only on the way back from
one — and every site here had framed it as the rare direction.

**The finding did not change the design, and saying why is the point.** Two
releases are enough *here* for the same reason the in-place contract change is
allowed at all — nothing consumes the command, so the legacy message reaches no
decision, and an empty payer is a charge that fails visibly rather than one
aimed at the wrong customer. What it changed is the guidance: a platform whose
Payments is live needs **three** releases — stop sending the field, drop the
property, drop the column — which is §7.4's own sequence with its *stop writing
the old one* step performed rather than skipped. The blueprint is a
specification somebody else will follow with Payments running, so a sequence
that is safe only under a condition it never states is a defect in the
specification even when the code is right.

**The gate is one assertion and the controls it cannot do without.**
`No_command_contract_carries_a_subject` asserts no command contract declares a
member spelled like a subject, and an empty offender set is exactly what a
broken detector produces — so one control points the same detector at
`OrderPlaced` and requires it to find the `CustomerId` ADR-028 *keeps*, one
names the command roots the judged set must contain and the exemptions it must
not, since a filter that selects nothing makes the rule vacuous while leaving
it green, and one pairs the spelling vocabulary against the probe that
exercises it, so a spelling added to the detector and to nothing else fails the
build. The cases themselves are generated from that vocabulary rather than
listed beside it, for the reason argued further down: a case list is a second
copy, and a second copy is what went stale here twice.
**No count of them is written here on purpose.** Successive review rounds each
added one, so any figure stated at any point in that would have been wrong by
the next round — which is the argument `CLAUDE.md` makes about its own line
count and the blueprint makes about its callout totals, arriving inside a
single branch rather than across several.

**Defining "command" took four attempts, and each fix opened the next fault.**
The first spelling read every contract. The second narrowed to those not
implementing `IIntegrationEvent`, citing §9.1 — which states that implication
one way only. Copilot pointed out the converse admits the line types events
carry, and an event is *permitted* a subject: had `OrderPlaced` factored its
`CustomerId` into `PlacedLine`, the gate would have failed the build on a shape
this very ADR requires. **A gate that refuses what the rule allows is a false
failure, and the "fails wide is safe" argument written beside it was true of
coverage and false of correctness.**

The third subtracted everything reachable from an event, and Copilot found the
hole that opened one round later: a payload carried by **both** a command and an
event became exempt *because an event reached it*, so a subject inside it would
travel on the command unjudged. **The fix for a false positive had created a
false negative on the exact path the rule exists to close** — and the second
fault is the worse one, because a refused build gets looked at and a silent pass
does not.

The fourth builds the judged set **up from the command roots** — the commands
plus everything they carry — which settles both: a shared payload is judged
because a command reaches it, and a purely-event payload is not because none
does. The consequence is worth stating rather than leaving implicit: a type
shared between a command and an event may not carry a subject at all, since the
command side forbids what the event side permits.

**Three directions measured, not argued**: a `CustomerId` in `PlacedLine`
passes; the same field in `StockLine` fails naming the member; and the same
field in a `PlacedLine` that a command also carries fails. The first would have
failed under attempt two, and the third would have passed under attempt three.

**The reusable half is about the shape of the correction rather than this
gate.** Two of the four attempts were fixes that introduced the opposite
defect, and neither was caught by the tests that existed at the time — the
suite was green after attempt two and green after attempt three. What found
them both was someone asking what the *set* contained, not what the assertion
returned.

**Then a fifth round found the same hole through the type system rather than
through the definition.** `ElementType` unwrapped a generic only when it had
exactly one argument — true of every collection this platform uses, which is
what made the gap read as completeness — so a member typed
`IReadOnlyDictionary<string, SomePayload>` would have left `SomePayload`
outside the closure and its subject unjudged. The traversal now visits every
generic argument.

**And the round's better finding was that none of it was pinned.** All three
directions had been *measured* during development and reverted, so nothing in
the committed suite would notice a return to attempt three: the live contracts
have no payload shared between a command and an event, so every assertion over
them stays green under the rejected implementation. Measured once and reverted
is pinned by nothing — the same failure as a gate observed only in the green
direction, one level up.

What closes it is four synthetic contracts in the test assembly — a command
reaching a shared payload through a two-argument generic, an event carrying
that payload and one of its own — driven through the same algorithm as a type
universe of their own. The closure had to become a function of a universe
rather than a fixed field to allow that, which is the shape worth carrying:
**an algorithm that can only be run against production data can only be tested
with the cases production happens to contain.** Both counterfactuals were then
re-measured against the committed tests: reverting to attempt three fails the
shared-payload case with the other thirteen green, and restoring the
single-argument unwrap fails both new cases.

**The coverage control named three command roots of seven**, which is the
repository's most-repeated failure reproduced inside the control written to
prevent it: discovery could have dropped `ReserveStock`, `ReleaseStock`,
`MarkOrderShipped` and `FlagOrderForReview` while the gate stayed green. It
names all seven now, read across §3.2's Accepts columns.

**The spelling list is incomplete by construction and says so.** It matches
`Customer`, `Buyer`, `Payer`, `Subject`, `User` and `Principal` as substrings,
so a subject added under a name nobody predicted gets past. That is stated at
the site rather than papered over — the control keeps the gate from being
*uninformative*, which is a different property from keeping it complete, and
conflating the two is how this repository's gates have failed before.

**Stating it was not the same as handling it, and review said so on the last
round.** A documented hole is still a hole: ADR-028 read *the rule is enforced
rather than reviewed* and §12 read *each is mechanical*, while `OwnerId` walked
through — measured, not argued, and the sharp part of the measurement is that
`No_command_contract_carries_a_subject` stayed **green** for it. This
repository had already paid for exactly this shape once and written the fix
down: *a deny-list of terminal states passes every state nobody listed …
enumerate what is acceptable.* The subject test is a deny-list of six
substrings.

So the gate gained its allow-list half: every member the judged commands may
carry is enumerated, and a name absent from that list fails the build. **It
decides nothing** — it cannot tell whether `OwnerId` is a subject — and that
is the honest claim: it converts a member added silently into a member added over
a red build, which is the scaffold's rule that a tool refusing input it has
never been shown beats one that guesses. A second test refuses a **stale**
entry, because an approved name no command carries is a seat reserved for
whatever arrives under it next — the deny-list hole reintroduced inside the
allow-list that replaced one. Both observed red: `OwnerId` on
`AuthorisePayment` fails only the new test, and a `LegacyPayerId` entry nothing
carries fails only the pairing.

**The first version of that allow-list approved a *name*, and an approval that
is not scoped is an approval that leaks.** `PaymentReference` is approved for
`ConfirmOrder`; a flat list of names therefore permitted it on
`AuthorisePayment` too, and a new command assembled entirely out of names
already in use would have passed with nobody adding a line — the forced review
never happening, which was the only thing the gate was for. Approvals are
`(contract, member)` pairs now, checked in both directions. Measured: adding
`PaymentReference` to `AuthorisePayment` passes the flat version and fails the
scoped one.

**And the root discovery still failed open, one level above the hole it was
built to close.** `RootsOf` calls a non-event a root when nothing else in the
universe carries it — so an **event** declaring a property of a command's type
removes that command from the roots, and nothing then reaches it, because only
events do. A subject on a command dispatched to its own queue would travel
unjudged. That is the shared-payload false negative exactly, moved up a level,
in the fix for the shared-payload false negative.

Inference cannot settle it: making a type carried only by events a root is
attempt two, which fails `PlacedLine`, and §9.1 gives no positive marker for a
command. **So the roots are declared and inference audits the declaration**, in
both directions — a command added to the contracts and not declared shows up as
an inferred root nobody listed, and a command inference *loses* shows up as a
declared root inference cannot see. The judged set is built from the
declaration, so the gate does not lose a command while the pairing tells
somebody it happened. This is the repository's own answer to a list that
drifts: declare it once, and assert the other copy matches.

The probe universe now holds an event carrying a command, because nothing in
the live contracts has that shape and the hole could otherwise only be argued.
**Its control asserts the failure**, not the fix — inference must still lose
that command — on the awaiting-signal gate's discipline: a list of things known
to be missing needs something asserting they are still missing, or the day the
premise changes passes unnoticed.

**That pairing still had one blind spot, and it is the intersection of the two
mistakes it was built for.** Each half catches one: a command inference loses,
and a command nobody declared. A contract that is *both* — carried only by an
event **and** absent from the declared roots — drops out of the inferred set
and the declared set alike, so the equality holds and nothing inspects the
type. Measured, and the measurement is the whole argument: a `RefundOrder`
record carrying a `CustomerId`, referenced from `OrderPlaced` and declared
nowhere, left **both** `Inferred_command_roots_and_the_declared_list_agree` and
`No_command_contract_carries_a_subject` green.

**No structural test can close it, and saying why is the point.** Whether a
type is dispatched as a command is not a fact the type system holds: §9.1
defines a command by what it does *not* implement, and a positive `ICommand`
marker — the obvious fix — only moves the forgetting to the marker, since a
new command that omits it is lost exactly as before. What *is* decidable is
whether a person has classified the type at all. So every non-event contract must
appear in the declared roots or in a declared payload list, and one in neither
fails the build. It does not say what the new type is; it refuses to let it
arrive unlooked-at, which is the scaffold's rule an assembly over and the same
move the member allow-list makes one level down. A stale entry is refused in
the same test, for the reason every list here refuses one: a classification for
a type that has gone is a seat reserved for whatever takes the name next.

**Four rounds in a row found a fail-open in the previous round's fix**, each a
level up from the last: the spelling cases, the member names, the roots, the
classification. That is not a run of bad luck. **A gate built by narrowing what
it inspects has a boundary, and the boundary is where the next hole is** — so
the question to ask of one is not "does it catch the case I have" but "what
does it decline to look at", and the answer is a place rather than a case.

**And for five of those six spellings the control was uninformative too.** The
positive control pointed the detector at `OrderPlaced.CustomerId` and nothing
else, so misspelling or deleting `Buyer`, `Payer`, `Subject`, `User` or
`Principal` left every assertion green. **The coverage failure this repository
keeps rediscovering, inside the control written to prevent it** — the same
shape as naming three command roots of seven, one round earlier and one level
down. A probe now declares a member per spelling and a second test pairs list
and probe by size. Measured: misspelling one entry fails exactly that case and
leaves the rest of the file green.

**That fix then had the same defect, and the sentence describing it was the
tell.** It was written as *parameterised over the vocabulary itself rather than
a copy of it* while the cases were an `InlineData` row per spelling — a copy,
with `SubjectSpellings.ShouldContain(spelling)` beside it and a comment
claiming the pairing failed in either direction. It failed in one. A spelling
added to the list **and** to the probe, but not to the rows, satisfied that
assertion vacuously, matched the size check exactly, and generated no case: the
same entry unobserved, one layer above where it had just been fixed. The cases
are generated from the list now, so there is no second copy to forget — the
argument §12.5's publish barrier wins over per-test discipline, one suite along.

**Counterfactual, because nothing in the file can distinguish these.** Add a
seventh spelling and its probe member and count the cases: the old shape ran
six and passed, the new one runs seven. Remove the probe member and keep the
spelling and the generated case fails by name alongside the size check —
so the case is exercised rather than merely counted.

**Three of this branch's controls have now had the defect they exist to
catch**, one of them twice, which is worth stating as a rule rather than as a
coincidence: *a control is code, and the reason it exists applies to it.*
Asking "what would make this gate green while the property is false" is a
question to ask of the control as well as of the rule — and the answer is never
in the suite, because a control that covers less than it claims is green by
construction. **Every instance here was found by review and none by a test**,
which is the part that generalises: the check that settles it is a
counterfactual somebody has to run.

**What this does not close is #44, and the residual is written into three
files rather than left to a reader.** One shared RabbitMQ principal still
writes every queue, so anyone reaching the bus can still send an
`AuthorisePayment`. What that command **alone** no longer does is carry the
payer: a forged one naming a real order re-triggers that order's own
authorisation instead of redirecting one at a customer of the sender's
choosing.

**The residual said more than that for two review rounds, and Copilot caught
it saying two things that cannot both be true.** The paragraph claimed broker
access could no longer choose who is charged, and three sentences later
conceded that a forged `OrderPlaced` seeds Payments' record. Both cannot hold:
forge the event, then send the command, and the payer is chosen in two messages
where one used to do it. **Payer selection is narrowed, not removed.**

What the narrowing actually buys is cost and visibility. The added message is
an **event other services consume** — Ordering's own saga starts on
`OrderPlaced`, and Notifications tells the customer — so a forged one runs a
fulfilment saga for an order the write model has no row for and emails somebody
about an order they never placed. The single forged command left none of that
behind. Capability unchanged; evidence very much not.

**This is the second residual overclaim on this branch, and the pattern is the
lesson.** The first named §8.5 as absorbing a duplicate it cannot reach; this
one described a narrowing as a closure. Both came from writing the residual
immediately after the fix, while the fix is the salient thing — and both were
falsifiable from a paragraph already in the same file. **The check is to ask
whether the same attacker, with the same access, reaches the same outcome by a
route the document itself already describes.** Twice here the answer was yes.

**The first draft of that residual named §8.5 as absorbing the duplicate, and
§8.5 cannot reach it.** `IdempotencyBehavior` is an Application-pipeline
behaviour constrained to `IIdempotentCommand` and keyed on a `CommandId`;
`AuthorisePayment` implements neither and, being a `Common.Contracts` message,
never enters that pipeline. §9.5's inbox is the broker-side control and keys on
`(MessageId, Endpoint)`, which a forger chooses freshly — it suppresses an
accidental redelivery, not a deliberate second send. **A residual that names a
control which does not cover it is worse than one that names none**, because
the citation is what stops the next reader checking. What survives is the
smaller claim — a forged command alone re-triggers rather than redirects —
plus an owed rule for the service that does not exist yet: Payments must make
authorisation idempotent per order against its own `PaymentIntent`. This is the
repository's own lesson arriving from the other side: *a registered name is not
a live signal*, one layer up, where the name was a whole mechanism.

**One consequence belongs to a service nobody has written, which is when it is
cheapest to write down.** §9.4 orders nothing between two deliveries, so an
`AuthorisePayment` can overtake the `OrderPlaced` it resolves against — the
race §3.2 already records for `ReleaseStock` and `ReserveStock`. **A missing
record is a wait, not a decline.** Payments must not publish `PaymentDeclined`,
which is a business verdict about a payer it has not identified.

**Saying that and then naming the ordinary retry envelope as the wait is the
third overclaim of this shape on this branch.** The first draft said to fault
the command and let retries carry it until §9.6's fifteen-minute timeout.
§9.8's command policy is five exponential in-memory attempts capped at a
minute, so the message reaches the paged error queue about fourteen minutes
early: a race this very entry calls routine becomes an operational fault, and
the timeout that was supposed to bound the wait never runs. **A wait needs a
mechanism that lasts as long as the wait**, and the endpoint takes delayed
redelivery — ADR-021's delayed exchange is already on this broker — with a
window reaching that timeout.

**All three overclaims share a shape worth naming.** §8.5's idempotency, the
broker residual, and now the retry envelope: each cited an existing mechanism
by name as covering a case, and in each the mechanism's actual parameters —
which interface it constrains, which key it uses, how many attempts over how
long — put the case outside it. **Citing a mechanism is not checking its
bounds**, and a named control reads as a checked one to everybody downstream.

**The expand/contract guarantee was an argument until Copilot asked for a
measurement.** Every site claimed the new build's `INSERT` omits the retained
column and the database default supplies `Guid.Empty`; nothing tested it. The
smoke test checks the migration was *applied*, which is a different claim, and
the saga endpoint test read only the row count and `CurrentState`. Two
integration tests now close it — one asserting a row this build writes stores
`Guid.Empty`, one asserting the default constraint exists — and they are
deliberately separate, because a row could read empty from something having
written that value, where the constraint makes it a property of the schema.
The value is what the whole mixed-version argument rests on: empty names
nobody, and any other default names a customer who never placed the order.

**The failing test was in the half that needs a daemon, which is the argument
for running both halves before believing a green one.** Three new contract
tests passed in the fast suite and `DatabaseSmokeTests` — which counts applied
migrations against a named list — failed on the eleventh, in the integration
half. The list is the assertion and the length is derived from it, so the fix
was one row; #126 had already removed the literal that would have made it two.

**Counts pinned to this branch**: the solution runs 915 tests, 725 of them
outside `Category=Integration`, and the three CI stages are 18, 707 and 190.
Reconciled against a full local `dotnet test Platform.slnx`, and an earlier
head of this branch was reconciled against **its own CI run**, whose log summed
to 899 over twenty-four per-project stage totals — the check this file names
for a restated number, performed rather than left owed. Several later review
rounds each added tests, and each time the **stage** was settled by
re-measuring the fast half rather than by assuming: the behavioural migration
tests are integration and left the fast half where it was, where every
`ContractTests` addition is unit and moved it. That distinction is not cosmetic
— the three stages have separate floors in `.github/pipeline-gate/`, so
guessing which one grew is guessing which floor to raise. **The intermediate
totals are deliberately not written out.** Each was superseded by the next
round, and a chronology of superseded figures is a row of numbers a reader can
check against nothing while every one of them contradicts the paragraph's own
opening sentence. Every `ContractTests` case lands in the **unit** stage,
because that stage's filter is `FullyQualifiedName!~ArchitectureTests` and
`Platform.IntegrationTests.ContractTests` matches neither that nor
`Category=Integration`, whatever the project's name suggests.

---

## The copy that was filled only by accident (#121)

§6.6's order summary wrote each product's name and thumbnail into every order's
JSON as an empty string and relied on a later `ProductPublished` to patch them
in. Ordinarily none arrives. A product must be published before it can be
ordered — `PlaceOrder` reads `ordering.ProductPrices`, which the same event
fills — so that event is ordinarily consumed *before* the summary row exists,
and a patch scoped to summaries that already contain the product then touches
nothing. **Every summary carried empty names in the normal flow**, which is the
payload the section exists to deliver.

**Ordinarily rather than always**, and the review round that raised it was
right to: `IntegrationEventConsumer` runs the two handlers sequentially and
each commits on its own connection, so an order placed after the price handler
commits and before the patch handler runs would find its row patched. The
window is narrow and the defect is the common case, but a specification that
turns a common outcome into an impossible ordering is making a different claim
from the one it can support.

**The cheaper repair was available and is the one that had to be refused.**
Reading the names at insert time satisfies the ordinary flow completely. What
it does not survive is the second door: `ProductPriceProjection`'s upsert
inserts on its `NOT MATCHED` branch for `PriceChanged` as well, so a product
whose `ProductPublished` never reached the queue still acquires a price row and
is orderable — and an order placed through that door would carry an empty name
permanently, with the only thing that could repair it being the patch handler
this change removes. So the fix is a product-keyed table and resolution on
read ([ADR-027](backend-architecture/adr/ADR-027-the-order-summary-stores-product-ids-and-resolves-the-name-locally.md)),
which fills every order that ever referenced a product the moment its name
arrives, retroactively and with no rebuild.

**The premise that justified the copy was false before this PR was written.**
§6.6 argued for denormalising the name on the grounds that "joining at read
time is not an option — the products live in Catalog". They do not, once
Ordering projects them: `ordering.ProductPrices` had been exactly that local
projection on the write path since PR-20, so a primary-key lookup against a
table in the same database was never the cross-service join the sentence was
about.

### One claim, and every sweep that verified clean before it was

**No count in this heading, deliberately.** It carried one — *seven sites,
three sweeps* — and both figures were stale within two review rounds, in the
one section of this log whose whole subject is a claim that kept being
restated. The predicate is what generalises; the tally was never the lesson.

The narrowing this PR needed — *no price row* means this service holds no price
for the product in the asked-for currency, not that a `ProductPublished` failed
to arrive — took several commits to apply, and **each intermediate sweep was
verified before being called done**:

| Sweep | What was searched | What it missed |
|---|---|---|
| 1 | The review's own list of sites | Siblings the report did not name |
| 2 | `grep … src/ docs/` | `tests/`, excluded by the scope rather than the pattern |
| 3 | `grep 'never published'` | A site where the phrase **wraps across a newline** |
| 4 | Three named files | A fourth that was not on the list |

**The third is the one worth carrying.** House style wraps prose at 80 columns,
so a line-oriented pattern goes quiet exactly where the corpus has been
wrapped, and reports the same nothing a finished sweep reports. What closed it
was a regex with `\s+` between the words, run over each file's whole text
rather than line by line. This repository will keep generating the case.

**Then it kept coming back in freshly written prose, which no instrument
reaches.** Comments and paragraphs added *after* a sweep said "a product
Catalog has not published" — a different error of the same family:
`Product.Publish` is Catalog's factory and always raises
`ProductPublishedDomainEvent`, so every product Catalog holds was published and
an absent row means *Ordering has not applied the event*. That happened more
than once, and the last instance was inside a paragraph of this entry
describing the error itself.

**So the rule is not a better grep.** Conflating another service's act with
this one's knowledge is the phrasing that comes to hand, and a sweep can only
remove the instances that already exist. What it needs is to be known as a
family before the next sentence is written.

### A bound one multiplication away from the bound that was cited

The new history query passes the page's distinct product ids to the second
statement. Both the chapter and the ADR said it was "bounded by the same clamp"
as the first — true, and irrelevant. §6.5's clamp bounds **rows** at a hundred;
§10.5's validator admits a hundred items per order; so the distinct ids reach
ten thousand against SQL Server's ceiling of 2,100 parameters, where the
request does not degrade but fails outright. The ids travel as one JSON
parameter read through `OPENJSON` instead.

**A limit reached by multiplying two documented limits is invisible to a reader
checking either one**, and a reassurance naming the wrong one of them is worse
than no reassurance. Six external review checks and three internal audit passes
went by before it was raised.

### What a test has to refute, rather than what it has to cover

§12 gained five cases for a projection that does not exist yet, and the
reasoning behind them was wrong once before it was right. The first version
claimed the ordinary flow (publish, order, read) would pass against the shipped
design — it does not: that patch ran when the product was published, before the
order existed. What the ordinary flow cannot distinguish is this design from
the insert-time repair.

Written out, each case refutes a different candidate:

| | Ordinary flow | Late arrival |
|---|---|---|
| Empty strings, patched later | fails | passes |
| Names read at insert time | passes | fails |
| Ids resolved on read | passes | passes |

Three more exist because a *branch* has no case rather than because a design
does: the replay that enters the `MERGE`'s `WHEN MATCHED` arm and its watermark
guard, the read taken before a name arrives that enters the reader's
`Where(named.ContainsKey)` filter, and the page wide enough to exceed 2,100
parameters. **Every one of those three would be satisfied by an implementation
that deleted the thing it exists to protect**, which is the gate-coverage rule
this repository keeps rediscovering, arriving at the specification rather than
at a gate.

### Cheap now, and not later

`OrderSummaryProjection` is unbuilt and Appendix C carries no row that builds
`OrderSummaries` at all — which is itself worth noticing, since a specified
mechanism with no row in the plan is exactly what forced Appendix C's *After
the plan* section into existence for §8.5. Nothing in `src/` changes behaviour
here; every source and test edit in the branch is a comment or an assertion
message. The same correction after that row lands is a migration, a backfill
and a projection rebuild.

---

## The rule that was missing from a pattern that looked complete (#131)

**No `PR-NN` heading**, on the terms the entries below set: Appendix C's plan is
complete, and this is a specification gap rather than a row that was ever in it.

**The blueprint reasoned about rolling deploys in a dozen places and every one
of them was about state.** §6.5's live cache key, §7.3's migration race, §8's
key rename, §9.4's `MessageTypeMap` alias, §15.5's backward-compatible
migrations — a schema, a key, a persisted type name, a persisted payload. Not
one was about **routing** or **vocabulary**, which is why this release could add
a saga binding and three reason codes with nothing to check them against.
**A gap inside a well-covered pattern is harder to see than a gap in an
uncovered one**, because the neighbouring cases make the topic feel answered.

**The sentence that was actually wrong is the one nobody would have looked
at.** §9.2 said additive changes need no version bump because "consumers
deserialising an unknown field ignore it" — true, and true only of fields. A
deserialiser is built to skip an unknown field; nothing is built to skip an
unknown message *type*, and a closed vocabulary is a whitelist by construction.
Both are additive by every ordinary reading and neither is safe, so the
tolerance had to be narrowed rather than the breaking-change paragraph below it
extended.

**Two instances, opposite failure modes, one root — and the quiet one is why
this is an ADR.** A new binding on a shared queue is handed to a replica whose
build declares no consumer, parked in `<queue>_skipped`, nothing thrown. A new
reason code is refused by the mapper, and §9.8 excludes
`ContractMappingException` from retries on purpose, so it reaches the error
queue on the first attempt. Loud and silent, closed by the same rule:
[ADR-026](backend-architecture/adr/ADR-026-consumer-capability-is-a-release-ahead-of-the-producer-that-uses-it.md).

**Ordering closes the vocabulary case and does not close the binding case**,
and running them together is what made this look like one problem with one fix.
A mapper can learn a code a release early. A saga cannot learn an event a
release early, because the build that declares `Event<T>` is the build that
publishes `T` — consumer and producer are in the same build, so ordering the
deploy separates nothing and the change has to be **split across two releases
before there is an order to impose**. §15.5 now carries that, and the
alternative to it: cut over without overlap and do not call it a canary.

> **"No ordering separates them" is what both this entry and §9.2 said first,
> and it contradicted §15.5 two files away**, which prescribes the split. The
> true claim is narrower and is the one worth carrying: ordering *a deploy*
> separates nothing when both halves are one artefact, and the fix is to make
> them two artefacts. The overstatement survived writing the rule, the ADR and
> the chapter, and was caught reading the finished section back rather than by
> any check.

> **A rule with no detection is advice.** The alert is what makes this an ADR
> rather than a paragraph — `SkippedQueueDepth` pages within a minute, where
> before a rollout that lost messages looked exactly like one that did not.
> No CI check can help: whether a consumer is deployed everywhere is a fact
> about the cluster, not about the branch. So this is detection rather than
> prevention, on the same terms as ADR-024's guarantees, which nothing holds
> Inventory to either.

**The alert needed its own rule rather than a wider selector, and that was the
one real design choice.** `.+_(error|skipped)` would have been one line and one
alert. An `_error` message is one a consumer accepted and could not finish; a
`_skipped` message is one no consumer would take. They are triaged from
opposite ends — **replay is the right first move for a skipped message and the
wrong one for the arrival `error-queue.md` opens with** — so folding them would
have produced one procedure that is wrong half the time. §13.9's pairing gate
forces the same conclusion from the other side: sharing a runbook needs an
argued `SHARED_RUNBOOKS` entry, and the argument would not have been available.

**Two counts went stale on the way in and both were removed rather than
incremented.** `platform-alerts.yaml` said "the two rules whose signal comes
from an exporter"; CLAUDE.md said twelve runbooks. The first is now named
rather than counted, on this file's usual terms; the second is a tree listing
where the number is the point, so it moved to thirteen.

**The second half of that sentence was true of one site and read as true of
the file, and [#155](https://github.com/alexander-shamray/blueprint-backend/issues/155)
is what it cost.** CLAUDE.md said twelve runbooks in *three* places; the edit
reached the tree listing and left the PR-24 narrative saying twelve twice
over, so this entry recorded a reconciliation that had covered a third of its
subject. That is the multi-target lesson this repository already carries —
**a multi-target edit that aborts has applied a prefix of its changes** — with
no abort to make it visible: nothing failed, the count that was checked was
correct, and the two that were not went unread for as long as nobody counted
them. **Resume from the list, not from the site you happened to fix**, and
where the list is prose in five files, the honest fix is to stop restating the
number at all. #155 dropped it everywhere but here and the tree listing, and
gave the predicate a gate — check 9 reads §13.6's and §13.9's tables against
`docs/runbooks` — so the one number that survives is now one a build
recomputes.

---

## The state that waits on two services (#124)

**No `PR-NN` heading**, on the terms the entries below set: Appendix C's plan is
complete, and this is a defect fix against a chapter §9.6 already owns rather
than a row that was ever in it.

**The interleaving that broke it is the one the design expects, which is why it
had never been argued.** `Compensating` is reached from `AwaitingPayment` with
`AuthorisePayment` sent and unanswered, so Inventory and Payments both owe the
saga an answer and §9.4 orders nothing between them. Both exits finalised on
the stock half alone. A prompt warehouse and a slow PSP is the ordinary shape
of that state, not the degenerate one — so on the common ordering the instance
was deleted and the authorisation still in flight correlated to nothing:
consumed cleanly, no `payment_authorised_during_compensation` row, nothing on
§13.6's pager. The escalation §9.6 provides for exactly this case could not
run, because the thing it needed was the instance.

**The state could not answer the question the exit was asking, and that is the
general fault rather than a missing branch.** `Compensating` is entered five
ways and whether a verdict is owed differs by route — nothing from
`AwaitingStock`, already answered from `AwaitingConfirmation`, and from
`AwaitingPayment` it depends on whether a decline, a timeout or a cancellation
brought it there. One state name compresses all five, so the machine had
discarded the fact before the exit ran.
[ADR-025](backend-architecture/adr/ADR-025-a-saga-state-that-waits-on-two-services-finalises-on-neither-alone.md)
records the rule that follows: record the obligation where it is incurred, and
finalise on the join.

> **A sixth state was the alternative and it is a product, not a state.**
> `Compensated` — stock settled, still waiting on a verdict — needs a mirror
> for the case where the verdict lands first, so the honest version is four
> states enumerating what two booleans and one join express. Worth stating in
> that direction, because the state machine is the tempting place to put it.

**A timeout ends the wait and not the obligation, and that distinction is the
whole of the bound.** A PSP that has not answered in fifteen minutes has not
declined; the authorisation it may still complete is precisely what the review
row exists for. So `PaymentVerdictOutstanding` survives a timeout, the
`AwaitingPayment` cancellation branch deliberately stops unscheduling
`PaymentTimeout` so the wait runs on into `Compensating`, and the timeout door
re-arms it once. The longest hold is thirty minutes from `AuthorisePayment`,
inside §13.6's one-hour alert with room to spare. **A join with no bound trades
a silent loss for a pager**, which is not a trade worth making.

**The bound raises no row, and the reason is that the ordinary case is
silence.** §3.2 has Payments consuming `OrderCancelled`, so an authorisation
abandoned on a cancelled order is what should happen. A row on the timeout
would page someone once per cancelled order the PSP correctly dropped — the
same failure ADR-024's argument turns on, one state later.

**`Ignore(PaymentDeclined)` had to stop being an `Ignore`, and that is the
transferable half.** A decline still escalates nothing, because no money moved.
But it is an *answer*, and ignoring an answer held the instance open until the
wait expired for a verdict that had already arrived. An `Ignore` is only safe
for an arrival carrying nothing the machine is waiting for, which is a narrower
licence than "nothing to do about it".

**`PaymentAuthorised` takes `OnMissingInstance(Fault)` and no other event
does.** The join keeps the instance for the interleaving that used to delete
it; the fault answers for the tail past the bound. What makes it safe for this
event alone is provenance rather than timing: Payments produces it, so unlike
`OrderCancelled` — Ordering's own echo, arriving at a finalised instance on the
ordinary path — or `StockReleased`, which ADR-024 has answered for every
release including a no-op one, it can never be routine. **The reasoning does
not transfer to #123**, and the attempt is worth recording: `Reason` looked
like the same kind of discriminator for a cancellation and is not, because
§11.4's endpoint parses the whole `CancellationReasons` map, so a caller may
send `payment_declined` and the code carries no origin. That residual is
already written at the registration and stays there.

**Both halves of that paragraph have since been overtaken, and the argument
in it is why.** #123 gave `OrderCancelled` an `Origin` field and a missing
instance branch that faults for anything it cannot account for, so
`PaymentAuthorised` is no longer the only event treated that way — and the
residual is closed rather than standing at the registration. What held is
the reasoning: **provenance rather than timing** is what licenses a fault,
and the reason `Reason` could not transfer is exactly the reason a field
written at the entry point could. Reconciled in place rather than rewritten,
per this file's header.

**Three of the five new tests were observed red against the unconditional
`Finalize`, and the fourth was the interesting one.** The decline test passed
both ways — a decline reaching no instance is discarded, so "no instance, no
review row" reads identically from either side of the fix. It became a guard
only after an `Exists` assertion was added between the release and the decline,
pinning that the instance is alive at the moment the branch under test runs.
**A negative that the defect also satisfies is not evidence**, and the way to
tell is to run it against the old behaviour rather than to read it.

**`PaymentTimeout.Received` moved across the structural partition rather than
being added to it.** It had sat in `Compensating`'s not-reachable list under
"the transitions that enter this state unschedule it" — true of four doors and
never of the fifth, which nothing had asked about. The partition is what made
that visible: a list of what a state accepts would have stayed green, because
nothing was missing from it.

**Counts pinned to this branch**, in the form the entry below uses: the
solution runs 896 tests, 708 of them outside `Category=Integration`, and the
three CI stages are 18, 690 and 188. Reconciled against a local
`dotnet test Platform.slnx` and owed a check against this branch's own CI run,
which is the arithmetic this file names for exactly this case.

---

## The release that answers for the order (#125, #129, #130)

**A specification gap closed three issues, and only one of them was a
specification issue.** [#130](https://github.com/alexander-shamray/blueprint-backend/issues/130)
asked whether a `ReleaseStock` for a reservation that was never held publishes
`StockReleased`. Nothing said. §3.2 gave Inventory the command and the event
and stopped there; §9 sends the command from every compensating transition it
has and never asks.
The answer is
[ADR-024](backend-architecture/adr/ADR-024-a-release-answers-for-the-order-not-for-the-reservation.md),
and once it is written down, #129 becomes a four-line change and #125 stops
being reachable.

**The two readings were opposite and both defensible, which is what made it a
gap rather than a bug.** A release of nothing "succeeding" is as reasonable as
a release of nothing having nothing to report. What decides it is not
elegance: `Compensating` settles its stock half exactly two ways, and the
second is a ten-minute timeout that raises `stock_not_released` for a human.
(That read "`Compensating` has exactly two exits" until #124 made the state a
join and gave it a conditional `Finalize`; the argument needs the stock half's
two settlements and never needed the wider claim.) Under the second reading
`StockReservationFailed` — an event that *proves* no reservation was taken —
leaves that state through the pager, naming stranded stock that never existed.
**A contract whose routine path escalates to on-call is the wrong contract**,
and that is the whole argument.

**The second guarantee was not obvious and is the one that closes #125.** With
"always publishes" alone, a release handled before its reserve is a no-op that
publishes, the saga finalises on it, and the `StockReserved` that follows
correlates to nothing. So the ADR also has Inventory *remember* a release for
an order whose `ReserveStock` has not arrived and refuse the reserve that
follows — answering with `StockReleased`, the same postcondition, rather than
with `StockReservationFailed`, which reports unavailable products a refusal of
that kind does not have.

> **The cheap saga-side fix is unreachable rather than merely weaker, and
> noticing that is what stopped it being written.** #125 offers sending a
> second `ReleaseStock` on `StockReserved` in `Compensating`. Under the first
> guarantee the no-op release has already published and the exit has already
> finalised, so the branch that would send it is one nothing enters. A fix
> whose precondition is the defect it ships beside is not a fix. **Only the
> participant that still holds both facts can reconcile them**, which is why
> the guarantee is Inventory's and not the saga's.

**#129 was three doors and there are four.** The issue names `AwaitingStock`,
`AwaitingPayment` and `AwaitingConfirmation` — the states whose cancellation
branch sends a release. `Confirmed`'s branch deliberately sends none, and
Inventory releases on `OrderCancelled` regardless (§3.2), so the derived event
arrives there too. **And there the retry discards where elsewhere it
rescues**: the other three are races §9.8's five retries — six deliveries with
the first — usually win by finding the instance moved to `Compensating`, while
`Confirmed`'s branch finalises, so the second delivery finds no instance — and
an event correlating
to none is consumed cleanly. That door is **silent**, not loud, which is a
better argument for writing it than the one first drafted here: the release is
lost with one fault behind it and nothing on the pager.

**The gate and the issue had the same blind spot, which is the reusable half.**
`The_two_states_a_confirmation_can_reach_write_it_out` covered
`AwaitingConfirmation` and `Confirmed`; the residual it left — "three of the
five states are checked" — is exactly where `Confirmed`'s door hid, because
that test asserted the events a state accepts and nobody had added the release
to either list. A gate covering three of four surfaces reports the fourth as
fine. The per-state arguments now cover all five states, and the two
behavioural tests were each observed red against the branch removed,
separately, so neither is passing on the other's account.

**"The partition now covers all five states" is what this said, and only one of
the five is a partition.** `Compensating` classifies every declared event; the
other four compare `NextEvents` to a written list and pass when a new event is
missing from both. So the sentence claimed the fail-closed property for four
checks that do not have it — the overstatement this entry is otherwise about,
made about the fix for it. A third test now asserts that every declared event is
receivable somewhere, both sides read from the machine, and it was observed red
against an unhandled event that every other assertion in the file accepted.

**`Ignore` is correct because of the ADR and was not correct without it**, and
the ordering of those two halves is the point rather than a footnote. #129
argues at length that ignoring is the wrong fix — the release is discarded, so
the instance waits out `ReleaseTimeout` and raises `stock_not_released` for a
reservation that came back. True under the second reading of #130 and false
under the first, because the saga's own `ReleaseStock` is then answered
whatever Inventory did with the event. **A code change that is only sound
because of a contract change has to land with it**, and the three absorbing
lines that depend on it say so at the site. (They were `Ignore` lines when
this was written; #143 made each of them record the arrival instead, which
changes what the absorption is *for* and not whether it is sound.) The fourth
is `Confirmed`'s, which refuses the dependency in as many words, and is the
subject two paragraphs
down — writing "the four" here and carving it out there is the collapse this
entry warns about, committed inside the entry that warns about it.

**Nothing enforces the ADR until Inventory exists, and that is stated rather
than gated.** No test can hold an unwritten service to a contract. What stands
in for one is that every site leaning on it cites it — the §3.2 bullets, the
comments in the machine, the runbook's `stock_not_released` section and this
entry — so an Inventory built to a different rule contradicts a paragraph rather
than failing silently. That is weaker than a gate and it is what is available.

**`Confirmed`'s absorption is the one that does not lean on it**, and an earlier
revision of this entry counted it in. That state sends no `ReleaseStock`, so
nothing there is waiting on an answer; it absorbs the release because Inventory
released off the cancellation itself, which is a §3.2 fact rather than this
ADR's.

## The barrier that was twenty call sites, and the run that proved it (#107)

**No `PR-NN` heading**, on the terms the entries below set: this is a defect fix
against §12.5, which the plan has no row for.

**`main` went red on the merge commit that closed #107.** That issue was the
saga suite flaking under a full-solution parallel run — a test failing after ten
seconds saying the saga did not send a command. It was closed by interleaving
waits into `Payment_declined_releases_stock_before_cancelling`, the one test
that had been observed failing, with a comment arguing the mechanism correctly
and at length. The next CI run failed on
`A_payment_timeout_compensates_with_its_own_reason`, ten seconds,
`Sent<ReleaseStock>` false.

**The diagnosis was right and its reach was not.** A publish returns when the
message reaches the transport, not when the saga has consumed it, so two
consecutive publishes are a race: the second event reaches the endpoint before
anything has created the instance — discarded in silence, since a non-initial
event correlating to nothing is consumed cleanly — or before the instance has
reached the state that handles it, which faults. Neither is visible where it
happens. The test runs on and the *next* waiting assertion bills the inactivity
bound, so the failure is reported four assertions away from its cause, wearing
the saga's message rather than the runner's.

**An audit of the file found twenty unfenced publishes across fourteen of the
twenty-seven harness tests it held then.** Three of the twenty publish a
scheduled expiry, which is the worst case: an expiry arriving early lands in a
state with no branch for it, and `ReleaseTimeout` in particular can only be
handled in `Compensating`, three transitions downstream.

**The counterfactual was measured rather than argued**, because the race does
not reproduce on a developer's machine — twelve concurrent runs of the suite
here passed, all in two seconds. Forcing the losing order instead —
`PaymentAuthorisationExpired` published before `StockReserved` — gives
`ReleaseStock` not sent, after 10.1 s. That is the CI failure exactly: the same
assertion, the same value, the same duration.

**The fix is not twenty more waits, and that is the whole decision.** Per-site
discipline is what produced twenty instances of one defect: it fails open, the
test that forgets is the test that flakes, and it flakes on a loaded runner and
nowhere else — so the suite that forgets looks identical to the suite that does
not, on every machine anyone will run it on. The barrier moved into
`Publish`, the helper every test in the file already calls, which leaves nothing
to forget. That is the argument `Common.Web.Tests`' assembly-wide
parallelisation attribute won over a shared collection, arriving at the same
answer from the same direction.

**It fences on the message's own id, not its type.** Tests in this file deliver
one type twice — the redelivery, the duplicate confirmation, and now the
barrier's own test — and a
type-level wait matches the first delivery and returns immediately, fencing
nothing for exactly the tests whose subject is a second arrival. Named in the
helper rather than counted, because a count of them is a number nobody re-runs —
this entry said four before anything measured it. The id comes off the send
context rather than the contract, because the saga's five scheduled expiries are
not contracts (Appendix D) and carry no §9.1 envelope.

**It costs nothing on a green run.** The suite runs in the same one second it
did before, because the consume the fence waits for is the one already
happening. What it spends the inactivity bound on is a message no consumer takes
— measured at 10.0 s against a type the machine does not declare — and that is a
real defect reported at the line that caused it.

**The barrier has tests whose subject is the barrier, and it had to.** Every
*pre-existing* test in the file stays green with the fence removed, on this
machine and on any developer's; that is precisely the property that let twenty
instances ship. There are two guards now and neither stays green, which is what
the plural is for.
`A_publish_returns_only_after_the_saga_has_consumed_that_message` publishes and
then reads the record **as of now**, on a spent token, asserting the
`ReserveStock` the `Initially` transition owes is already recorded. It was
observed red both ways the helper can break — with no wait at all, and with a
wait on the type rather than the id — before the fix was trusted.

**The two reds are not the same strength, and saying so is the point.** The
no-wait arm fails the first assertion deterministically. The type-level arm
fails only the second, and that one is a race by construction: the
early-returning publish leaves the duplicate's consume in flight, so the
spent-token read usually sees one and may legitimately see two. It was observed
red; it is not guaranteed red. **A counterfactual nobody can re-run is not
evidence the next reader can check**, so the distinction is written into the
test rather than rounded off into "observed red".

**The test is named for the consume and not the transition**, because its own
second stimulus falsifies the stronger name: a duplicate `StockReserved` lands
in `AwaitingPayment`, which declares neither a branch nor an `Ignore` for it, so
no transition runs at all. It faults — the default §9.6 keeps — and the test
says so with a `ConsumeFaults` assertion rather than leaving a message on the
error queue unremarked. That assertion earns its place twice: it is also what
proves the two deliveries are distinct and each reached the machine, where a
`Consumed` count alone reads the same whether the pipeline returned or threw.

**§12.5's own first sample published three events back to back**, so the chapter
was teaching the race while the file below it was failing on it. The sample now
interleaves its waits, and three callouts carry the mechanism, the reason the
barrier belongs in the helper, and the reason a barrier needs a test of its own.
The suite's explicit `Sent<…>` waits stay: they fenced and now only assert —
that each transition sent the command it owes — which is what they were worth
keeping for, and the comment beside them says so rather than reading as a
template for the next test.

`dotnet test Platform.slnx` came out of this at 886, up from 884, with a fast
half of 698 — two new tests, both the barrier's: one driving the saga, and one
driving a consumer the test holds open, because the first can only observe the
barrier through a race and the second settles it by construction.

**Pinned to then rather than restated**, on the same terms as the twenty-seven
harness tests above: this entry records what one PR measured, and the live
figure belongs in `CLAUDE.md` and `docs/testing.md`, which carry it. It read
"is 886" in the present tense until a later branch moved it — and the two
sentences sat four paragraphs apart in this one entry, one of them already
pinned. **A treatment applied to one number and not to its neighbour is the
half-applied reconciliation this file's own rule forbids**, which is what makes
this worth a paragraph rather than a silent edit.

---

## The state named for a command's intent (#126)

**No `PR-NN` heading**, on the terms the entries below set: Appendix C's plan is
complete, and this is a defect fix against a chapter §9.6 already owns rather
than a row that was ever in it.

**§9.6's `Confirmed` was entered in the activity that *sends* `ConfirmOrder`,
so it meant "a command is in flight" while everything downstream read it as
"the aggregate confirmed and Shipping has been told".** Those diverge for
exactly as long as one local command takes, and a cancellation arriving inside
that window took the `Confirmed` branch: it withheld `ReleaseStock` on the
argument that a reservation being picked must not be dropped — for a picking
nobody had requested — and raised `cancelled_after_confirmation` for an order
that was never confirmed, sending an on-call to stop a despatch that did not
exist.

**The fix is a state that waits for the acknowledgement, and it cost no
contract.** `Order.ConfirmPayment` already raises `OrderConfirmedDomainEvent`,
§9.3's mapper already produces `OrderConfirmed`, and §6.3 already stages it in
the transaction that sets the status. The evidence was being published the whole
time and nothing was listening to it — which is why the issue's own estimate
("costs a new event on the contract, a §9.2 version bump") was wrong in this
codebase's favour. **Read what the service already publishes before pricing a
new contract.**

- **A state entered on an intention is the general shape.** `Confirmed` was not
  so much mis-named as named for what its transition was *trying* to achieve.
  The test that catches it is whether anything outside the machine could
  contradict the name — here the aggregate could, and did. That question is
  cheap and applies to any state, flag or status field.
- **The saga now subscribes to a third of its own events, and the reason is
  different from the other two.** `OrderPlaced` and `OrderCancelled` are
  *origins* the workflow has to learn about; `OrderConfirmed` is the
  **acknowledgement of a command the saga itself sent**. §3.2's Consumes cell
  gained an entry whose justification is a round trip with both ends in one
  service — the platform's only one.
- **`Compensating` had to write `OrderConfirmed` out, and `Ignore` was the
  wrong answer.** `AwaitingConfirmation` cancels on the premise that the
  aggregate had not confirmed, which is unknowable at that moment: both events
  are Ordering's own outbox rows and §9.4 orders nothing between them. A
  confirmation arriving afterwards is the **only** evidence the premise was
  false — Shipping was told, a despatch may be moving, and a `ReleaseStock`
  for it has already gone out. Absorbing it would have restored the silence the
  whole fix was about, so it raises `cancelled_after_confirmation` there.
- **One code, two states, and that broke a navigation rule the runbook rested
  on.** `order-review.md` selected its procedure on the saga state, which
  PR-21's own entry had argued for; `cancelled_after_confirmation` is now
  raised from two states with different saga lifetimes and the same procedure.
  **The code carries the procedure and nothing persists the branch**, which is
  the right way round — an operator needs to know what to do, and the row says
  it. What no longer follows from the code is whether an instance is still
  alive, and the runbook now asks that separately.
- **Splitting a state moves what can arrive at BOTH halves, and the first
  pass got both wrong.** An adversarial review found two events reaching a
  state with no branch, which under this machine's kept default faults to the
  error queue §13.6 pages on. A second `OrderConfirmed` in `Confirmed` — from
  §9.5's unrecorded redelivery, and from a rollout, since the old machine
  entered `Confirmed` on the *send* and §15.5's canary runs both releases for
  the length of its ladder. And a `ShipmentDispatched` in
  `AwaitingConfirmation`, because §3.2 gives Shipping the same
  `OrderConfirmed` this saga now consumes and §9.4 orders nothing between two
  consumers — an ordering dependency the split *created*, since the old
  machine was in `Confirmed` before that publish existed. Both reproduce as
  `UnhandledEventException` and both are now written out.
- **The gate that exists to catch that covered one state, and the newest
  surface was not it.** `Compensating_writes_out_every_event_it_can_receive`
  partitions `NextEvents(Compensating)` and nothing else — so it demanded the
  `OrderConfirmed` branch in `Compensating` and said nothing about the two
  states carrying the new event. **This repository's most-repeated failure,
  arriving inside the test written to catch it.** `AwaitingConfirmation` and
  `Confirmed` have partitions of their own now; `AwaitingStock` and
  `AwaitingPayment` still do not, and that is the residual restated. It was
  deliberately not generalised into a loop: what makes a partition checkable
  is naming the events a state can receive *and why*.
- **A test can pass against the defect it is written for, if it reads the
  harness before the delivery lands.** The first version of the duplicate-
  confirmation test published and then snapshotted `ConsumeFaults` through an
  already-cancelled token — so it went green against a machine that faults,
  and only the counterfactual run exposed it. The fix is this file's existing
  pattern: give the duplicate its own `MessageId` and wait on *that* id.
  **"Observed red" is worth nothing unless the red run is the one you
  predicted** — here the first counterfactual was green and that was the
  finding.
- **A hard-coded count in a test failed where the list beside it was
  correct.** `DatabaseSmokeTests` asserted `applied.Length.ShouldBe(8)` next to
  eight named migrations, so a ninth migration went red on the number while the
  names were still complete. It asserts `expected.Length` now. **A literal
  beside a list it describes is a second place to edit that says nothing the
  first does not** — the same finding this repository has recorded for prose
  counts, arriving in a test.
- **The `ConfirmOrder` fault this race was said to cause does not exist.** The
  saga's own comment, and `order-review.md`, both told a reader to expect a
  `ConfirmOrder` in the error queue §13.6 pages on. `ConfirmOrderHandler`
  catches the `DomainException` and returns `OrderErrors.NotAwaitingPayment`,
  which is `Error.Rule`; `CommandConsumer` rethrows only
  `ErrorType.Unavailable`, so it is acked, counted and logged.
  `Confirming_an_order_that_has_moved_on_is_a_rejection` has pinned it since
  PR-21. **A consequence asserted in a defect report is not thereby a
  consequence** — this one was repeated across two files for two rounds
  because it sounded like the sort of thing that would happen.
- **The timeout's floor was argued from the wrong term.** The comment priced
  it at §9.8's retry budget on `ordering-commands` — "five attempts backing
  off to a minute apiece" — which is about seventy seconds in practice, not
  five minutes, because that ladder never reaches its cap. And it credited the
  outbox dispatcher's 500ms *poll*, which is the one quantity that cannot
  matter. What actually decides it is the dispatcher's *failure* ladder:
  `POWER(2, MIN(Attempts, 8)) * 5` seconds, so a publish succeeding on its
  eighth attempt lands after ten minutes have already passed and a
  `not_confirmed` review has been filed for an order that then confirms. Ten
  minutes is kept knowing that — seven consecutive publish failures is a stuck
  outbox with its own alert — but the argument now names the governing term.
- **ADR-021's volume argument moves with the state count**, and nothing in a
  state-machine review would surface that. Its scheduler cannot cancel, so
  every wait an order enters leaves one undeliverable delayed message in
  Mnesia — three before this change, four after. That total is the ADR's
  stated supersession trigger, so it is now written as a rule (one per wait
  entered) with the number as an illustration.

---

## The review loop's fail-opens, and the sweep grants beside them

**No `PR-NN` heading, on the closure gate's terms one entry down.** Appendix C's
rows describe the blueprint's specified system; this is agent tooling under
`.claude/`, which has never had a row and never will. What it closes is issues
#34, #51, #59, #69, #120 and the operable half of #75 — six findings from
authorised sweeps of this repository's own scripts, filed over five months and
sharing one property: **each is a protection that did not protect**, in the loop
that decides how much external review a pull request receives.

**They are one change because they are one permission lift.** `.claude/scripts/`
and `.claude/sandbox/` are `Edit`-denied to an agent session by design, so every
fix here is a human's edit made with that deny lifted and restored. Filing them
separately would mean lifting it six times.

### The cap that was not one

**A bound whose two halves are two commands is not a bound.** `ship.md`
specified the discipline as prose — post `grok-ledger.sh <n> reserve <N>`, then
invoke `grok-review.sh` — over two separately granted commands, and neither half
was enforced. A grep of the review helper for `ledger`, `reserve` or `release`
returned nothing, so the ordering was agent behaviour; and `release` was
accepted for any slot at any time, validating the number and posting the comment
without verifying that a skip had occurred or that the run it released had ever
happened. A run that invoked the review without reserving spent a check that
left no record, a resumed run read a lower count, and the PR ran past twelve
against a paid API.

Invocation and accounting are one operation now: `grok-review.sh` takes the slot
and the mode, validates both against the ledger's own vocabulary, resolves the
pull request from the branch it is about to clone, and posts the reservation
itself. **It took the PR number as an argument for exactly one review round**,
which is the finding recorded two paragraphs down.

**The placement of that write is the accounting rule, not an implementation
detail.** It sits immediately before the review's own `docker run`, so a slot is
reserved by no path that refuses earlier. Everything
that can refuse earlier — a dirty tree, no daemon, a missing credential, a bad
`suggestions.md` shape, and all three usage-limit skips — spends nothing. That
is what deleted the release path rather than merely tidying it: exit 12 has no
reservation to give back. A structural test asserts the ordering, because the
ordering *is* the property.

**One operation is not enough if the operation can be aimed elsewhere, and the
first version of this could be.** The helper took the pull request number as
argument one and then cloned and reviewed the *current branch*, with nothing
checking that the two were the same subject — so a numeric typo, or an
instruction substituting another open pull request, posted the reservation there
while reviewing this branch: this branch's cap stayed re-armed and someone
else's slot was spent, on a pull request whose ledger then read one check higher
than the reviews it received. Neither half looks wrong on its own, which is what
let it survive the first draft. The number is not an argument now —
`gh pr list --head "$branch"` resolves it from the branch about to be cloned,
and a branch with no open pull request, or more than one, is refused rather than
guessed at. Raised by Copilot against this very change, which is the loop
catching the fix to the loop.

The verb survives with no caller: `count` must still fold a released row out of
a PR ledgered before this was true, and a human reconciling a slot spent wrongly
has nothing else to reach for. `.claude/settings.json` denies both spellings to
a session, in the same mid-string wildcard form the `--output` deny already
uses — **a speed bump with its limit stated**, since a substring deny over a
shell command string is defeated by quoting, exactly as that entry records.

**Three verbs stay reachable, not two, and that is a deliberate departure from
the issue's own fix.** #59 asked for `count` and `status` to be all the agent
invokes; `converge` is left with them. It is a write, but it cannot re-arm the
budget — `count` skips converged rows by construction, so the marker moves no
number — and what it records is a judgement the agent is already the one making:
whether the loop ended clean or ran out of ceiling. Removing it would delete
`ship.md`'s only way to tell those apart on a resume and buy nothing, since a
false convergence marker is a lie the agent could equally tell in its report.
The two verbs that ARE denied are the two that move the count.

### The counter that answered on its own error path

**An `exit` in the last stage of a pipeline ends a subshell, and the consumer on
the other side has already answered.** `grok-ledger.sh`'s trust check bailed
with `exit 3` inside `gh api … | while …`. That killed the subshell, not this
script;
the `awk` on the other side of `ledger_rows | awk` saw EOF, ran its `END` block
and printed `0` — "nothing spent", which re-arms the twelve-check cap the helper
exists to enforce — and only *then* did `pipefail` and `set -e` abort with 3. A
model reading stdout had its answer before the failure existed. Reachable by
plain API rate limiting on a busy PR.

**What made the two cases indistinguishable was a behaviour that is correct.**
`END`-runs-on-empty-input is deliberate and documented: a fresh PR's ledger is
legitimately empty, and `pipefail` turning that into a failure was this helper's
first field defect. So the separation had to happen *upstream* of the fold. All
three consumers now buffer — command substitution hands back the status, where a
pipe hands the reader an EOF it cannot tell from empty input — and nothing is
written until the status has been checked.

Measured rather than reasoned: the pre-fix `count`, run against a stub whose
permission lookup fails, exits 3 with `0` on stdout; the fixed one exits 3 with
stdout empty.

### The verdict that passed everything nobody had listed

**A deny-list of terminal states passes every state nobody listed, including the
ones the next version invents.** The last of the three gates deciding whether a
review actually ran refused `"stopReason":"(cancelled|refusal|error*)"` and
passed the rest. grok's documented vocabulary for that field is `end_turn`,
`max_tokens`, `max_turn_requests`, `refusal` and `cancelled` — so a reviewer
that exhausted its output budget or its turn budget exited 0, wrote non-empty
JSON,
left no `suggestions.md`, and had that absence read as the clean verdict. **No
attacker is required**: a long branch is the ordinary way there, and a long
branch is when review matters most. One that wants it can buy it, and under the
two-clean-passes rule two such rounds end the loop.

It became an allow-list of one accepted value, `end_turn`. **That was still not
enough, and the reason is the more useful half of this entry.** A regex over a
serialised structure answers a different question from the one being asked: it
cannot tell a ROOT field from a nested one, so
`{"modelUsage":{"stopReason":"end_turn"}}` produced exactly one match, matched
the accepted value, and was read as a finished turn — a document whose turn
never ended, passing the check that exists to notice. Nor can `grep` establish
that the output is JSON at all, so a truncated write could be read as a verdict.

So the verdict is **parsed**: `jq` is asked for the root `stopReason` and the
answer is compared. That settles the shape, the nesting and the
well-formedness in one step, and collapses three distinct failures — not an
object, no root field, wrong value — into one question. `.stopReason` names a
field, where a regex only ever named a substring, so a mention inside the
review's own prose cannot be mistaken for the verdict either. The accepted value
stays pinned the way the client version is: a grok bump must re-verify it.

**Three corrections, and each one was the previous fix's blind spot** — a
deny-list that passed what it had not enumerated, an allow-list that could not
see nesting, and only then a parse. It cost `jq` as a stated precondition of the
helper, probed up front beside the Docker check on the same argument: a missing
tool should cost a second, not a round.

### The limit that read as a failure

**PR #117 round 6 spent a ledger slot on a review that never started.** The
usage-limit preflight exists so that a review the limits will not allow is
*skipped* (exit 12) rather than *failed* (exit 4), and its pattern did not match
what an exhausted prepaid balance produces: `API error (status 402 Payment
Required): Grok Build usage balance exhausted` is not `429`, not `quota`, and
not `(no|any) credits`. So the run reached three model calls and about 200k
input
tokens before dying, and reported "the review did not run" without saying it was
a billing state.

Both spellings are in now — the status code is stable, the prose is what a
provider change is most likely to reword. **A false positive here is the
expensive direction**: the pattern is matched against a probe's whole text, so a
bare `402` would also match a token count or a request id, and misreading one as
a limit reports a working reviewer as out of window and skips every round
silently.

**The first answer to that was `\b402\b`, and it was wrong**; what ships is a
status *context*, argued under *The suite that should have existed first* below.
The intermediate is named here rather than described as current, because this
paragraph specified it for two review rounds after the code had moved on — and a
reader reconciling the code to this file would have restored the very skip the
entry is about. Two statements in one entry cannot both be the design.

### The image that fetched an unverified installer

**Pinning a version is not pinning an artefact.** `.claude/sandbox/Dockerfile`
pinned the grok *client version* and refetched `https://x.ai/cli/install.sh` on
every build, executing it with no checksum and no signature — inside the one
image built to be a security boundary, which `grok-review.sh` then hands the
credentials the boundary exists to contain, with unrestricted egress. The
Dockerfile already made the argument for pinning; the integrity half was
missing.

**Pinning the installer would have been half a fix, and reading it is what said
so.** Grepping the script for `sha256`, `checksum`, `verify` and `gpg` returns
nothing: it performs no verification whatever of the 163 MB binary it downloads.

**The first version pinned both and was still not enough**, which a reviewer
established rather than this branch. It kept the installer, checksummed, and
hashed the binary *afterwards* — and the installer smoke-runs the binary before
that hash can be taken, as the reviewer user, with the network and a writable
`$HOME`. So a malicious artefact gets one execution in which to put the expected
bytes where the check will read them, and the check then passes and the image
ships. **A verification that runs after the thing it verifies has already
executed does not verify anything** — the same shape as the ledger publishing
its answer before its trust check failed, two files over.

So the installer is gone. The release artefact is fetched directly, its digest
checked before anything executes, and the few steps the installer performs on
Linux are done inline — read out of the pinned script rather than guessed: the
binary into `downloads/`, `chmod +x`, relative symlinks for `grok` and `agent`,
and the `[cli]` block in `config.toml`. `grok --version` is now the binary's
FIRST execution and runs only after the digest matched. An architecture with no
recorded digest **fails** rather than building unverified — the scaffold's rule
one tree over, that a tool refusing input it has never been shown beats one that
guesses.

The digests are trust on first use, and that is the point rather than a
weakness: from here a change to what x.ai serves for a pinned version fails the
build, and moving a pin is a reviewed diff, which is what
`Directory.Packages.props` and `global.json` are for. Both failure paths were
observed red — a wrong digest stops the build naming both values, and now does
so *before* anything is executed — and the amd64 artefact was cross-checked
against the md5 GCS reports in its own response header. The vendored layout was
verified against what the installer produced: the same relative symlinks, the
same `config.toml`, `grok 1.0.5` reported by the built image.

**The version moved to 1.0.5 in the same change**, because the file's own rule
is to track the host's client and the host had moved: a credential must mean the
same thing on both sides of the mount, which is the failure the pin was
introduced for. Verified end to end rather than assumed — the built image runs
`grok 1.0.5`, and a real call through the OAuth path the reviewer actually uses
returns `"stopReason": "end_turn"`, which is also the allow-list's own value
confirmed from inside the container that will run the reviews.

**That residual is closed, and how it closed is the lesson.** It was written
here as narrow and stated — the binary executes once and then fails the build —
and the reasoning was wrong in the direction that matters: the execution
*precedes* the check, so it can arrange for the check to pass. **Naming a
residual is not bounding it.** The bound has to be argued against someone who
gets to run first, and this one never had been. What replaced it is the option
this entry had already considered and declined as too risky to reimplement;
a reviewer pointing at the ordering is what made the trade obvious.

### The sweep guards that were narrower than advertised

Issue #75 collected five weaknesses from PR #73's review loops, sharing one root
cause: **each grant was documented by the operation it was added for rather than
by what its prefix admits.** Three are closed here, one was already closed, and
one is architecture.

**`Bash(mktemp:*)` was a filesystem write primitive.** `mktemp` takes an
arbitrary template, so the grant permitted creating an empty directory or file
anywhere the session could write, the checkout included — it could not write
content and could not clobber, so no source file was ever alterable through it,
but "the only mutations are the issues it files and the worktree" was false. A
prefix rule cannot constrain a template, so this needed a helper:
`git-worktree-detach.sh` creates the directory itself and prints it, its
interface drops from `<path> <commit>` to `<commit>`, and both sweeps dropped
the grant. Its shape check is now a tautology, which is the point.

**The `secsweep-??????` check was not a direct-child check**, though both
helpers' comments said so. A bash `case` does no pathname expansion, so `?`
matches `/` like any other character and `$tmproot/secsweep-a/bbbb` passed as
readily as `$tmproot/secsweep-abc123` — verified by running the issue's whole
table through a `case`. Prefix and length held; direct-childness did not. Both
helpers now compare `dirname` against the root and match the basename alone,
which cannot be talked past because a basename contains no `/`.

**`gh label create` is create-or-overwrite.** `--force` updates an existing
label's colour and description — `gh`'s own help says so — and `-R` unpinned put
that write in any repository, while the argument reaches that stage from a tree
that is prompt-injection input. It was held as two prose rules, which is a rule
a reader enforces and a finding can talk past. `gh-label-ensure.sh` leaves no
free parameter: one name out of a fixed six-entry case with its colour and text,
`--force` never spelled, and the repository resolved from the checkout rather
than named by a caller.

**Item 4 was already closed and item 5 is not a command edit.** The `Agent`
grant is pinned to one auditor type with every other registered type denied —
done when `/bug-sweep` landed, since the harness has no "only this type" allow.
Item 5 — the parent verifying while holding the mutation grants — needs either a
structured verdict the parent files on without composing a body from text it has
read, or the fan-out in a container. That is the same class of decision as the
container isolating the Grok reviewer, and it stays a named residual. What
narrows it further today is that two of its three mutations no longer take a
free parameter.

### The suite that should have existed first

**Six judgements the whole loop rests on had no test, five of which had already
shipped wrong.** `test_grok_helpers.py` grew across the review
rounds — every round but one added cases, and the count is deliberately not
restated here for the reason this file gives about counts, and each of those five defects is reproduced as one that fails against
the old behaviour: a gate only ever observed green is one nobody has established
is looking at anything. The sixth — that the label helper leaves no free
parameter a finding could steer — never shipped wrong; it is a grant closed by
moving it into a helper, and the suite is what keeps it closed.

**A figure and a list, in a file that already argues against restating either.**
Both went stale during this branch's own review loop and were corrected from
outside: the count when cases were added, the list when the label helper joined
it. They are here because an entry describing a suite is worth nothing if it
describes a different one — and the honest note is that neither was caught by
the person adding the cases.

**They are paired with positive controls, and those are not decoration.** A
negative case that passes because the pattern matches *nothing* is this
repository's most-repeated failure wearing a test's clothes — and this branch
supplied the worked example rather than merely citing one. The usage-limit
pattern used `\b402\b`, its negatives tested `47402` and `4021`, and neither
tested the number *alone* — so `"input_tokens": 402` matched, in defiance of the
comment sitting beside it, and every case stayed green. A false positive there
skips every review silently.

What ships requires a status *context* instead, and the controls follow the same
rule: `end_turn` is asserted to be accepted, an empty ledger to count zero, a
trusted reservation to count as spent, and the status anchor itself to match
`(status 402 …)` while missing `"input_tokens": 402`. Without that last one,
`47402` failing to match would prove nothing about whether the anchor ever
matches anything.

**It shells out to the same `grep -E` the scripts call.** Restating the patterns
in Python's `re` would be a second specification, and a hand-written double
cannot disagree with itself — this repository's own lesson from `StubCatalog`,
one artefact over.

**The patterns are declared once, together, away from the code that applies
them**, which is the `SOURCE_INPUTS` discipline the `deploy/**` gates arrived
at: a value a test asserts about has to be declared where the test can find it,
or
the test asserts about its own second copy. And because that proves only what a
pattern *matches*, the cases are paired with structural ones over the call
sites — every declared pattern has a use, every `exit 12` is guarded by the
declared pattern, and no second literal copy survives.

**A structural test is not a contract test, and the first version of the stdout
case was structural.** It asserted that two source strings existed, which proves
nothing about what a caller captures: an added debug `echo` recreates the defect
with the assertion green. It runs the real round trip now — detach at a real
commit, capture stdout as `security-sweep.md` does, assert one line and a usable
directory — and was confirmed as a negative by removing the redirection, which
fails it with `1 != 2` *and* fails its own cleanup, the original bug reproducing
in miniature because the polluted path is what the drop helper receives.

Two findings came out of writing it rather than out of the issues.
`git worktree add` writes "Preparing worktree" to stderr but `HEAD is now at
<sha> <subject>` to **stdout**, so the detach helper's first version returned a
commit subject followed by a path and the teardown failed with a `not an
existing directory` naming a whole commit message — **a helper's contract is its
stdout, so test what a caller captures, never what the code appears to print.**
And on this host an argv element crossing into `bash.exe` is re-parsed, so a
pattern containing `"` arrived with its quotes eaten and the suite reported a
working allow-list as broken; environment variables and stdin are not re-parsed.
That is the MSYS path divergence [`lessons.md`](lessons.md) already records,
one layer down — the argument rather than the path.

## The closure gate — landed inside PR #117, and the plan has no row for it

**No `PR-NN` heading, and that is the honest form rather than an omission.**
Appendix C's rows describe the blueprint's specified system; this is CI tooling,
and no gate here has ever had a row of its own — the licence gate rode PR-01 and
the pipeline gate rode PR-25, each inside a row about something else. This one
rode PR #117, whose subject is §9.6's saga, because the defect it closes had
just fired for the third time and the branch in front of it was the one being
merged.

**Two mechanisms answer "what does merging this close", and only one of them
is editable.** GitHub honours closing keywords in a pull request *body* and in
a *commit* body independently, and `gh pr view --json closingIssuesReferences`
reports the body only. So the one field a reviewer would check is blind to
half the answer, and both halves have failed here:

- **PR #112 closed nothing it claimed.** Its keywords lived only in the
  `| Closes |` metadata row. A cell boundary sits between the keyword and the
  reference, so GitHub was handed two cells rather than a pair — it is not
  declining to read a table. `closingIssuesReferences` reads `[]` to this day
  and three issues were closed by hand.
- **PR #116 closed two issues its own body disclaimed.** The review loop
  narrowed two claims and the description was rewritten to say one of them
  stayed open; the merge closed both anyway, out of commits written before the
  loop ran. Measured rather than recalled: that PR's commits carry
  `{30, 31, 32, 55, 56}` and its `closingIssuesReferences` reports
  `{31, 32, 55}`. The set difference is exactly the pair reopened by hand.

**So the description is reconciled to the commits, never the reverse.** A
description is editable and a commit message is not, which is why withdrawing
a closure from the body reads as sufficient and is not.

### Two comparisons, not three, and the third is the one to refuse

The gate compares what a pull request **says** — the table row — against what
merging it **does**, which is `closingIssuesReferences` together with the commit
keywords. It does **not** ask a commit to repeat a closure the description
makes. An issue the description closes and no commit mentions is the ordinary
case: the bare `Closes` line under the table is what fires, and a commit is
under no obligation to say it again. Adding that fourth pairing would make a
commit keyword *mandatory* — a rule nothing here states — and would fail correct
pull requests.

**The gate's own first sentence was that mistake.** It said the three
statements must agree, which reads as three pairs that all have to match, and
it was flagged from three separate sites before being rewritten rather than
annotated. A test now pins the absent comparison, because the symmetry
argument is what produced it the first time and will produce it again.

### Half the comparison is GitHub's parse and half is a regex

That asymmetry decides the failure mode, and it is why the suite is mostly the
parser. A regex matching too *much* makes the gate disagree with GitHub and
fail loudly, which is recoverable. A regex matching too *little* drops a
commit keyword; if the description omits it too, the two sets agree and the
gate reports a pass while the merge closes an issue nobody declared. So the
parser is deliberately literal — it matches inside backticks and quoted prose,
exactly as GitHub's linker does — and **anything keyword-shaped it cannot
resolve is reported rather than skipped.** A wrapper the strip does not know
(`~~#21~~`) is the case that proved it: before that fix the token matched
neither branch and vanished silently.

### One collection is paginated and one is preloaded, and only one needs a guard

`gh pr view --json commits` returns a single page, so a long pull request
hands the gate a prefix of its own history — and a keyword past the cut is
absent for a reason that has nothing to do with what the pull request says.
A list at or above the page size is therefore **refused rather than judged**:
a prefix of one page and a complete list of one page are indistinguishable
from inside.

**`closingIssuesReferences` is not exposed to that, and it briefly carried the
same guard on a symmetry argument.** Measured against `cli/cli` at v2.92.0:
`finder.go` dispatches to `preloadPrClosingIssuesReferences`, which loops on
`PageInfo.HasNextPage` until the collection is exhausted; commits get no such
treatment. A guard there would have refused every pull request with a hundred
linked issues while telling the reader to do the paginated fetch `gh` had
already done. **A false-refusal generator in a gate whose subject is not
trusting unchecked claims** — removed, with a test pinning the removal.

**A third way to read a short list turned up by being watched rather than by
being reasoned about.** Run seconds after a push that added a closing keyword,
`gh pr view` returned the commit list *without* that commit and the gate
reported a pass; the same command a moment later reported the problem. GitHub
had not indexed the push yet. A stale list is an unread subject with a clock
attached, and it fails in the silent direction exactly as a truncated one
does. The gate now takes `headRefOid` and refuses a commit list that does not
contain it — which also catches a page whose cut dropped the newest commit,
and is the stronger of the two guards for that case. It was observed on the
pull request that added it.

**Its absence from `REQUIRED_FIELDS` was green, and that is the second
lesson.** Deleting the new entry from that inventory failed no test, because
the field-presence case had been written once against one field rather than
once per field. A loop over `REQUIRED_FIELDS` would not have helped: it passes
whatever the list happens to contain, including a list an edit shortened.
**The subject of an inventory test is the inventory, so it has to be spelled
out beside it.**

### It went red on its own branch, and then did not close its own issue

Three live failures on PR #117 before the branch went green, and the first was
its own test fixtures: a commit body *quoting* `Closes ~~#21~~` while arguing
about wrappers. That is the hazard the prose warns about — GitHub's linker
does not read markdown, so a keyword discussed in an argument links exactly as
hard as a real one — firing on the branch that documented it.

**And the merge did not close the issue that specifies it.** The gate landed
mid-branch, the description was never given a bare keyword line naming it, and
the two comparisons were satisfied throughout because nothing claimed the
closure on either side. **A gate that catches disagreement cannot catch
silence**, and the issue was closed by hand afterwards — the defect in
miniature, committed by the change that fixes it.

---

## PR-32 — the marker inside the transaction, and a memorised migration name

**PR-32 pays the debt PR-28's own row names**, which makes it the first row in
Appendix C's *After the plan* section that a row above it predicted. §8.5's
`IdempotencyBehavior` releases its Redis claim on every exception out of
`next()`, and one of those exceptions is raised over work that **already
committed** — a `CommitAsync` that succeeded on the server whose acknowledgement
was lost. Releasing there frees the key for a command that ran, so the retry
writes it twice: the outcome the behaviour exists to prevent, arriving on the
one path it cannot see. The race has been recorded as knowingly open since
PR-09 and stated as an exception in §8.5's opening sentence ever since.

**The fix is a row in the command's own transaction**
([ADR-037](backend-architecture/adr/ADR-037-the-idempotency-marker-is-a-row-in-the-commands-own-transaction.md)):
`IIdempotencyMarkerStore` in `Common.Application`, `EfIdempotencyMarkerStore`
over the service's own `DbContext` in `Common.Infrastructure`, written inside
[§6.3](backend-architecture/06-cqrs.md)'s transaction and read at the top of it.
The Redis claim keeps its job — the fast, atomic exclusion that makes a
concurrent duplicate fail early — and the row is what makes the *ambiguous*
case decidable, which is the half Redis structurally cannot do.

### Holding the claim is not the fix — it only postpones the duplicate

The obvious repair is to stop releasing on the `catch` path: if the claim is
never freed, no retry can take it. §8.5's own release table already says what
that buys. **Every Redis entry has a TTL**, so a held key expires and the
attempt after that claims a free key and runs the command a second time — the
duplicate is moved to the retention boundary, not removed, and it arrives at the
least visible moment there is. It would also cost **every ordinary fault its
retry for a day**, which is a large availability price for a postponement: a
transient deadlock, a dropped connection, a validation exception thrown deeper
than it should be, all of them answered with "this key is taken" for
twenty-four hours.

A row has no TTL. It survives until something deletes it, and what deletes it is
a retention window this repository chooses — which is why
`RetentionPolicy.IdempotencyWindow` is the one window of the three with a
**floor**, and why the floor is read from `IdempotencyRetention.MarkerFloor`
rather than restated: a 24 written in two files agrees until one of them is
edited. That floor is the claim's `Window` **plus** `MarkerLeadAllowance`, and
the margin is not slack — two independent things put the claim's expiry after
the marker's, and both are invisible from the two numbers alone (#167, #168).

> **Both terms were closed at the source by PR-33 and the allowance is gone**,
> so `MarkerFloor` is now `Window` unchanged and a marker window equal to the
> claim's is admitted
> ([ADR-038](backend-architecture/adr/ADR-038-the-marker-and-its-claim-are-ordered-by-construction-not-a-margin.md)).
> Reconciled here rather than re-argued: the reasoning above is why the
> allowance existed, which is exactly what a reader tempted to reinstate it
> needs, and only the live claim about what the floor *is* has moved.

**So the release stays, and it is now a decision rather than a default.** The
retry it admits meets the marker, and is refused with
`CommandAlreadyCommittedException` before a handler runs.

### The scaffold had memorised the name of the last migration

`snapshot_from_designer` in `tools/new-service/new_service.py` hard-coded
`AddOutboxRetentionIndex` — the class name of what was, when it was written,
the last template migration. This PR adds a fifth. That literal then named the
**second-to-last** migration, so every anchor in the function missed at once and
the run stopped.

**It stopped loudly, and that was luck rather than design.** The scaffold's rule
is that every anchor must match exactly once and a miss raises `ScaffoldError`
naming the file, so the failure was the good kind — but nothing in the function
said it depended on being the last, and a hard-coded name that *had* matched
something would have rendered a service with the wrong snapshot in silence. It
derives the name from the file the caller already holds now, which is the same
fix shape as every other "a list drifts exactly as a number does" entry in this
log: the value is read from the thing it describes rather than restated beside
it.

### The build is the counterfactual for half of this, and only half

Two of the changes cannot be got wrong without the compiler saying so, and
saying so is worth writing down because it decides which tests were owed a
measured counterfactual.

- Deleting `idempotency.Claim(key)` from `IdempotencyBehavior` leaves an unread
  primary-constructor parameter, which is **CS9113** — an error under
  [ADR-019](backend-architecture/adr/ADR-019-warnings-are-errors-and-the-editorconfig-is-a-build-input.md).
- A `Claim` that assigns nothing is **CA2245** (assignment to itself) or
  **CA1822** (a member that does not use instance state), depending on how it
  is broken.

**And the restore that makes such a measurement possible is where the time
went.** A counterfactual is taken by putting the broken version back, running,
and putting the good one back again; restoring it with `shutil.copy2` restores
the file's **mtime** along with its bytes, so MSBuild saw nothing newer than the
outputs and rebuilt nothing, and the next `--no-build` run executed the assembly
compiled from the *broken* version. Five tests were reported as failing against
source that was already correct. This repository's existing rule is that a
counterfactual which does not rebuild reports the old behaviour as the new one;
this is the converse — the *restore* which does not rebuild reports the new
behaviour as still broken — and it is the more expensive direction, because the
red result reads as a finding rather than as a tooling artefact.

So *is the key published at all* has the build as its counterfactual, and no
test needs to reproduce it. What the compiler cannot see is **where** each call
sits — `Claim` after the successful `TryClaimAsync` rather than beside the key,
the marker read before `next()`, the marker write after the failure guard and
after §2.3's aggregate-count check — and those are exactly the assertions that
were observed red against the moved call. **The distinction matters because a
test whose counterfactual is the compiler is a test that would have passed
against every version of the code that exists**, which reads as coverage and
is not.

### It also closes #161, because one pull request is one row

A rendered service could not be committed: it carries credential-shaped literals
under its own name, and §15.1's secret scan reads the working tree. The scaffold
now **loads the real scanner**, runs it over what it has just rendered, and
appends one `path | rule | fingerprint | reason` line per distinct finding to
`.github/secret-scan/allowed-secrets.txt` — the seventh shared file it edits.

**The fingerprints have to be the gate's own.** A scaffold computing
`sha256(matched-secret)[:12]` itself would be a second implementation of which
substring each rule matches, and being wrong there is silent in the worst
direction: a fingerprint matching nothing is a stale entry, which the scanner
reports and fails the build on. A developer tool importing a CI gate is a real
coupling and it is the only shape with one implementation of the matching.

It writes nothing where `.github/secret-scan/` is absent, which is the case in
the scaffold suite's synthetic root — **a degraded path the suite cannot then
assert**, stated here and in the script's own docstring rather than discovered
from a green run.

### Left owed

PR-28's *second* residual is untouched: the stored payload still carries an
implicit schema for the whole retention, so changing an idempotent command's
result shape is still a migration.

And the refusal is a **409 and not a replay**, which costs more than it first
reads. Two paths reach it: the lost acknowledgement, where the attempt threw
before recording an outcome, and — the commoner one — a command that succeeded
and recorded its payload, whose Redis entry expired at `Retention` while the
marker lived on. So the response says the result is *no longer available*
rather than never recorded, because naming a cause would be wrong on the path
most callers take.

**That second path is a behaviour change nobody asked for, and it is the price
of the guarantee rather than an oversight.** While §8.5's promise ended at
`Retention`, a retry after the claim expired re-ran the command by design — the
expiry and the exception in that sentence were one fact, so removing the
exception removes the re-run with it. A caller retrying a succeeded command
between the claim's expiry and the marker's purge — six days on the shipped
windows — now meets a 409 where it used to get a fresh execution. Narrowing
`IdempotencyWindow` towards the claim's own length buys most of that back, and
the floor permits it down to `IdempotencyRetention.MarkerFloor` — the claim
**plus** `MarkerLeadAllowance`, five minutes. It does **not** permit equality,
and the review round that established this corrected a sentence here that
recommended exactly that. Two things put the claim's expiry after the marker's
and neither is visible from the two numbers: the marker is stamped inside the
transaction while the claim is re-armed after it commits, so the claim's window
starts later by the commit's tail ([#168](https://github.com/alexander-shamray/blueprint-backend/issues/168));
and the marker's age is the purging pod's clock against the writing pod's
timestamp, across three replicas ([#167](https://github.com/alexander-shamray/blueprint-backend/issues/167)).
Matching the claim leaves no margin for either, and a duplicate write arrives at
a boundary set by a retention setting. The default declines the narrowing
anyway, because the question the marker answers is "did this already commit" and
a wider window is the more conservative answer to it. A client that needs the
outcome reads the resource.

> **The floor permits equality again, and this paragraph is the third time that
> sentence has changed direction.** It recommended equality, a review round
> corrected it to the claim plus an allowance, and PR-33 closed both terms the
> allowance stood in for — so `IdempotencyWindow` may now be narrowed to
> `IdempotencyRetention.Window` exactly
> ([ADR-038](backend-architecture/adr/ADR-038-the-marker-and-its-claim-are-ordered-by-construction-not-a-margin.md)).
> **The two terms named above are what changed, not the reasoning about them**:
> the claim is no longer re-armed after the commit, and the marker is no longer
> aged across two pods' clocks, so "matching the claim leaves no margin for
> either" is true of a system that no longer exists.
>
> **The stretch it prices moved too, and in the direction that costs the
> caller.** "Six days on the shipped windows" was measured from a claim whose
> window started at the commit; the claim now expires earlier, so the stretch
> in which a succeeded command's retry meets a 409 rather than a fresh
> execution is *at least* six days. The narrowing advice is what buys it back,
> and it is now able to buy all of it.

---

## PR-28 — the section the plan forgot, and the registration nothing called

**PR-28 is not in Appendix C's original twenty-seven and had to be added to
it.** That is a different failure from a PR being late, and it is the one worth
carrying forward: §8.5 specified six types, §4.2's composition root showed the
line that wires one, §6.3's pipeline gave it a seat and §12 shipped a test only
its replay path could satisfy — while `grep -in "idempotenc"` over every row of
the delivery plan returned nothing. Five source files and four test files
deferred to "§8.5's PR". The deferral read as a schedule and was a dead
reference, and it stayed invisible because PR-12 had shipped
`RedisKeys.Idempotency`: a member named for the work, so a reader checking
whether the idempotency work had happened found something and stopped.

**A chapter that four other chapters cite is not covered by the plan merely
because the plan is finished.** Appendix C now has an *After the plan* section
so the next one has somewhere to go.

### `AddRedisConnections` had no caller, and that is what made this PR large

The behaviour resolves `IIdempotencyStore`, so it could not have started: PR-12
built §8's whole Redis stack — two keyed connections, HybridCache, the lock
factory, `RedisKeys` — and **wired it into no host**. So §8's deployment wiring
landed here too: both services' Infrastructure, Compose with `service_healthy`
dependencies, both Helm charts, both API fixtures and their two Redis
containers.

**The fixtures had said so in advance and nobody had read it as a task.**
`ServiceFixture`'s own summary carried *"The Redis containers of §12.4's full
shape still wait for the PR whose code reads those keys"* — a correct,
committed statement of what was owed, sitting in the file that would have to
change. **A comment naming what is owed is not a gate**, and this is the
cheapest possible demonstration: it cost nothing to write and nothing noticed
when the moment arrived.

### The gate this PR added found a defect in this PR

`Commands_carrying_a_CommandId_declare_IIdempotentCommand` went red on
`PublishProductCommand`, because the field had been added and the interface had
not. The solution compiled, every other test passed, and the command was
dispatched with no claim at all — which is exactly the silent state §8.5
describes and the reason the gate reads the *shape* of a command rather than
trusting its author. **A gate is worth writing when it can fail on the change
that introduces it.**

Each gate carries its own coverage half, on the rule `CLAUDE.md` states at the
top of its lessons list: an empty offender list is the same green whether every
command opted in or the selector stopped matching anything.

### `static abstract` was cheap here and would not have been later

§8.5 had argued for a declared operation discriminator and **declined to write
it**, on the grounds that a member on the opted-in interface is a change to the
contract every command implements. That reasoning is sound and its premise
expires: it is true only once the interface *has* implementors, and this PR is
where it is declared. Deferring would have meant paying the migration later,
against live keys, for a defect — a rename silently changing a key across a
rolling deployment — that costs a duplicate order.

**The generalisation: a deferral whose cost rises with time should be re-read
by the PR that first makes it cheap, not honoured because it is written down.**

### The store moved out of the service, and the chapter moved with it

§8.5 printed `RedisIdempotencyStore` in `Ordering.Infrastructure.Idempotency`.
It ships in `Common.Infrastructure.Redis` instead, because
`RedisDistributedLockFactory` sits one file over on the same connection with
the same keying and the same attribute — two per-service copies of one Redis
interaction drift the first time either changes. §4.3's one-assembly rule is
not in play, since every service already references that building block. The
chapter was amended rather than the code bent to it.

### What the chart gate could not have caught, and now can

Both Redis keys are required together even though only the coordination one is
read: `AddRedisConnections` is one call by design and reads both eagerly, so a
chart supplying one renders cleanly and produces a pod that will not start.

The subtler one is that the two `secretKeyRef` **keys must differ**. One key
copied onto both rows renders cleanly, passes every count assertion, and points
§8.5's claims at the `allkeys-lru` instance §8.1 exists to keep them off —
evicted under exactly the memory pressure that makes a duplicate write hardest
to reproduce. Counting rows cannot see it; comparing keys can, and the
assertion was observed red against that exact mistake before it was trusted.

### Left owed, and named rather than implied

The SQL-side marker that closes the lost commit acknowledgement is still §8.5's
debt (#113), and the stored payload still carries an implicit schema for the
whole retention — so **changing an idempotent command's result shape is a
migration**, on the terms a rename used to be and no longer is.

**PR-32 paid the first of those**, and the second stands. That the debt was
named here is what let the next PR be a row rather than a rediscovery.

---

## PR-26 — the contract a `.proto` cannot carry, and Pact's missing half

PR-26 delivered [Appendix C](backend-architecture/appendix-c-delivery-plan.md)'s
one optional row: a **consumer-driven contract** over
[§9.7](backend-architecture/09-messaging.md)'s single synchronous hop,
`Web.Bff → Catalog`. Six interactions, authored by the consumer, driven through
the BFF's own screen by `Web.Bff.Tests` and verified against the real service by
`Catalog.Api.Tests`. Plus [§12.6](backend-architecture/12-test-strategy.md)'s
second half, `ADR-023`, and no package at all.

**The row was conditional — "only if a consumer relationship becomes
contentious" — and the condition had already been met for some time.** Nothing
had noticed, because the thing that had gone wrong was the thing doing the
noticing. `StubCatalog` is a hand-written gRPC server standing in for Catalog in
the BFF's suite, and four of its behaviours had drifted from the service it
models: it filtered currency case-sensitively where Catalog does not, echoed the
*request's* spelling of the currency rather than its own stored one, formatted
amounts at the test's own scale rather than the column's `decimal(19,4)`, and
enforced no request ceiling whatever. The suite was green throughout all four,
which is not a surprise once stated plainly — **a double cannot disagree with
itself.**

**The sharpest consequence is not a stale stub, and it was measured rather than
argued.** `CheckoutEndpoints` compares a reply's currency to the request's with
`OrdinalIgnoreCase`, and the comment beside it says why: a producer's invariant
is not a consumer's guarantee. Because the stub echoed the request, that
comparison had never once been handed two spellings to reconcile. Tightening it
to `Ordinal` and running `Category!=Integration` over that project left **all
62 of its pre-PR container-free tests passing** — over a change that answers
500 to every lower-case currency a customer types, since Catalog projects its
own upper-cased column. **62 is the fast half, not the suite's 66**, and the
distinction is worth keeping rather than rounding up: the four it leaves out
drive the same endpoint through the same stub, so they would have been green
too — and they were not run, so they are not counted. So the
failure mode a double creates is worse than staleness: **a guard written for the
provider's real behaviour becomes untestable**, because the double never
produces the input the guard exists for. The counterfactual was run twice, once
against the branch's contract test (fails, exactly one interaction) and once
against `main`'s fast half (passes, all 62).

**Then the mechanism the plan named turned out not to reach the relationship the
plan made it conditional on.** Appendix C said Pact. PactNet 5.0.1 ships HTTP
and message pacts; Protobuf and gRPC are a *plugin*, and the .NET binding for
the plugin framework is `PactNet.Extensions.Grpc` — pull request 548 against
`pact-foundation/pact-net`, opened on 4 September 2025 and still open. Every
other relationship Pact could have expressed is uncontentious: the async
contracts travel as a shared assembly both ends compile
([§4.3](backend-architecture/04-solution-structure.md)) and §12.6 already
round-trips each one, the gateway is a reverse proxy with no semantic contract,
and the BFF's own HTTP API has no consumer in this repository. **The plan was
written against the tool's reputation rather than its surface**, which is the
lesson [`lessons.md`](lessons.md) now carries: a capability present in a
project's Rust core,
its JVM binding and its marketing is not thereby present in the one language
this repository compiles.

The out-of-band route was priced and refused. It needs `pact_verifier_cli` and a
platform-specific plugin binary installed into `~/.pact/plugins` — neither a
NuGet package, so `Directory.Packages.props` cannot pin them and the licence
gate, which reads that file and Appendix B as text ahead of the build, would
never see them. Pact's own documentation also rules out `WebApplicationFactory`
for provider verification, because its Rust core makes real TCP calls, so
Catalog's suite would have needed a second hosting shape as well.

**So the property was taken and the machinery declined**, which
[ADR-023](backend-architecture/adr/ADR-023-the-consumer-driven-contract-is-a-linked-file-not-pact.md)
records. What makes a pact worth having is that **one artefact is authored by
the consumer and verified against the provider**; the broker and the wire format
are how that property is shipped across a *repository* boundary, and this is a
monorepo. `pricing.proto` already makes the same argument one level down — one
file, two generated halves, linked rather than referenced — so the semantic
contract is shared exactly as the syntactic one is. The `.proto` is Catalog's
because Catalog serves the RPC; `PricingContract.cs` is Web.Bff's because only a
consumer can say what it needs.

**Three smaller things were decided by building it.**

The stub's **single currency for the whole catalogue** made the most interesting
interaction inexpressible. A basket holding one product priced in the requested
currency and one priced in another is exactly the shape that fills
`QuoteResponse.Unpriced` while still totalling the rest, and a single-currency
stub answers such a request either wholly or not at all. The currency moved onto
the row, where Catalog keeps it.

The request ceiling needs **both edges or neither**. A provider that quietly
lowered `MaxProductIds` still refuses a hundred and one; one that raised it
still serves a hundred. So one interaction requires a basket *at* the ceiling to
be served and the next requires one *past* it to be refused with
`InvalidArgument` — the status `UpstreamExceptionHandler` turns into the
caller's 400. A change in either direction now fails verification, deliberately:
a provider free to change a number its consumer wrote down has a contract nobody
is holding. That is not a contradiction of `CheckoutEndpoints` holding no
ceiling of its own — production code with a copy would refuse requests Catalog
would have served, where a contract with one is the consumer saying which number
it relies on.

And [§4.1](backend-architecture/04-solution-structure.md) calls
`Platform.IntegrationTests` "the only suite that references every service",
which reads as the obvious home for a cross-service contract and is the wrong
one. **A provider verification needs the provider running** — a migrated SQL
Server, a broker, the real host over them — so homing it there would buy a sixth
project a container set ([§12.4](backend-architecture/12-test-strategy.md)'s
stated price) in order to run six tests that `Catalog.Api.Tests` runs over the
`ServiceFixture` it already has. §12.6's existing suite stays where it is; those
tests are about the *shape* of the contract assembly, and this is about one
service honouring one consumer.

**The verification was observed red twice before it was trusted**, on the rule
this repository applies to every gate. Formatting the provider's amount under a
comma-decimal culture failed four of the six interactions — the two that survive
are the ceiling pair, which price nothing and therefore have no amount to
mis-format. Lowering `GetPricesValidator.MaxProductIds` to 50 failed exactly
one, the at-the-ceiling interaction, and nothing else. A green-only gate would
have established neither that it reads the wire nor that both edges do work.

**What it does not do is stated rather than implied.** It covers one
relationship, because the platform has one synchronous hop by §9.7's design; a
second would be the same conditional judgement again rather than an automatic
second contract. And it does not cross a repository boundary — extract the BFF
and this file becomes something that has to be published, at which point Pact is
the answer after all. The decision is reversible and its trigger is nameable,
which is the most that can be asked of one taken against a plugin that may merge
next month.

---

## PR-25 — the staged pipeline, and a canary that had no way to be measured

PR-25 delivered [Appendix C](backend-architecture/appendix-c-delivery-plan.md)'s
CI row: [§15.1](backend-architecture/15-cicd-deployment.md)'s path filter and
per-service image build, its `UT → IT` split, the quality gate that says each
stage ran, and [§15.5](backend-architecture/15-cicd-deployment.md)'s canary
with automated rollback. It closes the roadmap's M6.

**Decision — the canary is replica-weighted, and the mechanism was a decision
nobody had taken.** §15.5 specified the behaviour in four sentences and named
no machinery, so building the rollout was what forced the choice;
[ADR-022](backend-architecture/adr/ADR-022-the-canary-is-a-second-release-weighted-by-replicas.md)
records it. **The disqualifying argument is about this platform's topology and
not about taste**, which is worth keeping because the rejected option is the
one every reader reaches for first: an nginx-style ingress canary annotation
splits traffic at the Ingress, and this platform has exactly one — the
gateway's. Everything behind it is dialled by Kubernetes Service name from
YARP's route file and from `PricingHop.cs`, both of which hold those names as
literals *on the stated grounds that the host is the Service name*. So an edge
weight can canary the edge and **cannot canary Catalog or Ordering at all**. A
mesh or a rollout controller would give exact weights and is the better answer
at scale; it is also a cluster-wide component with its own upgrade cycle, added
to a platform whose entire deploy surface is `helm upgrade` — and one that
would have to exist before any of this could be tested, against a cluster that
does not.

**Finding — `service.version` is registered, exported, and constant, which is
§13.6's trap in a new place.** The analysis has to tell the canary's series
from the stable one's, and the resource already carries a version attribute, so
the design was finished before it was checked. `BuildInfo.Version` strips the
source-revision suffix **deliberately** — its own comment argues that a value
changing every commit turns one series into thousands — and
[§4.4](backend-architecture/04-solution-structure.md) pins no assembly version,
so every host in this platform reports `1.0.0`. The attribute exists, is
exported, and separates nothing. `deployment.track` through
`OTEL_RESOURCE_ATTRIBUTES` replaced it, and **no production C# changed**,
because the SDK's own resource builder already honours that variable — which is
a claim of exactly the kind that is true of a different overload, so it is
established by a test against an exported resource rather than by reading the
documentation.

**Finding — §15.5's first rung is not expressible at the replica count §15.3
ships, and rounding would have hidden it.** The served share is
`canary / (stable + canary)`, so at `replicaCount: 3` a single canary pod
already takes 25% — five times the 5% the chapter asks for. `canary.py plan`
therefore treats the requested weight as a **ceiling** and refuses where even
one pod overshoots, naming the 19 stable replicas that would satisfy the step.
**The alternative was one line and is the whole failure mode**: round to the
nearest expressible weight, and a step labelled 5% serves five times the blast
radius anybody authorised, in a rollout whose logs say 5%.
`autoscaling.maxReplicas` is 20 on the three service charts, exactly 19
plus one canary — the
smallest configuration in which the first rung is real is the largest the chart
allows, and neither number was chosen with the other in mind.

**Finding — the weight arithmetic was wrong in floating point at the one input
the ladder starts from.** It read `ceil(stable * f / (1 - f))`, and
`19 * 0.05 / 0.95` evaluates to `1.0000000000000002`, so `ceil` bought two pods
and served 9.5%. Not an exotic input — the *first step of the default plan*.
Every quantity involved is a count of pods or a whole percentage, so the exact
answer was available throughout, and the fix was to do the comparison in
integers. **What found it was the test asserting that the function naming the
required replica count and the function checking that count agree**, which then
turned out to be catching two bugs at once: they had also been written to
opposite rules, one taking the smallest canary at or above the weight and the
other the largest at or below.

**Decision — the coverage threshold PR-25 was entitled to add, PR-25
declined.** [§12.9](backend-architecture/12-test-strategy.md) and
`docs/testing.md` both read "a threshold that fails a build is PR-25's quality
gates", which defers rather than promises — and the same sentence argues that a
diagnostic wired to a build failure stops being read and starts being
satisfied. Both cannot be honoured. What the chapters *positively* define as
this PR's gate is the other one: "assert a floor on each stage's count rather
than trusting a green exit". So the threshold is refused on §12.9's own
argument, the three sites saying it was owed now say it was declined, and a
later change arguing for one will be arguing against that paragraph rather than
filling a gap it left open.

**Finding — a flag added for one gate moved another gate's input.**
`--logger trx` is how the stage gate counts, and adding it changes where the
coverage collector writes: each stage then leaves the run's merged attachment
**and** one partial attachment per test project — eight files for one stage
here, three of them empty. `domain_coverage.py` asserted exactly one file, and
that assertion was correct when it was written. **When one step's output feeds
another, adding a flag to the first is a change to the second**, and nothing
about the flag says so.

That merge then had to reproduce the collector's own arithmetic rather than
invent one, or the printed figure would have moved for a reason nobody could
name. Measured: `lines-valid` counts the lines under
`class/methods/method/lines` and **not** the ones under `class/lines` — 308
against 247 on this repository, and only the first reproduces the tool's own
totals. Hits merge with `max` over that key, which makes reading the same
attachment twice idempotent; summing would have grown the figure with the
number of test projects. The union is worth having on its own terms: 253 lines
from the unit stage, 192 from the integration stage, **257** from both, so four
lines are reached only by a test that needs a container.

**Decision — the reporter gains a suite and the argument for it not having one
still stands.** `domain_coverage.py` said in its own docstring that it has no
tests deliberately, because it gates nothing and asserts nothing about the
repository. Both halves remain true. What arrived with the merge is
*arithmetic* — a key that collides, a `max` that should have been a sum, a
partial attachment counted as a whole — and none of those fails loudly while
all of them move the number. A figure that is wrong but plausible is worse than
no figure, because it is the one people read.

**Decision — the Deployment's selector gains a track label, and that field is
immutable.** Two Deployments sharing a selector each count the other's pods as
their own and scale them away, so the track has to be in the Deployment's
selector — and it must **not** be in the Service's, or the Service would route
the canary nothing. One label, in exactly one of the two places. Adding it to
an installed release is a delete-and-recreate, so this is free today, because
nothing anywhere has ever installed these charts, and would be a downtime
window if taken later. **That is the argument for taking it now rather than
when a canary is first wanted**, and it is the same shape as PR-23's naming
decision: a field a Deployment will never let you change is one to get right
before the first install, not after.

**The gates were observed red before they were trusted**, on this repository's
standing rule. The chart gate's load-bearing new assertion — that a canary
pod's `app.kubernetes.io/name` still matches the stable Service's selector —
was run against a deliberately broken helper that gave the canary its own name,
and failed on all four charts; a canary with its own selector name runs,
reports healthy, serves nothing, and is promoted on an analysis of no traffic.
Every check in `.github/pipeline-gate/` has a negative case for the same
reason, and three of its tests contain no defect at all: their subject is an
empty pattern list, an empty matrix and an empty stage, each a state in which
every other check passes while reporting a complete inventory it never read.

**What is NOT established, and is named rather than implied.** The rollout has
never reached a cluster — no environment, no kubeconfig, no registry — so
`deploy.yml` is `workflow_dispatch` only, because a deploy on `push` would fail
on every merge and a pipeline red by design trains everyone to ignore it. The
deciding is tested; the acting is four commands nobody has run. And the premise
underneath the whole mechanism is unmeasured: kube-proxy spreads
**connections**, not requests, so keep-alive, HTTP/2 multiplexing to the gRPC
listener, or a client that opens one connection and holds it will each
under-deliver the weight. Nothing short of a cluster can measure that, and
ADR-022 records it as owed.

---

## PR-24 — the runbooks, and the four alerts that could not fire

PR-24 delivered [Appendix C](backend-architecture/appendix-c-delivery-plan.md)'s
ops row: [§13.9](backend-architecture/13-observability.md)'s runbooks,
§13.6's per-lane outbox alerts, `docs/secrets.md`, §13.8's dashboards as code,
and §13.7's k6 SLO run. It also built the code those alerts read —
`OutboxMetrics`, `IOutboxStats`/`OutboxStats` and `MetricsInitialiser` — which
§13.6 had specified since PR-14 deferred it by name and nobody had written.

**Decision — the alerts split into two files, and only one of them is
loaded.** Writing §13.6's conditions out as Prometheus rules established
that **four of them read an instrument nothing publishes**: the saga age and the
review queue have no gauge over their tables, `orders.placed` waits on
`OrderMetrics` and §6.6's `OrderSummaries` projection, and the cache ratio waits
on **an instrument and a consumer both** — §13.2 registers the HybridCache
meter, `Microsoft.Extensions.Caching.Hybrid` 10.0.0 publishes no meter at all
(EventCounters, via `HybridCacheEventSource`), and no host called
`AddRedisConnections` either. **The consumer half closed with PR-28 and the
alert did not move**, which is the correction below arriving in fact rather
than in argument: the instrument was always the binding constraint.

**That last row was first recorded here as owed a *consumer* rather than an
instrument, and the correction is kept rather than overwritten**, because the
mistake is the more useful half: the visible absence — nothing calls the
helper — was taken for the cause, and a gate was built on it, observed red and
removed once the package was actually read. See the fourth finding below.

**Why.** §13.6 spends a callout on exactly this: *"Two of the alerts in this
document were written against signals that did not exist, and both looked
correct: the dashboard is empty either way, whether the system is healthy or the
metric was never published."* Shipping every rule as loaded would have
made that sentence true again, in files, at the moment it was being quoted.
A rule that cannot fire is not a weak alert — it is a silent one, and silence
reads as health.

**Consequences.** `platform-alerts.yaml` holds the rules whose signal exists;
`awaiting-signal.yaml` holds the four that cannot fire and is not loaded. Every
runbook exists either way, because §13.9 asks for the procedure to be written
when the alert is created and every query in those four reads a table that
exists today. The cost is a second file to notice, paid down by the gate below.

**Decision — the gate asserts the awaiting file's metrics are published by
*nothing*.** Not merely that the loaded file's are published by something.

**Why.** A list of known-missing things that nothing re-checks becomes a list
nobody ever acts on. Asserting the negative makes the list **self-clearing**:
the day somebody ships `OrderMetrics`, `check.py` goes red and names the rule to
move into the loaded file. This is `CLAUDE.md`'s most-repeated lesson — a gate
whose subject is what it is *looking at* — applied to a deferral rather than to
a selector.

**Consequences.** Both directions were observed red before the gate was
trusted, along with the runbook pairing both ways, a typo'd metric in a loaded
rule, and a workflow filter that stopped covering a declared input. Five red
observations, five green.

**Decision — `check.py` declares its own `SOURCE_INPUTS` and asserts both
workflow triggers cover them**, copying `deploy/helm/smoke.sh` on day one.

**Why.** That list drifted three times in the Helm tree before it was declared
once beside the reads. There was no reason to rediscover it.

**Consequences.** The observability workflow filters on `src/**`, because
deciding whether an alert's signal exists means reading every instrument
declaration in C#. That is a broad filter for a gate measured in seconds, and it
is the honest one: a narrower filter over "files that look like metrics" is a
heuristic that fails silently, which is the whole category of defect this PR
kept finding.

### Four things were found by building it

**§13.6's `MetricsInitialiser` did not compile, two ways.** Its primary
constructor's parameters are named `_`, `__` and `___` and never read — three
**CS9113**s, and the discard-looking names do not escape it, because `_` in a
primary constructor is an ordinary parameter and not a discard. Its
`StartAsync` spells the token `ct`, which is **CA1725** against
`IHostedService`. Both are errors under
[ADR-019](backend-architecture/adr/ADR-019-warnings-are-errors-and-the-editorconfig-is-a-build-input.md)
and both were met by changing the code, adding no fourth suppression.

**The rule a reader expects to fire on those names does not.** CA1707 governs
underscores in identifiers and is disabled only for test projects — and it
passes on `_`, `__` and `___` as parameter names. An earlier draft of this
PR's comment asserted the opposite and was corrected by running it. **A
plausible attribution of an error to a rule is not a measurement of which rule
fired.**

**§13.6's `OutboxStats` reached for a scope it does not need.** The chapter
said it *"owns a scope per call rather than holding a `DbContext`"* — the right
concern, applied to the wrong dependency: §6.5's `IDbConnectionFactory` is a
**singleton** holding a connection string, and §4.2's own sample says so. The
scope was ceremony around a resolution that returns the same instance either
way. **A guard against the wrong dependency shape is not evidence about which
shape you have.**

**§13.6's saga alert excludes a state that does not exist.** The condition
excludes a saga *"awaiting despatch"*, and `OrderFulfilmentSaga` had four
states at the time — `AwaitingStock`, `AwaitingPayment`, `Confirmed` and
`Compensating`; #126 has since added `AwaitingConfirmation`, and the finding is
unchanged because none of them is called `AwaitingDespatch`.
The three-day despatch timeout is armed on the transition **into `Confirmed`**,
because the order is confirmed and now waiting on Shipping. A label selector
spelled `AwaitingDespatch` would match no series, exclude nothing, and page on
every healthy confirmed order an hour after payment — a filter that reads as a
safeguard and behaves as a pager, which is §11.4's ownership-guard finding in
another vocabulary. **Prose describing a state and code selecting one are
different acts; read the enum, not the sentence.**

**The gate's own instrument reader missed every gauge on its first run.** The
pattern required `Create…<T>(`, which every `CreateHistogram` and
`CreateCounter` in the corpus has and which `CreateObservableGauge` does not —
it infers its type argument from the callback. The three types this PR added
were therefore invisible to it, and it reported four correct alerts as having
no signal. It failed loudly because the answer it produced was checkable
against a fact the author knew; the same bug in a gate asserting a *negative*
would have passed. **A pattern one token too strict covers less than it claims,
and which way that fails is decided by what the gate asserts, not by how wrong
the pattern is.**

### What this binds

- **A new alert is three artefacts, not one**: a rule, a runbook, and a signal
  something publishes. `check.py` refuses any two of the three.
- **`OutboxDispatcher.MaxAttempts` is public**, because §13.6's abandoned-rows
  gauge counts exactly the rows §9.4's claim skips. One declaration, two
  readers — a second copy would stop agreeing the day somebody tunes it.
- **`OrderMetrics` joins `MetricsInitialiser` in the PR that adds §6.6's
  `OrderSummaries` projection**, and nobody has to remember: the convention
  test reads the container's registrations, so an unforced metrics type fails
  the build the day it is registered.
- **`docs/secrets.md` carries the procedure and never the inventory.** §15.4's
  table is the inventory and wins any disagreement — `docs/testing.md`'s
  relationship to §12, one chapter over.

---

## PR-23 — the charts, and a name the platform already depended on

PR-23 shipped [§15.3](backend-architecture/15-cicd-deployment.md)'s charts: a
library chart holding every template once, three deployables that are values
plus one-line includes and a fourth — the gateway — that adds one template of
its own, an umbrella,
[§7.4](backend-architecture/07-persistence.md)'s migration hook, and
`deploy/helm/smoke.sh` behind the second path-filtered workflow of §15.1. Ten
of its decisions bind what comes after — three of them found by review rather
than by building, and marked as such.

- **Helm's `fullname` convention would have broken the platform's routing, and
  nothing in the chart could have shown it.** The idiom is
  `{{ .Release.Name }}-{{ .Chart.Name }}`,
  and §10.2's route file resolves `http://catalog-api:8080/`
  while §9.7's pricing hop resolves `http://catalog-api:8081` — both literals
  in source, both arguing *on the record* that the value does not vary because
  "the host is the Kubernetes Service name". A release-derived name makes that
  sentence false the moment the umbrella installs the same workload under its
  own release, and the failure is a 502 at run time rather than a template
  error. So `workload.name` is a required value, the Service takes it verbatim,
  and **the selector carries nothing release-scoped**, because a selector is
  workload identity rather than release bookkeeping: these pods are found by
  the same name their callers dial, and a Deployment will never let that field
  change afterwards. The first justification here was the
  standalone-to-umbrella migration, and Copilot killed it — Helm rejects that
  adoption on ownership before the API server's immutable-selector check is
  reached. The conclusion outlived its argument, which is worth recording
  rather than quietly keeping. The gate that keeps this true reads the source
  files that hold those literals and asserts every rendered Service is a name
  one of them dials — and, since Copilot's third round, that Catalog is
  listening on the ports its own Service forwards to. The CI filter names each
  of those paths, which is the one place a `deploy/**` workflow reaches outside
  its own tree.
- **A gate cannot fail on a file that is not there, and this is that lesson at
  its earliest point.** "The gateway renders no migration Job" passed against a
  gateway that declared `image.migrator: gateway-migrator` — because the
  gateway chart carries no migration template at all, so the values key the
  comment beside it credited was never consulted by anything. It is not a gate
  that stopped covering a surface; it is one whose subject never existed. The
  assertion is now about the **agreement** between the two halves — a chart has
  a migration template exactly when its values name a migrator image — which
  fails from either side. Of every deliberate defect run through that gate it
  is the **one** that failed to turn a green run red, and it was found by
  running them rather than by reading it again.
- **§15.4's two Redis rows were required against a solution where nothing
  reads them**, and the consequence is worse than the over-supply that table
  already warns about. Supplying credentials no code path sends merely
  provisions something to rotate; a `secretKeyRef` naming a Secret nobody
  created is a pod stuck in `CreateContainerConfigError` and a service that
  never starts. No host called `AddRedisConnections` at the time, so both rows
  were made conditional on the consumer existing — a condition PR-28 then met
  for Catalog and Ordering, which is what put the two keys in the charts. The
  rule that resolved it is §14.1's,
  applied one deployment target over — **a key joins when a host's code reads
  it** — and it is the same rule that keeps the charts' environment identical
  in shape to the Compose blocks'.
- **`terminationGracePeriodSeconds` had a rule and no number, and the number is
  not free.** §15.3 requires the grace period to exceed the longest in-flight
  operation. The longest one is not per-service: `HostOptions.ShutdownTimeout`
  bounds the whole drain and defaults to **30 seconds** — measured on the
  pinned SDK rather than read off a documentation page, with nothing in this
  solution overriding it — and `ServiceOptions.OperationTimeout` (20 s) sits
  inside that window. Kubernetes' own default is also 30, which is the trap:
  **30 is not a margin over 30.** A pod at the default is `SIGKILL`ed at the
  instant the host would have finished draining, and nothing logs it.
- **A measurement taken through a tool that normalises reports the absence of
  the defect it was taken to find.** Go's template engine copies bytes through,
  so a CRLF template renders `path: /health/live\r` and the smoke's
  `$`-anchored greps match nothing on a Linux runner. Run here, the same
  mutation was **green** — MSYS `grep` treats CRLF as the line terminator and
  strips the CR before matching — so the first measurement said the hazard did
  not exist. Only reading the rendered bytes settled it. `.gitattributes` now
  pins `deploy/helm/**` to LF, on the same argument its `*.cs` paragraph
  already makes, and the paragraph records how the answer was nearly missed.
- **A rollout checksum has to cover every ConfigMap the pod mounts, and the
  narrow one is how that was learned.** Changing a ConfigMap changes nothing a
  running pod reads, so the pod template carries a hash that moves with the
  values — otherwise a config-only deploy (§15.1) reports success and rolls
  nothing. Hashing the *rendered ConfigMap* looked right and was not: the
  gateway renders a second one from its own template, so `cors.origins` and
  `ingress.trustedNetworks` — the two keys most likely to be edited without a
  rebuild — rewrote a mounted object while the annotation stayed
  byte-identical. It now hashes the whole of `.Values`, which over-triggers on
  keys the container never sees and is the safe direction. **Found by writing
  the assertion, not by reading the template**, which is the argument for
  writing the assertion.
- **A capability is a fact about the code, not an environment value**, and the
  charts modelled six of them as free booleans. `Catalog.Infrastructure` always
  resolves its connection string and always registers MassTransit; `Web.Bff`
  always binds `ServiceIdentityOptions` with `ValidateOnStart`. So
  `database.enabled: false` on Catalog is not a smaller deployment — it is a
  clean render and a pod that will not start. Helm has no immutable value, so
  the guard is **coherence**: a chart carrying the settings for a capability
  may not disable it, and the gate additionally checks each committed chart
  against the code it deploys. A *whole* override at deploy time
  (`--set` clearing both halves) stays outside a render-time gate's reach and
  is named as a residual. A `chart:` capability block is the better schema and
  is owed.
- **Validate a derived name; never truncate it.** The migration Job's name
  embeds the tag, and `trunc 63 | trimSuffix "-"` was a guard that could
  produce the thing it guarded against — a cut landing on a dot, which trimming
  a hyphen never touched, and two tags colliding on one name. The derived name
  is checked whole and a tag that overruns is refused, which is also why
  `app.kubernetes.io/version` stopped truncating: a cut version label names a
  tag no registry has.
- **The migration pod must not be selectable by the Service it migrates.** Its
  pod template carried `commerce.labels`, which contains the Service's
  selector verbatim — so for the length of every hook a pod with a database
  connection and no HTTP listener was a live endpoint, and inside the
  PodDisruptionBudget. The Job *object* keeps the ordinary labels, because
  object labels are not what endpoints are computed from.
- **`Chart.lock` and `charts/` are generated, not source.** `file://`
  dependencies resolve from disk, so the lock pins nothing a remote repository
  could move and the tarball is a binary copy of a directory two levels up.
  Committed, the lock would be a second copy of a version `Chart.yaml` already
  states — the drift the one rule exists to close — so both are ignored and
  `helm dependency update` is the whole of the setup, run by `smoke.sh` and by
  the CI job before anything else.

---

## PR-22 — the rest of §4.2, and a category that cannot drift

PR-22 put the last of [§4.2](backend-architecture/04-solution-structure.md)'s
dependency table behind gates — the cross-service clause three of its five
rows carry had no test, and the Migrator row had none of any kind — split the
suite on
`Category=Integration`, added `docs/testing.md` and started reporting
domain-layer coverage. Six of its decisions bind what comes after.

- **A gate the scaffold copies cannot be keyed on a name the scaffold
  rewrites.** The obvious instrument for "no service references another
  service's projects" is a list of §4.1's six service names, and it is wrong
  here for a mechanical reason: `new_service.py` applies its patches and *then*
  renames every casing of the template's name, so a list naming `Catalog`
  reaches the new service with `Catalog` **replaced** rather than joined —
  dropping the one service a scaffolded service is most likely to reference by
  accident. No spelling of the patch survives that, because the rename is what
  the patch output is fed through. So the gate asks a measured question
  instead: **every package this platform pins is strong-named and none of this
  repository's own projects is**, checked across all ten service assemblies.
  `Dapper` is the single unsigned package in the graph and is named as the
  residual. A second one would be misread as first-party and fail the gate —
  which is the direction it has to fail in, because the failure names an
  assembly nobody expected and the alternative predicate would have opened a
  hole silently. The rule then covers Inventory, Payments, Shipping and
  Notifications before any of them exists, which no list would have.
- **§4.2's table has two kinds of row, so it gets two kinds of gate.** A row
  saying what a project *may* reference is an allow-list and gets an allow-list
  over `GetReferencedAssemblies`; a row saying it may reference any package
  cannot have one and gets a named deny. Picking by the row rather than by
  taste is what keeps a gate from contradicting the sentence it enforces —
  a full allow-list on `*.Infrastructure` would have been the strongest
  instrument available and would have flatly denied the "any package" the table
  grants it. **The migrator is the row that most wanted this**, because its
  must-not is a sentence rather than a list — *anything it does not need to
  apply a migration* — which a deny-list cannot express at all.
- **A pre-granted exemption for a class that does not exist is a hole, not a
  provision.** §4.2's composition-root rule read "only `Program.cs` **and
  host-level `*ServiceCollectionExtensions`**", and the gate had never
  implemented the second limb; no host has such a class. Both directions were
  available and the prose was narrowed to the code rather than the gate widened
  to the prose. The exemption is the whole of that gate's trust, and its
  companion test — *the composition root is the only thing exempted* — is only
  meaningful while the exempted set is small enough to hold in mind. A host
  that genuinely wants a registration extension may have one; what it does not
  get is a licence written before it existed.
- **A category is the opposite of a skip, and this repository had refused them
  together.** `CLAUDE.md` said the container tests were "neither skipped nor
  categorised", on one argument that only applies to the first: a skip on a
  missing daemon fails open, so CI goes green on a runner whose Docker broke. A
  category decides which *stage* runs a test and never whether it may be
  absent. **Where it goes is what makes it undriftable**: the trait is declared
  on the `[CollectionDefinition]`, so joining the container collection *is*
  carrying the category — there is no per-class attribute to forget and
  therefore no reflection gate owed to check that nobody did, which is the
  first time this repo has closed one of these by construction rather than by
  adding a second test. xUnit v3's propagation was measured before the design
  was trusted: 10 and 71 of 81 on one assembly, 614 and 164 of 778 across the
  solution, with no third state.
- **"No container starts" is a claim about a run, so it was measured — and the
  first attempt to measure it proved nothing.** Pointing `DOCKER_HOST` at a
  dead endpoint and watching the fast half pass looked conclusive and was not:
  Testcontainers ignored the variable on this host, and the *integration* half
  passed against the real daemon under the same override. What settled it was
  `docker events --filter event=create` over the window, reporting nothing
  against a probe that captured a control container started beside it. **A
  green run under a broken override reads exactly like a green run under a
  working one**, which is the same shape as this repository's vacuous-gate
  failures one layer out.

- **Measuring a layer changes it, and the change was invisible on the machine
  that wrote the measurement.** `coverage.runsettings` instruments the Domain
  assemblies and nothing else — which are exactly the assemblies §4.2's Domain
  gates read `GetReferencedAssemblies` on. On the Linux runner an instrumented
  Domain assembly reports a `netstandard` reference no source line can produce,
  and **both** Domain gates went red on the first CI run that collected
  coverage. It does not reproduce on Windows: the same collector leaves
  `Ordering.Domain.dll` byte-identical, checked by hashing it either side of a
  run, so every local run — Debug, Release, with the collector and without —
  was green on a defect CI found immediately. **A green local suite is a claim
  about one machine**, and this is the sharpest instance of it the repository
  has: not a stale artefact, but a platform where the instrumentation mode
  differs.

  The one-line fix was to admit `netstandard` to the allow-list, and it is the
  wrong one — an architecture rule relaxed everywhere, for ever, and in every
  service the scaffold renders, to accommodate a test tool. CI runs the gates
  first and uninstrumented instead, and collects coverage over the complement;
  the two filters are exhaustive and disjoint, so the counts still sum to the
  suite. **If a change needs one of those gates relaxed, the gate is probably
  right** — this is that rule meeting a case where relaxing it would have been
  easier and nobody would have noticed.

Two smaller things are worth carrying. **Coverage is reported and never
gated** — §12.9 calls it a diagnostic, and a diagnostic wired to a build
failure stops being read and starts being satisfied; the threshold is PR-25's.
**PR-25 declined it**, on the first half of that same sentence, so what this
entry deferred is settled rather than still owed — see PR-25's block above.
The reasoning here is left exactly as it was written, and the outcome is
recorded beside it: that is the log's rule about a live claim restated inside
an argument, and this is its second instance after PR-10's compose timeout.
The filter is a *pattern*, `.*\.Domain\.dll$`, so every later service's Domain
joins it the day it exists rather than waiting for someone to edit a list. And
the collector is the one `Microsoft.NET.Test.Sdk` already carries, so the
figure cost no package and Appendix B no entry — measured at **83.4%** across
`Catalog.Domain`, `Ordering.Domain` and `Common.Domain` on the run that landed
this PR. That is the complement run's figure and it is the right one: the
architecture gates reach Domain types by reflection and nothing else, so
counting them inflated both halves of the ratio for no behaviour tested.

**One thing was found and deliberately not fixed.** `Catalog.Infrastructure`
carries a `ProjectReference` to `Catalog.Application` that its own code never
names — `GetReferencedAssemblies` does not list it, and no file in the project
has a `using` for it. Ordering's equivalent *is* used, so the two services
differ. §4.2 permits the reference either way, and removing it would break the
moment Infrastructure names an Application type, which §4.2 anticipates; it is
recorded here rather than tidied because the reverse case — a *used*
dependency that no csproj declares — is what `Common.Domain` in the Application
gate is, and the two look alike from a distance and are not.

**That paragraph is also the hole in the five gates, and the external review
found it there.** Copilot's round raised one finding at six sites: every gate
in §4.2's table reads `GetReferencedAssemblies`, which reports the emitted
`AssemblyRef` table, so a forbidden `ProjectReference` or `PackageReference`
that no compiled code *names* is invisible to all of them — the reviewer's
evidence being the unused edge recorded directly above. The mechanism is
correct and the consequence is real: a project may declare a forbidden
reference and the gate that names that row stays green.

**The instrument was not changed, and the reasoning is the part worth
keeping.** Reading the declared graph is the fix — the restore assets, or a
reference list MSBuild emits into an assembly attribute — and it is a
repo-wide build change whose own failure mode is the one this repository
repeats most: a target that quietly stops emitting leaves every gate passing
vacuously, so it owes a companion test whose subject is what the gate is
looking at. Landing that here would have put a new build-system dependency
into `Directory.Build.props` at the least-reviewed moment in the change, with
the Grok budget spent and one Copilot round behind it. **The limit is also not
this PR's**: the Domain gate has read the same table since it was written, so
the finding describes the gate family rather than the rows PR-22 added.

**The second round found a hole the first one's fix had just papered over, and
this one was closed rather than documented.** The cross-service gate subtracts
every assembly under this service's own prefix, so an `Api → Migrator`
reference passes it *and* the composition-root gate — while §4.2 names the
migrator in no row's "may reference" column, because it is a leaf job host.
`Nothing_in_this_service_references_the_migrator` is the third gate, over the
other four assemblies, and it was **observed red** against a deliberate
`Catalog.Api → Catalog.Migrator` reference that a line of `Program.cs` actually
used — the qualifier being the whole lesson of the round before: an unused
reference is invisible to this instrument, so a probe that only declares the
edge proves nothing.

**Two gates rather than one wider predicate**, because they ask different
questions — *whose is it* and *which layer is it* — and a single `.Where` doing
both reads as neither. The migrator is skipped as a subject rather than
exempted inside the predicate: an assembly does not reference itself, so
including it would pass vacuously, which is this repository's most-repeated
failure wearing its usual disguise.

**What did change is the claim.** The reach is now stated in §4.2 beside the
two-shape table, in `docs/testing.md`, and in all four test files a reader
meets before trusting a green run — the escape needs a reference that is both
forbidden and entirely unused, and it closes the moment anybody relies on it,
which makes these gates late rather than absent. **The declared-graph
instrument is owed**, and is the first thing to reach for the next time §4.2's
enforcement is opened.

## PR-21 — the saga, and the four things §9.6 did not say

PR-21 landed §9.6's `OrderFulfilmentSaga` with its four compensation paths and
four timeouts **as the machine then stood** — #126 has since made it five of
the latter — the four command handlers those timeouts send to, §9.4's
`ordering-commands` endpoint and §9.3's allow-list — empty since PR-18, and the
reason the saga had nothing to start on. Five of its decisions bind what comes
after.

- **No chapter had ever named a message scheduler, and §9.6's `Schedule`
  declarations do not work without one.** [ADR-021](backend-architecture/adr/ADR-021-saga-timeouts-are-scheduled-by-the-broker.md)
  settles it on MassTransit's delayed message scheduler, which on RabbitMQ is
  the delayed message exchange **plugin** — so §14.1's broker is now the one
  infrastructure service that is *built* rather than pulled. Quartz over an ADO
  job store was the serious alternative and is named in the ADR as the
  successor when the plugin's Mnesia store stops being adequate; what decided
  against it here was cost rather than correctness — three packages, ~200 lines
  of vendor DDL this repository would own, a `dbo` prefix cutting across §7.2's
  per-service schema, and a second set of hand-declared receive endpoints
  because this platform deliberately does not call `ConfigureEndpoints`.
  **The deciding argument was the test**: the in-memory transport implements
  the delay itself, so §12.5's harness runs the same two registration lines
  production does, where an in-memory Quartz would be a different mechanism
  wearing the same test.
- **A missing registration that fails at the first message, not at startup, is
  the shape this repository keeps meeting.** Nothing resolves a scheduler while
  the host builds, so both lines absent leaves a service that connects,
  declares its endpoints and reports ready — and faults its first `OrderPlaced`
  onto the error queue. Measured by deleting them: **11 of the saga suite's 13
  tests fail, every one as a timeout**, each reporting the command the saga did
  not send. Not one names the cause. The two survivors are the structural pair
  that construct the state machine without starting a bus, which is worse than
  none — they leave a deleted registration looking half-covered. That is
  §12.5's own trap arriving from a registration instead of from a loaded
  runner, and it is why the lines are stated in the sample rather than
  inherited.
- **§5.4's `Order.ConfirmStock` had no caller, and no way to acquire one.**
  The saga sends four commands; §3.2's Accepts column lists exactly those four;
  none of them advances the order out of `AwaitingStock`. So `ConfirmOrder`
  arrived at an aggregate whose `ConfirmPayment` requires `AwaitingPayment` and
  refused every confirmation the platform could produce — a happy path that
  could not complete, invisible until something drove it end to end. The fix is
  a **consumer, not a contract**: §3.2 already lists `StockReserved` in
  Ordering's Consumes column, so Ordering binds it twice — the saga to decide
  what to ask next, and an `IIntegrationEventHandler` to record it on the
  aggregate. A fifth wire command was the tempting alternative and would have
  changed three chapters to add a way for a peer to drive this service.
  - **That handler dispatches rather than mutating**, and the reason is §7.5's:
    work done inside an integration-event handler commits through the inbox
    filter's `SaveChangesAsync` and stages **nothing**, so the domain event is
    dropped in silence. Silent today only because no projection subscribes to
    `OrderStockConfirmedDomainEvent` yet — §6.6's `OrderSummaries` is not
    built — which is exactly the kind of debt that is free until the PR that
    pays it cannot find it.
  - **It needs a fourth receive endpoint**, `ordering-stock-events`, because
    the saga's endpoint carried no inbox filter by documented exemption and a
    plain consumer sharing it would have inherited that exemption without
    writing anything down. §9.8's "Ordering has three" became four. **The
    exemption is gone — see the entry below — and the separation survives on a
    different reason**: the saga's retry policy is written for inapplicable
    transitions rather than for the domain rejections `Order.ConfirmStock`
    produces.
  - **The two deliveries are unordered, so `ConfirmOrder` can arrive first**,
    and the handler answers `ErrorType.Unavailable` rather than a rule failure.
    §9.8 already draws that line — retry is for faults time might fix — and a
    `Rule` error would ack a paid order's confirmation for good. The window is
    a local write against a payment authorisation and is therefore small; that
    it is small is not why it is handled.
- **§9.6's escalation insert was a read-then-write with no range lock.** The
  printed `IF NOT EXISTS … INSERT` and the conditional `INSERT … WHERE NOT
  EXISTS` that replaced it both read and then write, so both race; the
  difference is `WITH (UPDLOCK, HOLDLOCK)`, which makes the second delivery
  wait and then see the row rather than violate the primary key. This is PR-20's
  `MERGE`/`HOLDLOCK` finding one table over, and the third time this repository
  has fixed a duplicate guard that had the defect it was written to prevent.
  The same block also stopped writing `SYSDATETIMEOFFSET()`: `RetentionPurgeService`
  already computes its cutoff from `TimeProvider`, and a row on the server's
  wall clock is one no substituted clock can reason about.
- **`ShippingAddressV1` silently dropped an address line.** The record carried
  `Line1`, `City`, `PostCode` and `Country`, the domain's `Address` carries
  `Line2`, and nothing noticed because nothing had ever populated the contract —
  PR-21's mapper is its first producer. Added here rather than deferred, on the
  rule this repository already states about contracts: **a contract with no
  consumers is the only cheap moment a contract ever has**, and the same change
  one release later is a §9.2 version bump.

- **§9.8's saga inbox exemption was wrong in both halves, and it is gone.** The
  chapter said a state machine needs no `InboxFilter` because its state is its
  idempotency check, and that an inbox row would suppress legitimate redelivery
  after a mid-transition crash. The first is an argument about **non-initial**
  events: `OrderPlaced` is handled in `Initially` and
  `SetCompletedWhenFinalized()` deletes the row, so MassTransit creates a new
  saga whenever none exists — and §9.4 guarantees at-least-once, so a duplicate
  arriving after the workflow finished reserves stock and authorises payment
  **a second time**. The second describes something the filter does not do:
  `InboxFilter` records after the inner pipe returns, so a mid-transition crash
  leaves no row and the redelivery does the work again. It was protecting that
  delivery from a mechanism that was never a threat to it.
  - Copilot found it, on the third round, in a suppressed-adjacent inline
    comment. It is the only **correctness** defect either review loop found in
    this PR's own code; everything else was a claim, a count or a missing test.
  - Reproduced first: a real-broker test that starts the saga, finalises it,
    republishes the same `OrderPlaced`, and asserts no instance returns. Red
    before the filter, green after.
  - The endpoint separation §9.6 argues for `ordering-stock-events` survives on
    a different reason — retry policy, not the inbox — and both chapters now
    say so.
  - **The half that survived was true and incomplete, and a later branch
    measured the difference.** "The transition is simply not applicable" is a
    statement about the state machine; MassTransit's way of *saying* it is
    `UnhandledEventException`, so a redelivered non-initial event that reached
    the machine was retried six times and filed in the error queue this entry's
    own alert argument depends on staying empty. **Not every redelivered
    non-initial event**: §9.5's inbox suppresses the completed one, since
    `OutboxMessage.Stage` persists the integration event's own message id and
    `OutboxDispatcher` restores it onto every publish. The delivery that
    reaches an advanced instance is the one whose inbox row was never written —
    `InboxFilter` adds its row after the inner pipe returns, so the window is a
    crash between the saga state committing and that second `SaveChangesAsync`.
    **An `OnUnhandledEvent(x => x.Ignore())` catch-all was written for that
    window, defended over several review rounds, and then removed — which is
    the entry's real subject.** `UseInMemoryOutbox` flushes after the inner
    pipeline returns, so the window contains the moment the instance is
    committed and its commands are not yet sent. Three arrivals reach the
    callback and it cannot tell them apart: a post-flush duplicate, which
    wants quiet; a pre-flush crash that lost the instance's commands, where
    quiet is permanent loss; and a misroute, which is a configuration fault.
    A log line was tried in between and is not a signal — §13.6 pages on the
    error queue, which is precisely what ignoring keeps the event out of.
    **So the machine keeps MassTransit's default and the enumeration does the
    work**: every legitimate arrival has its own `Ignore`, and a structural
    test partitions the declared next-events so a new one cannot be missed.
    #128 carried the durable fix, which makes the pre-flush case stop
    existing and a catch-all arguable again on evidence. It has since landed
    (ADR-032) — as `UseEntityFrameworkOutbox` on the endpoint, not the
    `UseBusOutbox` this line named — **and the catch-all did not come back**:
    two arrivals rather than three still cannot be told apart by a callback
    that answers both the same way.
    Reproduced first: a redelivered `StockReserved`
    in `AwaitingPayment` came back as `NotAcceptedStateMachineException`. A
    stale **timeout** never did — a scheduled message whose token id no longer
    matches the instance is discarded before the machine is asked, which is why
    ADR-021's uncancellable timeouts were harmless throughout and this was not.

**Five things were owed and are named rather than built — four still are.**
Each is a §9.6, §5.4 or §9.8 decision that PR-21 made *reachable* rather than
one it introduced, and naming them is the alternative to a silent gap. The
second is marked Closed in place rather than deleted: an owed item that turns
out to have been taken is part of the record, and removing it would leave the
next reader wondering whether it was ever there.

- **A stock timeout strands the reservation.** §9.6's `StockTimeout` branch
  cancels the order and finalises **without releasing stock**, so a reservation
  arriving afterwards has no saga left to compensate it —
  `ConfirmStockHandler` rejects it and the stock stays held. **The rejection is
  quieter than this entry first claimed**: `command.domain_rejected` is
  `CommandConsumer`'s counter, and this command is dispatched in process by
  `StockReservedHandler`, so the only record is `LoggingBehavior`'s line.
  Copilot caught the claim; the handler's own comment had it right and this did
  not. It is the second
  stranded-reservation path in §9.6 and only the other one escalates
  (`ReviewReasons.StockNotReleased`). Closing it means a compensating
  `ReleaseStock` on the timeout branch or a second escalation reason.
- **A customer cancelling mid-workflow is invisible to the saga.** §3.2 does
  not give Ordering a subscription to its own `OrderCancelled`, and §9.6's
  machine has no cancellation branch — so a cancellation racing `StockReserved`
  leaves the saga reserving and authorising, `ConfirmOrder` is refused by the
  aggregate, and three days later a false `not_despatched` review is raised.
  Copilot found it. **The complete fix is a chapter decision, not an
  implementation gap**: cancelling a *`Confirmed`* order needs a refund, and
  §3.2's Accepts column for Payments is `AuthorisePayment` alone — there is no
  refund contract to send. A partial fix covering `AwaitingStock` and
  `AwaitingPayment` is possible and was rejected here as a state-machine change
  §9.6 owns.
  - **Closed, and §9.6 took the decision this entry said it owned.** The
    machine declares `Event<OrderCancelled>` and has a branch in every state it
    can reach one in: `AwaitingStock` and `AwaitingPayment` compensate on the
    decline branch's own terms — and since #126 `AwaitingConfirmation` does
    too, deliberately identically — recording **the event's own reason**
    rather than a literal — both lines said `customer_request` until a round
    established that §11.4 accepts all five `CancelReasons` codes, so the
    caller's reason was overwritten and `Compensating`'s exit sent
    `CancelOrder` under a reason nobody had chosen; `Compensating`
    `Ignore`s it, because a cancellation is already the outcome there. **The
    refund gap is stated rather than closed** — `Confirmed` escalates and
    finalises, so the money reaches a person instead of a contract that does
    not exist. **Two new `ReviewReasons` codes rather than the one this entry
    first recorded**: a review found that `Confirmed` and `Compensating` raise
    the same escalation for different procedures, and the row persists
    `(OrderId, Reason, RaisedAt)` with the saga usually finalised by the time
    anyone reads it — so `cancelled_after_confirmation` and
    `payment_authorised_during_compensation` are what let the runbook select
    without a state
    that is gone.

    **#126 has since undone the premise of that last sentence while keeping
    its conclusion**, and the pair is worth reading together.
    `cancelled_after_confirmation` is now raised from `Compensating` as well as
    from `Confirmed` — an `OrderConfirmed` arriving there is the only evidence
    that the aggregate confirmed before the customer cancelled — so the code
    no longer identifies the state. It still identifies the **procedure**, which
    is what the runbook actually selects on, and that is why two codes remain
    right. The lesson the entry recorded ("a code named for one of its causes
    reads as an explanation and survives review") applies to its own reasoning
    here: the codes were justified by the states they came from, and the states
    moved.

    That
    also removes the false `not_despatched` this entry predicted — **by
    `Finalize()`, not by the `Unschedule` beside it**, which is the credit
    this entry gave until a review checked the mechanism. Deleting the
    instance is what leaves the timeout correlating to nothing when it
    arrives; [ADR-021](backend-architecture/adr/ADR-021-saga-timeouts-are-scheduled-by-the-broker.md)'s
    scheduler returns `Task.CompletedTask` from both `CancelScheduledSend`
    overloads, so **every `Unschedule` in this machine is a no-op** and the
    call removes nothing. The saga and §9.6 were corrected on this branch
    and this entry was not, which is the fix landing in the code and not in
    the record it came from.
- **The payment reference is accepted and goes nowhere.** `ConfirmOrder`
  carries it, `Order.ConfirmPayment` puts it on `OrderConfirmedDomainEvent` and
  stores no column, and `V1.OrderConfirmed` has no field for it — so it reaches
  a Local outbox row only once a projection handles that event, and §6.6's
  `OrderSummaries` is not built. Found by PR-21's own endpoint test asserting a
  column that does not exist. `PaymentReference`'s own doc calls it "the one
  thing that lets a support question about an order reach the provider's own
  records", which is not true of anything today.
- **`Unschedule` cancels nothing on ADR-021's scheduler**, so every order keeps
  its timeouts until they fire. Recorded in the ADR rather than here, because
  it is a property of the decision rather than of the saga — but it is the
  fourth thing this PR knows and does not fix.
- **The saga endpoint buffers its sends in memory.** §9.8 prints
  `UseInMemoryOutbox` there, and the saga repository commits the instance
  *inside* the consumer — so a crash between that commit and the flush leaves
  the saga advanced (or deleted, after `Finalize`) with a command or a schedule
  never sent, and the redelivery finds a state where the transition no longer
  applies. Copilot found it. **§9.4's own callout already states the
  premise** — "the in-memory outbox defers, it does not persist… a consumer
  whose sends must survive its own commit wants §9.4's transactional outbox" —
  so this is the chapter disagreeing with itself rather than a new discovery.
  Closing it means running MassTransit's transactional outbox alongside this
  platform's hand-rolled §9.4 one, which is a §9 decision about owning two
  outboxes and not something a saga PR settles. **It was settled by ADR-032**,
  which found the alternative this entry assumed existed — routing the saga's
  output through §9.4's outbox — to be unavailable rather than merely dearer,
  because a scheduled timeout's delay is a transport feature no dispatcher of
  ours can replay.

### What nine rounds of review moved, and the shape of the last one

**Every finding that changed behaviour arrived from a review, and the last one
to do so arrived on round nine.** That is worth recording as a fact about the
process rather than as praise for the reviewer: the suite was green and the
chapters reconciled after round three, and rounds four through nine still
found a replayed `OrderPlaced` restarting a finished saga, a healthcheck that
passed on a broker with no plugin, and three handler branches with no test
between them and a permanently unconfirmed paid order.

**Round nine's shape is the one to carry.** All eight of its findings were
*suppressed* — none surfaced as an inline comment — and five of the eight were
one claim: **a test that cannot fail for the reason it names.**

- `ConfirmOrderHandler`'s `StockNotConfirmed` is `Unavailable` so
  `CommandConsumer` retries it; a `Rule` error there acks a paid order's
  confirmation for good. Nothing tested it. The same for
  `MarkOrderShipped`'s `NotConfirmed` versus `NotShippable` and
  `ConfirmStock`'s `NotAwaitingStock` — three branches whose whole content is
  *which* `ErrorType` they carry, reachable only through a handler no endpoint
  test drives off the happy path. `SagaCommandHandlerTests` is the answer, and
  it is six tests for six branches.
- The duplicate-suppression test asserted a status that **cannot move either
  way**: `StockReservedHandler` drops the `Result` deliberately, so a
  duplicate reaching the aggregate is refused and leaves exactly the state a
  suppressed one does. The inbox row count was carrying the test the whole
  time and the status assertion was decoration reading as proof.
- `ConcurrencyMode.Pessimistic` was argued in a comment about two events
  arriving together, and every test in the suite delivered one at a time.
  **The test written for it does not pin the mode, and that is a measurement
  rather than a caveat**: with the registration flipped to `Optimistic` it
  passes in 915 ms. Each transition is a few milliseconds, so two messages
  published together are drained back to back and no concurrency conflict
  arises — publishing concurrently does not make a saga *consume*
  concurrently. Forcing a real overlap needs a transition slow enough to hold
  the lock while the second event arrives, which means production code written
  to be slow for a test. So the mode stays **registered, reasoned and
  uncovered**, and the test claims only what it demonstrates: both events are
  consumed without faulting and leave one instance or none. The name
  `..._are_serialised` was drafted and withdrawn — a name green against both
  settings is this round's own finding committed a second time.

**The generalisation is this repository's oldest one arriving by a new
route.** *A gate that silently stops covering the newest surface* is usually
about an architecture test's selector; here it is about an assertion that was
never watching the thing beside it. **Ask what the test does when the code is
wrong, not what it does when the code is right** — five of these were green
against both.

**Round ten found the same shape once more, and one thing genuinely latent.**
Three comments and a §9.6 paragraph said the command pipeline *stages*
`OrderStockConfirmedDomainEvent`. It does not: `DomainEventDispatcher` writes a
Local row only for an event with a registered projection handler, Ordering
registers none, and the event is on no Broker allow-list either — so it is
collected and cleared with **no row of either lane**. The argument the comments
were making survives, because it is about where the handler must live for the
row to appear once §6.6's `OrderSummaries` exists; what was wrong is the tense.
`ConfirmStockCommand` even carried the caveat, attached to the wrong clause: it
explained why the *bug* would be silent while the sentence above still asserted
the staging as a present fact.

**The delayed-message leak is real, unreachable today, and guarded by a
coincidence.** `Unschedule` is a no-op on ADR-021's scheduler, so every saga
test leaves its timeouts armed in the collection-wide broker; `ResetAsync`
truncates SQL and cannot touch them. If one landed mid-run it would cross
`InboxFilter` and write a row — and `Ordering.Api.Tests`' `InboxFilterTests`
asserts `ShouldBeEmpty()` over the whole table, in that same collection. What
stops it is only that the shortest schedule is **five minutes** and the
collection runs in **1 m 18 s**; a runner four times slower turns it into a
flake in a test that has nothing to do with sagas. Not fixed here: the fix is a
broker per saga class, which buys a container set on every run (§12.4's stated
price) against a hazard needing a fourfold slowdown to reach. **Recorded so the
next person to see `InboxFilterTests` fail for no reason finds this paragraph
rather than the timing.**

**Round twelve found a defect round nine introduced, which is the loop working
as intended rather than a sign it should have stopped.** `StockReserved` has
two consumers in this service by design — the saga correlates on it,
`StockReservedHandler` records it on the aggregate — so the publish helper
added for the concurrency test registered a teardown drain for the saga's
inbox row alone. The saga could finish, the teardown pass, and the next
`ResetAsync` truncate the schema underneath a `StockReservedHandler` still
committing: **exactly the flake this class's teardown exists to close, one
endpoint over.** Checking Copilot's cross-reference — it said the sibling suite
already did this — turned up a second instance in the same round's work: the
sentinel publish was marked `drain: false` when only the *duplicate* beside it
earns that, a suppressed message writing no row where a fresh id writes two.

**Three fixed sleeps became sentinel waits, and the honest limit is recorded
in the tests themselves.** A `Task.Delay` before a negative assertion is a claim
about the runner, not about the code; publishing a fresh message afterwards
and waiting for *its* effect scales with the machine. It is a bound and not a
proof — neither endpoint sets `ConcurrentMessageLimit`, so the sentinel may
overtake — and making it a proof would mean changing the production topology
to suit a test, which was considered and refused.

---

## PR-20 — the first projection and the first receive endpoint

PR-20 landed the first projection and the first receive endpoint — §6.6's
`ProductPriceProjection`, §9.8's `ordering-catalog-events` — and six of its
decisions bind what comes after:

- **`ConfigureEndpoints(context)` is gone from both services, and it is a
  fail-open rather than a leftover.** PR-13 left the call in Catalog and
  Ordering with a comment calling it "the line every later consumer rides in
  on"; what it actually does for a registered consumer whose explicit binding
  is missing is manufacture a queue named after the consumer type — carrying
  **neither** the inbox filter **nor** the retry policy, because both are
  per-endpoint configuration an invented endpoint never receives. §9.8 permits
  an endpoint without the inbox exactly once, for the saga, and requires the
  opt-out to be written down where it is taken; a queue MassTransit invents
  takes it and writes nothing. Measured both ways by deleting one
  `ConfigureConsumer` line: with the call present the event was still projected
  and **no inbox row was written**, and one of three tests noticed; with it gone
  all three go red. The cost is stated rather than dodged — a consumer now
  needs a line in two places and nothing at startup complains if it gets one.
- **§9.8's printed `e.UseInMemoryOutbox()` does not compile at this pin.** The
  parameterless overload carries `CS0618`, which ADR-019 makes an error, so
  three sites in §9 had been unbuildable since they were written. This is
  PR-19's `AddStandardResilienceHandler` finding and PR-17's `KnownNetworks`
  finding for the third time: **a sample nobody has compiled is a sample that
  does not compile**, and the only way to find out is to build it.
- **A withdrawal has to survive having no row to write to, and §6.6's printed
  `UPDATE` did not.** `ProductDiscontinued` carries no currency (§9.1) and
  `ordering.ProductPrices` is keyed by one, so the discontinue statement
  reached only the rows that already existed. §9.4 guarantees no ordering, so
  a withdrawal claimed ahead of a still-retrying publish matched nothing and
  the publish then took the upsert's `NOT MATCHED` branch — **the one branch
  no `UpdatedAt` comparison can cover**, because there is no target row to
  compare against — putting a discontinued product back on sale. A stale price
  for a currency the withdrawal never saw does it with no reordering at all.
  Copilot found it; both cases were reproduced as failing tests before the fix
  was written.

  **The answer is the one §6.6 already gives one projection up.**
  `OrderSummaries` uses a `MERGE` rather than an `UPDATE` for its status events
  precisely so a `Cancelled` claimed before its `OrderPlaced` does not "match
  no row, change nothing, and be marked processed". A status event can carry
  its own row into existence because it knows the key; a withdrawal cannot, so
  it writes a product-level watermark — `ordering.ProductWithdrawals` — and
  the upsert derives `IsAvailable` from it on exactly the branch that has
  nothing else to consult. A **watermark**, not a flag, for the reason
  `UpdatedAt` is a comparison: a later republish re-lists the product, in
  currencies that have rows and in currencies that do not.
- **`WITH (HOLDLOCK)` is a reasoned claim, not an observed one, and the test
  says so in its own remarks.** A bare `MERGE` takes no range lock over a key it
  failed to find, so two concurrent deliveries can both insert and the loser
  violates the primary key — which the endpoint's retry would absorb, so the
  defect reads as warnings rather than as a failure. Deleting the hint left the
  suite green at eight-way and again at sixty-four-way concurrency, three runs
  each. So the hint stays and §6.6 gained it, and the test carries the class it
  is in — PR-17's rate-limiter ordering row, reasoned and unobserved —
  rather than looking like the guard it is not.
- **The currency is normalised on the write side as well as the read side, and
  neither call is redundant.** Nothing between Catalog's `Money` and the
  `MERGE` normalises anything — `Currency` crosses the wire as a `string` like
  any other — so an unnormalised contract writes a row `ProjectedPriceReader`
  cannot find *and* a second primary-key row beside the one it can, under a
  case-sensitive collation.

  **What the reader's comment said before this PR is the lesson, and it took
  two review rounds to finish.** It justified its own `ToUpperInvariant` by
  asserting that the column "is written through `Money.Of`" — a claim about a
  file that did not exist yet, and one that stayed false after it did, because
  the value arrives over a wire and not through the domain. **A comment
  describing what some other file does is a claim about that file.** Round 1
  fixed the reader and §6.4's sample; three sites that described the *reader*
  went on quoting the retired rationale for two more rounds, which is the same
  defect one indirection out — a comment describing what a comment says.
- **§13.7's read-model row says *own events* now, and broker-fed staleness is
  a named gap rather than a row.** `projection.lag` is recorded by
  `ProjectionInvoker` off an outbox row, so a read model fed by another
  service's contract never touches it — and `ProductPrices` is the platform's
  first. The first fix pointed at the **event end-to-end** row instead, which
  Copilot refuted: `IntegrationEventConsumer<T>` records
  `messaging.delivery.lag` at the *top* of `Consume`, before it resolves a
  handler, so the measurement stops where the projection starts and excludes
  the SQL round trip, §9.8's retries and a terminal failure. That row can hold
  its two-second target while the table is stale or was never written.

  **A near-miss row is worse than an absence, and §13.7 already said so two
  paragraphs down** — "an SLO that cannot be evaluated is not a weak SLO, it
  is a claim that the service is meeting a bar nobody is checking". Closing it
  needs an instrument that fires after a broker-lane handler commits, which is
  a §13.3 change with a dashboard behind it; until then the gap is written
  down, which is the standing the two cut rows already have.

**`OrderSummaries` is deliberately not in this PR**, and the reason is worth
carrying: §6.6 has two projections, and only one of them is what this PR's
title names. The other is fed mostly by Ordering's own domain events on the
local lane and needs §13.3's `OrderMetrics` and §6.6's escalated history query
with it. Appendix C names no PR for it; whoever builds the history screen
builds it.

---

## PR-19 — the BFF, and Catalog as a gRPC server

PR-19 landed the BFF — §9.7's one synchronous hop, §11.5's client credentials,
the `web-bff` route's service — and ten of its decisions bind what comes after:

- **A cleartext Kestrel endpoint cannot serve HTTP/1.1 and h2c at once, and
  §9.7's printed `http://catalog-api:8080` could not work.** Measured before a
  line was written: at the default `Http1AndHttp2`, a client asking for HTTP/2
  *exactly* — which is what `Grpc.Net.Client` does — is answered
  `HTTP_1_1_REQUIRED` and the connection is closed. ALPN is what negotiates the
  upgrade and there is no TLS on this hop to carry it (§10.1). An `Http2`-only
  endpoint is the fix and it cuts the other way too: it answers an HTTP/1.1
  request with a 400. So Catalog declares **two** endpoints and §9.7 was
  amended to 8081.

  **The second half is the one that reaches other files.** `Kestrel:Endpoints`
  in `appsettings.json` **overrides** the container image's own port
  configuration — measured against both spellings, `ASPNETCORE_HTTP_PORTS` and
  `ASPNETCORE_URLS`, and neither produces a warning — and declaring *one*
  endpoint suppresses them just as completely as declaring two. So 8080 has to
  be restated in that file or it stops existing, and a host-run Catalog now
  binds 8080, **which is Keycloak's published port**. The compose README grew
  two `Kestrel__Endpoints__…__Url` exports and the reason for them, because the
  same configuration key from a higher provider is the only thing that can move
  a port this file has claimed.
- **§9.7's fluent chain does not compile, and only compiling it says so.**
  `AddStandardResilienceHandler` returns an `IHttpStandardResiliencePipelineBuilder`
  — a different type, scoped to the pipeline it just registered — so the printed
  `.AddStandardResilienceHandler(…).AddHttpMessageHandler<T>()` is CS1929. The
  fix holds the `IHttpClientBuilder` in a local and calls both on it, which
  keeps the **order**, which is the part that carries meaning. This is PR-17's
  `KnownNetworks` finding in another chapter: a sample nobody had compiled.
- **`Google.Protobuf` was pinned below its own floor, and `NU1109` is why that
  is fatal rather than cosmetic.** §4.4 printed 3.29.3; `Grpc.AspNetCore`
  2.71.0 floors it at 3.30.2, and with `CentralPackageTransitivePinningEnabled`
  a lower pin is a package **downgrade** rather than a floor NuGet quietly
  raises. The three `Grpc.*` rows had been unrestorable since they were written.
- **An HTTP resilience pipeline cannot retry a gRPC status, and the
  configuration §9.7 prints does not say so.** gRPC carries its outcome in
  `grpc-status` — a trailer on an HTTP **200** — so `AddStandardResilienceHandler`
  sees a successful response and hands it straight back. A Catalog answering
  `Unavailable` is asked **once**, whatever `MaxRetryAttempts` says. What the
  retries do cover is a transport fault, which is the shape a service that is
  genuinely down produces. Both halves are measured in `UpstreamRetryTests`;
  the test that found it was written expecting three calls and got one.

  **The fix is deliberately not a second retry loop.** gRPC's own retry lives
  on the channel and does understand status codes — and sits *outside* the
  `HttpClient`, so each of its attempts would get a fresh
  `TotalRequestTimeout`: three of them spend fifteen seconds against a
  five-second ceiling. Stacking the two is the one change that breaks the
  hierarchy §9.7 exists to protect.
- **`InvariantCulture` is half of a safe decimal parse; the `NumberStyles`
  argument is the other half.** `NumberStyles.Number` is the obvious choice and
  includes `AllowThousands`, so `"12,50"` parses under the **invariant** culture
  as twelve hundred and fifty — the exact hundredfold error the invariant
  culture was chosen to rule out, arriving through the other argument. Caught by
  a test that expected a 500 from a malformed upstream amount and got a 200. A
  wire format has no group separators, so the parse names
  `AllowLeadingSign | AllowDecimalPoint` and nothing else.
- **One `.proto`, four generated halves across three projects, and CS0436 is
  what decides where they live.** Catalog owns the contract because Catalog
  serves it — `Catalog.Api` generates `Both`, which is two of the four —
  and `Web.Bff`
  **links** the same file rather than copying it, so the client and the server
  cannot drift. The consequence is that any project referencing `Web.Bff` and
  also generating from that `.proto` has every message type twice, and CS0436
  is an error under ADR-019 — which is why `StubCatalog` lives in
  `Web.Bff.TestSupport` and why `Catalog.Api` generates `GrpcServices="Both"`
  for its own suite. The trade there was stated rather than dodged: a generated
  client nothing in production calls, against a transport adapter no test can
  reach.

  **A linked file is a `COPY` line in a Dockerfile**, and the BFF's is the only
  one in the repository that reaches into another service's tree. It is the
  same silent-breakage class PR-14 found with `ProjectReference`, with a worse
  message: Grpc.Tools names a path under `src/Services/Catalog` in a Dockerfile
  that builds no Catalog project.
- **The `.Endpoints`-only architecture gate went vacuous the moment a second
  transport namespace existed.** `PricingService` is an endpoint in every sense
  §4.2 cares about and lived in `.Grpc`, so the gate selected none of it and
  stayed green. PR-19 made the selector a pattern and — this is the half worth
  copying — added a **second test asserting the selection itself**, naming both
  adapters. Neither survives: a later shape dropped the selector entirely and
  moved that test's subject to the exemption, for the reason the next sentence
  gives. A gate that silently stops covering the newest surface is this
  repository's most-repeated failure, and the only defence is a test whose
  subject is what the gate is looking at rather than what it found.
- **The realm was built through the admin API and verified by re-importing it**,
  which is what PR-16's entry recommended and PR-16 itself did not do. The
  `web-bff` client is Keycloak's own JSON, spliced in; the whole file was then
  imported into a fresh Keycloak and eleven claims read off real tokens — the
  audience present, `sub` present, and the two negative ones that matter most:
  a service account carrying **no** `permission` claim, and every existing
  login keeping `sub`, `email` and `realm_access`.

  **What stands afterwards is less than that run, and the difference is worth
  knowing.** `KeycloakIdentityTests` pins four of the eleven — the audience on
  the BFF's token, the absent `permission` claim, that a host running the real
  `AddJwtAuthentication` accepts it, and that a client without the scope is
  refused. The login half is `RealmImportTests`, which reads the file and
  starts nothing. So no standing test mints a password-grant token for `demo`
  or `browser`: the claims those logins carry were verified once, by hand, and
  a realm edit that broke them would be caught statically or not at all.
- **A premise about a rule is falsified by the first case that needs it, and
  `No_client_secret_is_committed` was that rule.** It said no client ships a
  secret — true while no client used the client-credentials grant, which is
  precisely two parties holding one string, one of which is a committed Compose
  file. Letting Keycloak generate it would leave the realm and the deployment
  disagreeing and the BFF refused at the token endpoint on every call. The rule
  **narrowed rather than lapsed**, and narrowing made it stronger: the value is
  pinned to the documented local default, so a generated secret still fails and
  so does a real one. PR-15 recorded the same shape about `EfUnitOfWork`.
- **The scaffold's probe port had quietly taken 5200, which was the BFF's all
  along.** `PORT = 5199` carries a careful paragraph about avoiding the 51xx
  block; the second probe was spelt `PORT + 1`, and §14.1's fence has shown
  5200 beside `web-bff` since PR-06. Every render in that suite started
  refusing the day this PR landed. **A port chosen for one constant is not a
  port reserved for two** — and arithmetic at a call site is what hid it, so the
  second port is a named constant now.

  The rest of the scaffold reconciliation is the ordinary price
  `docs/repo-map.md` already names: ten new files classified, the gRPC
  package, the `Protobuf` item and both `Program.cs` blocks patched out, and
  the non-vacuity test replaced by the comment that tells the next service
  when to add it back. A
  scaffolded service was rendered and **built** afterwards, because the Python
  suite never compiles one.

## PR-27 — the body ceiling and ADR-020's response compression

PR-27 landed the last two entries of §10.1's "It does" list — the body ceiling
and ADR-020's response compression — and five of its decisions bind what comes
after:

- **`EnableForHttps = true` is what makes the edge compress at all, and this
  file argued the exact opposite first.** The claim was that TLS terminates at
  the ingress (§10.1), so the gateway is served plain `http`, so the flag never
  fires and setting it true merely says out loud what happens anyway. Every
  clause true, conclusion inverted: §4.2's forwarded-headers block enables
  `XForwardedProto`, `UseForwardedHeaders` rewrites `Request.Scheme` from the
  ingress's header, and the compression middleware decides at the first
  **write** — below the whole pipeline — so the scheme it reads is the
  rewritten one. At the default, a gateway behind an HTTPS ingress compresses
  **nothing** and no response says why. Copilot round 1 found it;
  `ForwardedSchemeCompressionTests` is the measurement, red against the
  property removed.

  **The lesson is not about compression.** A middleware that acts on the
  response decides *after* everything below it has run, so reasoning about
  what it "sees" from the position of its `Use` call is reasoning about the
  wrong moment. `UseResponseCompression` sits above `UseForwardedHeaders` and
  still reads the header `UseForwardedHeaders` wrote. Any claim of the form
  "this middleware runs before that one, so it cannot see X" is worth
  measuring rather than reading off the pipeline order.

  What survives the correction is the *decision* and the shape of its
  argument: the flag cannot be argued from the scheme in either direction —
  the response reaches the browser over TLS whatever the inner hop was — so
  ADR-020 argues it from content. No body crossing this edge pairs a secret
  with reflected input.
- **The one body that reflects a client-supplied value is the one the default
  MIME list omits, and that is luck rather than design — so a test pins it.**
  §10.5's problem+json carries the `X-Correlation-Id` the caller may have
  chosen (§10.4), which is the input half of BREACH; `application/problem+json`
  is absent from `ResponseCompressionDefaults.MimeTypes` and therefore travels
  plain. Nothing in this solution decided that, so `CompressedResponseTests`
  asserts both directions from the wire. **Adding a type to
  `CompressibleContentTypes` is re-taking ADR-020**, not a tuning change.
- **The 413 needed no exception handler, and the 400 and 409 rows each did.**
  Kestrel throws `BadHttpRequestException` carrying the status and
  `ExceptionHandlerMiddleware` reads it off the exception instead of defaulting
  to 500, so §10.5's shape arrives with `correlationId` and `traceId` for free.
  Measured over both framings — a declared `Content-Length` and a chunked body
  with none — because the plausible failure was the opposite one: YARP's
  forwarder absorbs client-body faults into its own 400, and it does not absorb
  this.
- **`ConfigureKestrel` is a silent no-op under `TestServer`, so the limit is
  the first property in the solution that a real server has to serve.**
  `WebApplicationFactory.UseKestrel(0)` is the seam, and its ordering is
  load-bearing: it throws once the host is initialised and `CreateClient` is
  what initialises one, so a factory whose client is taken first is a
  `TestServer` again with no failure to say so. The general rule is worth
  carrying past the gateway — drive `TestServer` for what the *application*
  decides, a real server for what the *server* decides, and the two are
  indistinguishable from the test.
- **The compression middleware has no ordering rule a test can catch, and
  saying so is the point.** Moved below the auth pair and the limiter, every
  test in `Gateway.Api.Tests` stays green — because the only bodies those
  middlewares produce are problem+json, which is not compressed. Its *absence*
  is caught immediately, which is the failure mode that matters:
  `AddResponseCompression` succeeds and compresses nothing without
  `UseResponseCompression`, exactly the shape §10.3's registration has. Both
  halves measured, in the habit PR-16 and PR-17 established — do not write down
  an ordering claim a test is not making.

**One test claim was found false by asserting it**, and it is the sharper
finding of the two: the chunked-body case was a second copy of the
`Content-Length` case, because `StreamContent` over a `MemoryStream` reads the
stream's length and sends the header anyway. It passed, for the wrong reason,
and only `ContentLength.ShouldBeNull()` told the difference. **A test named for
a case is not a test of it** — the streaming path is the one an attacker
chooses, since omitting a header costs the sender nothing.

**"A test that would pass" is this PR's most repeated error, and it was written
three times before anything checked it.** Copilot round 2's suppressed block —
which carried five findings under a heading saying no new comments were
generated — caught the same inversion at three sites: that the size-limit suite
"would pass" over `TestServer`, that a decompressing client would "leave every
assertion passing", and that a test carrying its own copy of the ceiling "would
pass" against a differently configured gateway. **All three are the opposite of
what happens.** Measured for the first: over `TestServer` the suite goes red,
two of three, because the oversized bodies reach the destination and answer 204
where 413 was expected.

The useful half is what the measurement added. Exactly **one** test passes
there — the one asserting a body *at* the ceiling is forwarded — so the silent
outcome is real but belongs to a suite written from the acceptance side alone.
Asserting the boundary from both sides is what converts it into a loud failure,
which the suite already did and the prose had not noticed. **A hazard framed as
"this would pass" is a claim about a run nobody performed**; this repository
already says not to write down an ordering claim a test is not making, and this
is the same rule for a counterfactual.

**ADR-020's escape hatch was named wrong too, and PR-19 is who it costs.** The
first version told the BFF to protect a secret-bearing response by *encoding*
it itself, on the ground that the middleware skips a response already carrying
a `Content-Encoding`. The mechanism is right and the instruction is useless:
gzip opens the same length side channel wherever it is applied, so a
BFF-compressed secret leaks exactly as a gateway-compressed one does. The
answer taken at the time was `Content-Encoding: identity`, skipped by the same
header check and readable on the wire — **which stood for two rounds and is
also wrong**; the paragraph below is where it lands. Copilot round 1 again,
and it is worth noticing that both of its findings were **the argument being
wrong while the code was right**: the flag and the header check were correct
in `Program.cs` throughout. A review that only diffs code would have found
neither.

**`Cache-Control: no-transform` was the opt-out all along, and the gateway was
violating RFC 9111 by ignoring it.** Round 3 proposed the directive and was
half right; round 5 pressed the other half and was fully right. The framework
does **not** implement it — measured twice, an 8 KiB body coming back gzipped
with the directive intact — but §5.2.2.6 says an intermediary "regardless of
whether it implements a cache" MUST NOT transform the content, a content coding
is such a transformation (RFC 9110 §7.7), and a YARP gateway is an
intermediary. So the gateway now carries
`NoTransformResponseCompressionProvider`, a subclass of the framework's own
with one case in front of `ShouldCompressResponse`, registered by `Replace`
rather than by sitting above `AddResponseCompression`'s `TryAddSingleton`.

**The intermediate state is the lesson, and it lasted two rounds.** Having
measured that the framework ignores the directive, this record treated the
measurement as though it settled the question — pinning the violation in a test
and telling PR-19 to use `Content-Encoding: identity` instead. A measurement
says what the code *does*; it never says what it *may* do. The specification
was one fetch away and nothing had read it.

**The request form is honoured too, and quoting it is what kept the claim
honest.** Round 8 asked for it and framed it as the same obligation; §5.2.1.6
says only that "the client is asking for intermediaries to avoid transforming
the content", where §5.2.2.6's response form is a MUST NOT. Both are refused —
a caller who says so explicitly should be believed, and it is one header read —
but the asymmetry is recorded rather than flattened into "the RFC requires it",
which is false of half of it. Three rounds in a row turned on the difference
between what a specification says and what everyone assumes it says, this one
included: **fetch the section and paste the sentence.**

**Reading a request header in a response decision costs a `Vary` entry**, and
the fix for round 8 introduced that debt in the same commit that paid off the
last one. The representation now depends on `Cache-Control`, so a shared cache
with no `Vary: Cache-Control` may serve a stored gzipped variant to the one
caller who asked for none — the policy undone from outside the process, by
something the gateway does not control. Advertised on **every** decision,
compressed ones included, because absence is a value. Round 9 found it, which
makes it the second time in this branch that fixing one thing quietly broke a
neighbouring one; the first was a wrap regression a script caught.

## PR-18 — Ordering, the second service

PR-18 landed Ordering — the scaffold's own output plus one domain, five
projects rendered by `tools/new-service` and then given §5's `Order`, with
nothing about the wiring hand-written or reconciled afterwards. It also carries
PR-16's deferred security test: user A *cancelling* user B's order → 404,
§11.4's ownership check, which needed the first resource in the platform that
has an owner.

**This block was written later than the rest of the log, and that is itself
worth recording.** PR-18's findings were filed as annotations on `CLAUDE.md`'s
directory tree rather than as a decision block, so when that tree was
compressed to one line per entry they had nowhere to land. Seven lessons were
one edit away from being lost, and none of them was inventory. **A decision
belongs in the log, not in a caption** — a tree annotation is read as a
description of what a directory holds, so a rule hidden in one is invisible
to the next person who trims the description.

- **`OrderLine` has its own `OrderLineId`, and a shared key type would be a
  silent equality bug.** `Entity<TId>` compares the *type* as well as the
  value, so a line keyed by `OrderId` would compare equal to the order that
  owns it. The separate key type is what keeps identity meaning one thing.
- **`CustomerId` deliberately has no `New()`.** A customer identifier is
  minted outside this service; a factory on it would invite an aggregate to
  invent one, which is the shape §6.4 removes from the command in the next
  entry.
- **`PlaceOrderCommand` carries no `CustomerId` — the handler reads
  `ICurrentUser.Id`** (§6.4). A subject supplied by the caller is a subject
  the caller can choose. `CancelOrder` is the same argument at the other end:
  §11.4's ownership check fails closed, and `CommandOrigin.User` is the
  **zero value** so an unset origin checks the owner rather than bypassing
  the check.
- **Handlers are public, because §6.2's scan is public-only.** An internal
  handler is not a compile error and not a registration failure — it is
  registered as nothing at all, and the first symptom is a command with no
  handler at runtime.
- **`OrderLine` is mapped as a related entity, not an owned collection**, and
  the reason is a framework limit rather than a modelling preference: an owned
  builder has no `ComplexProperty`, and the line carries `Money`.
- **The `Local`-lane round trip could not be copied from Catalog unchanged.**
  A record's generated equality compares an `IReadOnlyList` **by reference**,
  and two of Ordering's five events carry one — `OrderPlacedDomainEvent` and
  `OrderConfirmedDomainEvent` — so the assertion that passes
  for Catalog's single-valued event silently compares identity here. The
  domain allow-list is four entries for a neighbouring reason: the first event
  earned `System.Collections` and `Money.Of` earned `System.Linq`.
- **`OrderingPermissions` holds policies only.** `orders:admin` is not among
  them — it is a claim the handler reads directly, per §11.4, and registering
  a policy for it would imply an endpoint-level gate that does not exist.

`AssemblyMarker` is gone and `Order` is the gates' anchor, which is PR-11's
rule running in its stated direction: the scaffold **emits** a marker so a
service with no domain type has something for §4.2's two gates to name, and the
first aggregate is when it is **deleted**.

## PR-17 — the gateway

PR-17 landed the gateway — §10.2's routes, §10.3's limiter, §4.2's edge
pipeline — and fourteen of its decisions bind what comes after:

- **An unresolvable policy name stops the gateway; it does not silently drop
  the route, and four sites said it did.** §10.2, §4.2's sample, §11.4's
  callout and Appendix C's PR-17 row all described a per-route drop that leaves
  the host "up healthy serving whichever routes happened to validate".
  Measured: `ProxyConfigManager.InitialLoadAsync` throws out of
  `MapReverseProxy()` with an `InvalidOperationException` naming the policy and
  the route, for **both** registries — the authorization one and the rate
  limiter's. All four were amended. The correction runs the reassuring way, and
  the consequence worth carrying is that **the gateway is the one host where an
  unregistered policy name fails better than in a service**, where §11.4's
  endpoint still throws on the first request that reaches it.
- **The whole route file ships, three of its four services ahead of
  themselves.** This is the opposite of the Compose rule and the asymmetry is
  in what each costs: a Compose block naming an absent image fails `up`, a
  route to an absent destination 502s one path. What buys it is that PR-17's
  two config tests say nothing over a single route — §11.4 names a vacuously
  passing policy test as its own defect — and that delivering the file a route
  at a time makes each later PR re-decide the policies, which is §10.2's
  dual-version trap. **It is not licence to invent routes**: a `/api/v2/orders`
  route would fail the forwarded-path assertion, correctly, and the
  dual-version pair stays an example in the chapter.
- **The forwarded path is a prefix of the service's group, not an equality**,
  and Catalog is the counterexample that settled it: `/api/v1/catalog/{**}`
  strips to `/v1/catalog` while `ProductEndpoints` maps
  `/v1/catalog/products`. Appendix C said "equals" and was amended. The
  registry the assertion reads is hand-written, one entry per cluster, both
  directions asserted — `ContractSamples`' shape — because reading it from the
  services would mean the gateway's suite referencing every service, which is
  the coupling §10.1 exists to prevent.
- **A stub destination that answers beats an address that refuses, and the
  measurement is the argument.** Pointing the clusters at `127.0.0.1:1` cost
  ~2 s a request on this host, so exhausting §10.3's 100-request window took
  three and a half minutes, the window replenished, and the rate-limit test
  failed while the limiter worked. A Kestrel server on an ephemeral loopback
  port is faster *and* is the only thing that can observe the forwarded path,
  which is the assertion §10.2 says nothing else in the solution can make.
- **Both conditional reads are hoisted out of their options callbacks.**
  §4.2 printed `GetRequiredSection("Cors:Origins")` inside `AddCors`'s lambda,
  which runs when the CORS options are first resolved — on a request. "Enabled
  but unconfigured" then throws at a request rather than at a deployment, which
  is the exact deferral the flag pair exists to avoid. Both reads moved above
  their registrations and `ConditionalBlockTests` holds all four states.
- **§4.2's forwarded-headers block did not compile at this pin.**
  `KnownNetworks` carries `ASPDEPR005` in .NET 10 — an error under ADR-019, not
  a warning — and its replacement `KnownIPNetworks` takes `System.Net.IPNetwork`
  while the bare name binds to `Microsoft.AspNetCore.HttpOverrides.IPNetwork`,
  brought into scope by the `using` the `ForwardedHeaders` flags need. Two
  wrong spellings on one line, found by compiling it.
- **The 429 is written through `IProblemDetailsService`.** §10.3 printed
  `WriteAsJsonAsync`, which emits `application/json` and runs none of §10.5's
  customisation — so the one response a client is most likely to handle
  programmatically would carry neither the right media type nor
  `correlationId`, on a platform whose stated promise is one error shape.
- **`Retry-After` rounds up, and the rule needed a type to be testable at
  all.** The obvious `(int)remaining.TotalSeconds` truncates, so a lease with
  0.8 s left advertises `Retry-After: 0` — not a lost fraction but an
  instruction, sending a well-behaved client back into a limiter still
  refusing. What makes it interesting is the second half: the 429 test asserted
  a floor on the header and **passed with the truncating cast**, because the
  window is a minute long and a rejection carries tens of seconds. Reaching the
  defect through HTTP means holding a window open for fifty-nine seconds.
  `RetryAfterHeader` exists so three rows of a theory can do it instead — and a
  comment claiming the HTTP test caught it was written, and was wrong, before
  this was measured.
- **The authenticated rate-limit policy had no test, and the one added does
  not catch §4.2's ordering rule.** Only the anonymous window was ever driven
  to rejection, so the subject partition — the thing making a per-user quota
  per-user — rested on nothing. The new test proves two subjects hold
  independent buckets; run against a pipeline with `UseRateLimiter` moved above
  `UseAuthentication` it still passes, as does every other test in that
  project. The limiter is
  live under the reversal (the anonymous window still rejects), so the "degrades
  to per-IP" mechanism is reasoned and unobserved while the "silently" half is
  measured. §4.2 now says which is which. **PR-16's lesson repeated exactly**:
  keep the line, and do not believe a test is watching it.
- **The forwarded-headers block had no positive test, and the limiter's
  ordering row still has none — the contrast is the point.** Both are "this
  middleware must run before that one" claims about the same pipeline, and
  only one of them turned out to be observable. `ForwardedHeadersTests` spends
  one forwarded address's window, proves it is refused, and shows a second
  address still served; moved below `UseRateLimiter`, the two collapse onto the
  one connection the gateway can see and it goes red. The limiter-vs-
  authentication row reversed the same way and **nothing failed**. So a
  middleware-order rule is testable or it is not, case by case, and which is
  which has to be measured rather than assumed from the shape of the claim.
  Under `TestServer` the peer address is null, so the test installs an
  `IStartupFilter` to give the request one — the only seam that gets in front
  of a `Program.cs` a test may not edit.
- **"Blank counts as missing" had to be learned twice, and the second time
  was a review finding.** PR-16 wrote it into `AddJwtAuthentication` for
  `Identity:Authority` and PR-16's entry below records the argument — an
  environment
  variable set to the empty string reaches `Configuration` as `""`, not null.
  The gateway's `Cors:Origins` then shipped guarded by `GetRequiredSection`
  alone, which proves a section *exists*: `Cors__Origins__0=` binds to an array
  holding one empty string, `WithOrigins` accepts it, the host starts, and
  every browser request is refused by a policy matching no origin. **A lesson
  recorded in prose is not a lesson applied**; the guard is now a check on the
  bound values with a test behind it, which is the form that travels.
- **The fix that lands in code and not in the sample is this repository's
  most reliable defect, and PR-17 produced five of them.** `CLAUDE.md`'s *one
  rule that matters* already says a code change contradicting a chapter is not
  done
  until the chapter moves with it; what PR-17 adds is the direction it actually
  fails in. Not code drifting from a written spec — a *correction* landing in
  `Program.cs` or a test and never reaching the sample it was copied from. The
  CORS guard grew four clauses over four review rounds and §4.2's sample
  tracked it a round late every time; the stub-path assertion was tightened in
  `ProxiedRouteTests` and left weak in §12.4. **Each one re-arms the defect for
  whoever builds the next host from the chapter**, which is precisely who the
  chapter is for. The habit that catches it is mechanical: after fixing a line
  that came from a sample, grep the blueprint for the line you replaced, not
  for the topic.
- **401 and 403 carried no body at all, in every host, since PR-16.** §10.5
  opens by promising one error shape "regardless of which service produced
  it", and its own table lists both statuses — but a challenge and a forbid are
  written by the middleware before any endpoint runs, and
  `AddCommonProblemDetails` only supplies a writer that nothing on that path
  was calling. So the two statuses a client meets first were the two that broke
  the promise. **`app.UseStatusCodePages()` is the whole fix** — since .NET 8
  it writes through `IProblemDetailsService` — and it is one explicit line per
  host rather than something `AddCommonWebDefaults` can add, because it is
  middleware and §4.2 keeps middleware order visible at the composition root.
  Found by asserting the media type on a gateway 401, which is the assertion
  nobody had written: `ShouldBe(HttpStatusCode.Unauthorized)` passes just as
  happily on an empty response.
- **A permission a *route* requires obeys §11.4's rule exactly as an
  endpoint's does, and the realm role arrives in the same change as the
  constant.** PR-17 registered `inventory:admin` and named it on a route
  without adding the role to the realm's `commerce-api` client, so
  `/api/v1/inventory` was 403 for every principal Keycloak could issue — not
  a wrong answer a test would catch, a path nobody could reach. **Neither
  existing guard could see it**: §11.4's constant makes a *misspelling* a
  compile error and says nothing about a name the provider has never heard of,
  and `RealmImportTests`' closed-set assertion compares against a literal
  because `Common.Web.Tests` is a building block's suite and may not reference
  a host to read its constants. So the check lives with the constant —
  `GrantablePermissionTests` in `Gateway.Api.Tests`, observed red against a
  renamed role — and **Catalog owes the same test**: `catalog:write` is
  grantable today because PR-16 happened to add both halves at once, not
  because anything checks that it did. Verified in a live Keycloak rather than
  by reading the export: both roles present, `demo` still carrying exactly
  `catalog:write`, `browser` still carrying no `permission` claim at all, and
  `sub`, `email` and `realm_access` all intact — the negative half being the
  one §11.5 says matters most.

## PR-16 — security

PR-16 landed security — §11.3's JWT validation in `Common.Web`, §11.4's
policies and port, the realm import — and seven of its decisions bind what
comes after:

- **`ICurrentUser` and `HttpContextCurrentUser` are common, not per-service,
  and §11.4 was amended.** The chapter wrote `Ordering.Application` and
  `Ordering.Infrastructure` for the same reason §9.4 wrote
  `ordering.OutboxMessages` — it is Ordering's viewpoint. Nothing in either
  type names a service. The implementation could not go in
  `Common.Infrastructure` in any case: that project takes no
  `FrameworkReference` and `IHttpContextAccessor` arrives with one, so
  `Common.Web` is the only building block that can hold it. Both are
  registered by `AddCommonWebDefaults`, beside the `AddHttpContextAccessor()`
  without which `ValidateOnBuild` fails instead of the first ownership check.
- **`Identity:Authority` is an eager read that throws naming the key, not an
  options type.** §15.4 says `ServiceIdentityOptions` is deliberately the
  *only* options type in the solution and argues why; a second bag bound to a
  section holding one value is the shape that rule forbids. §12.4's fixture
  comment claimed `OptionsValidationException` here and was amended. The
  audience is a **constant** for the neighbouring reason — §11.5 gives the
  platform one audience, so the value never varies between environments, which
  is §15.4's own test for what is not configuration.
- **The GET stays anonymous, permanently, and says so.** PR-10's README named
  the whole slice as a temporary gap; only the write path was one. §10.2's
  `catalog-public` route matches GET alone and names `anonymous`, YARP's
  reserved value — it carried no `AuthorizationPolicy` at all until ADR-030
  made a route saying nothing inherit the fallback,
  so a product listing is public at the edge and public here. The group fails
  closed with `RequireAuthorization()` and the GET adds `AllowAnonymous()`
  explicitly — absence and decision must not look the same.
- **`WebApplication` adds the authentication middleware itself, so no test can
  catch `app.UseAuthentication()` being deleted.** §4.2's ordering table said
  its absence 403s every authenticated request and §12.4 named a 401 test as
  the thing that catches it; both were checked by deleting the line, after
  which every test in the repository still passed. Keep the explicit calls —
  they are about **order**, they are required by any host that is not a
  `WebApplication`, and an implicit pipeline is unreviewable — but do not
  believe a test is watching them.

  **The claim stops at deletion, and a review round found the table promising
  more than that.** Auto-insertion is suppressed by the markers the explicit
  calls set, so it repairs an *omission* and not an *ordering*: both calls
  present in the wrong order means authorization evaluates against a `User`
  nothing has populated, and every authenticated request 401s. Measured through
  a real `WebApplication` over three pipelines — correct 200, **reversed 401**,
  neither 200. So the framework protects a host from forgetting a line and not
  from misplacing one. `Common.Web.Tests` carries all four claims, the third
  being a regression guard on the framework and the fourth this one.
- **The realm is a full Keycloak export and shrinking it is a silent
  catastrophe.** A hand-written import naming only the `commerce-api` client
  scope is the obvious first attempt; Keycloak treats `clientScopes` as the
  **complete** set, so the built-ins are never created and the token loses
  `sub`, `preferred_username`, `email` and `realm_access` at once. `sub` is the
  one that matters — `ICurrentUser.Id` reads it. Found by importing exactly
  that file into a container and reading a token, which is also how the shipped
  realm was verified. **Build a realm through the admin API and export it; do
  not write one.**
- **Permissions are client roles on a `commerce-api` client, not realm roles.**
  Measured, not assumed: a realm-role mapper also emits `offline_access`,
  `uma_authorization` and `default-roles-commerce` into the `permission` claim,
  which puts Keycloak's internals into the platform's vocabulary and makes it
  open-ended. The negative half is what the verification turned on — an
  ungranted user must carry **no** `permission` claim at all.
- **`TestAuthHandler`'s constant is `SchemeName`.** `AuthenticationHandler<T>`
  already declares a protected `Scheme`, so §12.4's printed `public const
  string Scheme` hides it, and CS0108 is an error under ADR-019. The sample had
  been unbuildable since it was written; the same collision bit a second time
  inside a nested probe handler, where `Scheme` silently bound to the base
  property instead of the enclosing constant.

**Four more arrived from the review loops, and all four are about things no
test in the repository was watching.**

- **A `ProjectReference` is a `COPY` line in two Dockerfiles, and forgetting it
  breaks the images silently for as long as nobody runs one.** `dotnet restore`
  writes each project's own `obj/project.assets.json`, so a csproj absent when
  it runs is not restored and the `--no-restore` publish fails four steps later
  with `NETSDK1004` naming a project the Dockerfile never mentions. PR-14 drew
  `Catalog.Infrastructure → Common.Contracts` and `→ Common.Infrastructure`
  without the two lines, and **both images were unbuildable from PR-14 until
  PR-16 found it by running the stack**. `dotnet build Platform.slnx` cannot
  see this, and neither can CI: the compose smoke is the only job that builds
  these images and it is path-filtered on `deploy/compose/**`, while a
  reference lands under `src/`. Fixing the filter is a real option and a wider
  change than this PR; the honest state is that the gap is named in both
  Dockerfiles and in §15.2, and carried by whoever adds the next reference.
- **Keycloak's issuer follows the request host unless `KC_HOSTNAME` says
  otherwise, and both halves of the fix are load-bearing.** A token minted
  through `localhost:8080` and a discovery document read through
  `keycloak:8080` disagree about `iss`, so `ValidateIssuer` rejected the exact
  token `deploy/compose/README.md` tells a developer to obtain — on a stack
  where every container reported healthy. `KC_HOSTNAME` pins the frontend
  issuer and `KC_HOSTNAME_BACKCHANNEL_DYNAMIC` keeps the JWKS URI
  container-reachable; **one without the other trades one broken flow for
  another**, which is why they arrive together. Measured on the master realm
  rather than argued.
- **A host-run service is Production, and that is what breaks the inner
  loop.** No project ships a `launchSettings.json`, so `dotnet run` selects
  Production, where `RequireHttpsMetadata` is on — and against a plain-HTTP
  local authority the host never fetches the discovery document at all.
  `ASPNETCORE_ENVIRONMENT=Development` leads **every host-run block that names
  an authority** — Catalog's and, since PR-17, the gateway's, but not the
  migrator's, whose job never sees a token. This line said "both host-run
  blocks" and PR-17 made it false by adding a third: the gateway snippet went
  out without the export and did not start when pasted into a clean shell,
  which is what a rule stated as a count rather than as a reason costs. The
  containers set it, which is precisely why the Compose path never showed it.
- **`ICurrentUser`'s implementation reads one authenticated projection, not
  `HttpContext.User`.** Claims and authentication are independent: a
  `ClaimsIdentity` with no authentication type carries claims perfectly
  happily and still reports `IsAuthenticated` false, so members reading the
  principal directly answered a subject and granted a permission for a caller
  the interface denies. Nothing reaches it today — `JwtBearerHandler` produces
  an authenticated principal or an empty one — which is the argument for
  fixing a fail-closed contract while it is still theoretical rather than the
  argument against.

**One finding against the scaffold procedure** — `CLAUDE.md`'s when this was
written, `docs/repo-map.md`'s since — worth keeping because it cost work: the
cleanup it prescribes ends with
`git checkout -- Platform.slnx deploy/compose/`, which is correct only while
the PR does not itself change `deploy/compose/`. PR-16 changes all three files
in that tree, and the cleanup reverted them. **Commit before dogfooding the
scaffold**, or restore the tree's own changes afterwards.

## PR-15 — the consume side

PR-15 landed the consume side — §9's remaining contracts, §9.5's inbox, §9.4's
two consumers and one retention purge over both tables — and eight of its
decisions bind what comes after:

- **The contract assembly is complete, and §3.2 is what decided that.** Five
  versioned namespaces, twenty-six records and two static vocabularies —
  every name in §3.2's Publishes and Accepts columns plus the payload types
  §9.1 and §9.6 give them. This suspends the usual rule that a record belongs
  in the PR whose code publishes it, and Appendix C is what suspends it: the
  §12.6 suite constrains the assembly as a whole, so the rules "arrive with the
  assembly they constrain". **It is not licence to keep adding.** A sixth
  service's contracts arrive with that service.
- **`InboxFilter<T>` and both consumers are `Common.Infrastructure`, not
  per-service, and the chapters were amended to match.** §9.4 and §9.5 write
  `namespace Ordering.Infrastructure.Messaging` for the same reason §9.4 used
  to write `ordering.OutboxMessages` — the chapter is Ordering's viewpoint.
  Nothing in any of the three is per-service; what *is* per-service is which
  endpoint binds which contract, and that stays in each service's
  `AddMassTransitMessaging`.
- **The filter's `DbContext` is an alias, and the delegate in it is
  load-bearing.** `AddScoped<DbContext>(sp => sp.GetRequiredService<CatalogDbContext>())`
  is the registration; `AddScoped<DbContext, CatalogDbContext>()` compiles,
  resolves, and builds a **second** context in the same scope — so the inbox
  row commits in its own transaction and §9.5's atomic row silently becomes its
  non-atomic one. Nothing fails, which is why a test asserts the two
  resolutions are one instance.
- **Catalog binds no receive endpoint, and that is asserted rather than
  assumed.** §3.2 gives it one Consumes cell — `StockLevelChanged`, Inventory's
  — and no `IIntegrationEventHandler` for it exists until §8.4's cache
  invalidator has a cached query to invalidate. Binding a type with no handler
  is one of the two sites §9.4 says must throw, so the endpoint would fault
  every message it received. This is PR-14's `Local`-lane shape exactly: the
  consumers are proven by the in-memory harness in `Common.Infrastructure.Tests`,
  and the inbox and purge by container tests over the real host.
- **The inbox table ships to every service anyway, for `AddOutbox`'s reason
  inverted.** The purge runs from first boot and deletes from both tables, so a
  service carrying it without the table logs a failed delete every pass —
  where a dispatcher without its table logs a failed claim twice a second.
  Consuming nothing does not exempt a service; Catalog itself is the proof.
- **The inbox row is staged *after* the consumer returns, and staging it
  earlier is a silent disabling of the whole mechanism.** A row added before
  `next.Send` is a tracked entity on the context the consumer also uses, and
  every message-borne command reaches §6.3's `TransactionBehavior` →
  `EfUnitOfWork.ExecuteAsync` → `db.ChangeTracker.Clear()`, PR-09's line. The
  clear takes the pending row, the following `SaveChangesAsync` writes nothing,
  and no command is ever recorded. Two mechanisms already here, each right on
  its own, in tension where they meet — and invisible until a consumer does
  work, which is why the covering test drives one that clears the tracker.
- **A rolled-back unit of work now clears the tracker too, and the comment that
  said it need not is the lesson.** `EfUnitOfWork` returned on a failed
  `Result` leaving the rejected mutations tracked, because "§6.3's behaviour
  declines to SaveChanges … which is enough for tracked changes" — true while
  that behaviour was the *only* caller of `SaveChanges` on the scope. The inbox
  filter is the second, and it saves unconditionally, so a domain refusal
  would have committed its own mutations outside the rolled-back transaction.
  **A premise about who calls a method is falsified by the next PR that calls
  it**, and this one was.
- **`ProcessedAt IS NOT NULL` on the outbox purge is load-bearing, and is
  tested as such.** Purging on age alone deletes the abandoned rows §13.6's
  alert exists to surface — permanent data loss presenting as a clean, empty
  table. The inbox purges on age alone and the asymmetry is deliberate: an
  inbox row records completed work, so there is no unfinished state for a
  predicate to protect, and what protects it is a window that must outlast the
  broker's longest redelivery. Both windows are a registered `RetentionPolicy`
  rather than constants, because §9.5 tells the reader to check one of them.

**Two findings PR-15 made against the blueprint rather than against the code**,
both fixed in the chapters:

- **§12.6's round-trip assertion could not pass as written.**
  `ShouldBeEquivalentTo` compares the object graph, and a collection expression
  assigned to an `IReadOnlyList<T>` compiles to a synthesised read-only list
  where `System.Text.Json` returns a `List<T>` — a difference that is nowhere
  in the wire format. The suite compares the two **serialised** forms instead,
  because the wire form is what a contract actually is.
- **That comparison has a blind spot, and it takes a second test.** A member
  that fails to serialise at all is absent from both forms, so the contract
  loses a field and the round-trip stays green. A companion assertion requires
  every declared public property to appear in the JSON.

## PR-14 — the outbox

PR-14 landed the outbox — §7.5's flow end to end, §9.4's dispatcher, §9.3's
allow-list mapper — and six of its decisions bind what comes after:

- **`Common.Contracts` exists, with two files.** Appendix C put the project at
  PR-15 and it could not wait: `OutboxMessage.Stage` reads
  `message is IIntegrationEvent`, `MessageTypeMap` selects on that interface,
  and an allow-list mapper with an empty registry could not carry §12.4's
  "the domain type never reaches the broker" — which is only checkable
  because the contract and the domain event have different names. PR-15 adds
  the remaining records to a project that exists rather than creating one.
- **A value object on the `Local` lane needs a `JsonConverter`, and its
  absence is silent.** §5.3's `Money` is a `readonly record struct` with a
  private constructor and two get-only properties; `System.Text.Json` does not
  refuse that shape, because a struct always has a parameterless constructor —
  it builds the default, finds no setter, and returns `Amount = 0` with a null
  `Currency`. Two fixes were tried and rejected: `[JsonConstructor]` puts
  `System.Text.Json` in a domain assembly, which §4.2's allow-list gate names
  as forbidden, and a public constructor does not even work, because for a
  struct the implicit parameterless one still wins. The fix is
  `MoneyJsonConverter` in `Catalog.Infrastructure`, beside the
  `ComplexProperty` mapping that already persists the same type as two
  columns. `OutboxJson` is therefore a **registered instance taking its
  converters**, not a static field: the converters are half of what "both
  sides must agree" means. Verified red by deleting the registration.
- **`ProjectionRegistry`'s memo is a container-scoped singleton, not a static
  field.** §7.5's argument — DI registrations do not change at runtime — holds
  for one container and fails for a process holding several: two
  `WebApplicationFactory` hosts in one test assembly would share whichever
  answer was computed first, so the suite proving an event with no handler
  stages no `Local` row would poison the suite proving that one with a handler
  does.
- **`OutboxDispatcher` is registered with `AddHostedService<T>`, and the
  generic overload is load-bearing.** It records an `ImplementationType`,
  which is what `CatalogApiFactory` matches on to remove *only* this hosted
  service — MassTransit's bus is one too, so `RemoveAll<IHostedService>()`
  would stop the broker. A factory registration leaves `ImplementationType`
  null and that removal would match nothing, leaving the dispatcher draining
  rows underneath the assertions about them.
- **Catalog registers no projection handler and stages no `Local` row**, and
  that is asserted rather than assumed — it is the `IProjectionRegistry`
  contract observed from outside. §8.4's cache invalidator needs a cached
  query to invalidate and there is not one yet, so the lane's behaviours are
  proven by domain events and handlers in `Catalog.TestSupport`, admitted to
  the map through `MessageTypeSource.Add` — the mechanism §9.4 designed that
  type for.
- **The outbox schema is a registered `OutboxTable`, not a SQL literal.** §9.4
  writes `ordering.OutboxMessages` into code every service shares, which
  cannot be right; a dispatcher per service would be §9.3's prohibition on a
  second outbox table set arriving by the back door. The schema is
  shape-checked, because it is the one identifier interpolated into a
  statement rather than parameterised.

## PR-13 — the bus

PR-13 landed the bus — `AddMassTransitMessaging` in
`Catalog.Infrastructure/Messaging`, the RabbitMQ registration of §9 with no
consumer on it yet — and five of its decisions bind what comes after:

- **The helper is per-service, in the `Redis/DependencyInjection` shape.** It
  is where each service's consumers, sagas and receive endpoints will be
  configured (§9.6 registers Ordering's saga inside it), and it keeps
  MassTransit out of `Common.Infrastructure` until PR-14's outbox — the first
  common code that names a MassTransit type (`IPublishEndpoint`).
- **Broker readiness is MassTransit's own health check.** `AddMassTransit`
  registers `masstransit-bus`, tagged `ready`, itself — verified in the 8.5.3
  source — so no health-check line exists for the bus and the
  `AspNetCore.HealthChecks.Rabbitmq` pin is gone: its parameterless
  `AddRabbitMQ()` resolves an `IConnection` nothing registers, a latent
  defect §13.5 now documents. `WaitUntilStarted` stays false; readiness
  carries the wait, and `DatabaseSmokeTests` polls ready to 200 to prove the
  bus connects against a real broker.
- **`ConnectionStrings:RabbitMq` is read eagerly and throws naming the key**
  (the `AddSqlServer` posture), so every host over `Program` — fixtures
  included — must supply one; `ServiceFixture` therefore carries a RabbitMQ
  Testcontainer beside SQL, and `CatalogApiFactory` takes both connection
  strings.
- **Usage telemetry is off.** MassTransit 8.5 reports anonymous usage data to
  a vendor endpoint by default; `DisableUsageTelemetry()` is called with the
  argument in the registration — §13.2 owns this platform's telemetry.
- **The harness smoke proves composition; the readiness poll proves the
  transport.** `AddMassTransitTestHarness` replaces an existing
  `AddMassTransit` bus with the in-memory transport (verified at the pin), so
  `MessagingRegistrationTests` proves the helper composes and the pipeline
  delivers — and deliberately not the `UsingRabbitMq` half, which the swap
  removes and `DatabaseSmokeTests` asserts against a real broker. A
  test-local record carries the smoke: no contract invented before
  `Common.Contracts` existed, no retry policy before the receive endpoints
  it attaches to (§9.8).

## PR-12 — §8 as code

PR-12 landed §8 as code — `Common.Infrastructure`, the fourth building block,
one `Redis/` folder — and five of its decisions bind what comes after:

- **`Common.Infrastructure` has no project references, and that is a claim to
  preserve.** Nothing in the Redis helpers names a domain or application
  type, so no edge is drawn — the `Common.Application ↛ Common.Domain`
  argument, one project over. PR-14's outbox is what draws edges here;
  drawing one earlier is inventing a dependency the code does not have.
- **The Redis tracing instrumentation lives in `AddRedisConnections`, and can
  never move to `Common.Web`.** The connections are keyed services; the
  parameterless `AddRedisInstrumentation()` discovers only an unkeyed
  `IConnectionMultiplexer`, so in `AddObservability` it would silently
  instrument nothing — and the package reference would hand
  `StackExchange.Redis` to hosts with no Redis. §13.2 says this; the sample
  there deliberately does not show the call.
- **No service is wired.** Catalog gained no Redis env vars, no readiness
  checks and no cached query — caching a read before ADR-018's invalidation
  machinery exists (PR-14) would teach the defect §8.4 exists to prevent.
  The helpers are proven by their own Testcontainers suite, the same shape
  as PR-04's dispatcher landing three PRs before its first service. The
  Redis keys join the Compose file with the PR whose code first reads them.
- **The key prefix is `ApplicationName` verbatim — no normalisation.** One
  source shared with §13.2's `service.name`, nothing to drift. §8.3's
  lowercase examples show a service whose ApplicationName is `catalog`, not
  a lowering rule. `RedisKeys` has deliberately no `Cache(string)` method:
  cache keys are prefixed by `InstanceName`, and a full-key builder would
  double-prefix the moment its result reached `HybridCache`.
- **`RemoveByTagAsync` works at this pin, verified.** §8.4's invalidation
  mechanism was proven by the container suite two PRs before its consumer —
  along with the mandatory TTL on the lock (refused before any I/O), the
  token-checked release (a stale handle must not delete the next holder's
  key), and the span tests — one per keyed connection — which force the
  `TracerProvider` the way a host's startup would because a raw
  `ServiceProvider` runs no hosted services.

## PR-11 — the scaffold

PR-11 landed the scaffold of §4.5 — `tools/new-service/new_service.py`, stdlib
Python, one command per service — and six of its decisions bind what comes
after:

- **Catalog is the template, read at run time.** There is no template
  directory, so there is one copy of the wiring rather than two that drift, and
  the scaffold's tests render *this* repository. The consequence is stated in
  `docs/repo-map.md`'s scaffold section and worth repeating here: a Catalog
  change can turn `tools/new-service`'s suite red, and reconciling the script
  belongs in the same change.
- **The scaffold copies no domain.** The slice is excluded by name, so a new
  service is PR-07's state with the wiring accumulated through PR-16 on it —
  five service projects, three test projects and a `TestSupport` library
  (§4.1 calls that last one *not* a test project, and counting it as one is a
  drift a review has already caught here), both images, the Compose pair, the
  `InitialCreate` migration with `AddOutbox`, `AddInbox` and
  `AddOutboxRetentionIndex` beside it, the bus
  registration with its harness smoke, §9.4's outbox and §9.5's inbox wired and
  empty, the retention purge over both tables, PR-16's token validation and
  `TestAuthHandler`, and no aggregate.
  Five things arrive with the first real slice, each noted at the line
  concerned in the generated code: `Dapper`, the application-test container
  wiring, the two silent-scan registration tests, the permission constant with
  the policy that names it and `AuthorizationPolicyTests` beside them, and —
  with the first domain event — §12.4's round-trip assertion and a
  `JsonConverter` for any value object that event carries.

  **The middleware stays and the policies go, and the split is the point.**
  `UseAuthentication`/`UseAuthorization` are copied because §11.2 says every
  host validates its own tokens whether or not it has an endpoint; a
  `{Service}Permissions` constant and the policy registered from it leave with
  the slice, because a permission nothing requires is a name in the realm
  nobody can act on.
- **The outbox and the inbox ship with their tables, which is why `AddOutbox`
  and `AddInbox` are copied
  rather than dropped with Catalog's other migrations.** A service carrying
  the dispatcher without its table would log a failed claim twice a second
  from its first boot; one carrying the retention purge without the inbox
  table would log a failed delete every pass, and consuming nothing does not
  exempt it. The snapshot is EF's own description of the model that
  leaves — the **last** migration's designer with the aggregate's `Entity(...)`
  block
  removed, which is the one edit made to a machine-owned file here. **The last
  one, and taking an earlier one is a defect with no symptom until the
  service's first `migrations add`**: the outbox designer knows nothing of the
  inbox, so the snapshot would omit a table the `DbContext` maps and EF would
  emit a second `CreateTable` for one the scaffolded migrations had already
  created. Verified
  rather than argued, the same way PR-11's empty snapshot was: a scaffolded
  service was built, `migrations add` was run against it, the generated `Up`
  came out empty and EF's rewritten snapshot was byte-identical to the emitted
  one. Two details were found only by that diff — EF sorts `System` usings
  **before** everything else, which a plain alphabetical sort got wrong the
  moment a `System` using first appeared, and `System.Collections.Generic`
  leaves with the aggregate, because EF emits it for the
  `Dictionary<string, object>` a `ComplexProperty` is mapped as.
- **`AssemblyMarker` runs the other way, and it is easy to state backwards.**
  The scaffold **emits** it — a service with no domain type has nothing for the
  two §4.2 gates to name — and the first aggregate is when it is **deleted**
  and the gates re-anchor, which is what PR-10 did to Catalog's when `Product`
  arrived. It does not "arrive with the first slice"; it leaves then. Seeing
  one in a service that *has* an aggregate is a defect, not a convention.
- **The template has no single line ending, and a tool that reads it must not
  assume one.** `.gitattributes` forces `*.cs text eol=crlf`, so C# is CRLF on
  every machine — but `.csproj`, `.slnx`, the Compose YAML, the Markdown and
  the Dockerfiles carry no attribute and arrive CRLF on Windows and **LF on the
  Ubuntu runner**. The scaffold's first version spelt its anchors with CRLF,
  passed on the machine that wrote it and matched nothing in CI. Anchors are LF
  now, matched against normalised text, with each file's own endings restored
  on the way out. Anything else in this repository that reads a file as text
  and looks for a literal line has the same trap waiting.
- **The generated model snapshot is EF's own output, not a hand-written copy.**
  It is derived from `InitialCreate.Designer.cs`, which already holds the
  tool's description of an empty model with a default schema. Verified rather
  than argued: a scaffolded service was built, `dotnet ef migrations add` was
  run against it, the generated `Up` was empty and EF's rewritten snapshot was
  byte-identical to the emitted one. Two details were found only by that diff —
  EF sorts its `using` block by namespace (so `;` must not participate in the
  sort), and the sort order changes when the service name passes `Microsoft`.

## PR-10 — the first vertical slice

PR-10 landed the first vertical slice — `Product`, `PublishProductCommand`,
`GetProductsQuery` with §6.5's cursor pagination, the two Dockerfiles, the
Compose pair on port 5102 and the `docker-compose.infra-only.yml` override
(profiles technique, printed in §14.1) — and five of its findings bind what
comes after:

- **`ValidationExceptionHandler` is §10.5's 400 row, found by the first real
  endpoint.** Until PR-10 nothing translated `ValidationBehavior`'s thrown
  `ValidationException`, and the wire answered 500 for a malformed request.
  The handler lives in `Common.Web`, registered by `AddCommonProblemDetails`,
  and §10.5 now names it — the chapter previously implied the translation
  without showing it.
- **Locally there is one `sa` login and two configuration keys.** §7.1's
  callout used to claim Compose seeds both logins; §14.2, §12.4's fixture and
  the shipped Compose file all collapse the logins and keep the keys apart,
  and §7.1 was amended to match. The identity split is a cloud-side control;
  the key split is what every local environment exercises.
- **`Catalog.TestSupport` exists**, because PR-10 was the second consumer §4.1
  was waiting for (not PR-16, as `CLAUDE.md` once guessed): the handler tests
  live in `Catalog.Application.Tests` per §12.1 and share `ServiceFixture`
  with `Catalog.Api.Tests`. It is a Library, so it references
  `xunit.v3.extensibility.core` — `xunit.v3` itself refuses non-Exe output.
- **The compose smoke now builds images.** The application blocks carry
  `build:` stanzas, so the path-filtered workflow compiles the solution inside
  Docker; PR-10 raised its timeout to 25 minutes, **PR-17 raised it again to
  30** for the gateway's image, **PR-18 raised it to 40** for Ordering's pair
  and **PR-19 to 45** for the BFF's — six images, five minutes each on top of
  the 15 that pulls alone cost, the workflow header carrying the reason every
  time. The number lives
  in `.github/workflows/compose.yml` and is restated here, which is what makes
  it a claim to reconcile rather than a fact to read: it went stale the moment
  a third image joined, stayed stale for four review rounds, and went stale
  again in the very branch that raised it — this sentence was still saying 30
  while the workflow said 35, found by Grok round 4.

  **Then the raise itself was wrong, which is the more useful failure.** 35
  came from adding PR-17's +5 again, where PR-18 adds *two* images and owed
  +10; both stated rules — `30 + 2 × 5` and `15 + 5 × 5` — give 40, and the
  header said "two more take the same five minutes each" directly above the
  35. Copilot round 9 found it. **A count in a comment guards nothing until
  somebody multiplies by it**, and a sentence explaining the guard is the
  easiest thing in the file to read as already-checked. A change under `src/`
  alone does not re-run the workflow — per-service CI builds are PR-25's.
- **Chiselled images take the `-extra` tag, and the suffix is load-bearing.**
  Plain chiselled runs globalization-invariant and `Microsoft.Data.SqlClient`
  refuses to open a connection under it — found when the containerised
  migrator first ran, fixed in both Dockerfiles and §15.2's samples. Every
  later service image inherits this: `-extra` is ICU and tzdata, nothing
  else. Verified live: `up --wait` treats a `service_completed_successfully`
  one-shot as satisfied on exit 0 and failed on exit non-zero, so the smoke
  asserts the migrator's exit code for free.

## PR-09 — TransactionBehavior, and the retry fix it shipped

PR-09 landed §6.3's `TransactionBehavior` and did **not** draw the
`Common.Application → Common.Domain` edge — the behaviour reads
`ModifiedAggregateCount` as an `int` and calls `DispatchAsync(CancellationToken)`,
so neither signature names a domain type. PR-14 drew it, with §7.5's
`IDomainEventCollector`, exactly as predicted — and the argument survives the
edge: `TransactionBehavior` still reads an `int`, because counting behind the
port is what keeps EF's change tracker on Infrastructure's side of §4.2. A
reference existing is not permission to start using it. PR-09 brought
`IDomainEventDispatcher` forward as an interface only, over Catalog's
`NullDomainEventDispatcher`, which PR-14 deleted.

PR-09 also shipped PR #15's retry fix — `db.ChangeTracker.Clear()` at the top
of every `EfUnitOfWork.ExecuteAsync` attempt, so a transient fault cannot
re-run the domain method on attempt 1's tracked, already-mutated aggregates
and commit the mutation twice. Both halves are tested: a strategy subclass
retrying a marker exception proves the delegate re-runs and the raw write
commits once, and the identity-map half — attempt 2 must read committed
state, not attempt 1's mutation — is asserted through a **test-only
`IModelCustomizer`** that maps a `TrackedProbe` entity onto the fixture's
probe table in the retry tests' own `DbContextOptions` and nowhere else. That
was first deferred to PR-10 as needing an entity type; a Copilot review on
PR #18 pushed back, and the customizer is the answer that costs neither a
production model change nor snapshot drift. The technique generalises: a test
that needs an entity the model does not have swaps the customizer, never
edits `CatalogDbContext`.

**Two standing facts, restated here rather than left in commit bodies:**

- **Raised events are no longer dropped, and PR-14 picked them up without
  touching `Product`** — which is what the aggregate raising anyway between
  PR-10 and PR-14 bought. Every `Product.Publish` now reaches §9.3's
  allow-list and commits a `Broker` row in the same transaction as the
  product. What is still dropped is the *`Local`* lane: Catalog registers no
  `IProjectionHandler`, so §7.5 stages no row for one, and that is asserted
  rather than assumed.
- **`IdempotencyBehavior`'s seat was reserved rather than filled.** PR-09
  added the third behaviour and left the fourth's place *between* Validation
  and Transaction, with the registration comment naming it.
  `PublishProductCommand` carries no `CommandId` for the same reason — §6.4
  warns the field without the interface is unprotected, so both join with
  §8.5's PR. **How many behaviours the pipeline registers today is
  `CLAUDE.md`'s**, not this entry's: it changes when §8.5 lands, and a count
  in two places is one to reconcile.

**What PR-09's line does not fix is the commit-acknowledgement race**, and that
stays open past it on purpose. If `CommitAsync` succeeds on the server and
the connection drops before the ack, the strategy retries work that is already
durable, and no in-process tidying can tell those two states apart. Closing it
needs an idempotency marker written *inside* the transaction — §8.5's
`IIdempotentCommand` already carries a usable `CommandId`, but
`IIdempotencyStore` is Redis-backed and outside the transaction, so a Redis
claim is not atomic with the SQL commit. **PR-14 did not close it, and changed
what it costs rather than leaving it unexamined**: with the outbox in place a
lost acknowledgement republishes the same fact, which is the at-least-once
delivery §9.4 promises and §9.5's inbox is built to absorb — a duplicate
rather than an invisible double-apply. The SQL-side marker is still the fix
for the *command*, and it belongs with §8.5's `IdempotencyBehavior`, whose
seat between Validation and Transaction is already reserved.
**PR-32 wrote it** — the marker lands in §6.3's own transaction and is read at
the top of it (#113, ADR-037), so the race this paragraph leaves open is closed
and the paragraph stands as the record of how long it was open.

## PR-08 — the persistence layer

PR-08 landed the persistence layer, and three of its decisions bind what comes
after:

- **Catalog has a connection string, so it has a readiness check** (§13.5), and
  a host with no `ConnectionStrings:Catalog` no longer starts —
  `AddSqlServer` throws on a null one. Every `WebApplicationFactory` over
  `Catalog.Api` supplies one; `CatalogApiFactory` — in `Catalog.TestSupport`
  since PR-10 — is the single place that does it.
- **The migration is hand-authored and the snapshot is not.**
  `20260808035156_InitialCreate.cs` was rewritten into house style, because it
  is a file people edit — §7.4's hand-written DDL rides in its `Up`, and
  IDE0161 fails the build on the block-scoped namespace EF generates. The
  `.Designer.cs` and `CatalogDbContextModelSnapshot.cs` beside it carry an
  `auto-generated` header that exempts them from the analysers and are left
  **exactly** as the tool wrote them: the snapshot is the input to the next
  `migrations add`, and an edited one produces a wrong migration a PR later.
- **`dotnet test` needs Docker from here on.** Persistence is what made it
  true: Catalog gained a connection string and a real migrator run, so its
  container-backed suites cannot be satisfied by a fake. Each such suite owns
  its collection and therefore its own container set, which is §12.4's stated
  price. **The live list of which projects need a daemon is
  [`testing.md`](testing.md)'s** — it has grown twice since PR-08, and this
  entry records the decision rather than the tally. It was `CLAUDE.md`'s
  commands section until that section became a short form deferring to
  `testing.md`, which is where the five projects are now named.

---

## The boundary that was only ever a sentence (#44)

**Decision.** The broker is an authorisation boundary. Each service
authenticates as its own account and its `write` is scoped to what its own
source addresses, declared in `deploy/compose/rabbitmq/definitions.json`,
imported at broker start and held to the code by a gate
([ADR-036](backend-architecture/adr/ADR-036-the-broker-has-a-per-service-identity.md)).
`guest` is not created.

**Why.** [§9.4](backend-architecture/09-messaging.md) stamps a broker-borne
command `CommandOrigin.System` because it arrived on the service's own command
queue, and §11.4 skips the ownership check for one. The chapter's own callout
said the quiet part — arrival "is only as restrictive as the broker's
authorisation", and "this chapter does not specify one" — so the platform had a
control whose strength was documented as zero and left there.

**Measuring it made it worse than the issue claimed.** #44 said "one shared
principal". Read off the image rather than assumed: `guest` is tagged
`administrator`, and `rabbitmq:4.1-management-alpine` ships
`loopback_users.guest = false` under the comment *"allow access to the guest
user from anywhere on the network"*. `rabbitmqctl environment` reported
`{loopback_users,[]}` and both services connected from container addresses. One
principal, administrator, reachable from anywhere on the network.

### What building it cost, and every bit of it was a measurement

**The permissions were wrong three times, and each correction came from running
it rather than reading it.**

The first was one token too strict. `Common\.Contracts\.` misses
`Common.Contracts:IIntegrationEvent` — MassTransit's polymorphic exchange,
which every publisher declares — because that name has a colon where the
pattern wanted a dot. **It was in the topology capture the whole time and got
read past.** The lesson this repository already carries about a pattern one
token too strict, arriving through a permission instead of a metric name.

The second is the sharper one. `MassTransit:ReceiveFault` never appears on a
healthy stack, so no capture of a working system could show it — it surfaced
only because a deliberately forged message faulted a consumer. **A runtime
capture shows what RAN, not what CAN run.** That is also why
`payments-commands` is derived from `Endpoints.cs` and not from a broker: the
saga never reached the payment step, because there is no Inventory service to
answer the stock reservation that precedes it. **The code is the inventory; the
broker is a sample of it.**

The third was a verb. `queue.bind` takes `read` on the destination exchange, so
sending to a peer's queue needs more than `write` — found as a refusal with
`write` already granted.

**The exploit was attempted rather than argued, and the first attempt lied.**
As `catalog-svc`: `ConfirmOrder` onto `ordering-commands`, a forged
`OrderPlaced`, a `ReserveStock`, a forged `PaymentAuthorised`. The probe
reported all four ACCEPTED. They were not — the broker log showed four
refusals. `basic_publish` on AMQP 0-9-1 is fire-and-forget, so a refusal
arrives as a channel exception *after* the publish returns, and a probe that
publishes and closes cannot see it. `confirm_delivery()` is what makes the
measurement a measurement. **A negative test that cannot observe the negative
reports the property as absent** — and it fails in the direction that reads as
a security hole, which is at least the loud direction.

A positive control ships with it, because a probe that is refused everything
because the credential is broken proves nothing.

### The suite found the thing the design had not

Running `Ordering.Api.Tests` produced exactly the failure mode ADR-036's own
Dockerfile comment describes: a refused publish, retried for ever, with the
suite healthy and silent until it timed out forty minutes later naming a
message rather than a permission. The cause is that **the harness impersonates
services that do not exist** — §9.6's saga is driven by Inventory, Payments,
Shipping and Catalog events, and the tests publish them through the host's own
bus, as `ordering-svc`.

**The widening went into the harness and not into `definitions.json`**, and the
reason is one this repository already paid for elsewhere: loosening the
deployed artefact so a test can pass leaves the gate agreeing with a permission
set nothing deploys, which is a double that cannot disagree with itself. So the
production shape stays honest and the exception is visible in the fixture.

**What that costs is a claim, and the claim was narrowed rather than kept.** An
earlier draft of ADR-036 said `dotnet test` "exercises the real permission set".
It exercises `configure` and `read` — the half that rots as endpoints are added
— and not `write`. The negative property rests on the direct measurement and on
`check_permissions.py`, and the ADR now says so in those words.

### The fixture race the second suite paid for

Making Catalog's fixture build the broker image — it had used the stock tag,
which carries no definitions — gave both suites the same image name, on the
reasoning that sharing one image per machine beat dragging a second onto every
runner. Docker would have been fine with that. **Testcontainers writes the
build context to a tar named after the image**, so the two raced on
`ashamray-test-broker-4-1-delayed.tar` and the loser threw *the process cannot
access the file*.

**The symptom named neither the file nor the fixture.** Whichever suite started
second failed EVERY test in under 100 ms while passing alone, and it was not
always the same suite — a fixture fault wearing a suite-wide failure. The tell
is arithmetic rather than a message: a failure count equal to the suite's size,
at a duration too short to have run anything. **One image name per fixture was the first fix, and it was the wrong axis.**
It was measured green locally and failed on CI, because the unit is the
PROCESS: `Catalog.Api.Tests` and `Catalog.Application.Tests` both instantiate
that one fixture class and `dotnet test` runs them as separate hosts, so they
raced each other under the new name exactly as they had under the old one — 60
failures in 128 ms and 11 in 51 ms, with `Cannot locate specified Dockerfile`
where Windows had said the file was in use. A per-class name is per-process
only while the class has one caller, which is a premise about callers rather
than a property of anything.

**What ended it was declining to build.** Catalog needs the broker's
*configuration* — the accounts of #44 — and not ADR-021's delayed-exchange
plugin: it runs no saga and schedules nothing. So its fixture maps
`definitions.json` and `20-commerce.conf` onto the stock image and no build
context tar exists to be raced for. Ordering still builds, because the plugin
leaves it no choice, and it has one consumer today — written down where the
next caller will read it rather than assumed.

**The mapping is a second copy of the Dockerfile's COPY targets, so it is
gated.** A drifted path fails in the silent direction: the broker boots without
the definitions, seeds `guest` on an empty database, and Catalog's suite passes
green against the single shared administrator #44 exists to remove.

### What is owed

Three residuals, stated in ADR-036 rather than implied. `configure` cannot be
exclusive, because a consumer declares the exchange it binds. `read` on a
peer's command endpoint grants the consume along with the bind, because a
RabbitMQ permission pattern cannot tell a queue from an exchange of the same
name — so Ordering can consume Inventory's commands, and only Inventory
existing makes that observable. And provisioning the accounts on a deployed
broker is an obligation this repository states and does not check, on
[§15.4](backend-architecture/15-cicd-deployment.md)'s own terms and
ADR-033's.
