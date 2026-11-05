# CLAUDE.md

Guidance for Claude Code in this repository: what it is, where things are,
and the rules that hold before any other file is opened. Everything else
has an owner, listed below, and anything that moves when a PR lands is
cited from here, never restated. **Read the owner of what you are about to
touch, before you touch it.**

| | |
|---|---|
| `.todo/blueprint-backend.md` in the main checkout's parent directory (local) | Open PRs and issues, for the user; `.harness/agent-rules.md` says when it changes, the contract's §6 why it lives there |
| [`docs/change-locality.md`](docs/change-locality.md) | The operating contract: the trust order, the one rule, the change classes, and §6's working rules only this repository has |
| [`docs/change-locality-plan.md`](docs/change-locality-plan.md) | The PRs that make the contract fully true |
| [`docs/churn-plan.md`](docs/churn-plan.md), [`docs/churn-plan-2.md`](docs/churn-plan-2.md) | Where the corpus churns, measured on a named commit, and the sequences that reduce it |
| [`docs/token-plan.md`](docs/token-plan.md) | Where a session's tokens go and the steps that cut them; [`docs/token-usage.md`](docs/token-usage.md) holds the measurements |
| [`docs/pr-decision-log.md`](docs/pr-decision-log.md), [`docs/lessons.md`](docs/lessons.md) | Closed records — grep them, never read them whole |
| [`.harness/agent-rules.md`](.harness/agent-rules.md) | The working rules every repository on the harness shares, imported below; managed |
| [`docs/harness/working-rules.md`](docs/harness/working-rules.md) | Their detail for commits, pull requests, review, CI, tests and issues, and the review bar; managed |
| [`docs/harness/token-rules.md`](docs/harness/token-rules.md) | The reasons behind the subagent, review-round and plan rules; managed |
| [`docs/harness/pitfalls.md`](docs/harness/pitfalls.md) | Shell, git, machine and auto-mode traps; managed |
| [`docs/harness-boundaries.md`](docs/harness-boundaries.md) | What the harness grants these commands, and refuses; managed, its index names each section |
| [`docs/harness-local.md`](docs/harness-local.md) | What the managed commands name by role, filled in for this repository |
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
dotnet ef migrations add <Name> --project src/Services/Catalog/Catalog.Infrastructure \
    --startup-project src/Services/Catalog/Catalog.Migrator --output-dir Persistence/Migrations
```

- **Container tests are never skipped when Docker is absent**, because a
  skip fails open. No gate outside `dotnet test` is in `Platform.slnx`, so
  a green solution says nothing about them.
- **Exact pins** in `Directory.Packages.props`, never `Version=` on a
  reference. ADR-019's analyser policy makes a warning a failed build, and
  `#pragma` is not the way out. §4.2's architecture rules are a build
  failure, not a review comment.

## Style

`docs/style-guide.md` is the master copy; read it before "correcting"
anything, because its *Settled choices* name house forms a reviewer reads
as oversights. What has to be true before anyone opens it: **prose wraps
at 80 columns, code at 120, in British spelling, and identifiers keep
their real spelling**; **a comment says why and cites its owner, inside
the guide's budget**; and **no real credentials**, in a sample or in
source, §14.1's local-development defaults the one stated exception.

## The harness

Every path `.harness/manifest.json` lists is managed: `blueprint-harness`
owns it and a sync pull request brings it here. `docs/harness-boundaries.md`
is what the harness grants these commands and refuses; read it before
touching anything under `.claude/`. A residual only this repository has
goes in `docs/harness-local.md` *Residuals*, and a working rule only this
repository has in `docs/change-locality.md` §6. The rules every repository
on the harness shares are these:

@.harness/agent-rules.md
