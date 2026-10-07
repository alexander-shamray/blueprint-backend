# Token usage — the measurements

What [`tools/token-usage/`](../tools/token-usage/README.md) reported, one
dated section per measurement, for [`token-plan.md`](token-plan.md)'s
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
  worktree writes its own directory: 103 under `.claude/worktrees/` and 22
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
