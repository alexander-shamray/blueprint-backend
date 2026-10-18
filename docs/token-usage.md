# Token usage — the measurements

What [`tools/token-usage/`](../tools/token-usage/README.md) reported, and
the other measurements the plan's steps rest on, one dated section per
measurement, for [`token-plan.md`](token-plan.md)'s
baseline and each step's exit test. Every figure is a record of its day,
per the contract's §2, and none is kept current.

## 2026-10-07 — a cloud session against `main` at 0163787

**Not the baseline.** Transcripts live on the machine that ran the
sessions, and this one held only the session that wrote the plan; the
`/ship` and sweep runs the baseline needs are measured where they ran.
Two figures from it still move the plan's estimates:

| | |
|---|---|
| A session's first turn | 67.9k tokens of context before any file is read — the harness's system prompt and tools, `CLAUDE.md`, and the descriptions |
| A subagent's first turn | 35.7k for an `Explore` agent given a one-line task, before its one tool call |

The plan's subagent arithmetic uses the second as its base. That session's
51 responses re-sent 6.8M tokens from the cache and wrote 0.17M to it, an
input equivalent of 1.0M; its last turn carried 203k of context.

## 2026-10-07 — the baseline, the owner's machine, all history

Measured by the report as of 371a978 over every transcript under the main
checkout's directory, with no `--since`, so it is a total rather than a
rate; the span was not yet printed. Totals: 64,114 responses in 1,631
contexts re-sent 13.3B tokens from the cache and wrote 225M to it, an input
equivalent of 1.65B; output was 16.8M.

### Who spent it

| Who | Input equivalent | Share | Contexts | Calls each | Mean context per call | Per context |
|---|---|---|---|---|---|---|
| Main sessions | 725M | 43.9% | 624 | 34 | 295k | 1.16M |
| `general-purpose` subagents | 582M | 35.3% | 584 | 46 | 174k | 1.00M |
| `bug-auditor` | 258M | 15.6% | 327 | 38 | 159k | 0.79M |
| `Explore` | 37M | 2.3% | 66 | 33 | 126k | 0.57M |
| `fork` | 27M | 1.6% | 8 | 64 | 464k | 3.32M |
| `Plan` | 21M | 1.3% | 20 | 55 | 157k | 1.05M |

**Subagents spent 56%**, and the largest single spender is a type no
command here grants or spawns: the commands that name `general-purpose`
deny it or argue about it, so those 584 agents were started by the model,
by a plugin's workflow, or by the owner's standing instructions in
user-level memory, which the repository does not hold. A subagent writes
its whole context to the cache once — 160–175k per `general-purpose` or
`bug-auditor` context — so a cache write, priced above input, is a fifth of
the input equivalent.

**A main session's mean context is 295k a call.** The resident text the
plan measured — about 45k for `/ship` — is a sixth of that; the rest is
the conversation itself — tool output and file reads, which the report does
not yet split.

### `/ship`

39 runs: 388M in all, about 10M a run, of which 59% is subagents —
about four `general-purpose` and two `bug-auditor` contexts a run. The
main session took 131 calls a run at a mean context of 270k.

### What the report could not attribute then

73% sits under `(no command)`: a prompt that asks for a sweep, or a plugin
workflow, loads its skill without a typed command. The report as of
938ba06 names that work `skill:<name>` and prints its span, and the next
measurement uses it.

## 2026-10-07 — since 2026-09-15, with skills attributed

Measured by the report as of 938ba06 with `--since 2026-09-15`, on the same
machine: 1.61B input equivalent, 97% of the all-history total, so the
history is in effect these three weeks.

| Workflow | Input equivalent | Share | Subagents' part |
|---|---|---|---|
| `/ship`, typed or loaded as a skill, 69 runs | 583M | 36% | 56% |
| Prompts with no command or skill | 603M | 37% | 52% |
| The superpowers plugin's skills | 216M | 13% | 78% |
| `/check-links`, 5 runs | 78M | 5% | 60% |
| `/branch`, `/commit`, `/pr`, `/review-copilot` on their own | 65M | 4% | 37% |
| Work after `/reload-plugins` | 62M | 4% | 67% |

