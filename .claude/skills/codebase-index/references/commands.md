# Command Reference

Load this reference only when the intent table in `SKILL.md` is insufficient.

## Retrieval

```bash
bash .claude/skills/codebase-index/scripts/cbx search "<query>" --session <tag> --json
bash .claude/skills/codebase-index/scripts/cbx explain "<topic or flow>" --session <tag> --json
```

Useful search options:

- `--mode hybrid|fts|symbol|vector`
- `--token-budget <tokens>`
- `--limit <count>`
- `--offset <pagination offset>`
- `--raw` to disable snippet skeletonization
- `--no-fallback` to suppress fallback suggestions
- `--session <tag>` to name this conversation's context: unchanged evidence it
  already received comes back as `reused: true` without the snippet, and
  evidence that changed is listed under `memory.invalidated`

`explain` uses the HOW_IT_WORKS intent and a larger default token budget. Prefer
it over repeatedly rewording a broad search.

## Code graph

```bash
bash .claude/skills/codebase-index/scripts/cbx architecture --json
bash .claude/skills/codebase-index/scripts/cbx refs "<symbol>" --json
bash .claude/skills/codebase-index/scripts/cbx impact "<file-or-symbol>" --direction up --depth 2 --json
bash .claude/skills/codebase-index/scripts/cbx diff-impact --base HEAD --direction up --depth 2 --json
bash .claude/skills/codebase-index/scripts/cbx path "<source>" "<target>" --json
bash .claude/skills/codebase-index/scripts/cbx describe "<file-or-symbol>" --json
```

- `architecture` reads module analysis cached at index time.
- `refs` finds definitions, calls, and graph-backed references.
- `impact` walks dependents (`up`), dependencies (`down`), or both.
- `diff-impact` aggregates impact for tracked changes relative to a verified
  Git commit; new or excluded files are reported as unresolved.
- `path` returns the shortest known dependency/call chain.
- `describe` returns a node card with callers, callees, module, and centrality.

Use `graph` only for a visualization intended for a person:

```bash
bash .claude/skills/codebase-index/scripts/cbx graph "<target>" --direction both --depth 2 --output graph.html
```

For headless work, use `--output`; do not use `--open`. Exports also support
`--format graphml|dot|neo4j`.

## Evidence

```bash
bash .claude/skills/codebase-index/scripts/cbx verify --session <tag> --json
bash .claude/skills/codebase-index/scripts/cbx verify "<path:start-end@hash>" ... --json
```

- `verify` is read-only and needs no index: it checks evidence against the
  working tree. `all_valid` is true only when every checked span still holds.
- `--strict` exits 1 when anything is invalid (useful in scripts).
- See [memory.md](memory.md) for verdict states and when to reread.

## Index health

```bash
bash .claude/skills/codebase-index/scripts/cbx stats --json
bash .claude/skills/codebase-index/scripts/cbx doctor
bash .claude/skills/codebase-index/scripts/cbx update
bash .claude/skills/codebase-index/scripts/cbx index
```

Run `stats` and `doctor` when several unrelated queries have low confidence.
Low symbol counts or partial graph coverage can explain weak results.

## Query examples

```bash
bash .claude/skills/codebase-index/scripts/cbx search "auth token refresh" --session <tag> --json
bash .claude/skills/codebase-index/scripts/cbx search "AuthService class" --mode symbol --session <tag> --json
bash .claude/skills/codebase-index/scripts/cbx search "connection reset by peer" --mode fts --session <tag> --json
bash .claude/skills/codebase-index/scripts/cbx explain "checkout flow" --session <tag> --json
bash .claude/skills/codebase-index/scripts/cbx impact "User" --direction up --depth 2 --json
bash .claude/skills/codebase-index/scripts/cbx path "ApiController" "Database" --json
```
