---
description: Verify links, section cross-references and nav footers across the blueprint
allowed-tools: Read, Grep, Glob, Edit, Bash(ls:*), Bash(wc:*), Bash(comm:*)
disallowed-tools: Edit(~/.claude/projects/**), Edit(~/.claude/output-styles/**)
---

Check navigational integrity across `docs/backend-architecture/`, including
the ADR files under its `adr/` directory. Structural only — self-consistency
of content is `/validate-blueprint`.

## Checks

1. **Relative links resolve.** Every `](*.md)` and `](*.md#anchor)` target
   exists. Every `#anchor` matches a real heading slug in the target file. A
   link to an ADR names the ADR's file rather than an anchor in Appendix A,
   which has none left to hit; the path is relative to the linking file, so
   a chapter writes `adr/ADR-0NN-<slug>.md`, a sibling ADR writes
   `ADR-0NN-<slug>.md`, and an ADR's links to a chapter start with `../`.
2. **Section references resolve.** Every `§n` and `§n.m` corresponds to a
   heading that exists. Flag references to sections that were renumbered away.
3. **Link text agrees with target.** `[§9.3](09-messaging.md)` must point at
   chapter 9, and `[ADR-021](adr/ADR-021-….md)` at ADR-021's file. A link
   whose text names one section or ADR and whose href names another is a
   defect even when both exist.
4. **Nav footers.** Every chapter and appendix ends with exactly one `---`, one
   blank line, then a single footer line using ` · ` separators. Previous and
   next targets must match the reading order in
   `docs/backend-architecture/README.md`. First chapter has no `←`; last
   appendix has no `→`. Flag doubled rules — that regression has appeared
   before. An ADR file ends the same way but its footer is exactly
   `[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)`, with no
   previous or next ADR: appending an ADR must not edit the one before it.
5. **Index completeness.** Every `.md` directly in the directory (except
   `README.md`) appears exactly once in the README chapter table, and every
   table row points at a file that exists. The `adr/` files are indexed by
   Appendix A's table instead: every file there appears in it exactly once,
   every row points at a file that exists, the row's `ADR-NNN` matches the
   file's, **no two files and no two rows carry the same `ADR-NNN`**, and
   Appendix A holds nothing but its H1, the intro, the table and the nav
   footer: no further heading at any level and no `**Decision.**` line — an
   ADR body written there, with or without its heading, is in the wrong
   file. The uniqueness check is the one a per-file layout needs and a
   per-row match cannot give: two `/new-adr` runs on two branches each take
   `highest + 1` under different slugs, each row matches its own file, and
   the duplicate number exists only after the merge — so it is caught here,
   not there.
6. **Root README.** `../../README.md`'s entry point still resolves.
7. **Orphans.** Any file no other file links to, an ADR file included.

## Report

Group findings by check, cite `file:line`, and state the fix. Apply the
unambiguous fixes (broken relative path where the intended target is obvious,
missing/doubled rule, footer neighbour out of order). Ask before changing
anything where the intended target is genuinely uncertain.

Finish with a one-line count per check, including the ones that passed.