**The subagents are not the commands'.** `/ship`'s grant names only
`review-grok-triager` and `/check-links` names no agent, yet a `/ship` run
spawned about three `general-purpose` and one or two
`bug-auditor` contexts and a `/check-links` run about nine
`general-purpose` ones: about 8.4M a `/ship` run and 15.6M a
`/check-links` run. 96 of the 327 `bug-auditor` contexts were spawned
inside `/ship` runs, which run no sweep. Inside `/ship` they are the review
rounds the owner's user-level instructions ask for — a review cycle always
runs, and with Copilot suspended each round is one read-only subagent,
repeated until a round comes back clean — so a cut to them is a change to
those instructions as much as to the repository.

**The costliest agents sat under a plugin's label.** 66 `general-purpose`
agents at 2.1M each, 77 calls apiece at a mean context of 228k, counted
under `skill:superpowers:writing-plans`; their descriptions, read in the
review below, show most of them were review and fix rounds held under a
label that stuck, not plan writing.

The report as of this commit lists single subagents with the task each was
given (`--spawns`), which is the next measurement.

## 2026-10-07 — the forty costliest subagents, since 2026-09-15

Measured by the report as of e85bce1 with `--spawns 40`; the span printed
was 2026-09-15 to 2026-10-07. The forty spent 225M, 14% of the window, at
3.9–12.2M each; grouped by the task each was given:

| Task | Agents | Input equivalent | Each |
|---|---|---|---|
| A review round's slice — "Round 19 slice B review", "Review round 9 slice A" | 14 | 72M | 5.2M |
| Writing a plan — "Write Notifications PR-1 plan" and five siblings, two more by `fork` | 8 | 56M | 6.9M |
| A comment sweep — "Sweep Ordering test suites", "Bring Payments comments under comment gate" | 8 | 46M | 5.7M |
| Implementing — "Code track 1 implementer", "Implement Task 1: consumers" | 7 | 38M | 5.4M |
| Fixing a round's findings — "Fix round 1 slice A findings" | 3 | 14M | 4.7M |

**Review loops run long.** One pull request reached round 20 of a review
the model runs with its own agents — two slices a round at about 5M each,
then agents to fix what they found — and another reached round 9 with
`bug-auditor` slices. No command sets that loop's ceiling or what a later
round reads.

**A plan costs about 7M to write**, in one agent re-reading what it has
already written for 120–200 calls.

**The `/check-links` figure above overstates it.** A label holds until the
next prompt a person types, so a `fork` that built a whole feature
("PR-5 end-to-end: Catalog stock levels") counted as `skill:check-links`;
what `/check-links` itself spends is not yet separated.

## 2026-10-07 — what the review of this file corrected

The figures above were measured by a report with faults since fixed, as of
6da22d1; they stand as the record of what that report said, and none of
them has been re-measured with the fixed one; the section after this one
is that measurement.

- **A label ended early.** A compaction summary, a `!` command's input or
  output, a local command's output and an interrupt were each read as a
  typed prompt, so a long `/ship` run lost its label partway. The review
  patched the first three out of the report and measured the same window
  on the owner's machine: `/ship` 731M (45%), not 583M (36%), and
  `(no command)` 407M (25%), not 604M (38%), about 10.6M a run over the 69.
  That is the review's patch, not a run of the fixed report.
- **Most transcript directories were not read.** A session started in a
  worktree writes its own directory: 81 under `.claude/worktrees/` and 22
  sibling forks on the owner's machine, about 167 MB beside the main
  directory's 1.4 GB, and every figure above leaves them out. The report
  now reads every directory named for the checkout followed by a dash,
  which takes in both, and names what it read.

## 2026-10-07 — the fixed report, since 2026-09-15

Measured by the report as of 6a0d524 on the owner's machine, with
`--since 2026-09-15`, over the main checkout's transcript directory and
the 103 named for it followed by a dash; the span printed was 2026-09-15
to 2026-10-07. 70,167 responses in 2,497 contexts came to 1.73B input
equivalent; output was 19.7M. One fault was still in it: `/compact`,
`/autocompact` and `/reload-plugins` typed mid-run took the label, 81M
(4.7%) of the window, fixed in 8630e15; the rows below leave that as it
fell.

