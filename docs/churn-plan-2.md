# Churn, measured again — the second plan

**Goal.** The same as [`churn-plan.md`](churn-plan.md)'s: a reviewer reads
the diff, and every line in it that is not the change is a line they cannot
approve or refuse on its merits. The first plan measured the corpus at #204,
sequenced eleven steps, and every step landed (#205 to #225 and #236). This
file measures what the corpus does *now* that those rules are in force,
names the churn they did not reach, and sequences the work that stops it —
in the same form: measured on a named commit, one PR per step, an exit test
per step.

The rules already written are not repeated here. The contract's §2 owns the
rule for documents, the style guide's *Comments* section owns it for code,
and [`.github/comment-gate/`](../.github/comment-gate/README.md) enforces
the mechanical half of the second. What this file adds is the evidence that
a second half is needed, what that half says, and the order in which the
corpus is brought under it.

## 1. What was measured

**Measured on 2026-09-30 against `main` at 2c616def, the merge of #323.**
Every figure here is a record of that day, per the contract's §2, and
nothing in this file is kept current. A bracketed figure is the earlier
side of a comparison. In the history table it is the same measure taken
over the pull requests through #204, and it differs from the first plan's
own figures wherever the method does; elsewhere it is the first plan's value.

### The history since the first plan

The 74 pull requests merged after #204 carry 735 commits:

| | |
|---|---|
| Commits by type | `fix` 282 · `docs` 237 · `feat` 88 · `test` 59 · `chore` 32 · `refactor` 20 |
| Per pull request | 9.9 commits, 3.8 of them `fix` (14.0 and 7.2 through #204); 22 of the 74 carried 15 or more |
| Files re-edited four or more times inside one PR | 2.4 per PR (4.2), and 1.7 over the last 36 |
| What a `fix` commit touched | code only 225 · Markdown and code 54 · Markdown only 3 (133 · 378 · 178) |
| Size | mean +1,695 −452 over 21 files; 13 of 74 changed more than 3,000 lines (31 of 96) |

The first plan's rule did what it was for. Four `fix` commits in five used
to edit a document; now one in five does, and the file re-edited round after
round inside a branch is half as common. The `fix` count per PR halved and
then stopped falling: 3.9 over #205–#255, 3.7 over #256–#323. What a review
round changes now is code files — and, inside them, mostly comments.

### What a review round moves

Every added and removed line in the 735 commits, by the commit's type and
what the line is:

| Commit type | Lines | C# code | C# comment | Script code | Script comment | Markdown |
|---|---|---|---|---|---|---|
| `fix` | 25,423 | 12% | **23%** | 45% | 9% | 7% |
| `test` | 11,376 | 22% | **42%** | 11% | **25%** | 0% |
| `feat` | 56,480 | 60% | 24% | 8% | 2% | 2% |
| `refactor` | 28,469 | 13% | 21% | 50% | 15% | 0% |

A `fix` commit — which here is a review round — moves two C# comment lines
for every C# code line, and a `test` commit is two-thirds comment. Of the
282 `fix` subjects, 107 say what a comment now *says*: "the idempotency
double's remark cites §8.5 for SET NX", "dispatcher and handler-lifetime
comments name the ValidateScopes…", "the idempotency double's remark drops
its atomicity caveat". More than a third of the rounds since #204 argued
about the wording of a comment rather than the behaviour of the code.

### The comments, after the sweeps

Across the C# corpus, 33,571 of 97,919 lines are comment lines (26,942 of
69,260), 18,297 of them `///`. By tree: the building blocks 55% (59%), the
BFF 63% (58%), the gateway 45% (64%), Ordering 40%, Catalog 34%, Inventory
30%, Shipping 27%, Payments 26%, the common tests 23%. Python carries 5,458
`#` lines in 38,152 plus 6,627 docstring lines; shell 1,566 in 3,857; YAML
2,273 in 5,129.

The first plan's patterns, counted again over every comment line in C#,
Python, shell and YAML:

| Pattern | Lines |
|---|---|
| A pull request or issue number | 220 (642) |
| Copilot, Grok, CodeQL, "found in review", "round" | 158 (467) |
| "used to", "went stale", "no longer", "previously", "this comment" | 218 (406, on the first plan's pattern, which differs) |
| `**bold**` | 38 (846) |
| Comment blocks of ten lines or more, `///` included | 944 |
| `<summary>` blocks | 2,147, of which 343 one-liners |
| `<remarks>` blocks | 721, of which 232 cite no section, ADR or `cref` |

Two things the counts show that the first plan could not:

- **The gate's ceiling became the target.** Comment blocks by length: 198
  are nine lines, **298 are exactly ten**, 84 are eleven. A block written
  under a limit of ten is written *to* ten. The limit shortened the longest
  blocks and lengthened the rest.
- **The comments do not describe the code.** A comment whose content words
  mostly reappear in the three code lines below it — the "what the next
  line does" comment the phrase *remove obvious comments* means — occurs
  once as a `//` block in the whole corpus, and 24 times among the 343
  one-line summaries (`/// The exit code of the first real migration run.`
  above `FirstRunExitCode`, in four fixtures). What the corpus holds instead
  is argument: a `<remarks>` that states the invariant, the alternative not
  taken and the section it comes from, correctly, at ten lines, on a member
  whose name and one citation would have carried it.

Three specimens, each within the rule as written:

- `IdempotencyMarkerTable.cs` opens with twenty lines that argue why a
  third type exists rather than a third property, "on `InboxTable`'s own
  argument" — the argument is made once in `InboxTable` and once more here.
- `ShippingDbContext.cs`'s conventions summary explains that the conventions
  "landed while the model had no properties at all, and that timing was the
  argument", then explains why it does not list the tables — a history and
  an argument about its own shape, in a summary.
- Every `Commands.cs` record in `Common.Contracts` carries a `<remarks>`
  that restates §9.1, §9.4, §9.5 and §9.6 in one paragraph, per command.

### Where the files churn

Files by the number of commits touching them since #204, and the number of
pull requests those commits belong to:

| File | PRs | Commits |
|---|---|---|
| `docs/backend-architecture/15-cicd-deployment.md` | 13 | 35 |
| `deploy/canary/test_canary.py` | 7 | 32 |
| `.github/workflows/deploy.yml` | 8 | 28 |
| `deploy/canary/canary.py` | 4 | 28 |
| `docs/harness-boundaries.md` | 14 | 27 |
| `deploy/compose/README.md` | 15 | 26 |
| `deploy/helm/smoke.sh` | 9 | 23 |
| `.github/workflows/ci.yml` | 9 | 21 |
| `docs/repo-map.md` | 15 | 19 |
| `tools/new-service/scaffold/patch.py`, `test_new_service.py` | 8, 6 | 19, 19 |
| `tests/Shipping.TestSupport/ServiceFixture.cs` | 5 | 18 |
| `CLAUDE.md` | 14 | 17 |
| `.claude/commands/ship.md` | 6 | 17 |
| `deploy/helm/common/templates/_helpers.tpl` | 7 | 17 |
| `Inventory.Infrastructure/DependencyInjection.cs`, Shipping's | 6, 4 | 16, 16 |
| `deploy/canary/canary.json` | 7 | 16 |
| `.claude/scripts/git-rebase-onto-main.sh` | 4 | 15 |

And the files re-opened four or more times inside one branch: `canary.py`
27 times over three PRs, `test_canary.py` 26 over three, `deploy.yml` 22
over three, §15 21 over five — and eleven plans under
`docs/superpowers/plans/`, each re-edited 11 to 18 times inside the one PR
that added it (#219 took 20 commits, #221 16, #281 21) to land a document
that is frozen from the moment it merges.

Three shapes sit behind that table.

**One deployable, nine files.** Adding Shipping to the deployment touched
`deploy.yml`, `canary.py`, `test_canary.py`, `canary.json`, `smoke.sh`,
`_helpers.tpl`, the compose index, §15 and `repo-map.md`; `smoke.sh` names
the service 76 times, §15 20, `_helpers.tpl` 13. Each of #231, #256 and
#316 opened the same set, and every round that sharpened the canary's
verdict re-opened its script, its suite and the chapter together. This is
the first plan's "one gate, four documents", moved to the deployment.

**The scaffold multiplies.** Catalog is the template, so what Catalog
carries every service inherits: its 34% comment share, its
`ServiceFixture.cs`, its `DependencyInjection.cs`. There are now five
fixtures of 721 to 1,258 lines and five composition-root extensions of 192
to 254, each edited in most of its service's PRs, and a fix to the shared
shape is a PR per copy — #238 ("sibling services' broker fixtures"), #257
("the template stops rendering its own history into every service"),
#265, #266 and #267, one per service, each dropping the same template
history from the same rendered comments.

**A service is a pull request.** #226, #235 and #294 each landed a service
from the scaffold — 105, 97 and 145 files, +9,173, +8,128 and +10,932 lines,
17, 20 and 24 commits. #309 and #313 followed with 26 and 35 commits, 13 of
the latter `fix`. A diff of a hundred files is reviewed a file at a time
across twenty rounds, and each round's fix re-opens the composition root
and the fixture.

## 2. Why it happens

1. **The rule was about what a comment may not say, not how much it may
   say.** The first plan's patterns were the mechanical half and they are
   gone: 38 bold spans where there were 846. A block may still argue for
   ten lines, so it does, and what it argues is correct, cited and long.
   A correct ten-line argument is as much a claim surface for the next
   reviewer as an incorrect one, and 33,571 lines of claims is what the
   reviewers are given to find fault with.
2. **The review loops are pointed at claims, and comments are the densest
   claims in the tree.** `/ship` runs until a round is clean; a reviewer
   told to find a stale or imprecise statement finds the comment before the
   code, because the comment states more. 107 rounds since #204 were
   closed by re-wording a comment, and a re-worded comment is the next
   round's finding. The gate cannot see this: an added line that names no
   reviewer and no history passes it however many times it is re-phrased.
3. **The scaffold copies prose.** Its templates are Catalog's files, and
   Catalog's files were written to carry the argument for every mechanism
   they touch. Rendered five times, one comment is five comments and one
   fixture is five fixtures, and the locality that keeps a service's PR in
   its own slice keeps a template fix in five PRs.
4. **The deployment has no owner per deployable.** The service's name is
   written into the workflow matrix, the canary's config, the smoke
   script's cases, the Helm helper's map, the compose index and the
   chapter's inventory. §15 describes the ladder and then lists who is on
   it.
5. **A frozen document is reviewed as if it were live.** A plan under
   `docs/superpowers/plans/` is a pre-build record the moment it merges and
   is never edited after; the loops review it for prose, wording and
   completeness across sixteen to twenty rounds first.
6. **A service's first PR is the scaffold's output plus its first feature.**
   The scaffold renders a buildable service, and the PR that lands it also
   carries the aggregate, the first endpoints and the first consumer, so
   the reviewers review the template's hundred files again, per service.
7. **The harness repairs itself in series.** #290, #291 and #292 are three
   consecutive PRs on `git-rebase-onto-main.sh`, each fixing the previous
   fix; `harness-boundaries.md` is in 14 of the 74 PRs because it is the
   inventory of grants and every grant edits it — by design, and the design
   is the cost.

## 3. The rules that stop it

Each rule names its owner; this file cites and does not restate.

- **A comment has a budget, not just a vocabulary.** The style guide's
  *Comments* section gains the budget: a `<summary>` is one sentence and
  says what the name cannot; a `<remarks>` exists only to cite an owner or
  to state an invariant nothing checks, and is four lines or fewer; a `//`
  block is five lines or fewer. A member whose name says what its summary
  would say has no summary. The gate's `BLOCK_LIMIT` becomes five, and the
  gate fails a `<remarks>` that cites no section, ADR or `cref`. The
  ceiling is set where a cited sentence fits and an argument does not, so
  the argument goes where the guide already sends it — an ADR.
- **A review finding against a comment is accepted only for falsehood or
  a broken rule.** The review commands — `review-copilot.md`,
  `review-branch.md` and the adjudicator `/ship`'s Grok triage dispatches —
  refuse a finding that asks a true comment inside the style guide's rules
  to be reworded, completed or expanded, and accept one showing a comment
  that is false or breaks one of those rules. `/bug-sweep`'s auditor raises
  only a false comment, because a style rule is not a defect. The one fix
  for an accepted finding is to cut. A round whose only findings are
  refused changes nothing; each loop judges the round after it on its own
  terms.
- **A round's touch set is the finding's own sites.** The primer's
  *Working in this repo* gains one line: a `fix` commit closing a review
  finding names the finding in its subject and carries that finding's fix
  and nothing else; any other edit is a second commit with its own finding.
- **The gate reports what a PR is made of, then refuses.** The comment gate
  prints, per PR, added comment lines against added code lines for C# and
  for the scripts. After the sweeps in section 4 it fails a PR whose added
  C# comment lines exceed its added C# code lines, `docs`-titled PRs and
  the sweeps excepted. The number is the one that section 1's table shows
  a reviewer never sees.
- **A template carries citations only.** `tools/new-service/README.md` owns
  what a rendered comment may say: the section or ADR that owns the
  mechanism, in one line, and nothing that argues. The scaffold's suite
  asserts the rendered service's comment share against a ceiling.
- **A deployable has one descriptor.** §15 states that a deployable is
  described once, in a file the workflow, the canary, the smoke run and the
  Helm helper all read, and names the file; the chapter stops listing
  services. The descriptor's schema is the canary README's.
- **A plan is reviewed once, for contradiction.** `/ship` and the review
  commands treat a PR that adds only under `docs/superpowers/` as one
  round, whose findings are limited to a contradiction with the blueprint
  or with another plan; wording is not a finding in a document that will
  never be edited again.
- **A scaffold PR is the scaffold's output and nothing else.** The service
  build order in Appendix C.1 stays; the first PR of a service renders the
  scaffold and stops, and its proof is that re-running the scaffold on
  `main` reproduces the tree byte for byte. The reviewers then review the
  scaffold once, in the scaffold's PR, and never again per service.

## 4. The work, in order

One pull request per step unless it says otherwise; steps marked ∥ run in
parallel once step 0 has merged. A sweep's proof is the first plan's: the
two sides of every file compared with their comments stripped, byte for
byte, and the service the scaffold renders compared the same way. The exit
test for a tree is the gate run over the whole tree rather than a diff —
which is step 0's `--tree` mode — and the tree's comment share, which the
gate prints, at or under the ceiling the step names.

### Step 0 — the budget, the review rule and the gate's report

The PR this file arrives in. It adds the budget to the style guide's
*Comments* section, the review-finding rule to each review command and to
the adjudicator `/ship`'s Grok triage dispatches, the round's-touch-set
line to the primer, and this file's row to the primer's table. In the gate:
`BLOCK_LIMIT` 10 → 5; a `<remarks>` without a citation is a finding; a
`--tree` mode that judges every comment in the named tree instead of a
diff; and the added-comment-to-added-code report, printed and not yet
failed. The gate's suite gains a case per new rule, in the form its README
requires: the subject is what the gate is looking at. Class D, plus the
`.claude/` mutex, so nothing else under `.claude/` runs beside it.

Done when the gate's suite is green, the tree mode over `src/` and `tests/`
lists the blocks the sweeps will cut, and one PR after this one shows the
report line in its check output.

### Step 1 — sweep `src/` to the budget ∥ (one PR per slice)

`Common.Domain` and `Common.Contracts`; `Common.Application`;
`Common.Infrastructure`; `Common.Web`; `Gateway.Api` and `Web.Bff`; then
Catalog, Ordering, Inventory, Payments, Shipping. In each: a summary that
restates the name is deleted; a `<remarks>` keeps its citation and its
invariant and loses its argument, and an argument that no ADR records
becomes an ADR by `/new-adr` in the same PR — the contracts' per-command
paragraph becomes one ADR cited from `Commands.cs` once; a `//` block over
five lines is cut to the sentence a reader would otherwise discover by
deleting the line. Class A for a service, B for a building block or the
hosts. Catalog's PR runs the scaffold's suite and the dogfood sequence in
`repo-map.md`, because the scaffold anchors on Catalog's comments.

Done for a tree when the gate's tree mode is empty over it and its comment
share is under 20% — the ceiling that Payments and Shipping, the two
services written most recently under the first plan's rule, sit nearest
today.

### Step 2 — sweep `tests/` ∥ (one PR per suite)

The same budget. A test's summary is the claim it checks in one sentence
and the section that states the rule; the arrangement needs no comment
because the test's name and its `Arrange` are the arrangement. The
`<remarks>` that explain how a fixture was made to fail go to the commit
body that made it. Class A for a service's suites, B for a building
block's. Done when the tree mode is empty over the suite and its comment
share is under 20%.

### Step 3 — one fixture, five services

`ServiceFixture.cs` is 721 to 1,258 lines in five copies, and the copies
differ in the service's name, its migrator and the stubs it needs. The
shared body — the container, the broker, the identity stub, the migration
run and its exit code, the outbox observation — moves to one project the
five reference; each service keeps the part that is its own. §4.1 names
`*.TestSupport` as a per-service project that is not a test project, and a
shared one is a rule moving, so this step opens with an ADR that says
where the shared fixture lives and why the per-service ones keep their
name. Class B, with the ADR in the touch set. Done when every suite that
was green before is green after, and a fixture change is one file.

### Step 4 — the scaffold renders the budget ∥

After step 1's Catalog PR. The templates are re-anchored on the swept
Catalog; `tools/new-service/README.md` states what a rendered comment may
say; the suite gains an assertion on the rendered service's comment share,
with the same 20% ceiling. Class D. Done when the rendered service is
byte-identical to the swept Catalog with the names substituted, and the
suite's new case fails on a template that argues.

### Step 5 — the deploy descriptor

One file per deployable under `deploy/` — the canary's config already
holds a row per service, so the row grows into the descriptor and the
others read it: the workflow's matrix from the descriptor list, the smoke
run's cases from the descriptor's declared probes, the Helm helper's map
from the descriptor's names. §15 states the rule and names the file, and
its inventory of services goes. The canary's README owns the schema.
Class D+E, because the workflow and the chapter are D's and the Helm chart
is E's. Done when adding a deployable is one descriptor plus its chart, and
a test in `test_canary.py` asserts that every descriptor the workflow
reads is one the canary and the smoke run also read.

### Step 6 — the compose index stops being an inventory ∥

`deploy/compose/README.md` is in 15 of 74 PRs because it describes each
unit under `services/`. Each unit's file carries its own one-line header;
the README keeps the model — one baseline, one file per unit — and the
command that lists them. `repo-map.md`'s entry cites the README. Class D.
Done when a new unit touches its own file and nothing under `deploy/compose/`
besides.

### Step 7 — a plan is one round ∥

`ship.md` and the review commands gain the rule in section 3: a PR whose
diff lies wholly under `docs/superpowers/` gets one review round, and only
a contradiction is a finding. `docs/harness-boundaries.md` is read first
and gains nothing, because no grant moves. Class D, plus the `.claude/`
mutex. Done when the next plan PR merges in one round, or in two with a
contradiction named.

### Step 8 — the scaffold PR is reproducible

The service after Notifications is not on the plan, so this step lands as
a rule and a check rather than a service: `tools/new-service/README.md`
states that the scaffold's PR is the scaffold's output, and the scaffold's
suite gains a `--verify` that renders into a temporary directory and
compares against a named commit's tree. Class D. Done when the check passes
against Shipping's scaffold commit with the hand edits it carried listed
as the known difference — a record, as of that commit, of what the rule
would have refused.

### Step 9 — the gate refuses

After steps 1, 2 and 4 the report line in step 0 becomes a failure: added
C# comment lines above added C# code lines fails the PR, `docs` PRs and
PRs labelled as a sweep excepted. The suite's case for it asserts the
message and the exemption. Class D. Done when a deliberately over-commented
PR is refused by the check and the sweeps' PRs were not.

### Step 10 — the harness fix arrives with its failing test

`git-rebase-onto-main.sh` was fixed three PRs running. The primer's
*Working in this repo* gains one line: a fix to a script under `.claude/`
lands with the case in its suite that failed before it, and a script with
no suite gets one before its second fix. `harness-boundaries.md` states
nothing new. Class D, plus the mutex. Done when the next fix PR to a
script under `.claude/` carries its case, and `/ship`'s step 2, the checks,
refuses one that does not: step 0 runs before a fresh run has a diff.

## 5. What is deliberately not touched

- **The per-service `DependencyInjection.cs`.** It is the registration list
  and a feature adds to it by design; 192 to 254 lines is read whole, and
  splitting it per slice would only move the composition root §4.2 says is
  one file.
- **`harness-boundaries.md`.** It is the inventory of grants, edited by
  every grant on purpose; the cost is the design and the design is right.
- **The ADRs, the decision log, the lessons file, `docs/superpowers/`** —
  frozen or append-only, as the first plan said. An argument cut from a
  comment in step 1 that no ADR records becomes a new ADR, never a longer
  comment and never an edit to an old one.
- **The commit bodies already written.** They are where the sweeps send a
  reader for how a line came to be, and `git log -L` finds them.
- **The first plan.** It is a record as of #204 and stays one; this file
  does not amend it.

## 6. Questions for review

1. **Whether the C# ceiling is 20% or lower.** The two newest services sit
   at 26% and 27% under the first plan's rule with no sweep; the building
   blocks at 55%. Twenty is a floor the corpus can reach by cutting
   argument alone; fifteen would need the summaries on private and test
   members to go, which question 1 of the first plan left open and this
   plan closes by deleting the ones that restate the name.
2. **Whether the comment-to-code ratio should fail `test` PRs.** A test
   commit is two-thirds comment today; under step 2's rule a test's comment
   is one sentence and a citation, and a suite PR would pass the ratio. If
   the sweep shows suites that cannot, the exemption widens to `test` and
   the ceiling does the work instead.
3. **Whether a shared fixture is a building block.** Step 3 puts it under
   `tests/` as a project the five `*.TestSupport` reference; the other
   choice is `src/BuildingBlocks/Common.TestSupport`, which ships test code
   in the building-block tree and which §4.1 would have to admit.
4. **Whether the descriptor lives in `deploy/canary/` or beside the chart.**
   Step 5 grows the canary's row because it exists; the alternative is a
   file per deployable under `deploy/helm/<service>/`, read by the canary,
   which keeps the deployable's facts beside its chart and moves the
   schema's owner to the Helm README.
