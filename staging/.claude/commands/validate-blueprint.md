---
description: Multi-pass self-consistency audit of the blueprint, its roadmap and docs/testing.md, and of the code against them
argument-hint: "[chapter file or topic to focus on — omit for a full sweep]"
allowed-tools: Read, Grep, Glob, Edit, Bash(git diff:*), Bash(git log:*), Bash(wc:*), Bash(ls:*)
disallowed-tools: Edit(.git/**), Edit(./.git/**), Edit(.git), Edit(./.git), Edit(docs/superpowers/**), Edit(./docs/superpowers/**), Edit(docs/runbooks/**), Edit(./docs/runbooks/**), Edit(docs/commands/**), Edit(./docs/commands/**), Edit(docs/pr-decision-log.md), Edit(./docs/pr-decision-log.md), Edit(docs/secrets.md), Edit(./docs/secrets.md), Edit(docs/personal-data.md), Edit(./docs/personal-data.md), Edit(docs/personal-data-incident.md), Edit(./docs/personal-data-incident.md), Edit(docs/lessons.md), Edit(./docs/lessons.md), Edit(docs/harness-boundaries.md), Edit(./docs/harness-boundaries.md), Edit(docs/repo-map.md), Edit(./docs/repo-map.md), Edit(docs/style-guide.md), Edit(./docs/style-guide.md), Edit(docs/change-locality.md), Edit(./docs/change-locality.md), Edit(docs/change-locality-plan.md), Edit(./docs/change-locality-plan.md), Edit(docs/churn-plan.md), Edit(./docs/churn-plan.md), Edit(docs/churn-plan-2.md), Edit(./docs/churn-plan-2.md), Edit(docs/token-plan.md), Edit(./docs/token-plan.md), Edit(docs/token-usage.md), Edit(./docs/token-usage.md), Edit(.claude/**), Edit(./.claude/**), Edit(.config/**), Edit(./.config/**), Edit(.github/**), Edit(./.github/**), Edit(deploy/**), Edit(./deploy/**), Edit(src/**), Edit(./src/**), Edit(tests/**), Edit(./tests/**), Edit(tools/**), Edit(./tools/**), Edit(.dockerignore), Edit(./.dockerignore), Edit(.editorconfig), Edit(./.editorconfig), Edit(.gitattributes), Edit(./.gitattributes), Edit(.gitignore), Edit(./.gitignore), Edit(CLAUDE.md), Edit(./CLAUDE.md), Edit(Directory.Build.props), Edit(./Directory.Build.props), Edit(Directory.Build.targets), Edit(./Directory.Build.targets), Edit(Directory.Build.rsp), Edit(./Directory.Build.rsp), Edit(Directory.Solution.props), Edit(./Directory.Solution.props), Edit(Directory.Solution.targets), Edit(./Directory.Solution.targets), Edit(MSBuild.rsp), Edit(./MSBuild.rsp), Edit(nuget.config), Edit(./nuget.config), Edit(NuGet.config), Edit(./NuGet.config), Edit(NuGet.Config), Edit(./NuGet.Config), Edit(**/*.targets), Edit(**/*.props), Edit(**/*.rsp), Edit(**/*.csproj), Edit(**/*.sln), Edit(**/*.slnx), Edit(Directory.Packages.props), Edit(./Directory.Packages.props), Edit(Platform.slnx), Edit(./Platform.slnx), Edit(README.md), Edit(./README.md), Edit(coverage.runsettings), Edit(./coverage.runsettings), Edit(global.json), Edit(./global.json), Edit(.mcp.json), Edit(./.mcp.json), Edit(.codeindexignore), Edit(./.codeindexignore)
---

Audit `docs/backend-architecture/`, `docs/roadmap.md` and `docs/testing.md`
for contradictions and, once code exists, drift from it. Checks 1–8 and 10
cover the roadmap, 1–9 `docs/testing.md`. Appendix C beats the roadmap; §12
beats `docs/testing.md`. (why: docs/commands/validate-blueprint.md, *Scope*)

Scope: $1 — empty, sweep all three; a filename, audit that chapter against
every other; a topic, trace it everywhere.

**First, establish the phase.** If `Platform.slnx` (or any `src/`) exists,
run check 9; else skip it — say which you ran.

Run after a Class C change or an edit in scope, never after Class A
(`docs/change-locality.md` §5).

## What counts as a finding

Two statements that cannot both be true, or one untrue of the system;
never style or clarity. Hunt for:

1. **Numeric drift** — a timeout, retry count, TTL, page-size clamp, batch
   size or SLO differing from its owning code symbol; make the non-owner
   cite it, never pick a value for both. Counts are out of scope. (why:
   docs/commands/validate-blueprint.md, *What counts as a finding*)
2. **Type and member drift** — a type or member named differently in a
   sample and in prose or a registration list. Appendix D is never a site.
3. **Contract drift** — a route, event, queue, header, claim, health
   endpoint or config key spelled differently across chapters.
4. **Ordering and lifecycle** — pipeline, middleware and DI order, dispatch
   timing around `SaveChanges`, transaction boundaries.
5. **Rule violations** — a sample breaking a stated rule (Domain or
   Application touching a library the ADRs forbid, a route missing the
   gateway's policy, the `/api` prefix rule).
6. **Mis-citations** — a reference that does not state the claim, link text
   and target disagreeing, a section or file that does not exist.
7. **Register drift** — a sample's package absent from
   `appendix-b-licences.md`; an ADR number that does not exist. Never read or
   amend Appendix D as a register.
8. **Terminology drift** — one concept under two names, or one name for two.
9. **Code ↔ blueprint drift** (only when `src/` exists) — checks 1–8 across
   the boundary, including a `src/` type's name, signature or namespace
   against its sample, `Directory.Packages.props` against
   `appendix-b-licences.md` (presence; version against the register or a
   chapter), `AddXApplication()` / `AddXInfrastructure()` registration set
   and order, and ADR rules (no MassTransit or EF Core in Application or
   Domain, one composition root, no shared table). **For a value the code
   is the owner, and for a rule it is not**: a chapter cites the symbol;
   code breaking a rule is reported, never the spec amended to match.
10. **Roadmap drift** (`docs/roadmap.md`) — every Appendix C PR has exactly
    one row and every row a real PR; titles are Appendix C's verbatim and
    phases match its headings (**Appendix C always wins**); recompute the
    cumulative column, header, milestone totals and week numbers from the
    estimates and days-per-week ratio, never spot-check; re-derive the
    critical path from C.3's edges and the roadmap's estimates; its
    citations are check 6; its risk section's open questions (domain,
    `Directory.Build.props` analyzer policy, Aspire) are wrong once
    settled. **An estimate is never a
    finding** — only stale arithmetic or a vanished PR.

## Method

Work in passes, one axis per pass, traced claim-by-claim (never
chapter-by-chapter) across `docs/backend-architecture/`, `docs/roadmap.md`
and `docs/testing.md`. (why: docs/commands/validate-blueprint.md, *Method*)

- For a value, grep the owning **symbol**, then the value in the three
  audited paths; for a rule or name, grep the identifier there. Report the
  sites; never tour the corpus outside the three paths.
- A value's owner wins (the code symbol, else the one section stating it)
  and other sites cite it; for a rule, the statement the rest depends on
  wins and the others are amended.
- Fix every named site **inside the change's touch set** in one pass (Class
  C: the new ADR and the section it amends; no class: the three paths, a
  restatement fixed by citing the owner); report one outside and leave it
  (`docs/change-locality.md` §2). Never half-apply a reconciliation.
- Genuine design ambiguity: never silently pick — surface it and ask.

Keep going until a full pass produces no findings.

## Report

One block per finding:

```
FINDING — <one-line claim of the defect>
  Sites:    07-persistence.md:112, 09-messaging.md:340, appendix-c-delivery-plan.md:44
  Conflict: <A says X; B says Y>
  Resolved: <what you changed and why that side won>
```

A code ↔ blueprint finding adds `Verdict:` (`code` or `blueprint`) before
`Resolved:`. A roadmap finding takes none; its `Resolved:` names the figures
recomputed. End with the pass count and whether the last pass was clean — do
not round up.

## Do not

- Rewrite prose you merely dislike, or refresh or remove a count.
- "Fix" `docs/style-guide.md`'s settled choices (`var` is **not** one);
  check a finding against it and `CLAUDE.md` *Style*.
- Renumber ADRs or chapters.
- Revise a roadmap estimate, days-per-week ratio or one-engineer assumption.
- Edit `src/` or `tests/` (report code findings), or touch `.remember/`.

The frontmatter enforces this. (why:
docs/commands/validate-blueprint.md, *Do not*)
