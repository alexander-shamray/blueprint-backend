# The token-usage report

[`docs/token-plan.md`](../../docs/token-plan.md) is the plan this directory
measures for; this file records what a developer needs at the keyboard.

```bash
py -3.12 tools/token-usage/token_usage.py                      # this checkout's transcripts
py -3.12 tools/token-usage/token_usage.py --since 2026-10-01 --json
py -3.12 tools/token-usage/token_usage.py --since 2026-10-01 --spawns 40   # the costliest subagents and their tasks
py -3.12 tools/token-usage/token_usage.py --session <id>      # one session and its subagents
py -3.12 tools/token-usage/token_usage.py ~/.claude/projects/<dir> ...
```

It reads Claude Code's transcripts — a `<session>.jsonl` per session and a
`<session>/subagents/agent-<id>.jsonl` per subagent — and sums each
response's `usage` once, grouped by the slash command the session was
running and by who answered: `main` for the session itself, the agent type
for a subagent.

| | |
|---|---|
| `contexts` | prompts for `main`, subagents spawned for an agent type |
| `calls` | API responses, each counted once however many lines it was written across |
| `input`, `output` | uncached input and output |
| `cache_write`, `cache_read` | the prompt cache's writes and reads |
| `input_equivalent` | the input side priced as uncached input: writes at 1.25× (5-minute) or 2× (1-hour), reads at 0.1× |
| `woken_equivalent` | the part of `input_equivalent` spent after a wake, before the next typed prompt |

**A command owns everything until the next prompt a person types**, so a
command `/ship` loads through the `Skill` tool is counted as `/ship`, and a
subagent belongs to the command running when it started. A harness command
typed mid-run — `/compact`, `/reload-plugins`, `/model` and the others in
`CARRY_ON` — does not end it. **A skill the session loads with no typed
command** — a sweep a prompt asked for, a plugin's workflow — names the
work after it as `skill:<name>`, until the next typed prompt or, for a
plugin's skill, the next skill it loads; one of this repository's commands
loaded as a skill keeps the commands it loads, as a typed one does. That
can be long: a session that loads one skill and then runs a review loop on
wakes counts the whole loop under it, so read what a skill costs from
`--spawns` descriptions rather than from its row. **A prompt no person typed** — a task
notification, a scheduled wake — keeps the label, because most are a
background agent or CI watch reporting back inside the command's own run;
what the session spent from the first such wake to the next typed prompt
is `woken_equivalent`, beside `input_equivalent` and part of it. The
first and last day counted go to stderr.
`--spawns N` lists single subagents instead, costliest first, each with the
description it was spawned with — which says why a command no one wrote to
delegate spent most of its tokens in agents. A subagent's type
comes from its `.meta.json`, else from the parent's tool result naming it,
else it is `subagent`.

The default directory is `~/.claude/projects/` plus the checkout's absolute
path with every character that is not a letter or digit made a dash, which
is how Claude Code names it — and, beside it, every directory whose name
is that one followed by a dash: the worktrees `/branch` makes under
`.claude/worktrees/` and the sibling forks it makes when that directory is
not writable. A different checkout whose path only extends this one's is
read too, so name such a directory explicitly when it matters. Run from a
worktree, it reads the same set. The directories read go to stderr.
Transcripts are local to the machine that ran the sessions, so a baseline
is measured there.

Stdlib only, and the suite writes its transcripts to a temporary directory:

```bash
cd tools/token-usage && py -3.12 -m unittest
```
