# Token rules

Why `.harness/agent-rules.md`, *Subagents, review rounds and plans*, says
what it says. That section owns each rule it states; this file owns its
reason and the measure it rests on, the one rule on parallel agents that
section does not state, and which subagent task runs on which model. Every
figure behind a reason is in the target's own measurements, named in its
`docs/harness-local.md` *Documents*; none is copied here, so a remeasure
changes one file.

## What every session loads

Every turn re-sends the whole context, so a session's cost is what it
holds times the turns it takes, and a tool result stays in the parent for
the rest of the session. The measure was what filled a long main session,
summed per tool: tool results, not prompts, made up most of it. Two
consequences follow.

- **What loads before the first prompt is a budget, not free.** The
  managed always-loaded part — `.harness/agent-rules.md` and what it
  imports — is held by the harness's own test under the size backend's
  `CLAUDE.md` had before the rules moved out of it. That bounds the
  managed part alone; a target's own `CLAUDE.md` loads beside it, so it
  keeps only the product, its commands and its own hard rules.
- **Only a person empties a context.** No tool compacts; `/compact`,
  `/clear` and auto-compaction do, and auto-compaction fires late in a
  large window. So *one task per session*, with `/clear` between tasks,
  is the cut a session can make, and the auto-compact window is the
  owner's setting, outside any repository.

## Briefing a subagent

A subagent reads what it is handed and returns what it found; the parent
keeps the answer, not the files. The measure was the `general-purpose`
agents a few weeks of sessions started, and what each read.

- **A brief names the files and the question**, so the agent does not
  rediscover the tree, and the answer is short and structured — findings
  as `file:line` — so the parent holds a line per finding rather than a
  narrative.
- **`Explore` before `general-purpose` for a search**: it reads excerpts,
  not whole files, and returns the conclusion.
- **Nothing is delegated that one `Grep` answers**: an agent's start-up
  costs more than the search.
- **A step's reads go in one message**: each turn re-sends the context, so
  three reads in three turns cost three re-sends.
- **One or two parallel agents for work a session hands out itself, not
  a fan-out.** A batch of agents started together hit the session rate
  limit and most stopped mid-edit; batch such work into two by file type
  or tree, run verification in the background, and do small remaining
  pieces inline. A command that fans out by design — a sweep's areas,
  `/team`'s peers — sets its own count. This rule has no line in
  `agent-rules.md`; this file owns it.
- **A plan is written in one pass.** A plan-writing agent that reads back
  what it wrote re-sends the plan on every read; it was the largest
  subagent cost the measure found. Hand it the spec and the symbols it
  cites, and cite code by path and symbol rather than pasting it, under a
  size a gate holds.
- **A comment sweep is handed the gate's findings**, `--tree` output for
  its files, not whole suites to read.

## Reviewers

The review loop replaced external reviewers that were archived. An
unbounded loop once ran far past the round that could still find
anything; the measure was the cost a round, which a full read of the tree
set.

- **One `branch-reviewer` a round**, read-only by its tool list, handed
  the diff and not the author's conclusions, so it reads cold and a
  prompt-injected line in the diff cannot act. Never `general-purpose`,
  and never the session reviewing its own diff: on the case that set the
  rule, inline rounds missed mediums one subagent round found.
- **A recheck reads the diff since the last round and the open findings**,
  not the tree; a full pass runs again only after a bug-level finding or
  a large fix diff.
- **Seven rounds is the ceiling**, the sweeps' own, and a round whose only
  findings are refused under the style guide's *Comments* rule ends the
  loop: rounds that keep producing findings from their own fixes do not
  converge by running more of them.
- **Under `subagent-driven-development`, each reviewer is handed its
  task's diff**, not the tree, for the same reason.

## Which model a subagent runs on

A subagent runs on its parent's model unless its profile under
`.claude/agents/` sets `model:` or the spawn passes one, and the spawn's
wins. A cheaper model is right where someone else catches its error: the
parent opens the `file:line` a search returned, and a gate, a build or a
test checks a mechanical edit. Where the error is a silent miss, the
parent's model is not enough either, since a session started on a cheaper
model would make its reviewer cheaper too, so those profiles pin the
stronger one.

| Task | Model | When |
|---|---|---|
| `Explore`, search and locate | Haiku or Sonnet | now: the parent opens what it returns |
| Mechanical edits: comment-gate flags, renames, formatting | Sonnet | now: the gate, the build and the tests check it |
| A sweep's re-verification | Sonnet | now |
| A review recheck of a fix diff only | Sonnet | once the target's recall sets show no loss |
| A plan task's implementer | Sonnet | once the target's recall sets show no loss |
| `branch-reviewer`'s rounds, a sweep's fan-out | Opus | now: the profile pins it, whatever the session runs on |
| A `/team` peer | Opus, or the model `--models` names for it | now: the profile pins it, the user overrides per peer |
| Writing a plan | the parent's | until the recall sets show otherwise |

For `general-purpose` and `Explore` the brief names the model at spawn;
the repository agents the table moves carry `model: opus` in their
frontmatter, and a spawn's `model` overrides it.

## Waits

A wake re-sends the whole context, and once the prompt cache has expired
it writes all of it again; the measure found most of a span's spend came
after a wake. So a session about to wait — on CI, a background agent or a
scheduled check-in — runs the wait as one background task whose exit is
the only wake, rather than polling. A subagent cannot hold the wait: its
Bash is cut at 180 seconds and its own background task wakes nobody. And
no tool compacts before the wait (*What every session loads*).