| Workflow | Input equivalent | Share | Subagents' part | After a wake | Runs |
|---|---|---|---|---|---|
| `/ship`, typed or loaded as a skill | 767M | 44.5% | 55% | 53% | 78 |
| Prompts with no command or skill | 482M | 27.9% | 39% | 35% | |
| The superpowers plugin's skills | 255M | 14.8% | 81% | 80% | |
| Harness commands typed mid-run | 81M | 4.7% | 62% | 66% | |
| `/check-links` | 78M | 4.5% | 60% | 96% | 5 |
| `/branch`, `/commit`, `/pr`, `/review-copilot` on their own | 60M | 3.5% | 37% | 47% | |

| Who | Input equivalent | Share |
|---|---|---|
| Main sessions | 784M | 45.5% |
| `general-purpose` | 596M | 34.5% |
| `bug-auditor` | 259M | 15.0% |
| `Explore`, `fork`, `Plan` and the rest | 87M | 5.0% |

**The superpowers row is mostly a review loop.** A `skill:` label held
until the next typed prompt, so a loop run on wakes after a skill loaded
counted under it. By their descriptions, 67 of the 81 agents under
`skill:superpowers:writing-plans` were review or fix rounds, 165M, and 8
wrote plans, 15M; most plan-writing agents sat under `(no command)` and
`/ship`. What the plugin costs is plan writing, about 7M a plan.

**A `/ship` run is about 9.8M**, 4.6M of it before the first wake, which
agrees with the review's hand-patched 731M (45%) over fewer directories.

**`/check-links` is cheap on its own**, and the spawn descriptions, not
the wake split, show it: of the 65 agents under its label, 3 were about
links, 3M, and the rest were feature work and review rounds held under a
label that stuck after one `Skill` call, the `fork` "PR-5 end-to-end"
among them. Its own spend is at most the 2.7M before a wake and those
three, about 1.1M a run over the five.

**More than half of the window, 55%, was spent after a wake**: background
agents and CI watches reporting back, inside a run or after it.

**Attribution holds on a named run.** The latest `/ship` session,
a2177270, read with `--session`: every response, and its two `Explore`
and one `bug-auditor` subagents, counted under `/ship` in one context,
16.2M in all.

## 2026-10-08 — the code index, with and without `.codeindexignore`

Measured with `codebase-index` from PyPI in a cloud session, against
`main` at 2a15c63, each checkout indexed from scratch. Chunk tokens are
the index's own `token_est`; a search slot is one of the top five results
for twelve everyday questions, 60 slots in all, counted when it lands in
a migration or a closed record.

| `.codeindexignore` | Files | Chunks | Chunk tokens | Index | Slots on excluded paths |
|---|---|---|---|---|---|
| none | 1,994 | 16,797 | 4.32M | 39.7 MB | 7 (3 migrations, 4 closed records) |
| the owner's local file | 1,829 | 16,465 | 4.11M | 36.4 MB | 4 (closed records) |
| the tracked file | 1,759 | 12,691 | 2.79M | 28.1 MB | 0 |

**The closed records were the larger cost.** `docs/superpowers/` alone was
3,442 chunks and 1.18M chunk tokens, 27% of the index, against the
migrations' 330 chunks and 209k.

**The indexer already skips what `.gitignore` ignores**: with no file it
indexed exactly the 1,994 tracked paths, and an ignored file written into
`artifacts/`, a `bin/` or `.claude/worktrees/` stayed out after an update,
while one in untracked `staging/` went in. So a line repeating
`.gitignore` changes nothing.

**An untracked file does not reach a `/branch` worktree.** Seeded from the
main checkout's index, as `refresh-index.py` seeds it, a worktree without
the file went from 1,829 files to 1,994 on its first update, all 165
migration files back. With the file present it stayed at 1,829.

**Running the CLI by hand rewrote the tracked skill again**, the residual
`harness-boundaries.md`'s *Edit denies and agent self-protection* now
states: this session's shell lacked `CBX_NO_SKILL_AUTO_UPDATE=1`, and one
`codebase-index --help` replaced seven files under
`.claude/skills/codebase-index/`, the stock `SKILL.md` widening
`allowed-tools` from the pinned `cbx` wrapper calls to `Bash(cbx *)` and
bare `Bash(codebase-index … *)`; they were restored from `HEAD`.

## 2026-10-10 — after steps 4–6, and the costliest work since 2026-10-01

