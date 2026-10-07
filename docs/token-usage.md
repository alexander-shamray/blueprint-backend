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

## The baseline — owed

On the machine that runs `/ship` and the sweeps:

```bash
python tools/token-usage/token_usage.py --json
```

over the transcripts of two or three `/ship` runs and one sweep, recorded
here as a section in the form above.
