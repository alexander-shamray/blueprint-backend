# CLAUDE.md

Guidance for Claude Code when working in this repository. This file is a
primer: what the repo is, where things are, and how to act here. Anything that
moves when a PR lands — what has been built, what a PR decided, a count of
anything — is owned elsewhere and cited from here by name, never restated.
**Read the file that covers what you are about to touch, before you touch it.**

| | |
|---|---|
| [`docs/change-locality.md`](docs/change-locality.md) | The operating contract: the trust order, the one rule, the change classes and their touch sets |
| [`docs/change-locality-plan.md`](docs/change-locality-plan.md) | The PRs that make the contract fully true |
| [`docs/churn-plan.md`](docs/churn-plan.md) | Where the corpus churns, measured on a named commit, and the refactor sequence that reduces it |
| [`docs/pr-decision-log.md`](docs/pr-decision-log.md) | What each PR in its range decided — closed; the record since is commit bodies and PR bodies |
| [`docs/lessons.md`](docs/lessons.md) | Lessons that generalise past the PR that found them — closed |
| [`docs/harness-boundaries.md`](docs/harness-boundaries.md) | What the harness grants these commands, and refuses |
| [`docs/repo-map.md`](docs/repo-map.md) | What each entry in the tree is, and why it is shaped that way |
| [`docs/style-guide.md`](docs/style-guide.md) | The prose, C# and SQL dialect, and which rules the build enforces |
| [`docs/testing.md`](docs/testing.md) | What a checkout needs that the solution and the workflows cannot say: Docker, the Python floor, a gate run on its own, the coverage filter |
| [`.claude/skills/codebase-index/`](.claude/skills/codebase-index/) | Query the local index before reading whole files |

## What this repo is

`blueprint-backend` is a monorepo for an ASP.NET Core microservices platform
built with DDD, CQRS and TDD: two artefacts with one specification, the
blueprint under `docs/backend-architecture/` and the C# solution it specifies.
**The blueprint is the specification for the solution** — every chapter is a
commitment the implementation honours. Appendix C sequenced the code into a
numbered plan; the plan is complete and closed, and a rule that moves now is
an ADR. **Solution shape** is §4.1's — Catalog, Ordering, Inventory, Payments,
Shipping, Notifications, the building blocks, gateway and BFF — and **service
build order** is Appendix C.1's: **Catalog → Ordering → Inventory and Payments
→ Shipping → Notifications**, last because it publishes nothing and consumes
events the others own (§3.2). One thing is **undecided**: the READMEs call the
e-commerce domain "illustrative only" while §4.1 names six services concretely,
so build the structure and raise the domain question rather than assuming.
**Aspire is not adopted**; Compose is the baseline (§14.1).

### The tree

One line per entry; why each is *shaped* that way is `docs/repo-map.md`'s job.

```
docs/backend-architecture/   the blueprint — README index, the numbered chapters, appendices A–D
docs/roadmap.md              a calendar laid over Appendix C — closed
docs/runbooks/               NOT one per alert — the one sharer is declared
global.json, .config/        SDK pin (§4.4); dotnet-ef, pinned to the EF Core version
Directory.*.props            shared MSBuild settings and ADR-019's analyser policy; exact package pins
.github/workflows/           ci, compose, helm, observability, deploy, closure-gate, locality-gate, broker-permissions, realm
.github/<gate>/              licence-gate, secret-scan, closure-gate, locality-gate, pipeline-gate, coverage,
                             output-gate, comment-gate — a directory each
deploy/canary/               §15.5's ladder, its arithmetic and its verdict
deploy/keycloak/             §11's realm obligations, over any realm
deploy/compose/              §14.1's model: an index, the shared baseline, one file per unit under services/
deploy/helm/                 §15.3's charts — one library chart, one user per deployable
deploy/observability/        §13.8's dashboards, §13.6's rules, §13.7's k6 run
tools/new-service/           §4.5's scaffold, with Catalog as its template
src/BuildingBlocks/          Common.Domain, .Application, .Contracts, .Infrastructure, .Web
src/Gateway/Gateway.Api/     the edge, and the second host
src/BFF/Web.Bff/             the third host, and the one synchronous caller
src/Services/Catalog/        §4.1's five projects; the one gRPC server
src/Services/Ordering/       the same five, plus §5's aggregate and §9.6's saga
tests/                       per service .Domain/.Application/.Api.Tests and .TestSupport (NOT a test project, §4.1);
                             Common.*.Tests; the hosts' suites; Platform.IntegrationTests
```

