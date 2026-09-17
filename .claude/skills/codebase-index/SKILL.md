---
name: codebase-index
description: Use before answering repository questions about architecture, implementation, symbols, references, dependencies, refactoring impact, data flow, or bugs. Query the local hybrid index first so the agent reads only evidence-bearing file:line ranges instead of scanning the repository, and verify evidence gathered earlier before relying on it.
allowed-tools: Bash(bash .claude/skills/codebase-index/scripts/cbx search:*), Bash(bash .claude/skills/codebase-index/scripts/cbx explain:*), Bash(bash .claude/skills/codebase-index/scripts/cbx architecture:*), Bash(bash .claude/skills/codebase-index/scripts/cbx symbol:*), Bash(bash .claude/skills/codebase-index/scripts/cbx refs:*), Bash(bash .claude/skills/codebase-index/scripts/cbx impact:*), Bash(bash .claude/skills/codebase-index/scripts/cbx diff-impact:*), Bash(bash .claude/skills/codebase-index/scripts/cbx path:*), Bash(bash .claude/skills/codebase-index/scripts/cbx describe:*), Bash(bash .claude/skills/codebase-index/scripts/cbx verify:*), Bash(bash .claude/skills/codebase-index/scripts/cbx stats:*), Bash(bash .claude/skills/codebase-index/scripts/cbx doctor:*), Bash(bash .claude/skills/codebase-index/scripts/cbx update:*), Bash(bash .claude/skills/codebase-index/scripts/cbx index:*), Read, Grep, Glob
---

# Codebase Index

Use the local index before reading repository files.

The operating principle is **Find → Trace → Verify → Predict**:

- **Find** the implementation with ranked retrieval.
- **Trace** behaviour through definitions, callers, dependencies, and paths.
- **Verify** that evidence you already hold is still true before relying on it.
- **Predict** change impact while preserving an explicit evidence trail.

## Route the question

Run every command as
`bash .claude/skills/codebase-index/scripts/cbx <subcommand> …`.
That wrapper exports `CBX_NO_SKILL_AUTO_UPDATE=1` and refuses `graph`,
`clean`, `init` and `watch`. Prefix grants still match the typed string, so
do not put `$(…)`, a second command, or a write redirection on the same
line — `.claude/hooks/guard-index-argv.py` refuses those. The package is
named `codebase-index`; do not invoke that console script directly.

| Intent | Command |
|---|---|
| Where is X implemented? | `bash .claude/skills/codebase-index/scripts/cbx search "X" --session <tag> --json` |
| How does X work? | `bash .claude/skills/codebase-index/scripts/cbx explain "X" --session <tag> --json` |
| What is this codebase? | `bash .claude/skills/codebase-index/scripts/cbx architecture --json` |
| Find a named symbol | `bash .claude/skills/codebase-index/scripts/cbx symbol "X" --json` |
| Who calls or references X? | `bash .claude/skills/codebase-index/scripts/cbx refs "X" --json` |
| What changes if X changes? | `bash .claude/skills/codebase-index/scripts/cbx impact "X" --json` |
| What does my current diff affect? | `bash .claude/skills/codebase-index/scripts/cbx diff-impact --json` |
| How are X and Y connected? | `bash .claude/skills/codebase-index/scripts/cbx path "X" "Y" --json` |
| Describe X and its neighbourhood | `bash .claude/skills/codebase-index/scripts/cbx describe "X" --json` |
| Is what I read earlier still true? | `bash .claude/skills/codebase-index/scripts/cbx verify --session <tag> --json` |

Use `search --mode symbol` for exact symbol work, `--mode fts` for text and
error messages, and the default `hybrid` mode for mixed questions. Use pure
`vector` mode only when embeddings are enabled and exact vocabulary is unknown.

Read [references/commands.md](references/commands.md) only when command options
or routing remain unclear.

## Evidence protocol

1. Pick one session tag for this conversation (for example `auth-fix-1`) and
   pass `--session <tag>` to every `search` and `explain`.
2. Run the best-matching command with `--json`.
3. Check `index` before trusting the payload:
   - missing → run `bash .claude/skills/codebase-index/scripts/cbx index`, then repeat;
   - stale with fewer than 20 changed files → run `bash .claude/skills/codebase-index/scripts/cbx update`;
   - stale with 20 or more changed files → run `bash .claude/skills/codebase-index/scripts/cbx index`;
   - fresh → continue.
4. Start with ranks 1–3. Read only `recommended_reads` line ranges.
5. Trace one additional hop only when the question requires behaviour,
   ownership, or impact.
6. Before answering or editing from evidence gathered earlier in the task, run
   `bash .claude/skills/codebase-index/scripts/cbx verify --session <tag> --json` and reread anything whose
   state is not `valid` or `relocated`.
7. Answer with `file:line` evidence and state uncertainty explicitly.

Do not open whole files when a line range is available. A snippet may already
be sufficient. `skeletonized: true` means the response intentionally folded
unrelated body lines; read the supplied range when the missing body matters.

## Evidence memory

- `reused: true` with `snippet: null` — this session already received that
  exact text and its source is unchanged. Use your earlier copy; if you can no
  longer see it, Read the range.
- `memory.invalidated` — evidence this session received has changed since.
  Treat your earlier copy as wrong and reread before relying on it.
- `stale: true` — the index is older than the file. Run `bash .claude/skills/codebase-index/scripts/cbx update`
  or Read the range.
- A tag belongs to one context. Never give it to a subagent or another
  conversation. Start a new tag after the context is cleared or compacted, or
  whenever earlier snippets are no longer visible to you.

Verdict states and citing evidence in notes: [references/memory.md](references/memory.md).

## Confidence contract

- **high** — answer from the indexed evidence.
- **medium** — read the recommended ranges and confirm the key claim with one
  targeted lookup if necessary.
- **low** or no results — follow `fallback_suggestions`, then use a narrow
  Grep/Glob fallback.

On `refs` and `impact`, inspect `coverage`. If `coverage.partial` is true, an
empty result is inconclusive; confirm with targeted Grep before saying that
nothing references the target.

Edges carry `confidence`:

- `extracted` — exact parser evidence;
- `inferred` — heuristic resolution;
- `ambiguous` — unresolved or non-unique.

Never present an inferred or ambiguous chain as certain.

## Answer contract

Structure repository answers around:

1. **Answer** — the direct conclusion.
2. **Evidence** — the minimum supporting `file:line` references.
3. **Confidence** — only when evidence is partial, inferred, stale, or missing.
4. **Next check** — only when another check would materially reduce uncertainty.

Do not narrate every search step. Do not claim absence from a partial graph.
Do not replace evidence with a generated HTML graph.

For payload fields and failure handling, read
[references/response-contract.md](references/response-contract.md).