Measured by the report on the owner's machine, before it had
`mean_context`; the mean context below is that column computed by hand
from the printed ones, fresh input plus cache writes plus cache reads over
calls. Over 2026-10-09 to 2026-10-10, 5,721 responses in 306 contexts came
to 154M input equivalent; output was 2.8M.

| Who | Contexts | Calls | Mean context | Input equivalent | After a wake |
|---|---|---|---|---|---|
| Main, no command | 161 | 1,694 | 252k | 51.9M | 29.3M |
| Main, `/ship` | 16 | 1,642 | 274k | 50.5M | 23.7M |
| Main, `skill:validate-blueprint` | 2 | 405 | 477k | 20.3M | 20.3M |
| `general-purpose` under `validate-blueprint` | 19 | 971 | 140k | 17.4M | 17.4M |
| `branch-reviewer` under `/ship` | 43 | 309 | 41k | 3.2M | 1.9M |

**Most of it came after a wake**: 97.5M of the 154M, 63%, against 55% in
the window of 2026-10-07. A wake re-sends the whole context the session
had built, and a main session's call here sends 250–480k; the resident
text step 6 cuts is about 11k of a `/ship` call's 274k.

**One audit was a quarter of the span.** Its two `validate-blueprint`
sessions spent 40.6M, all of it after a wake, and the agents they spawned
include implementers — "Implement row 24: #706 and #707", 3.5M — so the
fixes ran inside the audit's context.

**What fills a main session**, from `--tools 30` since 2026-10-01: `Read`
results under no command, 2,881 of them, about 3.2M tokens; `Bash` results
under `/ship`, 6,235, about 2.1M. Each stays in the context for every later
call.

### The forty costliest subagents since 2026-10-01

From `--spawns 40`, 2026-10-01 to 2026-10-10, grouped by the task each was
given; most ran before steps 4–6 landed.

| Task | Agents | Input equivalent | Each |
|---|---|---|---|
| Writing a plan — "Write Notifications PR-1 plan" and eight siblings | 9 | 58.1M | 2.5–12.2M |
| A review round's `bug-auditor` slice — "Review round 9 slice A", rounds 1 to 11 | 14 | 57.3M | 2.5–6.4M |
| Implementing — "Code track 1 implementer", "Implement row 24: #706 and #707" | 9 | 36.6M | 2.9–6.4M |
| An audit of the blueprint — "Blueprint audit of branch topics" | 3 | 10.8M | 3.0–4.6M |
| A pre-flight scan of a plan — "Preflight scan of PR-3 plan" | 3 | 9.1M | 3.0–3.1M |
| Reviewing or fixing a pull request — "PR #412 review round 1" | 2 | 5.5M | 2.4–3.1M |

**The plans they wrote are large.** Of the 21 files under
`docs/superpowers/plans/` dated 2026-09-22 to 2026-10-03, 20 are over the
planning rule's limit in `CLAUDE.md`, at 54–236 KB, and `--docs` over the
month to 2026-10-10 counts 90–274 opens of each Shipping and Notifications
plan, most of them by subagents. None has been written since the rule
landed, so it has not yet been tested.

**The review rounds predate the loop's ceiling.** They ran on 2026-10-02
and reached round 11, before the ceiling landed in `/ship` step 5 with
step 4.

## 2026-10-11 — what a wake writes again

Measured by the report's new `woken_write` column, the part of
`woken_equivalent` that was cache writes, at 1.25× or 2× by their TTL. Over
2026-10-09 to 2026-10-11, at 557ebcec, 8,569 responses came to 198M input
equivalent, 106M of it after a wake.

| Who | Woken | Of it, cache writes | Share |
|---|---|---|---|
| Main, `/ship` | 26.7M | 1.8M | 7% |
| Main, no command | 34.6M | 2.0M | 6% |
| Main, `skill:validate-blueprint` | 20.3M | 1.0M | 5% |
| `general-purpose` under `validate-blueprint` | 17.4M | 4.1M | 24% |
| `branch-reviewer`, all three labels | 7.3M | 4.5M | 62% |
| Total | 106.3M | 13.4M | 13% |