**Precedence where two documents disagree**: Appendix C beats
`docs/roadmap.md`, §12 beats `docs/testing.md`, §15.4 beats `docs/secrets.md`,
and the blueprint beats `docs/superpowers/`, whose specs are **never** edited
to match the code that followed. `.remember/` is session state; never edit it.

## The one rule that matters

**The blueprint must not contradict itself**, and `docs/change-locality.md` is
how that is kept true: **every fact has exactly one owner and every other
mention cites the owner** — by symbol, section or ADR, never by value. Read it
before editing. It names the change classes, the touch set each may edit, the
repo-wide mutexes, what still applies unchanged, when `/validate-blueprint`
runs, and what to do with a stale restatement met in passing: leave it.

The blueprint and the solution are one artefact with two representations. A
code change that contradicts a chapter is not done until the chapter is amended
in the same PR, or the code is changed to match — pick one, in the PR. A
blueprint change the code already implements differently is a bug report
against one of them: say which, and why. Where the blueprint is genuinely
wrong, fix the blueprint; it is a specification, not a historical record, but
ADRs are superseded, never rewritten. This file and the files it points at are
inside the rule too, and no gate reads them: a rule stated here and argued
there moves in both.

## The commands

```bash
dotnet tool restore                # dotnet-ef, pinned in .config/
dotnet restore Platform.slnx
dotnet build Platform.slnx
dotnet test  Platform.slnx         # needs a running Docker daemon
dotnet test  Platform.slnx --filter "Category!=Integration"   # no daemon
dotnet ef migrations add <Name> --project src/Services/Catalog/Catalog.Infrastructure \
    --startup-project src/Services/Catalog/Catalog.Migrator --output-dir Persistence/Migrations
```

`Platform.slnx` is the list of test projects and the workflows are the list of
every other suite; `docs/testing.md` carries what neither can say. Three things
hold first: **`py -3.12`, not `python`**, because CI pins 3.12 and the local
default is newer; **container tests are never skipped when Docker is absent**,
because a skip fails open, so without a daemon they fail on `Failed to connect
to Docker endpoint`; and **a gate with a suite is tested and then run**, and
none outside `dotnet test` is in `Platform.slnx`, so a green solution says
nothing.

## Build policy

Versions live in `Directory.Packages.props` with **exact** pins — never add a
`Version=` attribute to a `PackageReference`. `Directory.Build.props` carries
**ADR-019**'s analyser policy: `TreatWarningsAsErrors`,
`EnforceCodeStyleInBuild`, `AnalysisLevel latest-Recommended`, no StyleCop. A
warning stops the build and `#pragma` is not the way out. Three suppressions
live there, each arguing its case in the file; a fourth is a decision about
the policy, argued there or, better, avoided by changing the code. Exactly
three `.editorconfig` rules are at `warning` — **IDE0055**, **IDE0065**,
**IDE0161** — and the rest is unenforced on purpose, because its exceptions
live in prose.

**§4.2's architecture rules are a build failure, not a review comment**; if a
change needs one relaxed, the gate is probably right and the design wrong. One
lesson stays here, because its subject is every other rule: **a gate that
silently stops covering the newest surface is this repository's most-repeated
failure**, and the only defence is a test whose subject is *what the gate is
looking at*, not what it found.

## Style

The dialect is `docs/style-guide.md`, the master copy; **read it before
"correcting" anything**, because its *Settled choices* table names the house
forms a reviewer reads as oversights. `/style-pass` moves a corrected form
through the corpus, `.editorconfig` and the guide in one change. These stay
here because they have to be true before anyone opens the guide:

- **Prose wraps at 80 columns** (tables, links and code may exceed it), **code
  at 120**, in **British spelling** with literal dashes — and **identifiers keep
  their real spelling**: `IPipelineBehavior`, never "corrected" or Americanised.
