# /team — the argument

This file holds why `/team` is shaped the way it is; the runbook is
`.claude/commands/team.md`, and it wins where they differ. Read this when
the command is disputed or edited, not when it runs. #1574 is the request.

## Peers, not subagents

A subagent handed the parent's conclusion tends to return it confirmed, and
it reports once and ends. What two hand-relayed sessions had on #1562 and
#1570, and what one session reviewing its own work lacks, is several
readings of the same evidence that can disagree and are revisited. So a
peer is handed its id, its role and the run directory, and never the
owner's conclusions or another peer's reply: the board is the only place
their readings meet, and each claim on it carries its own evidence.

**The owner is the session that ran the command.** It is the only one
holding a workspace, a shell and a write, as `docs/change-locality.md` §4's
*Partitions* already asks: one worktree and one branch per agent. The peers
are `team-peer` agents whose tool list is `Read`, `Grep` and `Glob`, for
the reason `docs/harness-boundaries.md` gives for `branch-reviewer`: the
board holds other peers' text, so a claim written to steer a peer can only
be refuted, and a peer cannot grant another anything it lacks. With two or
more peers the last is a reviewer, so every claim meets one peer whose job
is to break it; a lone peer investigates.

**Why not a workflow.** A workflow script keeps each round's replies out of
the owner's context, which is the cost `docs/token-plan.md` step 2
measured. It was rejected for two reasons. Its script has no file system, so
the board would live in the run's memory and journal rather than in files.
And its merge and convergence rules would be JavaScript, which no suite in
this repository runs. A reply is at most about 2k tokens, so four peers over
seven rounds hold at most about 56k in the owner, the same order as `/ship`'s
review loop, which keeps the same shape.

## Shared state is files

Each run is `artifacts/team/<run>/`, gitignored: `goal.md`, written by the
owner; `board.json` and its rendering `board.md`, written only by
`.claude/scripts/team_board.py`; one `round-NN/<peer>.json` per reply as
the peer returned it, and the `round-NN/<peer>.md` the merge made of it.
Nothing that decides the run lives only in an agent's context, so a session
that stops mid-run resumes at the round `init` names, and the user can read
the board while it runs. It stays a plain file rather than a published
page's database: the run's claims are about this checkout and stay on this
machine.

**A claim needs evidence that exists.** The merge refuses a claim or a
status change citing nothing, a `path:line` past the file's end or outside
the checkout, a path with no line unless it is a measurement, or anything
under `artifacts/` but the run's own `measure/`: the board, the goal and
the replies are other peers' text, not evidence. That check is mechanical:
whether the evidence supports the
claim is the owner's decision at the end, never the helper's.

**A peer cannot support its own claim**, because agreeing with yourself is
not a second reading; it may withdraw one by refuting it.

## Rounds and convergence

A round is every peer reading the board and the previous round's files and
returning one reply, merged in one step. The run ends on the first round
that adds no claim and moves no status, which the merge reports as
converged, or at the round ceiling, reported as unconverged the way
`/ship` step 5 reports its review. A refused claim does not hold a round
open: it never reached the board, so repeating it changes nothing.

A reply out of shape is spawned once more with the same brief; a second is
the round not having run. The owner never writes a peer's reply, because a
round the owner answered is the owner reviewing itself.

## Who measures

Only the owner. A peer that needs a run, a query plan or a timing asks
for it in `requests`, and the owner takes it with its own tools under their
ordinary prompts, writing the result under `measure/`, which the next merge
lists for the peers to cite. Giving peers a shell would make the profile's
read-only guarantee a prompt, and a measurement is the evidence most often
wrong in a way only a second reading catches: on #1570 a run comparison was
confounded by host saturation and a plan flip mid-series.

## Models

`--models` maps one model to each peer, in order. Without it a peer takes
its profile's model, Opus, because a peer's error is a silent miss — the
owner decides from what the peers found, and nothing checks what none of
them looked at. `docs/token-plan.md` step 7d's table owns that assignment;
a cheaper model is the user's choice per peer, named in the report.

## Budget

At most four peers, which is the session's small-workflow guideline of
under five agents, and at most seven rounds, `/ship`'s review ceiling.
Default five rounds. At most two peers are spawned at once, because six
parallel agents hit the session's rate limit. A request over a ceiling is
refused before anything spawns, never cut down to fit: a silent cap reads
as the team the user asked for. The plan is shown before the first round,
with the most spawns it can make.

## Output

The report names what was decided and by which evidence, the claims
refused and why, and what is left open, filed with `docs/change-locality.md`
§6's kind and severity and grouped as `/ship` step 5 files a ceiling's open
findings. `/team` makes no change: where the goal is one, the report ends
with the `/ship` invocation that carries it. The user runs that as a turn
of its own, because `/team`'s frontmatter denies every other agent type
until its turn ends, `branch-reviewer` among them.
