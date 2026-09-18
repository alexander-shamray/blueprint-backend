# Change locality — the plan

**Goal.** A bugfix or small feature touches its own slice and nothing shared.
Code is the source of trust; documents state rules and reasons and cite code
for facts. Several agents work the repository at once, including inside one
service, without meeting in the same Markdown file.

This is a plan for review, not the operating contract. Step 0 below turns the
contract half of it into a file agents read; the rest is the sequence of PRs
that makes the contract true.

## 1. What was measured

**Measured on 2026-09-03 against `main` at 371b99e, the merge of #185.**
Every figure in this section and the next is a record of that day, per the
contract's §2, and nothing here is kept current.

Over the 25 merged PRs before that merge (`#135` to `#182`), files ranked by
how many PRs edited them:

| File | PRs of 25 |
|---|---|
| `CLAUDE.md` | 25 |
| `docs/testing.md` | 22 |
| `docs/pr-decision-log.md` | 21 |
| `appendix-a-adrs.md` | 18 |
| `appendix-d-type-inventory.md` | 16 |
| `09-messaging.md` | 15 |
| `appendix-c-delivery-plan.md` | 12 |
| `12-test-strategy.md` | 12 |
| `11-identity-authorization.md` | 9 |
| `docs/roadmap.md`, `docs/lessons.md`, `15-cicd-deployment.md` | 8 each |