- **File-scoped namespaces**, with a blank line after. **Explicit types for
  locals**, with four carve-outs and only four: the right-hand side names the
  type, anonymous types, tuple deconstruction, and fluent resource DSLs.
- **A single statement may omit braces; two or more always take them, and so
  does one that wraps.** **One space before `=`, `=>` and `{`, never a column
  of them** — IDE0055 makes a padded column a failed build.
- **No `#pragma` suppressions and no real credentials**, in a sample or in
  source; §14.1's local-development defaults are the one stated exception.
- **A comment says why and cites the owner** — no history, no inventory,
  no review, PR or test named, no copy of an ADR's argument. The guide's
  *Comments* section is the rule, it reaches XML docs, docstrings and
  configuration comments alike, and a finding against a comment is closed by
  cutting it, never by appending the correction.

## Working in this repo

The contract's §6 lists what locality leaves in force; these are the rest.

- **Read before you edit**: the claim you are about to change is usually
  stated more than once. `Program.cs` in each `*.Api` is the only
  composition root (§4.2).
- **Commit messages** are semantic and present-tense — `docs:`,
  `feat(<scope>):`, `fix:`, `chore:` — and the body argues the change. The
  subject names what changed in words a reviewer would search for — a file,
  a symbol, a behaviour — and leaves the aphorism to the body. A
  `Closes #n` in a commit body fires on merge whatever the description says,
  so the description is reconciled to the commits and never the reverse.
- **The issue vocabulary is wider than the label helper**: kind is `security`,
  `bug` or `documentation`, and the helper does not create the last. A
  `## Severity` in the body and the label both have to say it.
- **A reply is not a resolution.** Resolve a review thread in the same act as
  the reply, and leave an `Ask` open on purpose.
- **A CodeQL alert is a defect in the pull request that raised it, and it is
  fixed there**: break the reported path at its source rather than policing
  the sink, or argue in the commit body why the path cannot be taken.
- **Uncommitted work in the tree belongs in the PR being worked on**, in its
  own commit with a body that argues it. **Never revert it to clean the tree**;
  if it does not belong here, say so and ask rather than decide by deleting.

## Available commands

| | |
|---|---|
| `/validate-blueprint` | Self-consistency audit of the blueprint, `docs/roadmap.md`, `docs/testing.md` and the code against them |
| `/check-links` | Link, cross-reference and nav-footer integrity |
| `/new-chapter` | Scaffold a chapter and rewire its neighbours |
| `/new-adr` | Append an ADR in the established form |
| `/style-pass` | Apply one corrected code form corpus-wide, then record it in the guide and `.editorconfig` |
| `/ship` | Clean `main` → `/branch` → checks → `/commit` → `/pr` → both review loops → merge → teardown. **It stops for nothing that is a judgement** |
| `/branch` | A correctly named branch **in a sibling worktree** the session moves into; in place when the tree is dirty or the parent is not writable |
| `/commit` | Split the working tree into semantic commits with arguing bodies |
| `/pr` | Open a PR in the house body form |
| `/review-grok` | Triage an external review into a resolution record |
| `/review-copilot` | Triage Copilot's PR comments — verify each before acting |
| `/review-branch` | Review the branch against `main` for contradictions; writes `suggestions.md` |
| `/security-sweep` | Loop a defensive security audit in a throwaway worktree, filing an issue per confirmed medium-or-above finding |
| `/bug-sweep` | The same loop aimed at defects, filed at **critical or high**, confirmed by reading because the grant runs no build |

### What cuts across them

`docs/harness-boundaries.md` is the inventory of what the harness grants these
commands and what it refuses them — every grant wider than the operation it
buys, and the hooks that closed the rest. **Read it before touching anything
under `.claude/`**, and state a new residual there rather than here. Two
rules reach every session, so they stay: **file permission rules take
`Edit(...)`, never `Write(...)`** — a `Write(path)` rule matches nothing and
stops Claude Code from starting; and **`.claude/settings.json` self-locks,
not instantaneously** — a change to it lands complete and goes last, and a
restore is verified by reading the file, never by trying what it forbids.
