# CLAUDE.md

Guidance for Claude Code in this repository: what it is, where things are,
and the rules that hold before any other file is opened. Everything else
has an owner, listed below, and anything that moves when a PR lands is
cited from here, never restated. **Read the owner of what you are about to
touch, before you touch it.**

| | |
|---|---|
| `TODO.md` (gitignored, local) | Open PRs and issues, for the user; the contract's §6 says when it changes |
| [`docs/change-locality.md`](docs/change-locality.md) | The operating contract: the trust order, the one rule, the change classes, and §6's working rules |
| [`docs/change-locality-plan.md`](docs/change-locality-plan.md) | The PRs that make the contract fully true |
| [`docs/churn-plan.md`](docs/churn-plan.md), [`docs/churn-plan-2.md`](docs/churn-plan-2.md) | Where the corpus churns, measured on a named commit, and the sequences that reduce it |
| [`docs/token-plan.md`](docs/token-plan.md) | Where a session's tokens go and the steps that cut them; [`docs/token-usage.md`](docs/token-usage.md) holds the measurements |
| [`docs/pr-decision-log.md`](docs/pr-decision-log.md), [`docs/lessons.md`](docs/lessons.md) | Closed records — grep them, never read them whole |
| [`docs/harness-boundaries.md`](docs/harness-boundaries.md) | What the harness grants these commands, and refuses; its index names each section |
| [`docs/repo-map.md`](docs/repo-map.md) | What each entry in the tree is, and why it is shaped that way |
| [`docs/style-guide.md`](docs/style-guide.md) | The prose, C# and SQL dialect; its summary comes first |
| [`docs/testing.md`](docs/testing.md) | What a checkout needs that the solution and the workflows cannot say |
| [`.claude/skills/codebase-index/`](.claude/skills/codebase-index/) | Query the local index before reading whole files |

## What this repo is

A monorepo for an ASP.NET Core microservices platform built with DDD, CQRS
and TDD: the blueprint under `docs/backend-architecture/` and the C#
solution it specifies. **The blueprint is the specification** — every
chapter is a commitment the code honours. Appendix C's plan is complete
and closed; a rule that moves now is an ADR. Solution shape is §4.1's,
build order Appendix C.1's, and the domain is a reference implementation
an adopter replaces (ADR-062). Compose is the baseline; Aspire is not
adopted (§14.1). Precedence where two documents disagree: Appendix C beats
`docs/roadmap.md`, §12 beats `docs/testing.md`, §15.4 beats
`docs/secrets.md`, and the blueprint beats `docs/superpowers/`, whose specs
are **never** edited to match the code that followed. `.remember/` is
session state; never edit it.

## The one rule that matters

**The blueprint must not contradict itself**: every fact has exactly one
owner and every other mention cites the owner — by symbol, section or ADR,
never by value. `docs/change-locality.md` is how that is kept true; read
it before editing. A code change that contradicts a chapter is not done
until the chapter is amended in the same PR, or the code changed to match.
Where the blueprint is wrong, fix it; ADRs are superseded, never
rewritten. This file and the files it points at are inside the rule too.

## Commands and hard rules

```bash
dotnet tool restore && dotnet restore Platform.slnx && dotnet build Platform.slnx
dotnet test Platform.slnx                                     # needs a running Docker daemon
dotnet test Platform.slnx --filter "Category!=Integration"    # no daemon
```

- **`py -3.12`, not `python`**: CI pins 3.12 and the local default is
  newer. **Container tests are never skipped when Docker is absent**,
  because a skip fails open. **A gate with a suite is tested and then
  run**, and none outside `dotnet test` is in `Platform.slnx`, so a green
  solution says nothing about them.
- **Exact pins** in `Directory.Packages.props`, never `Version=` on a
  reference. ADR-019's analyser policy makes a warning a failed build, and
  `#pragma` is not the way out. §4.2's architecture rules are a build
  failure, not a review comment.
- **A gate that silently stops covering the newest surface is this
  repository's most-repeated failure**; the only defence is a test whose
  subject is what the gate is looking at, not what it found.

## Style

`docs/style-guide.md` is the master copy; read it before "correcting"
anything, because its *Settled choices* name house forms a reviewer reads
as oversights. What has to be true before anyone opens it: **prose wraps
at 80 columns, code at 120, in British spelling, and identifiers keep
their real spelling**; **a comment says why and cites its owner, inside
the guide's budget**; and **no real credentials**, in a sample or in
source, §14.1's local-development defaults the one stated exception.

## Subagents, review rounds and plans

Every turn re-sends the whole context, so an agent costs what it reads
times the turns it takes, and a tool result stays in the parent for the
rest of the session; `docs/token-plan.md` step 2 measured where that went.

- **Delegate with a brief.** A subagent is handed the files, diff or
  question it needs and returns a short structured answer — findings as
  `file:line`, not prose. `Explore` before `general-purpose` for a search;
  nothing is delegated that one `Grep` answers; a step's reads go in one
  message.
- **The review loop.** With the external reviewers switched off, and until
  `docs/token-plan.md` step 4 archives them, a PR is reviewed in rounds of
  one read-only reviewer each, `bug-auditor` for code — read-only by its
  tool list rather than by the brief, and never `general-purpose`. When a
  round's diff touches prose or a chapter, an `Explore` agent briefed with
  `/review-branch`'s *What counts as a finding*, and told to read each
  touched document and each owner it cites whole, reviews that part:
  instead of `bug-auditor` when the diff is prose only, after it otherwise,
  never in parallel and never the session itself. `Explore` is read-only
  by its brief, not its tool list, until step 4's `branch-reviewer` covers
  both. Each reviewer gets the diff, not the author's conclusions. Round 1
  reads the branch diff; each later round reads the diff since the round
  before and the findings still open, not the tree. The diff is written
  under `artifacts/review/`, since `bug-auditor` refuses the scratchpad.
  The loop ends at a round with nothing open, at one whose only findings
  are refused under the style guide's *Comments* rule, or at the seventh,
  whose open findings are filed as issues before the PR is reported done.
- **Plans.** A change that fits one PR takes plan mode, not a plan file. A
  plan file cites code by path and symbol rather than pasting it and stays
  under 50 KB; research for it goes to `Explore` with a brief. Small tasks
  run with `executing-plans`; under `subagent-driven-development`, each
  reviewer is handed its task's diff, not the tree.
- **A comment sweep** hands each agent what `.github/comment-gate/` finds in
  its files with `--tree`, not whole suites to read.
- **One task per session**: `/clear` between tasks.

## The harness

`docs/harness-boundaries.md` is what the harness grants these commands and
refuses them; read it before touching anything under `.claude/`, and state
a new residual there. Two rules reach every session: **file permission
rules take `Edit(...)`, never `Write(...)`** — a `Write(path)` rule matches
nothing and stops Claude Code from starting; and **`.claude/settings.json`
self-locks, not instantaneously** — a change to it lands complete and goes
last, and a restore is verified by reading the file.
