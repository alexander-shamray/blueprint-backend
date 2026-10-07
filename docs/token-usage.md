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

**The costliest agents are a plugin's.** The superpowers `writing-plans`
skill spawned 66 `general-purpose` agents at 2.1M each, 77 calls apiece at
a mean context of 228k — the plans under `docs/superpowers/` that each run
writes are among the largest files in the repository.

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

The figures above were measured by a report with two faults, both fixed as
of 9e3e352; they stand as the record of what that report said.

- **A label ended early.** A compaction summary, a `!` command's input or
  output, and a local command's output were each read as a typed prompt,
  so a long `/ship` run lost its label partway. Re-measured on the owner's
  machine with them excluded, over the same window, `/ship` holds 731M
  (45%), not 583M (36%), and `(no command)` 407M (25%), not 604M (38%):
  about 10.6M a `/ship` run over the 69, not 8.4M.
- **The worktrees were not read.** A session started in
  `.claude/worktrees/<name>` writes its own transcript directory; the owner's
  machine holds 103 of them, about 112 MB beside the main directory's
  1.4 GB, and every figure above leaves them out. The report now reads them
  by default and names what it read.