The median PR changed about 25 files, roughly half of them Markdown. The
branch open on that day (#185, a fix to one hook script and one helper)
edited nine files, and four were shared documents: `CLAUDE.md`,
`docs/harness-boundaries.md`, `docs/lessons.md`, `docs/testing.md`.

Inside the code tree the same pattern shows at a smaller scale. Ordering's
`OrderingDbContextModelSnapshot.cs` was in 8 of 25 PRs and each service's
`ServiceFixture.cs` in 5 to 7. Those are the per-service mutexes two agents on
one service will meet at.

## 2. Why it happens

1. **`CLAUDE.md` carries state that moves with every PR.** Its phase section
   is a paragraph per landed PR (PR-30 through PR-37 on that day), plus counts
   (thirty-three projects, 1,119 tests, 874 and 245 by category) that are
   restated and nothing recomputes. It is loaded into every session and edited
   by every PR.
2. **The one rule is "reconcile every site", and the corpus has many sites per
   fact.** A retry count, a type name or a window lives in `Program.cs`, in a
   fenced sample, in prose in two chapters, in Appendix D and in the decision
   log. The rule is right that they must agree; the fix chosen each time was
   to edit them all, which is the mutex.
3. **History leaks into the specification.** Chapters carry "since PR-NN",
   "this sentence used to say", and closed-issue citations — 61 `#NNN` refs in
   §9 alone, 27 `PR-NN` refs in §4. Appendix C has a 288-line "After the plan"
   section that is a changelog. The ADR trail already is the history; the
   chapters retell it.
4. **Appendix D became an argument store.** 190 rows, 46 of them over 400
   characters. A row for a port carries the reasoning of three ADRs. Any
   member change re-opens that row, and the row is not the owner of the rule
   it argues.
5. **Append-only files that are one file.** All 43 ADRs are in one file, the
   decision log is 6,279 lines under 42 headings, `docs/lessons.md` has no
   headings at all. Two agents appending to the same tail is a guaranteed
   conflict, and an append-only record does not need to be one file.
6. **The review tooling enforces the churn.** `/review-branch` counts "phase
   markers, test counts" as findings. `/validate-blueprint`'s first pass is
   numeric drift across 22 files. The Grok and Copilot loops flag a stale
   count and the agent edits it, which is how a hook fix ends up editing
   `CLAUDE.md`.
7. **`docs/testing.md` restates what `Platform.slnx` and the workflows already
   say** — which suites exist and how each runs. It is edited whenever a suite
   is added, which is most PRs.
8. **Fenced samples are copies of code that now exists.** A chapter sample was
   the specification before `src/` existed. Now it is a second copy that
   drifts on every refactor and is reconciled by hand.

## 3. The target model

The model — the source-of-trust order, the rule, the change classes with
their touch sets, the partitions and the mutex surfaces — is
[`docs/change-locality.md`](change-locality.md), and this plan does not
repeat it: a second copy here would be a restatement written under a rule
against them. What this section keeps is the reasoning the contract states
without arguing.

**Why an owner per fact rather than a tour.** The old method kept a value
consistent by copying it and visiting every copy; every copy was then a site
the next change had to visit, and two agents touching the same fact met in
the same file whatever slice each was given. With one owner and citations
there is no second site to tour, so the obligation is withdrawn rather than
relaxed.

**Why Class A holds no shared Markdown.** Class A is the target for most
bugfixes, and the one document it may edit is the alert-owned runbook whose
operator action the change alters. That is the whole point of the plan.

**Why two agents fit inside one service.** Handlers, queries, validators and
their tests are scanned in by `PluggableInterfaces`, so a new slice needs
none of the per-service mutexes the contract names, and two agents meet only
when both need the same one.

**Why `.claude/**` is one agent.** `settings.json` self-locks and `ship.md`
is one file.

**Why two mutexes are removed rather than listed.** A file every PR edits
is a mutex for as long as it keeps that shape, so step 2 rewrites
`CLAUDE.md` and step 3 splits `appendix-a-adrs.md` rather than naming either
as a surface to queue on.

## 4. The work, in order

Each step is its own PR and is itself a Class D change — the contract's row
for D covers every tree the steps below touch — except step 8, which is
Class B per chapter. Steps
marked ∥ can run in parallel with each other once their prerequisite has
merged. Nothing here is done inside a feature PR.

### Step 0 — land the contract and stop the bleeding

Prerequisite for everything else. After it, feature PRs may already skip the
tour.

- Write `docs/change-locality.md`: the source-of-trust order, the rule that
  every fact has one owner, the change classes with their touch sets, the
  partitions and mutex surfaces, the procedure, and a ten-line checklist.
  It replaces `agent-locality.md`, which is untracked and is deleted.
- `CLAUDE.md`: replace the one-rule section's "grep the whole blueprint"
  paragraphs with a pointer to that file. Nothing else in `CLAUDE.md` moves
  yet; step 2 rewrites it whole.
- `.claude/commands/pr.md`: the body table gains `| Class | A |` and
  `| Touch set | … |` rows.
- `.claude/commands/review-branch.md`: remove "phase markers, test counts"
  from what counts as a finding; add "a file edited outside the declared
  class's touch set" as one.
- `.claude/commands/review-grok.md` and the adjudicator profile: a stale
  count, a "since PR-NN" sentence, or a restated value is rejected as a
  non-finding when the owner site is correct. Same for `/review-copilot`'s
  verification step.
- `.claude/commands/validate-blueprint.md`: numeric drift is chapter against
  the code symbol, never chapter against chapter; counts of tests, projects
  and chapters are out of scope; the command runs after Class C or an edit
  to a file in its scope — a chapter or appendix, `docs/roadmap.md`,
  `docs/testing.md` — and not after Class A.

Touch set: `docs/change-locality.md` (new), this plan, `CLAUDE.md`, six
files under `.claude/commands/` — the five above and `ship.md`, whose checks
step selects by class — one subagent profile, and one helper that reads the
two body rows for the review commands. Done when a Class A PR opened after
it passes both review loops without editing Markdown.

### Step 1 — freeze the changelogs ∥

- `docs/pr-decision-log.md`: a header saying the log is closed at PR-37, and
  that the record from here is commit bodies and PR bodies. No entry is ever
  added. Existing entries are not edited.
- `appendix-c-delivery-plan.md`: "After the plan" gets the same closing note.
  A rule that moves is an ADR, and an ADR is the row.
- `docs/roadmap.md`: frozen the same way; it laid a calendar over a plan that
  is complete.
- `docs/lessons.md`: frozen as written. A new lesson is a test or a gate
  whose subject is what the gate looks at, which is what the file's own kept
  rule says; the doc entry, if any, is one paragraph under a new `##`
  heading at the end. Rare enough that the tail conflict is acceptable.

Touch set: four headers. Done when no command's checklist names any of the
four.

### Step 2 — rewrite `CLAUDE.md` as a primer

Target under 200 lines. Keeps: what the repo is, the two-artefact framing and
the build order, the tree as a locator with one line per entry, the trust
order and class table by pointer, the command block, the build policy (exact
pins, ADR-019 analysers, the three suppressions), the short style list, the
command index. Drops: the phase section entirely, every count, every
"measured" anecdote, every "this sentence used to say", and the paragraphs
that explain why an earlier version of the file was wrong.

Anything in the dropped text that is a live rule with no other owner is moved
to its owner first — a chapter section, an ADR, `docs/repo-map.md` or
`docs/harness-boundaries.md` — in the same PR, and the PR body lists each
move. The test for "live rule": would a Class A agent act differently for not
knowing it. Most of the phase section fails that test.

Touch set: `CLAUDE.md`, plus the owner files that receive a moved rule. Done
when a grep of `CLAUDE.md` for `PR-[0-9]`, `[0-9],[0-9]{3}` and `Since ` is
empty.

### Step 3 — one file per ADR ∥

Move each ADR to `docs/backend-architecture/adr/ADR-0NN-<slug>.md`; Appendix A
becomes a table of number, title and link, kept by hand (or generated by
`/new-adr`). Cross-references of the form
`appendix-a-adrs.md#adr-042--…` are rewritten once, by script, and
`/check-links` verifies. `/new-adr` writes the new file and the index row.

Two agents each adding an ADR then touch two new files and one index row
apiece, and the index row is the only meeting point.

Touch set: Appendix A, the new directory, every file with an ADR anchor (a
one-time rewrite), `/new-adr`, `/check-links`. Done when `/check-links` is
green and Appendix A has no body text.

### Step 4 — retire Appendix D as a write surface ∥

D.1 to D.4 list types that `src/` defines. An agent answers "does this type
exist" with one `rg`, and the compiler answers it for every sample that is
also code. Those sections go. D.5 (deliberately not shown) and D.6 (framework
types) stay only for names that still appear in a sample and nowhere in
`src/`; check each.

The prose in the 46 long rows is triaged one row at a time: a rule goes to
the chapter section or ADR that owns it if it is not already there; history
goes; an argument already made in an ADR is replaced by the citation. This is
the one step with real reading in it, and it is why Appendix D is its own PR.

Generating Appendix D from reflection was considered and is not proposed: the
output would duplicate `Glob src/**` and the check it was built for (a sample
naming a type nothing defines) is now a build error.

Touch set: Appendix D, the receiving chapter sections, `README.md` index. Done
when no command or chapter tells an agent to update Appendix D.

### Step 5 — the locality gate ∥

`.github/locality-gate/` in the house pattern (Python, tested, then run in
`ci.yml`, on `pull_request` including `edited`):

- reads the PR body's `| Class |` and `| Touch set |` rows and the diff's
  file list, and fails unless the body carries exactly one row of each —
  the pair `pr-locality.sh` already requires, because half the metadata
  makes half the gate impossible;
- holds the class → allowed-path map in one YAML file the gate reads, and
  `docs/change-locality.md`'s table becomes a citation of that file, so the
  map exists once;
- checks each diff path against **both**: the class's map, and the touch set
  the author declared. A Catalog Class A change that also edits Ordering is
  inside the class map and outside its own declared set, and it is the
  second check that catches it;
- reads a `+`-joined class as the union of its members' maps, so `C+E` is
  one lookup per member and no duplicate map entry;
- fails the PR on a file outside either, naming it;
- its own test's subject is what it looks at: a PR with a class row and a
  file outside the set must be observed red before the gate is trusted.

Done when it has been observed red on a deliberate violation and the map file
is the only place the table lives.

### Step 6 — shrink `docs/testing.md` and trim `docs/repo-map.md` ∥

`docs/testing.md` keeps what cannot be read from `Platform.slnx` or the
workflows: which five projects need Docker and why a skip is refused, the
Python 3.12 floor, how to run a gate on its own outside CI, the coverage
filter. The suite enumeration and per-suite invocations are replaced by a
sentence saying where they live. `docs/repo-map.md` keeps the shape arguments
and loses the history of how each entry got that shape.

Done when adding a test project requires no edit to either file.

### Step 7 — strip history from the chapters ∥ (one PR per chapter)

Per chapter, in this order by measured churn: §9, §12, §11, §15, §8, §13,
§4, then the rest. Each PR:

- removes "since PR-NN", "used to say", "this paragraph said", and the
  correction narrative around a rule; the rule stays, stated once, in the
  present tense;
- replaces a closed-issue citation with nothing, or with the ADR that closed
  it; an open issue stays cited as a residual;
- reduces each fenced sample to the lines that carry the rule, and names the
  file in `src/` that holds the rest; a sample longer than roughly forty
  lines that is a copy of a source file is a citation, not a sample;
- keeps every callout that states a rule or a decision.

§9 first and alone, one agent, because it is the highest-churn chapter and
its PR sets the pattern the rest copy — section 7's third answer. Then one
agent per remaining chapter, in parallel. `/validate-blueprint` runs on
each, scoped to that chapter's references, and `/check-links` after all of
them.

Done per chapter when the greps in step 2's exit test are empty for it.

### Step 8 — code owns every value a chapter quotes ∥ (per chapter, Class B)

For §8, §9, §11, §15: each raw integer a chapter states that also exists in
code gets a named constant or option in code if it does not have one, and the
chapter names the symbol. No behaviour change, one chapter per PR, after that
chapter's step 7. The `validate-blueprint` numeric pass from step 0 then has
something to check against.

### Step 9 — split the compose file and the secret allow-list

`deploy/compose/docker-compose.yml` gains `include:` of one file per service
so a service's environment lands in its own file. The secret-scan allow-list
is checked for whether entries can be scoped per tree the same way.

Done when no deployable unit's Compose environment shares a file with
another's, and no single file under `.github/secret-scan/` holds suppressions
for two trees.

The criterion is about environments, not files: which Compose and
allow-list files stay shared is the contract's
[§4](change-locality.md#repo-wide-mutex-surfaces), and why the allow-list
splits per tree rather than per service is
[`.github/secret-scan/allowed/README.md`](../.github/secret-scan/allowed/README.md)'s.

## 5. Where this differs from `agent-locality.md`

Grok's draft is mostly adopted: the trust order, the five classes, the
partitions, the mutex list, and "never write a count". Three departures:

- **No `docs/changes/` directory.** A file per change is a new corpus that
  each PR must write and that nothing reads. The house already has arguing
  commit bodies and a PR body form; `git log` and `gh pr list` index them.
  The decision log is frozen, not replaced.
- **Appendix D is retired, not generated.** Argued in step 4.
- **ADRs split into files.** Grok kept one file; the churn table says it is
  the fourth hottest file and a per-file layout removes the conflict
  outright.

## 6. What is deliberately not touched

- `docs/superpowers/` stays frozen.
- No ADR is edited; step 3 moves text verbatim.
- `.claude/settings.json` is the harness's own boundary and its deny list
  is lifted and restored by the repository owner, never by a step; no step
  here depends on its content, and none edits it.
- The blueprint's rules do not change anywhere in steps 0 to 9. This is a
  change to where facts live and who may write them, not to what they say.

## 7. Questions for review

`docs/change-locality.md` is written on the three recommendations below.

1. Class A forbids all Markdown. Should a runbook edit be Class A when the
   fix is in the alert that runbook covers, or stay Class D as written?

   **Recommended: allow it, as an owned file rather than a shared one.** A
   runbook is one file per alert, so two agents on different alerts never
   meet in it — except the declared sharers: `SHARED_RUNBOOKS` in
   `deploy/observability/check.py` maps two error-rate alerts to one
   `error-rate.md`, and that file is a mutex between them. The touch set for
   Class A and B therefore includes the runbook the alert maps to when the
   PR changes what an operator does for it, and nothing else under `docs/`.
   The contract below is written that way.

2. Step 3's ADR split rewrites anchors across the corpus once. Acceptable as
   one large mechanical PR, or would you rather keep one file and accept
   tail conflicts?

   **Recommended: the one mechanical PR, sequenced before step 7.** The
   rewrite is a script over a fixed anchor shape and `/check-links` proves
   it; what makes it cheap is timing, because a chapter PR in flight would
   conflict on every citation it carries. Land it while no chapter PR is
   open, which is before the step 7 fan-out and after step 1. Keeping one
   file leaves the fourth hottest file hot for every future rule change.

3. Step 7 is seven-plus PRs of careful reading. Run them as parallel agents
   from the start, or sequence §9 first as the pattern the others copy?

   **Recommended: §9 first, alone, then the rest in parallel.** The first
   chapter settles the judgement calls the others inherit: which issue
   citations stay as residuals, how short a fenced sample can be before it
   is a citation, and where a stripped correction narrative's rule is
   restated. §9 is the largest and hottest chapter, so its review loop
   surfaces nearly every case. Its PR body records those calls, and the
   fan-out prompt for the remaining chapters quotes them.