**Cache writes are 13% of what a wake costs; the rest is the context read
back.** A write here is any cache write after a wake, the new turn's content
as well as context written again, and the column does not tell them apart.
A main session's wake is 5-7% writes, so what it pays for is the context it
re-sends at 0.1×, and compacting cuts that in proportion to the context it
sheds. A reviewer's wake is mostly writes, but a reviewer's context is small
(41k, in the 2026-10-10 section). So step 7's rule is worth what a main
session's mean context is worth (274k for `/ship`, in that section).

## 2026-10-11 — the quality sets, and a first score

Built by `tools/quality-sets/` at c294776d, from the issue tracker and the PR
review comments of that day.

| Set | Entries | Of which scorable | Pinned commits |
|---|---|---|---|
| `sweeps.json`, issues a sweep filed | 87 | 85 | 2: `5bea8b26` (51), `2a15c639` (34) |
| `reviews.json`, Copilot's inline comments on merged PRs | 1,478 | 1,478 | 546 heads over 127 PRs |

An issue is scorable when it names a commit this clone holds and a file
that exists there; the two that do not are #181 and #184, filed before
issues carried a commit. Two commits hold every scorable sweep entry, so
two sweeps score the whole set.

**The first score is a pilot, not a baseline.** `branch-reviewer` was run on
the diff of PR 187 at `ef3b03bf`, which holds 12 of Copilot's comments across
four files. The same head scored 0 of 12 on one run and 2 of 12 on the next,
from 2 findings each time, both in `.claude/agents/review-adjudicator.md`.
That is the noise a comparison has to beat, and one head is not a sample of
the 546. **No sweep was scored in this section**: a full first round was 41
slices as of c86e6ec, which this change did not spend. The 2026-10-12 section
scores both.

## 2026-10-12 — the first scored runs

Run by `tools/quality-sets/` against the sets of the section above, on the
owner's machine. The reviewer sample, the security sweep and the first bug
sweep attempt ran from a checkout at 6ec49559 holding the runner changes that
became 07376d0a; the usage-limit stop, in that commit, was added after them,
because the bug sweep is what showed it was needed. The scored bug sweep ran
from c7ef8e84, which holds it.

| Run | Entries | Found | Recall |
|---|---|---|---|
| `branch-reviewer`, 15 heads, twice each | 170 | 6 | 3.5% |
| Security sweep, round 1, at `5bea8b26`, 41 slices | 51 | 11 | 21.6% |
| Bug sweep, round 1, at `2a15c639`, 42 slices | 34 | 6 | 17.6% |

**The reviewer.** The heads are spread evenly over the pull requests that
hold at least three Copilot comments, PRs 3 to 250. Four of the 15 heads had
any hit, and on four heads the two runs disagreed. The reviewer reported 2.4
findings a run against 5.7 comments a head from Copilot.

**The security sweep.** No slice failed and the auditors reported 33
findings. A hit is any finding in one of the issue's files, so 21.6% is an
upper bound for round 1.

**The bug sweep.** The first attempt is not a score: the auditors began
answering "the weekly usage limit is almost used up" and stopped; 7 of the 42
slices returned no findings after a retry, and the run's 0 of 34 measured the
limit. The runner now ends a run on such a notice rather than retrying
through it. The run was repeated once the limit had reset. No slice failed,
the auditors reported 38 findings, and 6 of the 34 entries were found. As for
the security sweep, a hit is any finding in one of the issue's files, so
17.6% is an upper bound for round 1. Its 42 slices are the runner's cut of
the tree at its own commit; `token-plan.md`'s 41 is dated to c86e6ec.

**What the numbers do not say.** Both exits ask for "at least". Neither is
met by any of the three runs on this measure. The measure is lenient for a
sweep, which is hit by any finding in one of the issue's files, and stricter
for a review, which also needs a line within ten. The earlier sweeps ran up
to seven rounds and had a second auditor verify each issue, against one round
here.
Copilot raised small points, wording and link forms among them, which
`branch-reviewer`'s bar leaves out ("incomplete but true" is not a finding).
Reading 3.5% as the reviewer being worse by that factor would be wrong.
Reading it as meeting the exit would be wrong too. The runner does not
count tokens, so what these runs cost is still unmeasured.

## 2026-10-18 — a week after step 7, read against steps 5, 6 and 7

Measured by the report on the owner's machine at f1cb6278, with
`--since 2026-10-11`, the day step 7's runbook landed, and repeated with
`--spawns 40`, `--tools 30` and `--docs`. Over 2026-10-11 to 2026-10-18,
20,416 responses in 1,390 contexts came to 408M input equivalent, 86.5M of
it after a wake; output was 8.5M.

| Who | Contexts | Calls | Mean context | Input equivalent | After a wake |
|---|---|---|---|---|---|
| Main, `/ship` | 139 | 10,365 | 185k | 228.5M | 49.5M |
| Main, no command | 571 | 3,083 | 127k | 59.0M | 6.5M |
| Main, `skill:ship` | 11 | 871 | 305k | 29.4M | 9.5M |
| `general-purpose` under `/ship` | 270 | 2,954 | 86k | 49.0M | 11.9M |
| `branch-reviewer` under `/ship` | 240 | 1,356 | 47k | 15.2M | 4.8M |
| Every main row | 732 | 14,666 | 179k | 323.1M | 67.1M |
| Every `general-purpose` row | 339 | 3,532 | 84k | 59.1M | 12.5M |

The last two rows sum the report's rows by agent; the mean context is
fresh input plus cache writes plus cache reads over calls, as the column
computes it.

**Step 7's exit, part by part.** The span is eight days, against the two
that set the step.

- A main session's mean context is 179k, not under 150k, so step 2's exit
  does not hold here. It was about 286k on 2026-10-10, that section's three
  main rows weighted by calls.
- 21% of the input equivalent came after a wake, under 40%; it was 63% on
  2026-10-10.
- No `/validate-blueprint` run has a main session of its own in the span:
  the report has no `skill:validate-blueprint` row, because `/ship` step 2
  dispatches the audit's passes and never invokes the command. Found by the
  task each `general-purpose` agent was given, 162 audit passes ran from 63
  sessions, for 40.7M over 2,750 calls; none was an implementer. The
  agents the two audit sessions of 2026-10-09 to 2026-10-10 spawned,
  implementers among them, came to 17.4M in two days, and those sessions
  to 40.6M in all, their own main contexts included, which the dispatch no
  longer has. The main sessions that dispatched them averaged 226k a call,
  45 of their 94 rows under 150k, but that is the whole `/ship` context,
  not the audit's, so the exit's subject did not occur and is not read from
  this figure.
- No plan was added under `docs/superpowers/plans/` after the plan-size
  gate landed, at 2557f8d3 on 2026-10-11, so the plan limit holds
  vacuously: there is no plan to test it on.

**Step 6's exit.** A `/ship` turn's resident text, by the method of
`token-plan.md` §1, bytes over four, is about 15.2k: `ship.md` 30,626
bytes, against 28,020 when step 6 landed, with `branch.md`, `commit.md`,
`pr.md` and `CLAUDE.md` at 30,245 between them. That is a third of the 45k
measured on 2026-10-07, not the 12k the exit names. The end state held: 141
pull requests merged over the span, under the short runbook.

**Step 5.** Its exit is recall, the 2026-10-12 section's, and not met
there. `token-plan.md` §2's targets for its cost, a first sweep over the
slices and a repeat sweep from `refs/sweeps/<kind>`, have no figure: no
`/bug-sweep` or `/security-sweep` ran in the span, and the sweeps the
2026-10-12 section scored ran `claude -p` from temporary worktrees, whose
transcripts the report does not read.

**What fills a main session**, from `--tools 30`: `Bash` results under
`/ship`, 7,914 of them, about 2.5M tokens; `Read` under no command, 733,
about 0.8M; `Read` under `/ship`, 627, about 0.6M.

**The forty costliest subagents**, from `--spawns 40`, cost 0.4M to 1.5M
each; all but two are audit passes, review rounds and `Explore` searches,
the two a cold-start check and a flake's root cause. No plan writer
and no implementer is among them, where on 2026-10-10 those two were 94.7M
of the forty.

**Which documents are opened**, from `--docs`:
`appendix-e-reference-domain.md` 278 times, 177 by subagents;
`15-cicd-deployment.md` 235, 159; `10-api-gateway.md` 191, 114;
`09-messaging.md` 132, 88. These chapters, opened most by subagents, are
the evidence #847 waits on to decide whether the plan gains a step 8.

## 2026-10-18 — how much of a chapter one context reads

Measured by the report at bcc177e8 with `--since 2026-10-11 --docs`, and
the two columns added with this section: `contexts`, the main sessions and
subagents that opened a file, and `over_half`, those whose opens together
covered more than half its lines. A refused call covers nothing, and a
stage of a pipe after the read, such as `head` or a grep, narrows it. The
rows sum the report's rows under `docs/backend-architecture/`, outside
`adr/`.

| Chapters | Files | Opens | By subagents | Contexts | Over half |
|---|---|---|---|---|---|
| Over 1,000 lines | 10 | 1,193 | 792 | 503 | 15 |
| All 21 | 21 | 1,437 | 913 | 649 | 26 |

A long chapter takes more opens to read, since a context pages through it
in ranges and the `Read` tool imposes them above its line limit, so the
section above ranks the chapters partly by their length. Counted per
context, 488 of the 503 that opened a long chapter covered half of it or
less. Fourteen of the fifteen that covered more came from the two sessions
that moved the reference services out of the chapters: their implementers,
the audits of what they moved, and the sessions themselves. The fifteenth
was an audit of `appendix-e-reference-domain.md`, which has seven of the
fifteen; `10-api-gateway.md` and `13-observability.md` have none.

**What it decides for #847.** The issue's first two options, a brief that
names a section and the index before `Read`, exist to stop a context
reading a chapter it needs a section of, and 97% of contexts already read
half a long chapter or less. The third, splitting the largest chapters, is
taken only if those two fail. What is left is fifteen contexts in eight
days, nearly all of them moving the very text they read, so
`token-plan.md` gains no step 8.

## 2026-10-18 — what the scored runs cost, and the reads a pattern places

Measured by the report on 21168a10 with the changes this section lands with,
`--since 2026-10-11`. The report now reads the transcript directories that
`tools/quality-sets/` runs file under their temporary worktrees, counted
under `quality-sets:review` and `quality-sets:sweep`. So the runs the
2026-10-12 section scored, which step 5 of the 2026-10-18 section read
against steps 5, 6 and 7 found uncounted, have a cost:

| Run | Directory | Sessions | Input equivalent | Mean context |
|---|---|---|---|---|
| Security sweep, round 1, 41 slices | `secsweep-2316f4` | 41 | 5.5M | 43k |
| Bug sweep, the attempt the usage limit stopped | `secsweep-fbfaa2` | 53 | 7.3M | 54k |
| Bug sweep, round 1, 42 slices, scored | `secsweep-a54a55` | 42 | 10.6M | 74k |
| Five `bug-auditor` sessions, no scored run | `secsweep-5a5c35` | 5 | 1.2M | 68k |
| `branch-reviewer` | 38 `qs-review-*` | 38 | 10.3M | 91k |

Together 34.9M. The reviewer ran 38 times where the scored sample needed 30,
and the report cannot tell which 8 were not scored; a run cost about 0.27M.
Against `token-plan.md` §2, a first sweep's round 1 cost 5.5M for security
and 10.6M for bugs, where the target is about 25M by round 3. These are a
floor and not that figure: each ran through `claude -p` with no main session
to orchestrate, verify or file, and one round of seven.

**The reads a pattern places.** `--docs` now places a `sed -n` address with
a pattern, found in the file's text, and a `grep` after a read, by the lines
its result printed. On the transcripts behind the chapter table of the
2026-10-18 section on how much of a chapter one context reads, without the
runner's sessions, the long-chapter row still reads 503 contexts and 15
over half: those forms moved no context past half, so that row was not a
lower bound in practice. With the runner's sessions, read as the subagents
an interactive sweep would have spawned:

| Chapters | Files | Opens | By subagents | Contexts | Over half |
|---|---|---|---|---|---|
| Over 1,000 lines | 10 | 1,253 | 850 | 530 | 35 |
| All 21 | 21 | 1,511 | 985 | 687 | 56 |

The 27 contexts more on the long chapters are 23 sweep auditors and 4
reviewers. All 20 of the new over-half contexts are auditors, 6 security
and 14 bug, reading a slice that held a chapter, which is the work a slice
hands them. Their line counts are today's, against a tree at the run's
commit. #847's decision stands: what reads a long chapter whole is moving
or auditing that text.
