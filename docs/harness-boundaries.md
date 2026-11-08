# Harness boundaries

## Index

**Read the section you need, not the file**: each is under 3.5k tokens and
the whole is about 31k. `Grep -n "^## <title>"` gives its first line.

| Section | What it covers |
|---|---|
| Purpose and provenance of this file | Why the section left backend's CLAUDE.md, the four edits made in the move, and the three passages since moved to backend's residuals. |
| Edit denies and agent self-protection | Edit-only file rules, the denied .claude and .harness trees, and why the skill and index-wrapper grants need guarding. |
| Index refresh and worktree seeding | The PostToolUse refresh hook, seeding worktree indexes, carrying MCP approval into forks, and the SessionStart refresh. |
| Index hint hooks and hook wiring | Query, search and MCP-root hooks, MCP_TIMEOUT, and why hook commands run without a shell. |
| Settings self-lock and permission root | The settings.json self-lock and its delay, settings.local.json, how EnterWorktree moves rules, and the hooks deny. |
| Grant semantics | allowed-tools as auto-approval rather than whitelist, and a deny as the only refusal; where the external reviewers' machinery went. |
| Prefix grants and wildcard denies | Table of over-wide git grants, the reset-grant lesson, mid-pattern deny wildcards, the quoting bypass and the helper argv guard. |
| The guards' threat model | What the guard hooks are written against, which guard findings are filed, and which are recorded as out of scope. |
| The guard launcher | run-guard.sh starting the five guards on any host's Python 3.12, exit 2 on every failure, the mark that skips the probe, run-guard.py's proof, and the residuals. |
| Destructive classes | One table of what the harness refuses, the layer that refuses each, and the test that holds the table to the deny list and the hooks. |
| Git argv guard hook | How guard-git-argv.py resolves the executed argv, its successive bypasses, git -c and stated residuals. |
| The ext transport and pinned grants | Why ext:: cannot be written as a rule, hook coverage, and fetch and pull grant pinning. |
| Grant inventory and push helpers | Frontmatter grants, the numbered inventory, /ship's closed entries, sweep helpers, the three lease-bound force-push helpers, the branch-drop helper and the sync-PR reader. |
| Pull request reads and the locality verdict | Fixed-field PR helpers, the gh allow-list gate, #150, and pr-locality.sh's contract. |
| Sweep push denies and chained commands | Push denies in the sweeps, and why a command /ship runs before it pushes never denies push. |
| Output deny and shell-expansion residuals | The --output entry closed by the hook, expansion readings, locale quotes, process substitution, crashes and pipes. |
| Agent-type and push entries | Agent-type deny enumeration and the inverted push check. |
| The edit-target guard | The symbolic-link premise and guard-edit-target.py refusing spelling mismatches. |
| Edit guard path resolution | Link anchors, case folding, checkout-root anchors, UNC and device prefixes, and short-name aliases. |
| Normalisation, residuals and the sweeps | Unicode and per-directory traits, the .. admission, out-of-tree residual, how the sweeps share worktrees, and their slice lists and ref. |
| The sed line-ending guard | guard-sed-crlf.py refusing `sed -i` on a tracked CRLF file: what it reads, what a crafted path makes it run, where it fails open, and its residuals. |
| Headless dispatched sessions | Unattended `claude -p` executors, what holds them, why no grant was added, and the night's residuals: a refusal read in the morning, and the weekly stop. |
| The suite's shard runner | shard-harness-suite.py dealing the `.claude/scripts` classes to parallel workers, what it may never stop placing, its tables, and its one-run-per-user lock. |
| Managed files | Which paths blueprint-harness owns and where they are changed, the `local` paths a repository keeps, and the drift gate that holds them. |

## Purpose and provenance of this file

**What the agent harness grants this repository's commands, what it refuses
them, and every place a grant is wider than the operation it was added for.**
This was the *What cuts across them* section of backend's `CLAUDE.md` until
it reached 582 lines. It is load-bearing whenever you touch `.claude/` — a
command's frontmatter, a helper script, `settings.json`, a hook, a subagent
profile — and inert for every other kind of change, which is what earns it a
file rather than a permanent seat in every session's context.

**The content is verbatim in its arguments**, on
backend's PR decision log's terms and for its reason: a summary
of an argument is how a rule gets "corrected" back. Four edits were made on the
way out, and naming them is cheaper than a claim that does not survive a grep.
The `### What cuts across them` heading became this file's title. One paragraph
arrived from the *The commands* section of that `CLAUDE.md` — the one recording
that #150's suppression clause moved three grants along before it stuck —
because its subject is a grant and not a command; its opening was rebased from
"That last clause", which pointed at a paragraph that did not travel with it.
And **two self-references** were rebased the same way: the grant inventory's
count of rotted totals said "in this file" of `CLAUDE.md` and named two siblings
that now live in two other files, and the sandbox residual said the commands
cite "this file" — true while it sat in `CLAUDE.md`, and a contradiction of the
paragraph below the moment it moved. Not one argument was shortened, and no
paragraph was dropped. Three were later moved out when the file became the
harness's, because only backend's own commands make them true: the callout
on what backend's link and blueprint checks read, the paragraph on its
mutation sweep, and the ninth entry's argument on a build tool's grants,
which that entry now summarises and hands over. They are backend's
residuals now.

**The second of those was found by review rather than by the move**, which is
worth recording where the edits are listed: a self-reference that stays *true
of its old home* reads correctly in isolation and only contradicts something
two screens away. Grep for "this file" after extracting prose, not just for
links.

**No headings were introduced.** The section is one long run of bold-led
paragraphs and it stays that way — inventing headings would mean deciding where
the topic boundaries are, which is an edit to the argument wearing a
navigation aid's clothes. Grep the bold lead-ins; they are the index.

**`/ship` and both sweeps cite this file for the harness's boundaries and their
residuals.** **A new residual is stated here**, and `CLAUDE.md` carries the
pointer rather than a second copy.

> **A grant is not a whitelist, and that is the trap under everything below.**
> If you read one paragraph here, read the one beginning *A grant is not a
> whitelist* — `allowed-tools` is an auto-approval list, so omitting a tool from
> a command's frontmatter withholds nothing. Refusing a capability takes a
> **deny**.

---

## Edit denies and agent self-protection

**File permission rules take `Edit(...)`, never `Write(...)`.** `Edit(path)`
covers every file-editing tool, `Write` included; a `Write(path)` rule matches
nothing and Claude Code **refuses to start** until it is removed:

```
Permission deny rule (.claude\settings.json): Write(.remember/**) is not matched
by file permission checks — only Edit(path) rules are.
```

So `Edit(.remember/**)` and `Edit(./.remember/**)` are the whole of the
`.remember/` protection, and the absence of a `Write` twin is correct rather
than a gap. This has been "fixed" twice by adding the twin back, and both times
it broke startup. **A reviewer who has not run the harness cannot see this;
check a permission claim against the harness before acting on it.**

**The `Edit` denies bind the agent's own tooling**, in both spellings each:
`.claude/scripts/**`, `.claude/commands/**`,
`.claude/agents/**`, `.claude/hooks/**`, `.claude/skills/**`,
`.claude/settings.json` and `.claude/settings.local.json`.
Read the list in `.claude/settings.json`, do not count it here — it has
already grown twice, once inside the pull request that introduced it. The
commands grant those helpers by name, so a session that could rewrite
one before invoking it would make every fixed endpoint a fiction.

**Three of them arrived with #33 — `commands/`, `agents/` and `settings.json` —
and the argument for them is the helpers' applied one level up.** They are the
files that *grant* what `scripts/**` protects. Commands carry an unrestricted
`Edit` or `Write` and some read untrusted input by design, so a single applied
edit could append a grant to a command's `allowed-tools`, remove a line from
this deny list, or rewrite `.claude/agents/security-auditor.md` — whose
read-only guarantee is precisely its `Read, Grep, Glob` tool list, as
`/security-sweep` says outright: read-only there "is a property of the agent's
tool grant, not a word in its prompt".

**`branch-reviewer` rests on the same property.** It reads the diff the parent
wrote under `artifacts/review/`, inside the checkout, and returns JSON the
parent verifies before acting on any of it. Its reads stay in the checkout by
its prompt, not its tools — the sweeps' residual for the same tool set. The rest
is the parent's: it holds the shell the reviewer was denied, so a finding
written to steer an edit is caught by that verification or not at all.

**The residual is that the chain has no outside reader, and no grant expresses
it.** What reviews a branch before it merges is the same model family that
wrote it, with its own context and its own blind spots, and `/ship` merges
unattended. Nothing in the harness can close that: independence of judgement is
not a tool grant. It is accepted, the external reviewers being archived (*Grant
semantics* says where they are held; blueprint-frontend recorded the decision
in its #117), and the control is procedural and stated: a full pass, a recheck
of every fix with a full pass again after a `bug`-severity or large one, a
seventh-round ceiling whose open findings are filed as issues rather than
dropped, and CI, which judges the branch by rules the model did not choose. It
is recorded because a reader who learns only that the reviewer is read-only has
been told the safe half. Restoring an outside reader starts from
`archive/external-reviewers`.

**`team-peer` rests on it too, over a wider input.** `/team`'s peers hold
the same three tools and read a board other peers wrote, so a line planted
in the code can reach a second peer as a claim. The residual is that
`team_board.py` checks only that a claim's evidence exists, never that it
supports the claim; the owner's decision at the end, made with the shell
the peers were denied, is the only check of that
([`docs/commands/team.md`](commands/team.md)).

**`.harness/**` is the same argument one level further up.** Its
`config.json` is what the next sync adds to the base settings to render
`.claude/settings.json`, and its `sweep-rows.tsv` decides what the sweeps
read; the rest is managed or written by `apply`. A session that could edit
the config could not unlock itself, since the config cannot remove a base
deny, the change shows in its pull request's diff and it takes a render to
apply, but it could widen `allow` or drop a target's own denies, and would
hand every reviewer a settings file to re-read for what changed. So a change
to anything under `.harness/` is the owner's, as one to
`.claude/settings.json` is; the deny names the whole directory rather than
the config alone, because a sweep able to narrow its own rows has audited
less than it reports.

**`.claude/skills/**` is the same argument one directory over.** A skill's
`allowed-tools` is auto-approval, so a session that can rewrite `SKILL.md`
widens the next invocation's Bash surface. The deny list in
`.claude/settings.json` is the owner of the paths; this paragraph is the
reason `skills/` is on it.

**A command that holds `Write` or `Edit` denies the user's memory and output
styles.** `~/.claude/projects/*/memory/` is loaded into later sessions as the
user's own standing instruction, and `~/.claude/output-styles/` shapes how they
answer; a command that reads a branch diff and holds `Write` could otherwise
plant one. `guard-edit-target.py` refuses only named control paths below
`~/.claude`, and these two are not among them, since a session's own memory
lives there and it cannot tell a save from an injection, so
`Edit(~/.claude/projects/**)` and `Edit(~/.claude/output-styles/**)` sit in
`disallowed-tools` of `/ship`, `/pr`, `/style-pass`, `/dispatch` and `/team`,
and `AWritingCommandDeniesTheUsersMemoryAndOutputStyles` fails when a managed
command that grants either tool lacks them. Like every frontmatter deny they
last only the turn that loaded the command (*A boundary an agent must keep
across turns*), so they cover that turn's reading of a branch and not `/ship`
after a background wake or a later round of `/dispatch` or `/team`. Within that
turn a session cannot save a memory: it saves it afterwards. A target's own
command is its own to hold, since a deny in a local file is no payload suite's
to require. **Measured in blueprint-admin** (its #291): on 2026-10-20, on
Windows, in a turn whose `/ship` frontmatter was in force, a `Write` to each
directory was refused with no prompt and one to `~/.claude/scratch/` succeeded,
so the home-relative specifier binds the absolute path a tool is given. **The
git guard does not back them (blueprint-harness#246):** `guard-git-argv.py`
judges paths anchored at a checkout's root, so it admits a redirect, `rm`, `curl
-o` or `tar -x` writing under either directory, read at `99cc539` by running the
guard over each. Whether the rule itself reaches such a command has not been
measured in blueprint-harness; a path deny reaches a `Bash` command naming the
path where it was (*A boundary an agent must keep across turns*), and a command
that computes its path would not name it. Admit the shell as open until that is
read.

**The index skill's prefix grants are the same speed bump as `git *--output*`.**
`Bash(bash .claude/skills/codebase-index/scripts/cbx search:*)` matches the
typed string; the shell still runs `$(…)` and a second command on that line.
`.claude/hooks/guard-index-argv.py` is the argv-level guard: substitutions,
extra command runs, write redirections, and `graph` / `clean` / `init` /
`watch` are refused.
The `cbx` wrapper is the other half — whitelist and
`CBX_NO_SKILL_AUTO_UPDATE=1` — and is not a substitute for the hook.
`cbx.ps1` runs in its caller's process, so the two variables it sets
(`CBX_NO_SKILL_AUTO_UPDATE` and `PYTHONSAFEPATH`) are put back in a `finally`,
and the one `exit` after it is the only way out: left set, `PYTHONSAFEPATH`
takes the script's directory off `sys.path` for every later `python` the
session starts. `test_cbx.py` runs it with `&`, in a session that reports its
environment afterwards, and skips where PowerShell is not installed.

**`CBX_NO_SKILL_AUTO_UPDATE=1` is also in `.claude/settings.json`'s `env`,
because the wrapper is not the only way the CLI runs.** A bare
`codebase-index` call from a checkout, which the argv guard admits, replaced
the tracked skill with the package's stock copy, measured on 2026-10-06:
seven files, the wrappers' `-P` and `PYTHONSAFEPATH` among what went. The
`env` block reaches every command a session runs **when the session applies
it**, and a cloud session's shell on 2026-10-08 did not have it: one
`codebase-index --help` there rewrote the same seven files and widened the
skill's grants to `Bash(cbx *)` (backend's token-usage log, 2026-10-08). So a
run outside the wrapper and the hooks sets the variable itself.

## Index refresh and worktree seeding

**`PostToolUse` runs `.claude/hooks/refresh-index.py` after every edit and
every shell call**, over the checkout the **edited path** belongs to, resolved
before it is walked. `cwd` answers when the event names no file,
`CLAUDE_PROJECT_DIR` when it names neither, and the working directory last,
and they differ exactly when it matters: an edit is admitted against the
session's tree or the one it forked from, so only the file's own path says
which of them changed. The hook spawns the CLI detached, discards both streams
and always returns 0: it runs on every call, so it may not make one wait, and
an index that cannot refresh is not a reason to fail the call that provoked
it. The exit status is the one thing it does keep, and a request whose update
failed goes back rather than counting as served: contention is what fails
here, the streams that would have said so are gone, and the loser may be the
run carrying the newest edit. A marker the worker cannot remove, a directory
in its place or a file Windows has open, is not an absent one: read as absent,
the worker took the lock and looked again for ever. It lets go of the lock,
waits briefly and tries a bounded number of times, then leaves the marker for
the next edit. The worker runs from the system temp directory,
outside every checkout, and only the CLI inside the checkout: Windows will not
delete a live process's working directory and `/ship` removes a worktree
moments after its last call, and Windows looks in the starting process's own
directory for a program named bare, so a checkout's parent, or for a worktree
a directory of the main checkout, would let a merged file stand in for the
CLI, which is started by the full path a PATH-only lookup found instead. The
CLI's own run still holds files in the checkout while it lasts, and the worker
holds the lock for that whole run, which is why `/ship`'s
`git-worktree-remove.sh` takes the lock before it removes. The lock covers a
worker and not the survivor its record names after a kill, which the helper
does not wait for: on Windows such a removal can leave part of the tree, and
the helper's exit 4 reports that. The worker is
started in a session of its own on POSIX, and on Windows with
`CREATE_NO_WINDOW` and a new process group, never `DETACHED_PROCESS`: a worker
started with that flag was measured to exit 0 without its child ever running,
and the hook discards every stream, so nothing said so while the index went
stale. `test_refresh_index.py` holds the set, because a run only passes on a
machine that tolerates the wrong one.

**The lock and the marker are the hook's own writes, so they live in the git
directory and not in the tree.** Each checkout has a pair under
`<git directory>/index-refresh/`, named for the checkout's device and inode so
that two spellings of one path share it. A branch can track a link at any
component of `.claude/cache/codebase-index/`, and `open` follows a link there
and truncates what it finds, so a state kept in the tree would be written
wherever the branch aimed it. Git tracks nothing under its own directory. The
directory is the one the hook file's own checkout reports, never the target's,
and where it, or the state directory in it, is itself a link there is no
state and so no refresh. Only those two names are looked at, so a link higher
up the path to the git directory is not seen. `git-worktree-remove.sh`
computes the same name from the worktree it removes, and
`test_git_worktree_remove.py` takes the hook's own for the lock it holds, so a
name that drifts in either fails there. It takes the name while the path still
stats and deletes the pair once the removal succeeded, because the inode a
later worktree is given would inherit the file and the pid in it; a file that
will not go does not change the helper's exit, and a worktree that never had a
pair is not given one.

**The hook refreshes a checkout of its own repository that holds an index,
and nothing else.** The repository is the one the hook file's own checkout
belongs to, compared by git directory, so a checkout of another repository,
or a submodule, that holds a cache directory is not this hook's to write
into. An index is a file past SQLite's 100-byte header: a cache directory
alone is not one, and an `update` there is a build, which the hook never
starts. An event that names a file the hook cannot place, a relative path
with no `cwd` or one the system cannot resolve, refreshes nothing, and is
never answered with the `cwd`, which names a tree the edit did not touch. The
worker is handed its root as an argument and runs for as long as edits keep
arriving, so it judges the root again at its entry and before every update.

**The lock is the worker's, and the indexer it starts is not.** A worker
killed mid-update frees the lock while the indexer goes on writing the cache,
and the next edit would start a second beside it. So the worker writes its own
pid into the lock file, through the handle that holds the lock, before it
starts the CLI, the CLI's once it exists, and nothing once the CLI is known to
be gone: reaped after a kill, not merely asked to end. A worker that takes the
lock and finds a live process recorded leaves the tree to it, and the marker
stays for the next edit. Whether a pid is live is asked of the system without
starting anything, `kill(pid, 0)` on POSIX and `OpenProcess` with the exit
code on Windows, where `os.kill` would end the process. Where it cannot be
told, the process counts as running: one skipped refresh, never two indexers.
The look is made before every attempt, a retry included, since an attempt
overwrites the record. The record is read with its age, the file's
modification time, so that a pid something else now holds cannot hold workers
off for ever: past 630 seconds, `REFRESH_TIMEOUT` plus `REAP_TIMEOUT`, a record
names nothing. A worker that finds its indexer outliving the kill writes the
record again, so the bound runs from that moment and not from the start of an
update that has already used it up. The cost is a residual: nothing kills the
orphan of a killed worker, so once its record is that old a worker may start an
update beside it.

**The CLI is found on `PATH` alone, and the answer is a full path, so the
operating system searches nothing.** A `PATH` entry naming a checkout is the
bare name's hole by another route, so the lookup takes absolute entries only,
none of them the working directory, and no candidate inside this checkout or
the main checkout it was forked from, a link's target included. On Windows
only an `.exe` or a `.com` counts, never `PATHEXT`'s `.bat` or `.cmd`: those
run through `cmd.exe` in the checkout, which looks there first for every
command the script names, so a shim found safely would reopen the search this
closes. pip writes the console script as an `.exe`. The worker runs that
script, then the module through its own interpreter: `py -3.12 -m pip` is a
supported install that leaves the script in a directory that need not be on
`PATH`, so both `cbx` wrappers fall back the same way, and a pipx install has
no module this interpreter can import, so the script is not dropped for it.
`CBX_NO_SKILL_AUTO_UPDATE` keeps an unpinned package from rewriting the
tracked skill, and `-P` keeps a checkout's own `codebase_index.py` off the
import path. The hook sets `NoDefaultCurrentDirectoryInExePath` for everything
it starts, the worker and the CLI, and does not leave it to its launcher: the
CLI runs in the checkout, where a branch can plant a program that Windows
would otherwise find first for any name the CLI starts bare.

**Both shell tools are in the matcher, `Bash` and `PowerShell`, because
agents write through them more than through any edit tool** — a commit, a
pull, a rebase, a formatter, an edit script — and each left the index
describing the tree it replaced while it reported itself fresh. A shell call
names no file, so its `cwd` decides, and that field follows the shell into a
worktree. It is not gated on the command, which would
miss every helper and script: a call that wrote nothing costs one `update`
that changes nothing, measured at about a second on this corpus, detached and
behind the lock's coalescing. A commit changes no file, and that same `update`
is what moves the index's recorded HEAD.

**A linked worktree with no index is seeded from its main checkout's, and
nothing is ever built.** `.claude/cache/codebase-index/` is ignored, so every
`/branch` worktree starts without one, and the first query there used to run
a full `index` inside the agent's turn: 230 s here, where copying the main
checkout's `index.sqlite` took 0.4 s and the `update` after it 5 s. The
worker seeds under the refresh lock before it updates, through SQLite's
backup rather than a file copy, because the main checkout's index may be
mid-`update` and a copied file can tear. The copy is made beside the index
under a name for the worker's process and renamed in only once its header
says WAL, the mode the package's readers need, and the next seed, which holds
the same lock, removes the partial and the files SQLite opened beside it that
a killed worker left. Only `index.sqlite` crosses — the
main checkout's memory and configuration are its own state — and only from a
regular file at the main checkout's own index path. The worktree is told
from the files git writes, a `gitdir:` pointer whose `commondir` leads to a
`.git` directory, so a submodule and a bare repository are not worktrees. A
main checkout with no index leaves the worktree with none, and any other
checkout with no index is left alone, because unindexed is not stale.
`git-worktree-fork.sh` runs the new worktree's own hook once, because
`/branch` enters it mid-session, where no `SessionStart` fires.

**A checkout's `.git` is a claim it makes about itself, so the hook takes
only a checkout git made.** A directory whose `.git` file names one of this
repository's admin directories reports this repository's common directory
without git having made it, and its tree would be indexed with this
checkout's tooling. So a `.git` directory must be the repository's own, and a
`.git` file must be no link, have one name, and name an admin directory under
`<common>/worktrees/` whose `gitdir` names that file back. A hard link shares
the real file's inode and so its backlink, which is why a second name is
refused; while one exists the real worktree is refused too, a denial of
refresh by someone who can already write beside the repository rather than a
tree of theirs being indexed. The main checkout a worktree is seeded from is
judged the same way. A checkout named `secsweep-…`, in any case and as it
resolves through a link, is refused outright: both sweeps detach their
worktrees under that prefix, and the tree one holds is audit input.

**An index is opened only at rest, in WAL mode, and with no link on its
path.** A branch can force-track any file under
`.claude/cache/codebase-index/`, the first connection to a rollback-mode
database replays a `-journal` it finds beside it, and SQLite opens each
sidecar by name, so a link at one aims its write where the branch chose.
`index_openable` refuses a link at any component of the cache path, at the
database or at its `-wal`, `-shm` or `-journal`, a journal at all, and a
header that is not WAL; a root with no index, or one shorter than a header,
is admitted, since nothing in it can be replayed. It is asked of the target,
of the seed's source and target, before every update, and before and after a
compaction. A `-wal` is admitted: every open reader keeps one. The cost of a
refusal is a stale index, never a write.

**A seed lands only where git ignores the index path.** It leaves the main
checkout's indexed text at a path only `.gitignore` keeps out of a commit,
and a branch checked out in the worktree can drop that line, which would put
that text in front of an unscoped `/commit`. `git check-ignore` is asked
last, only where a seed could otherwise happen, so a checkout with no index
and nothing to seed it from costs no git call; it is asked once per root and
process, with `GIT_DIR` and its kin removed and a three-second git budget,
and a yes or a no git gave is kept; an answer
git did not give is a no, and is asked again later. Nothing is seeded over
anything already at the index path, a placeholder a branch tracks included.

**The worker compacts what it refreshed, because the package never does.**
Each `update` leaves full-text segments and free pages behind, and with
`auto_vacuum` off the file only grows. Past half the file in free pages, or
ten segment rows per chunk, a successful update is followed, under the same
lock, by FTS `optimize` and `VACUUM`, never in place: the index is read
through an immutable read-only connection, which takes no lock, reads no log
and replays no journal, copied into the state directory, compacted there on
a connection that distrusts the file's schema and has its triggers off, and
renamed over the index. Only a file whose tables are the package's shape is
touched. Nothing is done while a `-wal`, `-shm` or `-journal` exists, since a
log beside a replaced database is replayed into it; that, the file's size and
modification time are asked again just before the rename, and a statement or
a copy step past the update's timeout is interrupted. Every failure leaves the
index as it was. Two residuals stand: the state directory and the index must
share a volume, or every rename fails; and a connection that opens the index
between the last look and the rename would have its log replayed into the new
file.

**`cbx` asks the same judgement before it starts the CLI.** `gate.sh` runs
`gate-index.py`, which loads this hook and asks `index_openable` of the
working directory and of the checkout git puts it in; exit 3 is a refusal,
and no Python 3.12 is exit 4 rather than a pass, since the CLI would open an
index nobody judged. `cbx.ps1` runs the same `gate-index.py` inside its `try`,
under the first `py -3.12` or 3.12 `python`; the gate's exit becomes the
script's and the CLI starts only when none was set, so the one `exit` after
the `finally` still holds. `test_cbx.py`'s launcher stubs answer the gate's
`-I` call with the exit `CBX_STUB_GATE_EXIT` names; the bash wrapper's
stubs always pass it. The window between the
gate's look and the CLI's own open is the one every check here leaves.

**The approval of `.mcp.json`'s servers belongs to a directory, so the fork
carries it.** A session started in a worktree reads that worktree's own
`.claude/settings.local.json`, untracked and so absent, and `claude mcp list`
there reports `codebase-index` pending approval while every plugin server
connects; one that enters the worktree mid-session keeps the main checkout's
approval. The same keys in the tracked `.claude/settings.json` were measured
to leave the server pending, so of the project's two settings files only the
local one carries them. `git-worktree-fork.sh` writes the worktree a
`settings.local.json` holding one key, `enabledMcpjsonServers`, naming the
servers the main checkout approves and does not disable, with an
`enableAllProjectMcpServers` expanded to the servers `.mcp.json` defines at
the fork, and nothing else. The rest of that file is the main checkout's own
`permissions` rows, and copying it would widen every worktree by them; the
approve-all itself would approve servers defined after the fork. `.mcp.json`
is gitignored and not copied: the session finds the main checkout's by
walking up, because the helper forks only under `.claude/worktrees/` and the
worktree has none of its own. Two residuals stand. The helper writes a file
the session's `Edit` is denied, and what it writes is one key read from a
file the session is denied too, or, under an approve-all, from `.mcp.json`,
which the session's `Edit` is denied as well and whose servers the main
checkout's approve-all already approves. And the approval names servers
rather than definitions, so a `.mcp.json` that reaches a worktree through
git, force-added past the ignore, is found before the main checkout's and
approved under the same name without a prompt. The fork writes nothing at a
`settings.local.json` the branch already carries, a dangling link included,
and the locality gate refuses a pull request that leaves an ignored path in
the tree, so neither file reaches `main`; a branch checked out before its
pull request is judged still can carry a `.mcp.json`.

**`SessionStart` runs the same script, for the moves no tool call makes.** A
merge, a switch or a pull made outside the session rewrites the tree with no
tool event behind it, so a session opening onto one of those reads an index
describing the tree it replaced. The event names no file, which is what the
`cwd` fallback above is for, and the spawn is the same detached one, so a
session start waits for nothing. What is left to a query's stale report is a
tree that someone other than the agent moves mid-session, and that is the one
a person is present for.

## Index hint hooks and hook wiring

**`UserPromptSubmit` runs `.claude/hooks/index-query-hint.py`, because keeping
the index current never got it read.** The skill's `description` says to
query it, and from the transcripts no session here did after the day the
skill was tuned. A prompt that reads as a locate, explain, references, impact
or named-symbol question gets one line of `additionalContext`: the MCP tool
that asks it, or the skill and the route-table row for that question, and
the session-tag rule. Every other prompt gets nothing, and so does every
slash command but `/ship` and `/branch`, whose arguments are read as the task
they carry. **It names the skill and not only the command** because, but for
the four reads `/ship` grants for its own steps, the skill's `allowed-tools` is
the only grant for the `cbx` wrapper, and it holds only while the skill is
loaded; **it names the tool's full name** because
Claude Code lists an MCP tool by name only, and a session must load it with
ToolSearch before the first call, while Grep is always loaded. It reads only
the decoded `prompt` string, never the payload's paths, writes nothing,
spawns nothing and leaves with 0 whatever happened: exit 2 under this event
blocks the prompt and erases it, which is also why it is started the way the
paragraph on hook commands below says. `test_index_query_hint.py` holds
every command it emits to a row of the route table and a prefix the skill
approves. **The term in each command is single-quoted**, here, in the skill's
table and references, and in the search hint's refusal: inside double quotes
bash still runs a `$(…)`, a backtick or a `$VAR` the term carries, and a term
copied from a log or a branch's file can carry one. `guard-index-argv.py`
refuses the first two and not the third, so `cbx refs "$event"` asks about
nothing and says so to no one. A single quote inside a term is written
`'\''`, and `test_index_query_hint.py` holds every `cbx` line of the skill's
markdown free of a double quote.

**`PreToolUse` runs `.claude/hooks/index-search-hint.py` before `Grep` and
both shell tools, because the prompt is not where the code questions are.**
A task prompt — `/ship` and an issue — asks none of them; they come up inside
the turn, where a prompt hook never runs, and from the transcripts not one of
the sessions here that searched code in the week to 2026-10-06 made a lookup.
A session's first search of the tree for a named symbol — the Grep tool, or a
recursive `grep`, an `rg` or a `git grep` whose pattern is an identifier or
an alternation of them — is refused once, while the session has asked the
index nothing, and the reason is the lookup that answers it: `find_refs` for
what uses each name and `find_symbol` for where it is defined. The rerun goes
through, and so does every later search.

**It asks before the search because a hint after it changed nothing.** The
first version spoke after the call, on the first search and every fifth, and
in the first `/ship` session that ran it the agent read both hints and
grepped on, ending with no lookup (#564): by then the grep had answered, and
asking the index meant redoing a search that had worked. A refusal is the one
form the agent cannot read past, so it is spent once, on the search the index
answers better. A symbol is an identifier that mixes cases or carries an
underscore and does not end in a lowercase extension; **anything else is
text and is never refused**: a word in one case, `timeout` or `TODO`, a file
name with an extension, `CLAUDE.md` or `settings.json`, a message, a regular
expression or a phrase is a search for every occurrence, which grep lists
completely and a ranked top three does not, and so is a search confined to
`docs/` or to Markdown, of one file, or of a pipe.

**Once is a file, and the transcript says the rest.** The refusal is recorded
by creating a file named after the session under the temporary directory,
which only one call can create, so a parallel batch is refused once and a
session whose file cannot be made is never refused, since the rerun would be
refused too. A lookup made by any route — an MCP tool or the CLI, through
either shell — lets every search through, and the hook reads that from the
session's transcript, parsing a call's line only when it names the index
and a result's only when it answers a lookup, and stopping at the first
lookup that ran: on a 62 MB transcript the whole read took 0.1 s. A call a
guard refused, or a tool's error, asked the index nothing and is not a
lookup; a shell command naming the CLI that exited non-zero counts as one,
though a chain can fail before the CLI runs. A subagent is never refused:
its profile may hold neither the MCP tools nor a shell.

**It names one route, the one that answers from this tree.** The MCP server
starts with `--root .` in the directory the session started in, and
`EnterWorktree` does not restart it, so after the move its answers describe
the main checkout's index and not the branch's edits (#563); `cbx`, run from
the worktree, reads the worktree's own seeded index. So where the session
stands in another checkout than the one `CLAUDE_PROJECT_DIR` names, the reason
names the skill and the CLI alone, each command alone on its line because
`guard-index-argv.py` refuses a shared one, and elsewhere the MCP tools alone,
with the ToolSearch spelling that loads them. That is the comparison
`index-mcp-root.py` below refuses a call on, so a session started in a worktree
is sent to the MCP tools there, and one that moved back to the main checkout
to the CLI. With no such variable, or no checkout at either end, a linked
worktree is taken to be off the server. A server that follows the session is
not something `.mcp.json` can say. `test_index_search_hint.py` holds the wiring,
what is refused and when, the route against that hook's refusal, and the
commands it emits to prefixes the skill approves; `test_index_query_hint.py`
holds the two hooks to one map from question to tool.

**`PreToolUse` runs `.claude/hooks/index-mcp-root.py` before every
`codebase-index` MCP tool, because naming the CLI did not stop the MCP
call.** The server reads `CBX_ROOT` on every call but sets it once from
`--root`, and never asks the client for its roots, so no session can move
it (#563). The hook knows both ends: `CLAUDE_PROJECT_DIR` is the directory
the session started in, which is the one `--root .` resolved against, and
the event's `cwd` follows the session into a worktree. When the two lie in
different checkouts the call is refused, and the reason names the `cbx`
command to run alone from the session's checkout: the whole line, arguments
quoted for the shell, where it can carry every argument the call holds, and
otherwise the subcommand alone, since a line that dropped an option such as
a depth or a limit would answer another question. `healthcheck` is never
refused, since its `root` is how a session sees which tree the server reads,
and a session started in a worktree is let through, since its server reads
that worktree. `test_index_mcp_root.py` holds the wiring, the map from tool
to command against `index-query-hint.py`'s and the wrapper's whitelist, and
every command it emits to `guard-index-argv.py`.

**The two hint hooks run under `.claude/hooks/hint-bound.sh`, because a hook
the harness kills leaves nothing to say it did not run.** Raising their
timeout to 15 s narrowed the window a slow `py` launcher start can fall
into and could not close it: the harness discards the output of a hook it
times out, so a hint that stalled looked exactly like a hint that did not
apply. The wrapper runs the hook under `HINT_BOUND_SECONDS`, 10 s and below
the settings timeout, and on a stall, or any non-zero exit, prints one
`additionalContext` line naming the hook that did not run. It leaves with 0
either way, since exit 2 under these events blocks the prompt or the call,
and it writes the hook's output to a file and not a pipe, so a launcher's
orphaned child cannot hold it past the bound. **It wraps the two hints and
no other hook**: the guards fail closed on their own and carry no timeout,
and `index-mcp-root` and `refresh-index` print nothing a session reads as
advice. Two residuals are accepted, because neither can be reported from
inside the hook and neither has been seen: the wrapper's own start, where
`bash` stalling past the settings timeout is a case no hook can report, and
a machine without Git Bash, where the paragraph on hook commands needing no
shell says why the hint is dropped. A fresh `bash -c true` on the owner's
workstation, read 2026-10-13, took 0.1 s to 3.3 s over six starts, so the
cold start that stalls a `py` launcher stays far below the settings
timeout. A `shell` field with an invocation that needs no `bash` would
close the second, and is not taken: it would run a hint hook the wrapper
cannot bound.
`test_hint_bound.py` runs the wrapper against a launcher that never answers,
one that fails and one that answers, and holds `settings.json` to routing
both hints through it with a timeout above the bound.

**`MCP_TIMEOUT` is raised in `.claude/settings.json`'s `env`.** The plugin
servers started through `npx` — `playwright` and `chrome-devtools` — failed
their first start often enough to drop out of sessions, and Claude Code
caches such a failure for 15 minutes in the user-level
`~/.claude/mcp-needs-auth-cache.json`, so every session on the machine then
started without them. This repository's logs on the owner's workstation, read
2026-10-05: of the plugin starts under the 30 s default that logged an
outcome, 104 connected and 28 timed out, and the connected ones reached
29.7 s with a 95th percentile of 28.3 s and a median of 3.8 s, so the tail
runs into the limit rather than stopping short of it. Under the raised limit
none has timed out yet, and three `claude mcp list` runs started at once each
connected every plugin server within 6 s. It is set high because a long limit
only delays the report on a server that is truly broken, and the session does
not wait on it. It covers sessions started here, while the cache is
user-wide, so the same key in the user's own `settings.json` `env` is what
closes it everywhere; the owner's already carries it. No failure was a tool
call, so `MCP_TOOL_TIMEOUT` stays at its default. `.mcp.json`'s
`codebase-index` is not a plugin, and its failures are never cached.

**A hook command needs no shell, and that is a constraint rather than a
preference.** An env-var prefix — `VAR=1 cmd` — is POSIX syntax that `cmd`
and PowerShell do not run, and a bare `bash` is whatever `PATH` resolves
first, which on Windows may be Git Bash or the WSL launcher: different
programs, with different filesystems. Either way the failure lands in the
redirect and reports nothing. So each index hook here is `py -3.12` and
its arguments, with no env-var prefix and no bare `bash`. **The two hint
hooks and the guards are the exceptions**, started as
`bash …/hint-bound.sh` and `sh …/run-guard.sh`: Claude Code passes a hook's
command to Git Bash on Windows, so the `bash` and `sh` inside it are Git's
own and never the WSL launcher. A machine without Git Bash falls back to
PowerShell, where neither wrapper runs: the hint is dropped, as it was before
the wrapper existed, and the guards' residual is *The guard launcher*'s. The
index hooks are
started through `runpy` from `-c` rather than as a file argument, because
Python exits 2 on a file it cannot open, as when a worktree is emptied under
a session still running in it: under `UserPromptSubmit` that erases every
prompt, and under `PostToolUse` it reports an error after every call, where
through `runpy` the failure exits 1 and only reports. They read
`CLAUDE_PROJECT_DIR` from the environment rather than having it spliced into
the program, which a path holding a quote would break. The refresh hook sets
its own environment — `CBX_NO_SKILL_AUTO_UPDATE=1`, which it owes because it
calls the CLI rather than the `cbx` wrapper that would have set it.

`examples/hooks/settings.json` carries the same block. It sits under
`examples/`, which Claude Code does not read, so it documents the wiring
rather than being it — and a second spelling there would be the copy that
stops agreeing with the file that runs.

## Settings self-lock and permission root

**`settings.json`'s own entry self-locks, and that is a working constraint, not
a curiosity.** Once it denies itself, the session cannot edit it again —
including to undo the edit. So a change to it is one edit that lands complete,
and it goes **last** in any PR that also touches `commands/` or `scripts/`;
split across two edits, the second is refused and whatever the first omitted
ships missing.

**The lock is not instantaneous, and the gap is a trap rather than a
convenience.** Measured on Claude Code 2.1.247, in the session that restored
these denies for the harness-bounds PR: immediately after writing
`Edit(.claude/scripts/**)` back, a `Write` creating a file under
`.claude/scripts/` **still succeeded**; minutes later an `Edit` under
`.claude/hooks/` and an `Edit` of `settings.json` were both refused. The file
is re-read, but not at the instant it is written.

So **a probe taken right after the restore reports the deny as inert and is
simply early** — and an agent that treats that reading as the answer will
conclude it still has access it is about to lose. Verify a restore by reading
the file, never by trying the thing it forbids.

**The practical consequence is an ordering rule for the human doing the
lifting**: restore the lines only when the work is genuinely finished, because
the window closes on its own schedule and a defect found afterwards needs the
lift granted again. That happened on the PR that added the argv guard — the
guard shipped refusing its own commit message, and the fix needed a second
lift.

**A lift lags the same way, and a relaunch is what ends it.** Measured in
blueprint-backend on 2026-09-21: with the four `Edit(...)` deny lines deleted
from the worktree's `.claude/settings.json` and the file read back, an `Edit`
of `.claude/commands/ship.md` was still refused as *in a directory that is
denied by your permission settings*. So a session needing an edit under
`.claude/` has the owner lift the deny and relaunch Claude Code from the
worktree directory, where `/ship` step 0 stays in the unfinished worktree;
launched from the main checkout, `/branch` stops on a slug already taken.
The denies name directories, not the manifest, so a path a target lists
under `local` in a denied directory is refused exactly as a managed one is.

**`.claude/settings.local.json` was the gap a review found**, and it is the
enumeration lesson in miniature: an exact-file rule cannot cover a sibling, and
Claude Code loads both files. Denying `.claude/**` wholesale was considered and
refused — `.claude/worktrees/` is where `/branch` puts working checkouts, so
that blanket would deny a main-checkout session every edit to a live worktree.
A test pins both halves: every loaded settings file denied, and the
worktree root never denied.

**Every rule here anchors at the session's permission root, and
`EnterWorktree` moves it.** Measured with nested `claude -p` sessions: once a
session has entered `.claude/worktrees/<slug>`, `Edit(.claude/commands/**)`
and `Edit(.claude/hooks/**)` refuse the worktree's own `.claude/` and `docs/`
stays writable. From the main checkout the same paths sit under
`.claude/worktrees/<slug>/.claude/`, which no rule here names; what refuses
them there is the edit guard, which judges a worktree the session has not
entered by this target's own denies (*The edit-target guard*), and behind it
Claude Code's own sensitive-file check, which measured every `.claude/`
target inside a nested worktree — hooks, scripts, commands, agents, settings
— as a refusal under `acceptEdits` and a prompt otherwise. The residual is a
path there that no deny names, which that check alone prompts for, as it
does for a path outside the project.

Changing any of them is a human's edit, made with the deny lifted. Like the
push denies it is defence in depth — a redirection or a writing verb is judged
by the git guard (*Git argv guard hook*), and any other program can still
write a file — but it removes the quiet path.

**`.claude/hooks/**` joined the list when the first hook landed, and the way it
joined is the lesson.** It had been excluded on a stated condition — "no hook is
configured here" — which was true, and which is the kind of exemption that
expires without anyone noticing, because nothing re-reads the condition when
the fact changes. #30's argv guard made it false. A hook is the sharpest case
on the list: it grants nothing, but it **runs on every Bash call**, so a session
able to rewrite one could delete its own guard and then act. `settings.json`
denies it, and a case in `test_harness_denies.py` asserts the deny, so the next
hook arrives behind a control rather than behind a sentence that used to be
true.

## Grant semantics

**The external reviewers are archived, not deleted.** Grok's container
review and Copilot's loop, their commands, agents, helpers, hooks and
suites, and the sandbox image left backend's tree with its token plan's
step 4; the tag `archive/external-reviewers` holds them, and
`git checkout archive/external-reviewers -- <paths>` restores them. The
sections below keep the residuals those tools raised where the grant or the
guard they shaped still stands.

**A grant is not a whitelist, and this is the trap under every row below.**
`allowed-tools` is an **auto-approval list**: the harness documents that it
"does not restrict which tools are available: every tool remains callable, and
your permission settings still govern tools that are not listed". So *omitting*
a tool from a command's frontmatter withholds nothing — it only decides
whether the call prompts. Refusing a capability takes a **deny**:
`permissions.deny` in `.claude/settings.json` for the repository, or the
`disallowed-tools` frontmatter key for one command, which removes the named
tools from the pool while it runs. Precedence is **deny → ask → allow**,
first
match wins, so a deny beats every allow including a frontmatter one.
Measured, not read: a `general-purpose` subagent spawned fine under
`--allowedTools "Agent(Explore)"`, and was refused under
`--disallowedTools "Agent(general-purpose)"` with `Agent type 'general-purpose'
has been denied by permission rule 'Agent(general-purpose)' from cliArg` — and
again, from a command's own frontmatter, `from command`.

## Prefix grants and wildcard denies

**A helper is the answer whenever a git grant is wider than the operation it
buys**, because **an allow rule is a prefix and cannot exclude a flag**. Each
of these was confirmed by running the offending form rather than reasoning
about it:

| Raw grant | What it also bought |
|---|---|
| `Bash(git switch:*)` | `--discard-changes` and `-C` — and the flags **combine**, so `git switch -fC <name> <start>` defeats any `Bash(git switch -C:*)` deny |
| `Bash(git worktree add:*)` | `-B`, which resets an existing branch rather than creating one |
| `Bash(git checkout -b:*)` | the trailing flag — `git checkout -b <name> -f origin/main` discards tracked modifications |
| `Bash(git branch:*)` | `git branch -fd <name>` — force and delete behind a spelling the `-d`/`-D`/`--delete` denies do not match |
| `Bash(git reset HEAD:*)` | `git reset HEAD --hard` — the `--hard` deny matches the other word order |
| `Bash(git log:*)`, `Bash(git diff:*)`, `Bash(git show:*)` | `--output=<path>`, which is an arbitrary file write with `--format=` choosing the bytes. Reproduced: `git log -1 --format=%s --output=<scratch>` wrote the commit subject, silently |
| `Bash(git fetch:*)`, `Bash(git pull --ff-only:*)` | a URL in the repository position, and `ext::<cmd>` is a git transport that **runs its argument as a command** |

**The reset grant does not narrow, and the attempt is the sharpest lesson
here.** It was "fixed" to `Bash(git reset HEAD --:*)` on the reasoning that
`--` turns a later flag into a pathspec. True of *git*, irrelevant to the
*rule*: an allow rule is a prefix match, and `git reset HEAD --hard` starts
with `git reset HEAD --`, so the narrowed grant admitted the exact command it
excluded — while the commit message said the hole was closed. **The git
behaviour was verified and the matching was not.** Anything whose safety
depends on what follows a token needs a helper, or a **deny**, not a cleverer
allow.

**A deny is the thing an allow cannot be: `*` matches at any position in it,
including the middle.** `Bash(git *--output*)` refuses `git log`, `git diff`
and `git show` carrying `--output` anywhere in the argument list, and leaves
plain `git log` alone — both halves measured, because a deny that blocked the
command outright would read the same from the failing side. Removing the three
read grants instead would have bought nothing: the harness treats read-only
forms of `git` as promptless built-ins whatever the allow list says, and its
own note is that "to require a prompt for one of these commands, add an `ask`
or `deny` rule".

**"Including the middle" is the measurement; the documented rule is wider, and
the two are worth keeping apart.** What was measured here is a wildcard between
two literals. The harness's own reference says a Bash rule's wildcards "can
appear at any position in the command, including at the beginning, middle, or
end" — so a pattern *starting* with `*`, which this repository had no precedent
for, is supported rather than merely untested. That was read out of the docs
after a branch shipped two such denies and flagged them as an unverified guess,
which is the right order: **write down which half you measured and which half
you looked up.** Two neighbouring constraints came from the same page and
match what is recorded above — the `:*` form is recognised only at the end of a
pattern, and a trailing `*` preceded by a space enforces a word boundary, so
`Bash(ls *)` misses `lsof` where `Bash(ls*)` catches it.

**It raises the cost of the naive spelling and it is not a boundary**, and the
distinction is the whole of the reset-grant lesson one paragraph up. A
permission rule matches the command *string*; the shell reassembles adjacent
quoted fragments before `exec`, so `--out''put=<path>` reaches `git` as
`--output=<path>` while never presenting contiguous `--output` to the matcher.
Copilot raised this against the rule as shipped and it is accepted. **The
concatenation is now MEASURED**, where this paragraph used to say it was not:
`printf '%s' --out''put=/tmp/x` prints `--output=/tmp/x`, and so does
`--"out"'put'=`. It had rested on documented quote-removal semantics because
the earlier probe was refused by the classifier layer — a second net worth
noting and not evidence. It is evidence now, and it says the deny was genuinely
defeatable rather than theoretically so.

**The helper grants have the index grants' gap, and a guard of their own.**
`Bash(py -3.12 .claude/scripts/dispatch_board.py:*)` matches the typed string
too, and takes text a model composes from issue titles, where a backticked
name is the house style; a pull request's title is the same text. So the text
stays off the command line, in the files `/dispatch` *Issue text stays off the
command line* names, and in the title file `gh-pr-create.sh` reads, which
withdrew the raw `gh pr create` grant (*Grant inventory and push helpers*).
`.claude/hooks/guard-helper-argv.py` is what holds when a session spells it
otherwise: it refuses a command substitution anywhere in a command that runs a
`.py` under `.claude/scripts/` or `gh pr create`, as `guard-index-argv.py` does
for its own. So `oid=$(git rev-parse HEAD)` and a helper line beneath it are
refused though that line is clean: a heredoc's substitution runs for the
helper's stdin on no helper line, so the whole command is what is judged, and
such a value is read in a call of its own. It takes the command word past
assignment prefixes, `env`, `timeout`, `nice`, `nohup`, `stdbuf`, `time`,
`command` and `exec`, the interpreter's value flags (`py -X utf8`) and gh's
`--repo`, as `guard-sed-crlf.py` does; a wrapper it does not name (`xargs`,
`sudo`, `bash -c`) is a residual. It leaves the `.sh` helpers, whose
arguments are numbers and names, and it judges substitutions alone: a second
command is the grant's own matching.

## The guards' threat model

**The guard hooks catch an agent's honest mistakes; they are not a sandbox
against a session that sets out to defeat them.** `guard-git-argv.py`,
`guard-edit-target.py`, `guard-index-argv.py`, `guard-helper-argv.py` and
`guard-sed-crlf.py` are written against a session that types a dangerous
command by accident or by a plausible shortcut — a forced push, a write
into a denied tree, a removal of a parent of the checkout, a `sed -i` over
a CRLF file — in any spelling
a shell user writes in ordinary work: quoting, `-C` and `--work-tree`,
environment prefixes, `cd` chains, the launchers and wrappers, heredocs,
pipes into a shell. A guard that judges a file the command names, as
`guard-sed-crlf.py` does, is held to that list where the command's text
holds the name; a file list another program computes is its stated
residual (*The sed line-ending guard*).

They are not written against a session that builds a command so the hook
cannot read it. A reader that runs before expansion can always be
out-spelled: prompt and indirect expansion (`${x@P}`, `${!x}`), an escape
placed to split a token or hide a pipe, text assembled and then evaluated,
another interpreter's string arithmetic, a file redirection into a group
that launches a second shell, input sized to outrun the hook's timeout. Each
such spelling found and closed exposed another — the run in blueprint-admin
from #297 to #461 is the record — and none of them is how an agent doing
ordinary work goes wrong. What bounds a session that sets out to do harm is
elsewhere: the permission deny list, auto mode's classifier, `main`'s branch
protection (no direct push, no force push, required checks), and the owner's
review of every merged pull request.

**A finding against a guard is in scope, and filed, when one of these
holds:**

1. the spelling is one an agent types while doing ordinary work: it appears
   in a transcript, a runbook or a helper, or it is a routine shell idiom;
2. the guard fails open on input given in good faith, by a crash, a decode
   error or a timeout on ordinary input, because then it admits the honest
   mistake it exists to stop;
3. the guard refuses a command it should allow, because a false refusal
   blocks work and teaches sessions to route around the hook.

**Out of scope, and not filed:** a spelling whose only purpose is to hide a
command from the hook. A review or a sweep that finds one records it in its
summary as outside the guards' threat model and moves on; an issue already
open for one is closed as not planned, with a link to this section. This
section is the owner for all three repositories; blueprint-admin and
blueprint-frontend cite it rather than restate it.

## The guard launcher

**Each guard's hook command is
`sh "${CLAUDE_PROJECT_DIR}/.claude/hooks/run-guard.sh" <guard>.py`, and the
launcher leaves with 2 on every way it can fail.** A `PreToolUse` hook blocks
on exit 2 alone; any other status is a non-blocking error, and the tool runs
unguarded. A raw `py -3.12 <guard>` that cannot start exits 127, and `py` is
the Windows launcher, absent from a standard macOS or Linux Python, so there
every call ran with no guard at all. The launcher came from
blueprint-frontend, where that happened, and is in the payload because
`render_settings` only adds hooks: a target cannot replace a base hook
command through its config. It runs the five guards and nothing else.

- **A closed set**, as every helper's: `guard-git-argv.py`,
  `guard-index-argv.py`, `guard-helper-argv.py`, `guard-sed-crlf.py` and
  `guard-edit-target.py`. Any other argument, a path included, exits 2, so
  the hook wiring cannot run an arbitrary script, and a guard name missing
  from the set refuses every call rather than passing it unguarded.
- **Each candidate is run before it is chosen**, `py -3.12`, `py -3`,
  `python3` and `python` in that order, with a probe that exits non-zero
  below 3.12: a name on PATH is not a working interpreter, and on Windows
  `python3` is the Store alias, which is why `py` comes first. One `exec`
  then runs the guard. A `||` chain would run it twice whenever the first
  refused, judging the event a second time. No candidate passing exits 2.
- **The probe is paid once per checkout.** The first call that finds the
  first candidate working leaves an empty directory,
  `.claude/cache/run-guard.<candidate>`, beside the launcher of the checkout
  `CLAUDE_PROJECT_DIR` names, and a later call that finds it skips the probe;
  a session started in a new worktree pays its own first probe. The mark is
  never opened: a session can make one, so it holds nothing that could name
  a program, and a forged one buys a skipped probe of
  the program the probe would have run. Only the candidate probed first is
  remembered, so a host whose first candidate fails pays the probe every call.
- **A remembered candidate runs `run-guard.py`, as a child, and is believed
  on three statuses only.** An `exec` cannot notice an interpreter
  uninstalled or repointed below the floor since the mark was made; it exits
  49, 103 or 127 and the tool runs. `run-guard.py` checks the floor in the
  interpreter about to judge, runs the guard as `__main__` from its source,
  and answers 91, 92 or 93 for the guard's 0, 2 and anything else, which the
  launcher turns back into 0, 2 and 1. Every other status, 0 included, is the
  call refused and the mark removed, so the next call probes again. A mark
  something was put inside cannot be removed, and refuses every call until
  somebody removes it, saying so.

The hints keep `hint-bound.sh`, which bounds them and leaves with 0, and the
index hooks keep `runpy`, which leaves with 1 on a file it cannot open
(*Index hint hooks and hook wiring*): neither guards anything, and the
launcher's exit 2 would erase a prompt or block every call.

**Residuals.** A machine without Git Bash runs a hook command under
PowerShell, which has no `sh`: the command fails with a status that is not
2, and the guards do not run. Claude Code wants Git Bash on Windows and the
owner's machines have it, so this is accepted rather than closed. A host
changing under a mark costs one refused call. A signal sent to the launcher
from outside ends it as it would end the guard. `test_run_guard.py` holds
the launcher, its mark and its proof; each guard's suite holds its wiring to
the launcher, and the launcher and `run-guard.py` to the 3.12 floor.

## Destructive classes

**One table of what the harness refuses, the layer that refuses it, and a
test that holds the table to both.** Every class of operation here is
destructive on the repositories or on the machine, and each is stopped by
a layer that can be named: a permission deny in `.harness/settings.base.json`,
`guard-git-argv.py`, `guard-edit-target.py` or `guard-sed-crlf.py`, the `main`
ruleset on the server, or a granted helper's own refusal of `main`. A reader
asking whether `git push origin +HEAD:main` is stopped, and by what, finds the
row; a base deny added without one is a failing test, not a paragraph that
went stale.
`.claude/scripts/test_destructive_classes.py` reads the table and checks it
five ways: every `permissions.deny` entry of the rendered root's
`settings.base.json` is matched by some `deny` row's example, every row's
example is refused by the layer it names, every non-`ruleset` row's *Test*
names a suite file that exists, the push, trailer and drop helpers each have a
`helper` row, and the table parses to at least fifteen rows, so a renamed
heading cannot leave the other four vacuous. Its root is the directory two
above it: the rendered root in a target, `payload/` in the harness repository,
which has the same layout.

**The columns.** *Class* is the operation, *Example* one spelling of it,
*Refused by* the layer the example exercises, and *Test* the suite file that
holds the row, which the suite checks exists (a `ruleset` row names
`ruleset-check`, and a `helper` row a suite and a test, as below).
*Refused by* takes six values, and the test accepts no other:

- `deny` — the example matches an entry of the base deny list. A `Bash`
  entry follows Claude Code's prefix reading: `Bash(x:*)` matches `x` and
  `x …`, and a `*` inside a rule matches any run. An `Edit` entry is
  matched by an example spelled `Edit(<path>)`, with the `./` stripped from
  both sides, so one row holds a rule and its `./`-prefixed twin.
- `guard-git-argv` — the example is a command, fed to
  `.claude/hooks/guard-git-argv.py` as the JSON a PreToolUse call carries.
  The hook exits 0 and its stdout holds `permissionDecision` `deny`.
- `guard-edit-target` — the example is spelled `Edit(<path>)`, and the test
  feeds the hook an `Edit` of that path in a scratch checkout in which
  `linked/<name>` is a link to each denied tree (`scripts`, `commands`,
  `agents`, `hooks`, `skills`, `remember`, and `claude` for the
  directory holding `settings.json`). The hook refuses a path whose spelling
  is inside an allowed tree and whose resolution is not, so the example
  spells its way in through a link.
- `guard-sed-crlf` — the example is a command, read by
  `.claude/hooks/guard-sed-crlf.py` from a scratch git checkout tracking
  `crlf.txt`, which holds CRLF endings. The test calls the hook's `offence`
  with `strips_cr=True` rather than running it, since the hook judges on
  Windows alone (*The sed line-ending guard*) and CI runs this suite on
  Linux.
- `ruleset` — the server-side backstop on `main`, and its test is
  `ruleset-check`, which the table names and the suite does not run. It is
  blueprint-harness's `py -3.12 -m harness_sync ruleset-check`, which asks
  GitHub and is run by that repository's `rulesets` workflow against every
  target's `main` ruleset, so it is not in the target this file renders into.
- `helper` — a granted helper's refusal of `main`, which is what bounds the
  force push or the branch deletion it relaxes (*Grant inventory and push
  helpers*). The example invokes the helper, `bash .claude/scripts/<helper>.sh
  …`, and the *Test* is that helper's suite and test, spelled
  `test_<helper>.py::test_main_is_refused_however_it_is_spelled` with the
  helper's hyphens as underscores. The suite does not run it: it checks that
  the helper and its suite exist and that the suite defines the test, which
  is run where every other payload suite is.

Where a class is refused by more than one layer, the row names the layer its
example exercises, and the server-side backstop has rows of its own. A guard
row is therefore a spelling the deny list does not match.

| Class | Example | Refused by | Test |
|---|---|---|---|
| Push to `main`, bare | `git push origin main` | `deny` | `test_destructive_classes.py` |
| Push to `main` with upstream | `git push -u origin main` | `deny` | `test_destructive_classes.py` |
| Push to `main`, `HEAD` refspec | `git push origin HEAD:main` | `deny` | `test_destructive_classes.py` |
| Push to `main`, `HEAD` refspec with upstream | `git push -u origin HEAD:main` | `deny` | `test_destructive_classes.py` |
| Push to `main`, full ref name | `git push origin HEAD:refs/heads/main` | `guard-git-argv` | `test_destructive_classes.py` |
| Push to `main`, from another branch | `git push origin feature:main` | `guard-git-argv` | `test_destructive_classes.py` |
| Push to `main`, long upstream option | `git push --set-upstream origin HEAD:main` | `guard-git-argv` | `test_destructive_classes.py` |
| Push to `main`, forced refspec | `git push origin +HEAD:main` | `guard-git-argv` | `test_destructive_classes.py` |
| Force push, long option | `git push --force origin feature` | `deny` | `test_destructive_classes.py` |
| Force push, lease | `git push --force-with-lease origin feature` | `deny` | `test_destructive_classes.py` |
| Force push, short option | `git push -f origin feature` | `deny` | `test_destructive_classes.py` |
| Force push, combined short options | `git push -fu origin feature` | `guard-git-argv` | `test_destructive_classes.py` |
| Force push, lease with a value | `git push --force-with-lease=feature origin feature` | `guard-git-argv` | `test_destructive_classes.py` |
| Force push, `+` refspec | `git push origin +feature` | `guard-git-argv` | `test_destructive_classes.py` |
| Remote branch deletion, option first | `git push --delete origin feature` | `deny` | `test_destructive_classes.py` |
| Remote branch deletion, option last | `git push origin --delete feature` | `guard-git-argv` | `test_destructive_classes.py` |
| Remote branch deletion, `:branch` refspec | `git push origin :feature` | `guard-git-argv` | `test_destructive_classes.py` |
| Soft reset to a remote-tracking ref | `git reset --soft origin/main` | `guard-git-argv` | `test_destructive_classes.py` |
| Direct push to `main` | `git push origin HEAD:refs/heads/main` | `ruleset` | `ruleset-check` |
| Direct commit to `main`, landing through a push | `git push origin main` | `ruleset` | `ruleset-check` |
| Force push to `main` | `git push --force origin main` | `ruleset` | `ruleset-check` |
| Deletion of `main` | `git push origin :main` | `ruleset` | `ruleset-check` |
| Merge commit onto `main` | `git push origin HEAD:main` | `ruleset` | `ruleset-check` |
| Rebase and force-push `main` through the rebase helper | `bash .claude/scripts/git-rebase-onto-main.sh main start` | `helper` | `test_git_rebase_onto_main.py::test_main_is_refused_however_it_is_spelled` |
| Force-push `main` through the push helper, `main` checked out | `bash .claude/scripts/git-push-force.sh` | `helper` | `test_git_push_force.py::test_main_is_refused_however_it_is_spelled` |
| Rewrite and force-push `main` through the trailer helper, `main` checked out | `bash .claude/scripts/git-trailer-pr.sh 7` | `helper` | `test_git_trailer_pr.py::test_main_is_refused_however_it_is_spelled` |
| Delete local `main` through the drop helper | `bash .claude/scripts/git-branch-drop.sh main` | `helper` | `test_git_branch_drop.py::test_main_is_refused_however_it_is_spelled` |
| Discard the working tree and index | `git reset --hard origin/main` | `deny` | `test_destructive_classes.py` |
| Delete untracked files | `git clean -fdx` | `deny` | `test_destructive_classes.py` |
| Delete a branch | `git branch -d feature` | `deny` | `test_destructive_classes.py` |
| Delete a branch, forced | `git branch -D feature` | `deny` | `test_destructive_classes.py` |
| Delete a branch, long option | `git branch --delete feature` | `deny` | `test_destructive_classes.py` |
| Rename a branch | `git branch -m feature other` | `deny` | `test_destructive_classes.py` |
| Rename a branch, forced | `git branch -M feature other` | `deny` | `test_destructive_classes.py` |
| Rename a branch, long option | `git branch --move feature other` | `deny` | `test_destructive_classes.py` |
| Move a branch over another | `git branch --force feature HEAD~1` | `deny` | `test_destructive_classes.py` |
| Remove a worktree, discarding its work | `git worktree remove -f .claude/worktrees/x` | `deny` | `test_destructive_classes.py` |
| Merge a pull request past its checks | `gh pr merge 7 --rebase --admin` | `deny` | `test_destructive_classes.py` |
| Write a file through a read-only command | `git log --output=out.txt` | `deny` | `test_destructive_classes.py` |
| Run a program through `--upload-pack` | `git fetch --upload-pack=prog origin` | `deny` | `test_destructive_classes.py` |
| Run a program through `--receive-pack` | `git push --receive-pack=prog origin feature` | `deny` | `test_destructive_classes.py` |
| Run a program through `--exec` | `git fetch --exec=prog origin` | `deny` | `test_destructive_classes.py` |
| Edit the agent notes | `Edit(.remember/notes.md)` | `deny` | `test_destructive_classes.py` |
| Edit the helper scripts | `Edit(.claude/scripts/helper.sh)` | `deny` | `test_destructive_classes.py` |
| Edit the commands | `Edit(.claude/commands/ship.md)` | `deny` | `test_destructive_classes.py` |
| Edit the subagent profiles | `Edit(.claude/agents/branch-reviewer.md)` | `deny` | `test_destructive_classes.py` |
| Edit the hooks | `Edit(.claude/hooks/guard-git-argv.py)` | `deny` | `test_destructive_classes.py` |
| Edit the skills | `Edit(.claude/skills/codebase-index/SKILL.md)` | `deny` | `test_destructive_classes.py` |
| Edit the settings | `Edit(.claude/settings.json)` | `deny` | `test_destructive_classes.py` |
| Edit the local settings | `Edit(.claude/settings.local.json)` | `deny` | `test_destructive_classes.py` |
| Edit inside the git directory | `Edit(.git/config)` | `deny` | `test_destructive_classes.py` |
| Edit the git directory itself | `Edit(.git)` | `deny` | `test_destructive_classes.py` |
| Edit the MCP server definitions | `Edit(.mcp.json)` | `deny` | `test_destructive_classes.py` |
| Edit the index's ignore list | `Edit(.codeindexignore)` | `deny` | `test_destructive_classes.py` |
| Edit the harness config | `Edit(.harness/config.json)` | `deny` | `test_destructive_classes.py` |
| Edit a hook through a link | `Edit(linked/hooks/guard-git-argv.py)` | `guard-edit-target` | `test_destructive_classes.py` |
| Edit the settings through a link | `Edit(linked/claude/settings.json)` | `guard-edit-target` | `test_destructive_classes.py` |
| Edit the agent notes through a link | `Edit(linked/remember/notes.md)` | `guard-edit-target` | `test_destructive_classes.py` |
| Rewrite a tracked CRLF file with LF endings | `sed -i s/one/uno/ crlf.txt` | `guard-sed-crlf` | `test_destructive_classes.py` |

**Adding a row.** A deny entry added to `settings.base.json` needs a `deny`
row in the same pull request, or the suite names it. A new *Refused by* value
is one more entry in the suite's `REFUSERS` table and one more bullet above.
A row whose example the layer admits fails the suite, which is the point: a
refusal that quietly stopped covering its class is the harness's most
repeated failure.

## Git argv guard hook

**What closed it is the second of the two things named here as owed: a rule
over the executed argv rather than the typed string.**
`.claude/hooks/guard-git-argv.py`
is a `PreToolUse` hook on `Bash` that resolves the command the way the shell
would — quote removal, heredoc bodies, comments, line separation and, since
#183, redirections — and judges the argv that is left, so the fragments are
rejoined before anything is compared and the dodge stops working.
**It reaches further than any rule could**, and that is the part worth carrying:
hooks run on every tool call in the loop, including the read-only `git` forms
the harness waves through as promptless built-ins, where no allow or deny rule
is consulted at all. Measured, not read — the docs do not say so, and a
`git log --out''put=` probe was refused by the hook with no file written.

The deny stays beside it as defence in depth. **A substring deny over a shell
command string can never be more than a speed bump**, which is still the
generalisation worth carrying past this one flag — what changed is that the
speed bump is no longer the only thing there.

**The hook's own residual, stated rather than left to be found:** `shlex`
resolves quoting and not expansion, so a flag assembled at run time —
`F=--output=x; git log $F` — arrives as the token `$F` and is not seen. Closing
that needs the argv after expansion, which no hook is given.

**This paragraph once claimed more than that, and a reviewer was right to say
so.** "Every spelling a caller can type literally" was false while
`git log "$(git push origin +HEAD:main)"` was one `shlex` token and two commands
to the shell — a command substitution is *executed*, not quoted away, and
calling it inert because it survived tokenisation intact is the same mistake as
reading a heredoc body as an argument list.

**That sentence then said both were closed, and they were closed in two
functions that disagreed with each other.** The stripper knew a heredoc body
was data; the extractor ran on the raw string ahead of it, with a quote tracker
of its own and no notion of heredocs or comments. So a substitution inside a
`<<'EOF'` body was refused although the shell never expands one there, and a
substitution inside a bare `<<EOF` body was **admitted** although it does — an
apostrophe in the body being an opening quote to the extractor and an ordinary
character to bash. Raised in review; both directions verified against the guard
as shipped, and the bash behaviour measured rather than argued.

**A second one was found while fixing it, one layer down and the same shape.**
`shlex.shlex` sets `commenters = "#"` and honours it at any character position,
where bash starts a comment only where `#` begins a word — so
`git log --grep=#x ; git push origin +HEAD:main` tokenised to three tokens, the
push left with the comment, and the hook reported no offence at all. Measured
under bash with a `git` shim: both invocations run.

Both are closed by giving the guard **one** model of where the shell expands
things — `expandable_regions`, over the quote-and-comment-aware scanner the
heredoc opener already used — rather than a second model per function. Command
substitution is now judged wherever bash would perform it and nowhere else.

**And then the bound was wrong for a fourth reason, which had nothing to do
with the shell at all.** Every version of this paragraph has named the residual
in shell terms — quoting, then expansion — while `git -c` sat one layer up:
`git -c "alias.x=!echo PWNED" x` runs the echo, and git executes a long list of
config keys besides — `core.pager`, `core.editor`, `core.sshCommand`,
`core.hooksPath`, `diff.external`, `credential.helper`. Measured in a scratch
repository. The hook admitted every one of them, because they are ordinary
arguments to an ordinary `git`, and nothing about the SHELL is involved. The
option is refused outright now, in the global position only, since `-c` after
`commit` means "reuse this message" and breaking that is how a guard gets
turned off. Enumerating the executing keys would be the deny-list this file
already refuses twice over.

**So state the bound as what has been looked for, not as what is left.** The
residual named today is what the shell **computes** rather than what a caller
writes, and it has two measured shapes: a flag or command assembled from a
variable (`F=--output=x; git log $F`), and a substitution whose OUTPUT becomes
the command line (`sh -c "$(echo 'git push origin +HEAD:main')"`). Closing
either needs the argv after expansion, which no hook is given. Both are pinned
as **admitted** in the suite, because a residual nobody can run is one the next
reader assumes was closed.

Three earlier versions of that sentence were each falsified by a spelling
nobody had gone looking for, and the pattern is that the bound gets narrower
every time somebody probes rather than reasons. **It is a claim about the last
search, not a proof.**

**Three ways round it had nothing to do with git's grammar.** The hook read its
event as text, and Python reads a Windows pipe in the ANSI code page, so a
command carrying a character whose UTF-8 bytes include one cp1252 leaves
undefined raised before anything was judged, and landed in the branch that
admits a malformed event. Both argv guards now decode the bytes as UTF-8, as
the other hooks do, and refuse an event that is not UTF-8; an event that is
UTF-8 but not JSON is still admitted, the one argued fail-open. The hook was
registered on `Bash` alone, so git through the PowerShell tool was judged by
nothing here: a second registration sends PowerShell calls to the git guard,
which parses Bash's grammar and not PowerShell's, so it admits a PowerShell
command only as one of a few read-only cmdlets over plain words that do not
name `git`. A quote, an escape, a variable, a call, a separator, a wildcard, a
backslash or any other first word refuses it, because each is a way another
interpreter, an encoded command or a computed name could run git unseen, and a
list of launchers would trail PowerShell's. The index guard stays on
`Bash`, because the skill's prefix grants are `Bash(...)` rules and a
PowerShell call is prompted like any other.
And a heredoc with no closing line, whose body then fails to tokenise, is
refused outright, as an undecodable delimiter is, and the untokenised
fallback now refuses whatever reaches it, because a line is easy to make
untokenisable on purpose, bash runs the lines before the one that breaks, and
no crude check reads git's options or sees a name that case, a glob or an
expansion builds. The cost is a rephrased command where bash would have run
one `shlex` cannot read.

**An `Edit(...)` deny binds the editing tools, and `Bash(ls:*)` is granted, so
the guard judges the same write past them.** A redirection and a writing verb
(`cp`, `mv`, `rm`, `tee`, `sed -i` and the rest of `WRITING_VERBS`) are held to
this target's own denies, read from `.claude/settings.json`, so `.github` is
protected only where a target denies it. A path is anchored at the root of the
checkout that holds it and folded for case, which leaves `payload/.claude/…`,
`staging/.claude/…`, `.claude/worktrees/` and `.claude/cache` writable, and a
link is judged where it lands. A verb that takes a tree whole, `rm`, `mv`,
`rmdir`, `shred` or a copy into a directory, is refused a directory below the
checkout root that holds what a deny names as well, so `rm -rf .claude` is
refused and `.claude/worktrees/x` and the root itself, `rm -rf .`, are not. What
a copy or a move puts into a directory is judged by the name it lands under, so
`cp notes.md .claude/` is admitted and `cp -r /tmp/hooks .claude/` is not; a
glob or brace source has no known name, and `-T` lands on the directory itself,
so each is judged as the whole directory.
`~` and the variables this process shares with the session are expanded and
judged, and a literal `cd` is followed, through a subshell's end as well.

**What it cannot read it admits, and that is the narrowing, not a gap.** An
unknown variable, a computed operand, or a relative target after a `cd` nothing
can read is admitted: `> "$var…"` was typed 523 times, `cd x && … > y` 736 and
`cp "$a" "$b"` 107, so refusing them is the false refusal *The guards' threat
model* files. An expansion's readings, which exist to find a hidden program,
place no file. The list of verbs trails the programs that write: `curl -o`,
`rsync`, `tar -x`, `patch` and an interpreter's own writes are not on it, and
that is a residual. A worktree removed from disk is still judged as one by its
place under `.claude/worktrees/`, and a checkout under the temp root that the
session is not standing in is not this target's: mutation proofs write there,
which the replay of backend's transcripts found.

**Five lists reach what the flag check above did not.** The short spellings of
the flags that run a command, keyed by subcommand because a letter means
something else on another; `bisect run` and `submodule foreach`, which run
their operand; `--strategy` and `--template`, per subcommand; `--ext-diff`,
`--textconv`, `--extcmd` and `--open-files-in-pager`, with `--text` read as the
option it is; and `filter-branch`, `send-email`, `instaweb`, `daemon`,
`http-backend` and `web--browse` refused whole. Those are lists and not an
allow-list of subcommands, which would refuse `git ls-remote`, `git config` and
`git stash`, typed hundreds of times. `xargs` is refused only in front of a
push, whose destination it supplies unseen; a fetch is refused its `-f`, `-u`
and a `+` refspec onto a branch, which discard local commits under a read's
grant, and `git pull --ff-only` any option that overrides it.

**Three refusals are traps, not attacks**: the command does what it says,
and what it says loses work. They are the honest mistakes *The guards'
threat model* above is scoped to, and its criterion 3 holds for them as for
any check, so a false refusal by one is filed. A
`git push` whose output is piped and whose pipeline is then chained by `&&`
or `||` with no `set -o pipefail` first runs what follows on a failed push;
`git update-index --cacheinfo` with a literal mode drops or adds an
executable bit unseen on Windows, refused unless the command carries
`--add`, which stages a path the index lacks and is admitted because the
guard cannot see whether the index holds the path; and
`git reset --soft` to a remote-tracking ref recommits over whatever another
session's fetch moved in. Each is refused
because the guard can see it from the command's text, and each message names
the form that works, so the remedy arrives at the refusal rather than in a
memory (`docs/harness/pitfalls.md` holds the traps no guard can see). The
refusal of a heredoc handed to a shell names the same kind of remedy: a
script's input goes through `< <path>`, written with `Write`.

## The ext transport and pinned grants

**The `::` in a value collides with the `:*` suffix syntax, and the collision
fails silent in one direction and loud in the other.** `Bash(git *ext::*)`
passes settings validation and then matches nothing, because the trailing `:*`
is consumed as the prefix-wildcard form and the literal becomes `git *ext:` —
a probe of `git log -1 ext::foo` ran clean under it. Writing `Bash(git
*ext::**)` to dodge that is rejected at startup: *"The `:*` pattern must be at
the end."* So **`ext::` cannot be expressed in a Bash rule at all**, and the
transport is closed on the allow side instead — **and the two grants do it by
two different mechanisms, which an earlier revision of this paragraph ran
together.**

**Since #30 it is also closed on a third mechanism, and that one is not a
rule.**
`.claude/hooks/guard-git-argv.py` refuses an argv element containing `ext::` on
a git subcommand that takes a **repository** — `fetch`, `clone`, `pull`,
`push`, `remote` and their neighbours. **Not every element after `git`**, which
is what this sentence said until the scoping landed and did not reconcile it:
the transport is only meaningful where git expects a repository, and judging it
everywhere made a branch name or a path carrying the sequence indistinguishable
from a use of it — a commit body arguing about the transport included. A hook
is not bound by the pattern grammar at all,
which is why it can express what `Bash(...)` provably cannot — and the whole
argument below about which grant pins what remains worth reading, because a
hook is one file and defence in depth is the reason the allow side was narrowed
in the first place.

`Bash(git fetch origin:*)` **pins the remote**: a literal remote name occupies
the repository position, so a URL never reaches it. That is the control the
sentence describes, and it is real.

`Bash(git pull --ff-only)` pins **nothing** — no remote name appears in it at
all. What it does is drop the `:*`, so the grant is the documented
no-argument invocation and nothing else. Whether that closes
`git pull --ff-only ext::<cmd>` turns on the prefix-match rule stated in the
reset-grant paragraph above — **an allow rule is a prefix match** — which has
never been measured for the no-wildcard case. (Named rather than counted: a
numbered offset inside a section still being edited is how this repository's
callout totals used to rot.) If that holds for a grant without `:*` as well, the
pull side is still open and belongs in the residual inventory rather than in
this paragraph. Nobody has run the probe. Until someone does, treat the pull
grant as *narrowed, not proven* — the two words this repository keeps having to
tell apart.

**The probe that would settle it is not available from inside a session, and
that is a property of the question rather than a lack of trying.** Every exact
grant in `.claude/settings.json` — `Bash(git branch)`, `Bash(git remote -v)`
and the rest — is a **read-only** git form, and the harness treats those as
promptless built-ins whatever the allow list says. So a trailing argument on
one of them runs cleanly and demonstrates nothing about prefix matching: it
would have run either way. `git remote -v --verbose` was tried and is exactly
that null result. A decisive probe needs an exact grant on a command the
harness does not already wave through, which means adding a rule to a file the
session reads at startup — so it cannot be arranged from within the session it
would govern.

## Grant inventory and push helpers

**A command's frontmatter is a grant like any other, and it is the one nobody
reads twice.** The first five rows above were all found in command frontmatter,
and for a while that supported a second claim — that the global file had it
right all along. **It did not, and the sixth row is where that broke**: the
`--output` write primitive sat in `.claude/settings.json`'s own allow list,
reachable from every command in the repository, and it had been read past for
as long as the frontmatter rows had. The lesson survives with its converse
attached: frontmatter is the grant nobody reads twice, and the global file is
the one everybody assumes somebody else already read.

**This paragraph is the inventory of grants wider than the operations they buy,
and no command file keeps a second total** — `ship.md`'s callout carried one,
and it went stale the moment a branch pinned that file's fetch grant.

**The entries are numbered and the numbering is stable; the headline count that
used to open this paragraph is gone on purpose.** It read "six" and was made
false by #56 closing the fourth, which is the third time a restated total in
this repository's guidance has rotted — backend's `CLAUDE.md` callout totals
and its testing document's test count went the same way,
and the fix that held there was to drop the number and keep the named entries.
Ordinals are kept even where an entry is closed, so a cross-reference to "the
fourth" still lands, and each entry says its own state. Read the entries; do
not count them here.

Two are `/ship`'s, and both are closed:
`Bash(git worktree remove:*)` admitted the `-f` that discards work, and
`Bash(gh pr merge --rebase:*)` admitted a trailing `--admin` that merges past
failing checks. Both are withdrawn, and the base denies both raw forms,
`Bash(git worktree remove:*)` and `Bash(gh pr merge:*)`, so no target's
config or session grant brings the flag back: a deny judges the typed
command and never one a script runs, so the helpers are untouched by it.
`gh-pr-merge.sh` takes a number and a
head oid, spells `--rebase --match-head-commit` itself, and refuses a pull
request that is not the checked-out branch's own, from this repository, into
`main`. `git-worktree-remove.sh` takes a `.claude/worktrees/<name>` path and
the branch forked there, run from the main checkout. It refuses `main` as that
branch, a directory that is not a linked worktree of this repository, and one
that does not hold the branch, a detached one included. It spells
`git worktree remove` with no flag, under `core.longpaths` so that a file past
Windows' 260 characters is deleted too rather than left in a directory git has
already unregistered; it first waits, to a bound it declares, for the
worktree's code-index refresh lock, because Windows will not delete a file a
running refresh holds (#516).

Another, shared by `/pr` and `/ship`, is closed the same way:
`Bash(gh pr create:*)` admitted a trailing `--repo`, `--head`, `--base` or
`--body-file`, which choose a repository, a branch, a base and a body the
command never derived, and the last publishes any file it can read. It is
withdrawn for `gh-pr-create.sh`, taken from blueprint-frontend. It takes two
paths, the title's and the body's, reads the repository and the head from the
checkout and spells the base `main`, and refuses an input that is not a file
git ignores under the checkout's `artifacts/`, is a symbolic link itself, or
resolves outside `artifacts/`: an ignored file is untracked, so the branch
under review never writes its own pull request, and every target already
ignores `artifacts/`.

**Two more sat in the sweep command files rather than in this paragraph, and
both are now closed — which is worth recording because they were closed the
same way.** `Bash(mktemp:*)` took an arbitrary template, so it was a filesystem
write primitive able to create an empty directory or file anywhere the session
could write; `git-worktree-detach.sh` makes the directory itself now and prints
it, and both sweeps dropped the grant. `Bash(gh label create:*)` was
create-*or-overwrite* — `--force` updates an existing label's colour and
description, and `-R` put that write in any repository — and it was held as two
prose rules; `gh-label-ensure.sh` leaves no free parameter, taking one name out
of a fixed case and resolving the repository from the checkout. **The
root cause both shared is the one that generalises**: each was documented by the
operation it was added for rather than by what its prefix admits, and reading
the tool's own `--help` found something every time it was done. That they were
inventoried in a command file and not here is the same drift this paragraph
warns about, running the other way.

**A fifth was opened deliberately rather than found**: the force push, which
three lease-bound helpers carry and nothing else does. Branch updates are
rebases, so a branch's published commits are rewritten, and
`.claude/settings.json` denies `git push --force` and `--force-with-lease`
alike. **That deny is untouched**, for every raw spelling: the helpers relax
the client's policy and the deny list does not move. What lets the client
relax at all is the `main` ruleset on the server, read back green for every
target, so a helper that got its guards wrong still cannot rewrite or delete
`main` (*Destructive classes*). `git-rebase-onto-main.sh` carries the flags
itself, runs none of the target's hooks (`core.hooksPath` is an empty
directory for the run, as in `git-trailer-pr.sh`), spells over every
`rebase.*` key that changes a replay (`--no-rebase-merges` and
`--no-update-refs` answer settings that act today; `--no-autosquash`,
`--no-fork-point` and `--no-autostash` are defensive, since git limits
`rebase.autoSquash` to interactive mode, takes no fork point when an upstream
is named and the start refuses a dirty tree; `--merge` keeps the apply backend
out, which leaves a commit it never applied looking like one a resolution
emptied), takes one branch name that has to equal the one checked out, refuses
`main` by name before it reads the checkout at all, refuses a remote holding
commits this checkout lacks, leases the push against the commit the same run
read, and refuses (exit 7) an `origin` with more than one push URL, or whose
one URL, after `pushurl`, `pushInsteadOf` and `insteadOf`, names another host
or path than the URL the remote was added with. A protocol rewrite on one host
passes, and a URL with no host and path to compare has no key and is refused:
`file://`, `ext::`, a one-component hosted path, a dot or empty segment, a
query or a fragment. **A rule could have bought none of that**: a permission
pattern matches the text of a command, and every guard in that list is a fact
about the checkout. Which is the general form — where the safety is a property
of the state rather than of the words, the grant has to be a helper.

**A second helper rewrites published commits, and is a second force push.**
`git-trailer-pr.sh` adds `PR: #<n>` to the commits of the branch checked out,
which changes their SHAs, so it leases its push as the rebase helper does.
It takes one number and spells the `git rebase -x` command itself, so no
`-x` grant stands beside it. It refuses `main` checked out in any spelling,
folded as the rebase helper folds it, a pull request that is not an open,
same-repository one into `main` for this branch, a dirty tree, a remote that
differs from the checkout in either direction, a merge commit, and a commit
that already names another pull request. A rewrite failing on `index.lock`, as
one does when a concurrent git reader holds the index, is aborted and tried
once more; any other failure stops. Its residual is a push that fails
after the rewrite: the local branch then carries the trailers and origin's
does not, and a rerun finishes it, because a commit that has the trailer is
skipped. `git-revert-pr.sh` writes no remote; it reads trailers off
`origin/main` and reverts on the current branch, so its grant is a plain one.

**A third helper is the force push for every other rewrite.** A branch amended
or reworded for a reason neither helper above covers had no route but the
deny. `git-push-force.sh` publishes the branch checked out and takes no
argument, so no name a caller passes can point it at another. It refuses
`main` in any spelling, folded as the rebase helper folds it; a detached
`HEAD`; and a branch origin lacks, asked of origin itself rather than of a
tracking ref a deleted branch leaves behind. It fetches the branch fresh and
leases the push against the tip it fetched, so a push landing in between is
git's to refuse. And it refuses a remote tip the branch's own reflog does not
hold, because a lease is not consent: once another session's commits have been
fetched they satisfy it, and they would be discarded. That is the rebase
helper's `require_remote_carries_nothing_new` for a rewrite with no fork point
to measure from. Its residual is the reflog itself. An entry `git gc` has
expired, or a branch made with no reflog, fails closed; and a branch that
holds origin's tip only because it was rebased onto it is refused as well,
where `git push origin <branch>` needs no force.

Its exit codes are the rebase helper's, those it can reach: 0 published, or
nothing to force because origin already holds `HEAD`; 2 usage, an argument
given; 3 the branch is `main`, however it is spelled; 4 a detached `HEAD`; 6
origin has no such branch, so push it normally; 7 origin carries commits this
checkout never held; 12 origin could not be asked where the branch is, or
fetched from. A push origin refuses, a lease that no longer holds among them,
exits with git's own status, as the rebase helper's does.

**One more helper relaxes a deny, and it deletes rather than pushes.** `git
branch -d`, `-D` and `--delete` stay denied. `git-branch-drop.sh <branch>`
refuses `main` in any spelling, a name that could be read as a flag, a
symbolic ref, which `rev-parse` would follow to its target's tip, and a branch
checked out in any worktree, this one included, being rebased or bisected in
one, or to be moved there by a `rebase --update-refs`. The last three `git
worktree list` reports only as a detached HEAD, so the name is read from each
git dir's rebase, bisect and `update-refs` state, where `git branch -D` looks
too from git 2.38. A superseded sync's aside, it deletes only when
`pr-for-branch.sh` reports a pull request for the branch `MERGED` into `main`
whose head is the branch's tip, which is `/ship` step 0's finished predicate:
a commit added after the merge is work no merge holds, and keeps the branch,
and the helper reads only this repository's rows, so a fork's same-named pull
request lands nothing. A branch named as a sync head, and no other, also goes
when every row reads `CLOSED`, one into `main` at its tip, and origin no longer
has it: `harness_sync` closes a superseded sync that way, so no merge can land
it and nobody can reopen it. Exit 12 is an origin it cannot ask. The delete is
`git update-ref --no-deref -d` against the tip that was checked, a
compare-and-swap, rather than `git branch -D`, which deletes whatever the
branch holds when it runs: the read of the pull requests takes seconds, and a
commit landing in that window would go with the branch. The worktree check runs
last, because `update-ref` does not make it, and the branch's configuration
section, which `update-ref` leaves, is removed after the delete. The check and
the delete are two steps, so the check runs again after it: a worktree that
took the branch between them, or a second read that fails, puts the branch back
at the tip and exits 4 or 12 as the first read would have. Its residual is a
commit made in that worktree in the instant the branch is gone, which recreates
the ref, so the putting back fails: the helper then exits 4 and names the tip,
and the `git branch` line that restores it if it is still absent.

Its exit codes, in the rebase helper's meanings where one fits: 0 dropped; 2
usage, or a name it will not take, a symbolic ref among them; 3 the branch is
`main`, however it is spelled; 4 the branch is checked out in a worktree, this
one included, or is being rebased or bisected in one; 6 no local branch of
that name, so nothing to drop; 7 it has not landed, with no `MERGED` row into
`main` at its tip, or the tip moved after it was checked; 12 its pull requests
or its worktrees could not be read, so whether it landed or is in use is
unknown.

**A reader of pull requests is granted by its whole command line.** `Bash(py
-3.12 .claude/scripts/sync_pr.py:*)` admits `list`, `verify <n>`, `review-diff
<n> <out>` and `local-suites`, and nothing in it writes to GitHub: its `gh`
calls are `pr list` with a fixed field set and `gh api` reads of the harness
repository's `git/trees` and `compare`, which it spells itself, so no caller
chooses a field, a repository or a method. A pull request's body is text anyone
can post, so `list` reads only whether its last line is the sync footer and
never passes the body on. `verify` is what a branch name is not: it fetches the
listed head and fails closed on anything it could not read, a truncated tree, a
`gh` failure or timeout, a manifest it cannot parse, or a head whose oid is not
the listed one. `--head <oid>`, 40 lowercase hex or exit 2 with nothing read, on
`verify` or `review-diff` also requires the listed and the fetched head to be
that oid, so a session binds its proof to the head it checked out and a push
after it is refused as moved. It proves six things: the pull request is a
`list` row; the manifest's `harness_commit` is on the harness's `main` and not
older than the commit `main`'s own manifest names, so a rollback fails; every
manifest path but `.claude/settings.json` has the payload's blob id there, mode
aside; the head's `.claude/settings.json` is what the drift gate would render,
CRLF folded, since `run` starts an executor on that file before CI has judged
it; the manifest lists each payload file the config neither excludes nor keeps
local, and lists no path it does; and the head changes only manifest paths,
old-manifest paths and the manifest, with `.harness/config.json` and every
`local` path untouched. The settings proof executes code from the head: its
`.harness/render.py`, read by oid and never from a tree, runs only once that
commit is on the harness's `main` and the renderer and
`.harness/settings.base.json` are manifest paths whose blobs are the payload's
there, and it renders from those and the base commit's config. A difference, a
missing file, a renderer that raises or one not trusted is a failure line.
`review-diff` proves all of it first, on the head it then reads, and refuses
with those lines. Its one write is a `.diff` under `artifacts/review/`. Not
proved: the manifest's hashes. Residual: blob ids are compared exactly where
apply and the gate fold CRLF, so a target whose stored blob holds CRLF is
refused every sync; that fails closed.

**A worktree on a sync branch is granted by its whole command line.**
`Bash(bash .claude/scripts/git-worktree-sync.sh:*)` admits `<path> <branch>`
and nothing else. A prefix grant on `git worktree add` also buys `-B`, which
resets an existing branch past the `git branch --force` deny, so the helper
spells `git fetch origin <branch>` and `git worktree add --track -b <branch>
<path> origin/<branch>` itself. The branch must match `SYNC_HEAD` in full,
`main` included, and a harness test holds the helper's pattern to `pr.py`'s;
the path and root checks are `git-worktree-fork.sh`'s; a branch that exists
locally, or that origin lacks, is refused. A name that matches is not a proved
sync, which is `sync_pr.py verify`'s to say, and `/ship` and `/dispatch` run it
before the helper. Residual: called directly, the helper opens any branch named
like a sync, which it checks out and runs nothing in. The helper also seeds
neither the MCP approval nor the code index that `git-worktree-fork.sh` does,
and that is left so: a sync run reads a diff and runs suites, which need no
index, and a session an owner opens there sees `codebase-index` pending until
they approve it. Seeding would add a read of the main checkout's `.mcp.json`
and settings to a grant that is meant to do two git commands and nothing else.

**`Bash(py -3.12 -m unittest discover:*)` runs the code of whatever tree it is
pointed at, and is `/ship`'s alone.** A sync's step 2 runs the target-local
suites with it, as it runs the payload suites `sync_pr.py` names as walking its
commands, and a prefix grant cannot pin `-s`, so what bounds it is where it
runs: only in a worktree whose `HEAD` equals the proved oid and whose `git
status --short` is empty, which the sync section requires before anything in the
tree runs, and which `dispatch_board.py sync-launch` and `run` compare before a
`--dispatched` run starts in it. The settings do not allow it, so no other
command reaches it. Residual: a `/ship` run that skips the comparison runs the
head's own tests, which is the code the comparison exists to vouch for.

**`gh-issue-harness.sh` files in the harness from a target, and only /ship holds
it.** `<bug|test-gap|enhancement> <critical|high|medium|low>
<harness|harness,test|documentation|code|code,test>`, with the stdin shape of
`gh-issue-create.sh` and the hand route's last line (an enhancement's is the
request route's), for the sync row, which finds payload defects from a target
whose checkout is another repository. The repository is a literal in the helper
and no word selects it: a word `gh-issue-create.sh` took would be the caller's
to declare, which is why no command is granted that helper. So
the grant is the boundary: `/ship`'s frontmatter names the helper, the settings
do not allow it, and both sweeps deny it by the same key as the force-push
helpers, for the day someone allows it globally. It first searches the open
issues for that exact title and comments on a match instead of creating, every
`gh` call under a timeout, and a failed search files nothing. It skips the label
sibling, which resolves the checkout's repository, so a label the harness lacks
fails the create rather than being made. Residual: `/ship` can file any text in
the harness with it, which is its whole use.

**The issue route is the entry point a command is granted, never an argument.**
`gh-issue-create.sh` takes the route as its fourth word and so lets whoever
holds a prefix grant on it claim any route's last line, among them the sweep
route's sentence that a second read-only auditor verified the finding. So no
command is granted it. `gh-issue-sweep.sh <kind> <severity> <area>` fixes the
`sweep` route and is the sweeps' only filing grant; `gh-issue-hand.sh` fixes
`hand` and is `/ship`'s, `/team`'s and `/dispatch`'s;
`gh-issue-mutation.sh <severity> <area>` fixes `mutation` and the kind
`test-gap`, the one its sentence is true of, and is for a target's local
mutation sweep, so no payload command holds it. Each execs
`gh-issue-create.sh` with the route spelled, so the kind, severity, area, stdin
and trailer checks stay in one place. A grant on one entry point withholds
nothing from another, so each command denies the entries it does not hold by
name, and `gh-issue-create.sh` besides; `/ship` denies agent types and no tool a
command it chains uses, because a deny on one of those outlasts the step.
`test_command_grants.py` walks every command in the tree it runs in, a target's
local ones too, so whether a route needs an entry point is a question about
every target, not the payload.
The `request` route has none, since no command in any target files under it; it
stays reachable by calling `gh-issue-create.sh` directly, and a command that
comes to need it gets its own entry point of the same shape. Residual: a prefix
grant on a helper lets its holder choose that helper's own arguments, which here
are the severity, the area and, on every entry but the mutation one, the kind,
all in closed sets.

**The replay's provenance and its divergence baseline are files under `.git`,
and that is a stated limit.** The helper keeps a record in the git directory
(`claude-rebase-pending`, naming the branch, the lease and the replayed
commit), writes a marker into the rebase state (`started-by-this-helper`), and
reads git's own `orig-head` for where a replay began. `continue` takes the
marker as proof that the helper started the rebase and `publish` forces over
the lease the record names, so anything able to write there could steer a
publish: a forged marker passes `continue`, a rewritten record moves the lease.
What stands in the way is partial: the base settings deny `Edit` on `.git` in
both spellings for every session, and `guard-helper-argv.py` refuses a command
substitution in a command that runs a helper. Neither reaches a `Bash` command
that writes there by another route. It is accepted, not guarded, because the
same files exist in every target that runs the helper; this repository measured
nothing about it, and the limit is written down so a reader does not take the
helper's own checks for protection of the files they read.

**Some of the rebase helper's edges are accepted rather than guarded.** A replay
amended after a failed push no longer descends from the commit `publish`
recorded, so `publish` refuses it and names the tip to reset the branch to by
hand: resetting it for the caller would discard the amend, which is the work.
And `abort` mid-replay discards uncommitted edits as `git rebase --abort` does,
because discarding the stop's work is what an abort is for; the helper lists
them before it aborts. And a two-parent merge on the branch is held to git's own
merge of its parents in the files that merged cleanly, and in a conflicted file
it took whole from one side, which must still carry the other side's clean
hunks. The rest is left to `--cc`, which cannot see a clean hunk dropped inside
a file: every file of an octopus, a conflicted file resolved by hand, a binary
one, and one whose parents have no single merge base or whose base lacks it. A
conflicted name git quotes even with `core.quotePath` off stops the run, because
a name the check cannot read is not one it may pass.

The third is `Bash(git fetch origin:*)`, which no longer admits a URL but still
admits a trailing flag; `--upload-pack`, `--receive-pack` and `--exec` are
denied by name, so what is left is the flag nobody has enumerated yet. The
honest fix is the helper the transport issue asked for — a
`git-fetch-origin.sh` taking a branch name and nothing else.

## Pull request reads and the locality verdict

The fourth **was** a pull request's comment feeds read unfiltered, and #56
closed it. A review body, an inline comment and an issue comment are text
anyone can post, and a command holding `Edit` that reads one holds an
injection vector. **No command grants `Bash(gh pr view:*)` or
`Bash(gh pr list:*)` any more**: `ship.md` reads state through
`pr-state.sh`, `pr.md` feeds the closure gate through
`pr-closure-input.sh`, every command that needs a branch's PR resolves it
through `pr-for-branch.sh`, and `ship.md` judges a PR's changed paths
through `pr-locality.sh`. Every one fixes its field set,
because a caller that chooses fields can choose `reviews`. **`gh pr list`
was the door found last**: `--json reviews,comments` returns every pull
request's review bodies at once, and three commands kept it for the
harmless job of finding a branch's PR.

**`pr-for-branch.sh` answers three questions before its caller reads a row.**
Its listing asks `gh` for `--limit 1000`, because the default page of 30 is cut
before the fork filter and the landed row can vanish; a response of exactly the
limit may be a prefix and is refused with exit 4. Only rows into `main` are
listed, since every pull request this chain opens targets `main`, and a newer
one into another base would otherwise mask it. A `MERGED` row is dropped when
the row's merge commit is an ancestor of the local branch: that branch was
recreated from a `main` holding the merge, so the row is an earlier
incarnation of a reused name. A branch with commits added after its merge does
not carry the merge commit, and keeps the row. With no local branch, or a merge
commit not fetched, the row stays, since stopping is the answer when the drop
cannot be shown. The merge commit is requested and not published. Residual: a
landing that leaves the branch on the merge commit itself, such as a
fast-forward, loses its row, which keeps a workspace standing rather than
removing one.

**The gate that pins this is an ALLOW-list of `gh` subcommands, and it is the
second one in this repository to be rewritten that way.** Its first version
banned `gh pr view` by name and passed while three files still granted
`gh pr list` — a deny-list passing every spelling nobody thought of.

**This paragraph used to name `gh issue view` and `gh issue list` as still
admitted, and #150 removed both.** The measurement it rested on is unchanged
and was never the thing in question — `gh issue view <pr-number> --json
comments` does return an empty array, because `gh` keeps issues and pull
requests distinct even though GitHub's model does not. Membership of the
allow-list is a different fact, and it moved: `gh issue view` returns `author`
to the same session, and `gh issue list` returns `author` **and every issue
body** at once. Both went, and `gh repo view` with them.

**Two paragraphs in this file disagreed about one gate, and the stale one was
the dangerous half** — a reader reconciling a new grant against "stay admitted"
would have put back exactly what #150 closed. Raised in review, against the
branch that closed it: the record of the removal was written a thousand lines
earlier, and the rule nobody re-read was not.

**The set itself is deliberately not written here**, which is the fix rather
than an omission — it is `GH_GRANTS_THAT_CANNOT_REACH_A_FEED` in
`test_pr_helpers.py`, beside the assertion that reads it, and a copy in this
file is what went stale. This paragraph carries the argument for why a
subcommand joins or leaves that set; the set carries the membership.

**`pr.md` was the third holder and was found by a test, not by reading.** The
issue named two commands; the case whose subject is *every* command's
frontmatter caught the third. That is the gate-coverage lesson paying for
itself inside the change that added the gate.

**#150's suppression clause moved three times in one branch, each time one
grant along from the one just closed, and it is the clearest instance of #56
this repository has.** `gh repo view` went when the suppression helper resolved
the owner itself. `gh issue view` went when a reviewer observed it returns
`author` to the same session. `gh issue list` survived both rounds and returns
`author` **and every issue body** at once. Each fix cited the same rule — a
helper that fixes its field set does not bind a caller who still holds the raw
grant — and each left the next grant standing. **The gate now asserts on the
grant rather than on the instruction line**, because a listing line naming four
fields is a rule a reader follows and a prefix grant beside it is what the
session can actually run.

**`pr-locality.sh` prints a verdict and never the cell it judges.** It
reads `body` from the pull request, with any carriage return removed, and
`filename` and a rename's `previous_filename` from the files endpoint, so both
ends of a move are judged as the CI gate judges them. It prints one `class`
line whose value is letters it validated, then one line per changed path,
where the path is the diff's own and the word is the script's:
`outside-class` when the class's tree set does not admit it, whatever the
touch set says, since no row may declare what its class does not admit;
otherwise `outside` when the touch set does not, and `inside` when both
do. The tree set is read from this checkout's
`.github/locality-gate/classes.yml`, where the CI gate reads the map out of
the base branch's tip, so the round's verdict is the gate's only while this
branch's map and this helper match that tip's: a pull request that edits
either, or a branch behind a base whose map has since moved, is judged here
by its own copies and in CI by the base's, which is how the gate stops a
pull request widening its own class. #1545's review rounds read `inside` for a
runbook that a Class C PR's widened row admitted and its class did not, and
CI was the first to refuse it. A pull request author is not a
trusted party, and a path grammar cannot keep prose out of a cell —
`Ignore_all_previous_instructions.md` is a path — so the cell is consumed
there and only the verdict leaves. A changed path is the author's text too,
since git permits a newline in a name, so each arrives JSON-encoded and is
printed only if it decodes to a plain path: no escape, path characters
only, a `/` or a `.` in it, no `..` segment. Any other name refuses the
whole run, because a verdict list with one line withheld reads as complete.
For the same reason it reads the pull request's `changedFiles` and exits 3
when the endpoint's count of `filename`s differs, a rename being one file: the
endpoint stops short of a very large pull request, and a short list would print
verdicts for part of the diff.

**Its grammar refuses rather than guesses.** A body carrying neither row
prints nothing, and the caller skips its touch-set check and says so; one
row without the other is refused, because each caller reads the pair. A
class cell is one letter A–E, two distinct letters joined by `+`, or
`A+D+E`; a touch-set cell is a comma-separated list of path tokens, bare or
in balanced backticks, a backticked one optionally followed by a space and its
reason in parentheses (no backtick, no nested parenthesis; dropped unread,
never printed, and its commas and braces its own), of path and glob
characters, each carrying a `/` or a
`.` and each repository-relative — no leading `/`, no `./`, no `..`, brace
alternatives included. A token may hold `@` and `+` as a changed path may,
the `+` read as a character and not as a quantifier, in the class map's items
as in a row; an empty brace alternative is refused, because GNU and BSD `grep`
read an empty alternative differently and one set would judge a path `inside`
on one machine and `outside` on another. The verdicts come from one `grep`
per pattern over every changed path, and a path is checked by `case`, so no
process is spawned per path: on Windows a spawn per path and pattern made
the helper look hung. A row that fails its grammar exits 3 naming the row
and not its content, and so does a class map that is absent, names a class
twice, leaves one without items, or holds a line or a token outside its
grammar: a verdict half-read from a map the helper refuses would be one no
gate gives. **The verdict narrows
and grants nothing**: the caller's deny list and the class's tree set hold
authority, an `outside` or `outside-class` line is a finding, and an
`inside` line is not a licence for anything the caller's grant refuses.
`test_pr_helpers.py` pins all of it.

**Residual: the helper's token grammar is the wider one a frontend-style
tree needs.** It admits `@` and `+`, as a changed path may. A target's own
locality gate is that target's file and must be widened in step, and a gate
that is not refuses them: until it is, a row naming `@` or `+` reads `inside`
here and is refused in CI. A token both
admit is held to one answer by the target's own suite, not by this one.

**Every `gh` read these helpers make is bounded, because one that stalls
holds every step chained behind it.** #603 was `pr-locality.sh` printing
nothing for minutes in a review round, the round's locality file left empty,
and a rerun a minute later answering in seconds. Each `pr-*.sh` sources
`gh-read-bound.sh` and reads through its `gh_read`, which ends a read still
running at `GH_READ_BOUND_SECONDS` with `timeout`'s exit 124 and a line on
stderr naming the call. A stall then fails as loudly as a refused login
does, rather than looking like a slow API. `test_pr_helpers.py` runs every
`pr-*.sh` against a `gh` that never answers, so a helper added later is held
to the bound by the glob rather than by someone remembering it. macOS ships
no `timeout`, and every helper reading through it exited 127 there (#191),
so the file's `bounded`, which `gh_read`, `gh-issue-harness.sh` and
`hint-bound.sh` all run under, takes GNU `timeout` where it is installed
and otherwise a watchdog sending TERM to the command it started, answering
124 the same way. Unlike `timeout`, it does not signal the command's process
group, so a command that leaves work in a child it did not `exec` keeps that
child past the bound; `gh` is one process. `GH_READ_TIMEOUT` names the
program it looks for, so the stalled-read and pass-through cases and every
`hint-bound.sh` case also run under the watchdog on hosts that have `timeout`.

## Sweep push denies and chained commands

The fifth is **`git push` under the two sweeps**, and it is the one that looks
closed and is not. Both commands state a read-only boundary, and both used to
close it with "no `git push` is granted either, so the branch cannot move" —
which reads an *absence* as a control, the exact rule the sentence beside it had
just retired. `.claude/settings.json` **allows** `Bash(git push origin:*)` and
`Bash(git push -u origin:*)` globally, so a push of the current branch does not
prompt at all; only force-pushes and pushes to `main` are denied. Naming `git
push` in each sweep's `disallowed-tools` was the obvious fix and stayed
**unverified** for a while: that key's `Bash(...)` form had never been measured
here — the `Agent(...)` form is what was — and a nested `claude -p` probe could
not separate a rejected pattern from a command that failed to load. **It is
measured now**, by a throwaway command in a detached worktree carrying `Bash(git
diff:*)` in both keys: the diff was refused with the harness's own "has been
denied" text while a `Bash(wc:*)` in the same session ran, which separates the
two exactly. Both sweeps deny `git push origin`, its `-u` form, the raw `gh
issue create` by name and every `gh` command (`Bash(gh:*)`, for `gh issue new`
and `gh api`), and the deny wins over the global allow because precedence is
deny first. The base settings also grant `git-push-force.sh` and
`git-branch-drop.sh` globally, so both sweeps deny those two helpers by the same
key: a terminal read-only command has no branch to force-push or drop. They deny
`gh-issue-harness.sh` beside them though no setting allows it, so a later global
allow cannot reach them.

**A frontmatter deny holds for the rest of the user turn, not for the command
that states it (blueprint-admin#17).** Measured there: `/ship` ran `/commit`,
which denied `Bash(git push:*)`, and then pushed in the same turn, and the push
came back "has been denied" in under a second with no hook reason, every time,
while the identical push after a new user message ran. No command `/ship` chains
here carries that deny, and the rule is that none gains one: **a command `/ship`
runs before it pushes never denies push.** It reads as hardening and is not —
`/branch` and `/commit` leave the push to `/pr`, and the git-argv hook and
`settings.json` refuse `main`, force and delete whoever asks.
`NothingShipChainsDeniesPush` in `test_command_grants.py` names the commands and
fails if one denies push. A terminal, read-only command — the two sweeps — keeps
its deny, because nothing pushes after it. A target whose *Ship checks* run a
command of its own names it in `CHAINED_BEFORE_A_PUSH` in a `local_commands.py`
of its own beside the suites, listed under `local`: that case holds it to
denying no push, and `ACommandThatDoesNotPublishCannotPush` stops asking it to
deny one. A subclass in the target's own suite could do the first and not the
second: `unittest discover` still runs the payload's
`ACommandThatDoesNotPublishCannotPush` as written, which asks every command it
does not exempt to deny push.

## Output deny and shell-expansion residuals

The sixth **was** the `--output` deny itself — the inventory's one entry that
is a *deny* rather than an allow, listed because a deny over a command string
is defeated by shell quoting. **#30 closed it, and not by improving the rule.**
`.claude/hooks/guard-git-argv.py` judges the resolved argv, so the quoted
spelling is refused too, and it fires on the promptless read-only `git` forms
no rule is consulted for. The deny stays as defence in depth and the three read
grants stay with it, because removing them still buys nothing against a
built-in. What survives as residual is one line rather than a grant: a flag the
shell *computes* — `F=--output=x; git log $F` — is not visible to a hook that
resolves quoting but not expansion. **#183's redirection strip inherits that
same bound**, and the residual is now narrower than the sentence above says. An
expansion written in the source is read four ways — empty, whitespace, its own
default, and a single-element brace range — because each is what bash does in a
shell with nothing set, and a command is admitted only if it is safe under all
of them. So `git ${x:-push} origin +HEAD:main`, `git push${IFS}origin
+HEAD:main`, `git $@push …` and `git p{u..u}sh …` are refused, and so is
`git ${N}>&1 push origin +HEAD:main`, which an earlier revision of this
paragraph named as the admitted case.

**What survives is the run-time half alone**: a value the shell is *told*
rather than one written where the guard can read it — `F=--output=x; git log
$F`, or `N=2; git log -${N}`. Closing that needs the argv after expansion,
which no hook is given.

**One construct puts that residual inside a word a caller types literally, and
it is refused for exactly that reason.** `$"…"` is a *translated*
double-quoted string: bash resolves it through gettext against `TEXTDOMAIN`
and `TEXTDOMAINDIR`, both ordinary environment variables, so a catalogue
placed in the checkout decides what the word says. Measured with a hand-built
`.mo` — `$"safe"` printed `printf`, and in command position `$"safe" RAN`
executed it; the same lookup can return `git`. Raised in review, against a
guard that had already met this form once and refused only the *expansions*
inside it, as though `$"safe"` were the word `safe`. Every locale quote is
refused now. The cost is a construct nothing in this repository writes, and
`$'…'` is unaffected: its escapes are decoded, and one outside the decoded set
was already refused.

**A shell fed by a process substitution is refused for the same reason a
printer feeding one is.** `bash < <(printf '%s' '…')` executes the
substitution's output, and `bash <(echo '…')` executes it as a file — both
ran, and every pass judged the halves apart: the inner command is data, the
redirection strip removes `< <(…)` whole because a process substitution *is*
the target, and what is left is a shell with no script. Reading the inner
command instead would be right for `<(echo '…')` and wrong for every spelling
that computes, so this joins `unmodelled_printer` rather than trying. The cost
is `bash < <(cat script.sh)`, which nothing here writes; `diff <(a) <(b)` and
`echo <(…)` are untouched, because the run has to be a shell.

**A crash in the guard is a fail-open, so a crash now refuses the command it
crashed on.** `PreToolUse` reads empty stdout as non-blocking, and a
traceback is empty stdout — so each of the four crash paths found here (two
`str.index`, one `list.index`, one recursion) admitted whatever the command
was, including a force push. Fixing them one at a time leaves the next one
open, so the direction is set at the door instead. **This is not the same
answer as the malformed-event case beside it, and the two differ on purpose**:
an unreadable hook event has established nothing about any command, so
refusing there would stop the session for a defect in this file, while a crash
while judging *this* command says this command broke the parser — and refusing
one command is proportionate, states why, and puts the traceback on stderr.

**Each of those readings was found by measurement, not by reasoning**, and the
paragraph that stood here reasoned. It claimed every unresolved shape fails
closed; the second command disproved that, and then four more readings
disproved the narrower claim that replaced it. **A guard's stated failure
direction is a claim to measure in every position**, not one to derive from the
case in front of you.

**A shell reads a script from three places and the third is a pipe**, so the
guard costs a stated over-refusal to cover it. `bash -c '…'` and
`bash <<<'…'` put the text where it can be read; `echo '…' | bash` puts it
one run away, and `cat <<'EOF' | bash` puts it in a heredoc belonging to a
run that is not a shell at all. What decides is whether some later stage of
the pipeline will *execute* its stdin, and that cannot be settled by naming
the wrappers which exec their argument — `command`, `env`, `nohup`, `nice`,
`stdbuf`, `setsid`, `timeout`, `ionice` and `chrt` are nine before anyone has
looked hard, and the tenth is the bypass. So a shell name **anywhere** in a
stdin-consuming run counts, which is the direction `DATA_ONLY_COMMANDS`
already argues for, and it costs `echo '…git push…' | grep bash` a refusal
it does not deserve. That shape needs a printer writing a push into a pipeline
whose far end merely mentions a shell; it has never been typed here, and the
alternative fails open on a wrapper nobody listed.

## Agent-type and push entries

**A seventh thing is a gap in the mechanism rather than in a grant.** Pinning a
command to one subagent type is a **deny list of every other type**, because
the harness has no "only this type" allow — so `security-sweep.md`,
`bug-sweep.md`, `team.md` and `ship.md` each deny every registered type but the
one they grant (`ship.md` also leaves `general-purpose`, which step 2
dispatches its audits as), and **a newly added agent under `.claude/agents/` is
admitted by default** until someone adds it to every list;
`test_command_grants.py` fails a granting command that does not. That is the
shape this repository already knows rots; it is taken here because the
alternative on offer is prose. **It rotted once already on the day a third
agent arrived**: `review-adjudicator` was added for #149 and every command then
holding a list had to name it, which is the enumeration's cost paid in the
change that proves it.

**A command that does not publish denies `git push`, and one that holds `Edit`
or `Write` is held off the repository's machinery by the settings.** The
settings allow `git push origin`, so a command that neither grants nor denies
push reaches it; `ACommandThatDoesNotPublishCannotPush` in
`test_command_grants.py` derives the commands from their frontmatter, so a new
one fails until it decides. `branch.md` and `commit.md` are the exemption:
`/ship` runs them before it pushes, and a frontmatter deny outlasts the step.
`Edit` and `Write` carry no path scope, so `.git`, `.mcp.json` and
`.codeindexignore` are denied in the base settings in both spellings, for
every command at once, because the grant is per command and a new editing
command should arrive behind the deny; `test_harness_denies.py` checks it
against each command that holds either tool. No payload command edits any of
the three, so the deny costs none. A target whose own command must edit one
changes the base in `blueprint-harness`, because a deny beats every allow.
Residual, as for the rest of the list: the git guard judges a redirection and
the writing verbs (*Git argv guard hook*), and any other program still writes
what `Edit` refuses.

**The eighth was the push deny-list (#23), and it closed the way the sixth
did.** Two broad allows — `Bash(git push origin:*)` and the `-u` form — paired
with a list of exact spellings, so `git push origin +HEAD:main` was a force push
to `main` carrying neither `--force` nor the literal `origin main`, and five
more slipped alongside it: `origin :branch` deletes where the `--delete` deny
does not look, `origin HEAD:refs/heads/main` is the fully-qualified spelling,
`origin feature --force` puts the flag where a leading-flag deny cannot reach.
**Adding four more denies was refused.** Deny-list enumeration trails git's
refspec grammar forever, and the grammar keeps growing — which is the issue's
own conclusion — and judging three *properties* instead was not enough either.
Two review rounds took that apart: `--force-with-lease=<ref>` is not equal to a
set entry, git accepts `--for` as an abbreviation, `-fv` bundles, `--branches`
and `--all` and `--mirror` carry no refspec to inspect, `refs/heads/*` includes
`main` while equalling nothing, and bare `git push origin` names no destination
at all. That is the deny-list trailing the grammar in parser form. **The
question is inverted now**: a push is refused unless every part of it is
recognised, so the spelling nobody has listed is refused for being
unrecognised rather than admitted for being unlisted. Its suite carries the
six the issue listed and the spellings it did not, plus the three pushes
`/ship` actually makes, because over-reach here breaks the delivery chain and
would be found at the worst moment. **One spelling still got through**: git
matches a destination that is not fully qualified against `refs/%s`, so
`HEAD:heads/main` is `refs/heads/main`, and the parser stripped only the
longer prefix (#1154). It strips either now, once, so `refs/heads/heads/main`
stays the branch it names.

**The ninth was a build tool's grants in backend's branch-review command**,
and it is that repository's to keep, under *Residuals* in its
`docs/harness-local.md`: a file the build imports by name that does not exist
yet is on no list read from what exists, so a deny enumerated from the tree
cannot cover it.

**A boundary an agent must keep across turns lives on its profile.** It was
measured in blueprint-admin (its #19, #23 and #27) and blueprint-backend (its
#52 and #149) while a writing agent still ran in the chain, and this repository
has not re-measured it. A command loaded inside an agent can discard its own
frontmatter deny; a type list inside a subagent's `Agent` grant is ignored; a
command's `disallowed-tools` lasts only the turn that loaded it, so a deny a
later step must still meet cannot be put there. A command's `disallowed-tools`
propagate to the subagents it spawns, and a path deny reaches a `Bash` command
naming the path; the harness offers no deny that reaches the parent and not the
child. The local review holds no writing agent, so none of this binds a
boundary today, and it binds the first one added: `branch-reviewer`,
`bug-auditor`, `security-auditor` and `team-peer` hold `Read`, `Grep` and
`Glob` on their profiles for that reason.

## The edit-target guard

**The tenth was `/review-grok`'s `Edit` and `Write` beside an untrusted
review (#52, #149)**, closed by splitting the reading from the writing and
now archived with it. What outlived it is the premise its writing step rested
on — the helper suite fails on any tracked mode `120000`, so `main` carries
no symbolic link — but a premise about `main` was never one about the
branch being edited, which introduces files before CI judges them (#181).
`.claude/hooks/guard-edit-target.py` is the check that premise stood in for
— the second hook in this repository, and the argument for it is one
sentence: **an edit target must be the file its path spells.** It resolves
the target of every `Edit`, `Write`, `NotebookEdit` and `MultiEdit` — the
matcher, the hook's own `EDITING_TOOLS` and the registration case all name
those four, and a boundary documented narrower than the one configured is the
drift this file exists to refuse — re-anchors it on the
resolved checkout root, and refuses the call when the two disagree — into a
denied tree, or out of the checkout. The premise stays as defence in depth
and its gate stays green.

**That guard holds no copy of any deny list, and refusing to write one is
the whole of why it is safe to run on every write this repository makes.**
A path deny is *correct* when the spelling is true of the file, so inside a
checkout the session stands in the hook judges only the disagreement and
leaves every list — `.claude/settings.json`'s and both commands' frontmatter —
to say what may be edited; where no rule reaches it reads that list rather
than copying it, as the next paragraph says. Nothing here goes
stale as those change, and the PR that lifts a deny to edit a helper is not
refused by the guard on its way past. A test pins that control: a denied tree
spelled as itself is **admitted** here.

**Two things a path deny cannot reach are judged here all the same, and
neither takes a copy of a list.** Every rule anchors at the session's
permission root, so a worktree of this repository that the session has not
entered, the main checkout seen from a linked one included, is reached by none
of them: its `.claude/scripts/x` and its `.git`
are judged by this target's own `Edit(...)` denies, read from
`.claude/settings.json` through `guard-git-argv.py`'s `denied_path`, anchored at
that worktree's root and folded for case, and a link inside it is judged as in
any checkout. `.github` there is refused only where a target denies it, and
the worktree the session stands in is the project, not this rule. A worktree is
one only if git's backlink agrees, so a `.git` file alone makes none, and a
checkout of another repository is not one, because this target's denies say
nothing about what it protects.

**Outside every checkout the guard refuses by name and admits the rest.**
`~/.ssh`, the shell profiles and `~/.gitconfig` run at login under every
session, and `~/.claude`'s credentials, settings, hooks, commands, agents,
plugins, scripts, skills and sandbox apply to every project, judged on the
first component only, so a name below `projects` is a session's own state. An
allow-list of roots is the stronger form and it was measured against what
sessions write: it refuses sibling checkouts, `run-locally.md` and the task
list, a false refusal under *The guards' threat model*. `~/.claude/CLAUDE.md`
stays writable because it is the owner's rules rather than a grant, and a
change to `~/.claude/settings.json` is the owner's to run from a script.

## Edit guard path resolution

**Two properties of the resolution are load-bearing and were each found by
running it rather than by reasoning about it.** The anchor is resolved as
well as the target, because a checkout reached through a link — `/tmp` on
macOS, an 8.3 or `subst` path on Windows — would otherwise make every edit
under it look like the thing the guard refuses; and the comparison folds case
where the **filesystem** does, because Windows' `realpath` answers with the
on-disk case and `DOCS/x` is not a link. **Filesystem, not platform, and the
distinction is the second bypass this guard shipped with**:
`os.path.normcase` folds on Windows and nowhere else, while macOS mounts APFS
case-insensitively by default — so `/Users/x/Repo` and `/users/x/repo` are one
directory there, a target spelled in the other case was under no anchor at
all, and the fall-through admits. It is asked of the filesystem now, by
`stat`ing a case-flipped spelling and comparing device and inode. Raised by
Copilot; the platform default stands in only where the probe cannot run. The
two platforms also
disagree about `..` after a link — POSIX resolves it against the link's
target, Windows collapses it before the filesystem sees it — and the guard
follows each rather than picking one, measured both ways in
`.claude/scripts/test_edit_target_guard.py`.

**That suite's link cases run against a real link, on every primitive the
platform grants**: a symbolic link where the session may make one, and a
directory junction on Windows, which is all an unprivileged process gets
there. Neither is a skip, because a skip on a missing capability reports a
pass, so a platform that grants no primitive fails the module. `..` after a
link, case folding and the link primitives are asserted as the platform's or
filesystem's own answer, because the guard follows each, so the verdict is the
host's rather than a hard-coded expectation.

**What an anchor is, and why it is two rules rather than one, is the part
that shipped wrong.** An anchor excuses exactly one link traversal: the one
on its own root prefix. So an anchor is a checkout **root** — the event's
`cwd` is walked up to the directory holding a `.git` and dropped if it has
none — and **every** anchor containing the target must agree, where the first
form admitted on the first that did. Either rule alone leaves the bypass: a
session standing in `docs/tree`, where `tree` links into `.claude/scripts`,
took that directory as an anchor, and re-anchoring `docs/tree/helper.sh` on
it makes the spelling and the resolution agree — so the guard admitted
precisely the write it exists to refuse. Raised by Copilot, and **measured
both ways** against the shipped commit: it admits, and the fix refuses.
Requiring agreement is also what makes an environment-supplied
`CLAUDE_PROJECT_DIR` safe to take as given, since an added anchor can then
only narrow the guard and never widen it.

**A spelling can also evade the matcher through the path grammar rather than
through a link, and two of those were measured here — both admitted before the
guard refused them.** Windows names one file in more than one alphabet: the
extended-length prefix `\\?\` and the device prefix `\\.\`, which exist
precisely to skip the normalisation a matcher depends on, and the UNC form
`\\server\share\…`, which reaches the local disk through the administrative
shares. With `.claude/sandbox/**` denied, a `Write` to
`\\?\C:\dev\ashamray\.claude\sandbox\probe-unc.txt` **was created**, and so was
one to `\\localhost\C$\dev\ashamray\.claude\sandbox\probe-share.txt`; the plain
spelling of either file is refused. Both probe files were deleted.

**So the guard refuses the family rather than the prefixes** — every spelling
beginning `\\`, unless an anchor that is itself named that way **contains the
target**. The exemption was session-wide for one round, which reopened the
bypass it was carved for: once any checkout was `\\`-spelled, an alias of that
same checkout — `\\?\UNC\localhost\C$\…`, not lexically under
`\\localhost\C$\…` — skipped the anchor loop and was admitted. Measured with
the administrative share as the project directory, and raised by Copilot. The
legitimate case it protects is a repository genuinely on a network share,
editing its own files. Enumerating the
prefixes is the deny-list shape this repository has rejected twice, and the UNC
form is exactly what such a list missed. Refused rather than resolved because a
hook can only allow or deny: it cannot hand the matcher the plain spelling it
would have judged. **The 8.3 alias is the same class again and needs no special
case**: `.claude` has the alias `CLAUDE~1` on this volume, `realpath` answers
with the long name, and the spelling therefore disagrees with the file.
Whether the matcher alone would have refused that one is untested — the hook
runs first — so it stands as defence in depth rather than as a measured gap.

**Then CI found the general form, and it replaced the list those spellings
were becoming.** A Windows runner's `GetShortPathNameW` shortens the whole
prefix rather than the leaf, so `C:\Users\RUNNER~1\…\GUARD-~1\DOCUME~1\a.md`
matched no anchor at all and fell through to the residual while resolving
squarely inside a checkout — the 8.3 case went red there having passed on a
volume where only the leaf is aliased. Case folding and Unicode composition
had each closed one spelling by teaching the comparison an equivalence, which
is a deny-list in slower clothing. **The rule now is that the residual belongs
to a file genuinely outside every checkout, and not to one inside under a name
the anchors cannot place**: a target resolving into a checkout that did not
recognise its spelling is refused, whatever alphabet the spelling is in.
Measured against the commit that shipped it — admitted there, refused after —
and the control is the worktree case, where a session standing in the aliased
directory makes it a root of its own and the spelling is recognised again.

## Normalisation, residuals and the sweeps

**Unicode normalisation is the same shape on a different platform, and it took
two rounds because the first fix argued its way past the probe.** A
case-insensitive APFS volume is also insensitive to normalisation, so `é`
composed and `e` plus a combining accent name one directory there and two
strings in a comparison — a checkout prefix spelled in the other form matched
no anchor and reached the branch that admits. The fix composed to NFC
**unconditionally**, on the reasoning that composing "can never make two paths
look like one" — which is false wherever the mount is normalisation-sensitive:
NTFS and ext4 keep both names, so a link resolving to the same relative path
under the coexisting sibling compared equal to a path inside the checkout, and
the escape was admitted. Measured on NTFS against the commit that shipped it.
So normalisation is probed exactly as case is and the two answers travel
together as the anchor's traits. **Both were raised by Copilot, and the second
is the one worth carrying: an equivalence asserted about "filesystems" rather
than measured on the mount is a bypass in whichever direction the assertion is
wrong.**

**The traits are the root's, and a child directory may disagree — which is true,
and turns out not to be a bypass in the shape it was raised.** Windows sets case
sensitivity **per directory** (`fsutil file setCaseSensitiveInfo`, no privilege
needed, and only on an empty one), so a case-sensitive `docs/` keeps `BETA` and
`beta` apart while the root's traits fold them. For folding to hide anything,
though, a link's resolution would have to differ from its own path only in case
— and a link's resolution *is* its target, so the write lands on exactly the
file the path names. The guard carries an identity check for the shape that
argument does not cover — a sub-mount whose equivalences differ from the root's:
where two paths agree **only** because an equivalence was applied, `samefile` is
asked. That is safe in this position and not in the anchor test, because it
compares two concrete paths rather than deciding what counts as a root. Where
either path does not exist yet — the ordinary `Write` — there is nothing to
compare and the folded verdict stands. Raised by Copilot.

**The final review round argued that this check *admits* an alias, and it has
the direction backwards — recorded rather than acted on.** The block runs only
where `same(...)` is already true, which is the branch that **admits**; the
refusal beneath it is guarded by `not same(...)`. It can therefore only turn an
admission into a refusal, and deleting it would admit strictly more. The
narrower half of the remark is true: the *folded comparison* it sits inside can
equate two spellings a case-sensitive child keeps apart. For a deny to be
evaded that way, `resolved` and `expected` must differ **purely** by case or
normalisation — which puts them at the same position under the same parents —
and a deny pattern must then name one of those spellings and not the other.
That is the residual, stated rather than closed. **Failing closed on the
missing-leaf case was the other suggestion, and it is refused on this guard's
own rule**: `expected` rarely exists when a `Write` creates a file, so refusing
there would fire on the commonest innocent call the hook sees, and a guard that
fires on innocent traffic is one somebody turns off.

**A `..` that traverses no link is admitted, and the reason is a measurement
of the harness rather than a judgement about paths.** The case for refusing it
is that `docs/../.claude/hooks/x` carries no `.claude/**` spelling, so a
matcher reading the string would not deny it. **The matcher does not read the
string.** Measured in this checkout with `.claude/sandbox/**` denied: a `Write`
to `docs/../.claude/sandbox/probe-tmp.txt` was refused with the harness's own
*"denied by your permission settings"*, while `docs/../docs/probe-tmp.txt` was
created — so a path is normalised and then matched, and `..` is not what was
rejected. Refusing every `..` in the guard would buy nothing against the deny
list and would refuse the second of those two spellings, which is innocent
traffic. Raised by Copilot, and the premise is the half that failed; a passing
case pins the verdict and names what to invert if the harness ever stops
normalising.

**Its residual is the half the harness itself needs, stated rather than
rounded up.** The subject is a target spelled *inside* a checkout the session
is standing in; a path spelled entirely outside one is judged only where no
rule reaches it, a linked worktree of this repository by this target's denies
and the home directory by its named files (*The edit-target guard*), because
the session's memory and scratch state are written that way by absolute path
and an allow-list would take both with it. The residual is a passing test,
not a paragraph alone.
**The sweeps' item 5 (#75) closed by the same shape** — a second read-only
dispatch returns a verdict, the parent opens nothing in `$work`, and
`gh-issue-create.sh` leaves `gh issue create` with no free parameter — so
the two residuals #149 named as one class went in one change, and the raw
`Bash(gh issue create:*)` is denied by name in both sweeps now that the
fifth entry's measurement exists. `Bash(gh:*)` is denied beside it for the
aliases, `gh issue new` and `gh api` (#1287). The residual: a spelling of the
command word other than the literal `gh`, such as an env-prefixed,
path-qualified or `gh.exe` call, or one wrapped in a shell, matches neither
rule and nothing in the tree refuses it. The default mode prompts for such a
call; in a mode that skips prompts, only the helper being the sweeps' one
granted route stands in the way.

**The two sweeps are one shape asking two questions**, split by what makes a
finding rather than by where they look. `/security-sweep` files what an
attacker can reach; `/bug-sweep` files what is wrong on its own terms. Both
fork a detached worktree, verify every subagent claim before filing,
de-duplicate against the whole issue set, never fail open, and file without
fixing. Three things differ: the threshold, the fan-out cut, and what
confirmation can mean — **`/bug-sweep` executes none of the snapshot it
audits**, because building a tree executes it (build targets, source
generators, analysers, and under a test run the tree's own test code) and
the audited repository is prompt-injection input. So a defect claim there is
confirmed by reading, and the class of bug only execution catches is named as
the residual.

**Both sweeps' worktrees carry the `secsweep-` prefix**, and the second is
borrowing. `git-worktree-detach.sh` and `git-worktree-drop.sh` refuse any path
that is not `secsweep-` plus six characters under the canonical temp root —
the shape check that stops a poisoned finding from naming a PR worktree
and having it deleted. Renaming the prefix would have to move in both helpers
and every caller at once, so it stands; what is lost is attribution.

**A sweep's start also drops what an interrupted sweep left.** Before it
forks, `git-worktree-detach.sh` hands each sweep-shaped worktree this
repository has registered, and that is over a day old, to
`git-worktree-drop.sh`, then runs `git worktree prune`, which forgets every
registration of the repository whose directory has gone, a sweep's or a PR's,
and deletes no file. The candidates come from git's own list, never from the
tree or an argument, so a poisoned finding cannot add one; the drop helper's
shape checks and git's refusal of a tree holding anything modified or
untracked still decide, so a worktree a refused teardown left for inspection
stays. The day stands in for "no sweep still runs there", which no helper can
ask; a sweep running that long would lose its worktree mid-round.
A harness suite that forks a sweep worktree of this checkout to test the
helpers is a sweep start too, and drops and prunes the same way. Those tests
keep the shared temp root on purpose: one a stopped run leaves there is then
a worktree the next sweep's start can drop.

**Both sweeps now write two things besides their issues and their worktree.**
`sweep-slices.sh` writes its slice lists to the worktree's path plus
`.slices`, a name it derives from the same checked shape rather than takes,
and `git-worktree-drop.sh` removes that directory once git has removed the
worktree, a half-finished removal included: where git unregistered the worktree
and failed to delete its tree, an empty directory goes and any other is named,
exit 4, for the owner to remove, since no later sweep's start can see it.
`sweep-mark.sh` moves `refs/sweeps/bug` or `refs/sweeps/security`
to a commit, and only to an existing one by its full SHA. The ref is local
and a missing one means a full sweep, so the write can make a later sweep
read less only by pointing at a commit that really was swept clean; a
poisoned tree cannot reach it, because the parent runs it once, on a clean
end, with `$pinned`.

## The sed line-ending guard

**Under Git Bash, `sed -i` drops the CR from every line it writes**, so a
file the checkout holds with CRLF endings comes back LF: a C# file that
`.gitattributes` forces to `eol=crlf` then fails a whitespace analyser at
lines nobody edited, and a file whose index holds CRLF changes every line
of its diff; under `core.autocrlf`, whose index holds LF, the diff shows
only the edited lines, and the cost is a working copy left LF for an
analyser or an editorconfig check to find. Measured with GNU sed 4.9
under Git Bash: `one\r\ntwo\r\n` through `sed -i 's/one/uno/'` comes back
`uno\ntwo\n`, and through `sed --binary -i` keeps both CRs. `grep -c` cannot
confirm it, since MSYS grep strips the CR before matching (#37).

`.claude/hooks/guard-sed-crlf.py` is a `PreToolUse` hook on `Bash` that
reads the file a `sed -i` names, because nothing in the command's text says
what line endings that file holds. It reads the command's lines as bash
does, continuations joined and comments and heredoc bodies dropped, splits
them into simple commands with `shlex`, follows a plain `cd <dir>` within
its subshell, finds `sed` behind the prefix words and the `env`, `nice`,
`timeout` and `stdbuf` wrappers, judges what a shell runs — its `-c` line,
or a heredoc, a here-string, or an `echo` or a `cat` piped into one reading
its input —
as a command of its own, and reads sed's options as `getopt_long` does:
clusters, `-i<suffix>`, `--in-place=`, unique prefixes of long names, and
the first operand as the script when no `-e` or `-f` names one. It refuses
when an operand, globbed as bash would, is a regular file whose first 4 MiB
hold a CRLF and which `git ls-files --error-unmatch` finds in its
checkout's index. The refusal names the forms that keep the endings: the
Edit tool, a Python script that opens the file with `newline=""`, or
`sed --binary -i`, which is admitted. **It judges on Windows alone**: GNU
sed on Linux and macOS keeps the CR as an ordinary character, so a refusal
there would block a harmless edit, the false refusal *The guards' threat
model* files.

**The line reader is one scan that tracks quotes**, because each of its
three jobs turns on whether a character sits inside one: a backslash-newline
inside single quotes is kept, a `#` starts a comment only where it begins a
word, and a `<<` opens a heredoc only outside quotes. `shlex` knows none of
this, so an indented continuation would reach it as a newline token, read
as a separator, and an apostrophe in a comment as an unbalanced quote.

**What it reads, and what a crafted path makes it run.** Only a regular
file is opened, read-only, and nothing read leaves the hook but a path: the
one the command spelled, or the file in its directory that a glob of it
matched. So a device, a FIFO or a directory is never opened,
and a path outside the checkout costs a read the session could `cat`
anyway. The one thing a path can make it run is git in that path's
directory, whose checkout may be foreign: the call switches
`core.fsmonitor` off, since `ls-files` would otherwise run the command a
hostile config names, and sets `GIT_OPTIONAL_LOCKS=0` so the read writes no
index. Each git call is bounded at 5 seconds, and a command at 200 files.

**Which git it runs.** The first on `PATH` by the hook's own search, the
rule *Index refresh and worktree seeding* gives its console script but for
the main checkout, which it does not fence, and never
`shutil.which`: on Windows that looks in the current directory first, where
a merged change could plant a `git.cmd` to run inside this hook, and only
`NoDefaultCurrentDirectoryInExePath`, which a hook cannot rely on, turns
that off. Only absolute entries count, and nothing at or below the hook's
working directory, the event's `cwd` or `CLAUDE_PROJECT_DIR`. On Windows
only an `.exe` or a `.com` counts, never a `.cmd` or a `.bat`: those run
through `cmd.exe`, which reads an `&` in the file name the command gave as
its own. So a git installed under one of those directories, as under a
session started in the home directory or at a drive root, and a git that is
only a `.cmd`, read as no git on `PATH`.

**Where it fails open, by design rather than by crash.** An operand holding
an expansion (`"$FILE"`, `$(git ls-files)`), a `cd` it cannot follow, a file
that does not exist or cannot be read, a command `shlex` cannot tokenise, no
git on `PATH`, or a git that cannot answer: each is admitted, because a
guard that refuses what it cannot resolve blocks ordinary work for want of
a resolution. An unreadable event is admitted, as in the other guards.

**Its residuals, stated rather than left to be found, and pinned in its
suite as what the hook returns.** The launchers and wrappers *The guards'
threat model* names reach this guard only where the command's text holds
the file list: a `sed` behind `xargs` or `find -exec` takes its files from
another program's output, which no hook is given, so neither is judged,
however routine the idiom; nor is a `sed` through the PowerShell tool, or
an untracked CRLF file, whose endings are not yet the checkout's. A
`printf` piped into a shell is read as its words rather than its format's
expansion, so it is refused where the format holds the `sed` and its
arguments carry the file, and admitted where an argument carries the whole
line. A quoted
glob is expanded although bash would pass it literally, which can only
refuse more.

## Headless dispatched sessions

Up to `--pairs` headless `claude -p` sessions, a number the owner sets at
launch (its ceiling is `docs/superpowers/specs/2026-11-01-dispatch-design.md`
§3), run `/ship --dispatched` at night, each in its own worktree and each to
a merge, with nobody to answer a prompt. `.claude/commands/dispatch.md`
starts them; [`docs/commands/dispatch.md`](commands/dispatch.md) argues it.

**What holds them.** The permission mode is the settings' own: the launcher
passes no skip flag and the command denies `claude` as a shell word, so
`dispatch_board.py run` is the only way in. Every guard hook runs in an
executor as in any session. Beyond the stops `/ship` names, `--dispatched`
hands a change back unmerged in three cases: an edit under `.claude/`, a
resource only the owner has, and a decision the issue leaves to the owner.
`/dispatch --berserk` lifts the third for one run and releases rows that wait
on the owner or reach `.claude/`; it lifts no deny, guard or stop.
It also changes the executor: it never tears down its workspace, step 6 runs
only lines 1 and 8, step 0's finished teardown is skipped, and it never
writes the task list, not even for an issue it files or closes, which goes
only in its result's `filed`. The dispatcher grants no agent type and
denies every one, because it spawns sessions.

**No grant was added for this.** Both spikes, one on docs and one on code,
ran with `permission_denials` empty
(`docs/superpowers/plans/2026-11-01-dispatch-spike.md`). The
reason is the owner's user settings: `defaultMode` is `auto`, a `claude -p`
session inherits it, and `/ship`'s frontmatter grants auto-approve its
helpers. This repository's settings set no mode, so the executor depends on
a setting outside the repository; on a machine without it the first prompt
hands every executor back.

**The dispatcher writes the task list alone.** It writes only through
`dispatch_board.py tasks`, with no `Edit` grant on the file. Every `tasks`
action refuses a run with no `state.json`, `tasks rewrite` refuses any
change to the *In progress* section, and `done` and `back` touch only the
rows the run's own move recorded, `done` only once `verify` has passed.
After `verify`, the dispatcher pulls with `--ff-only`, checks
containment with `git merge-base --is-ancestor`, runs `tasks done` and
removes the worktree with `git-worktree-remove.sh`.

**An issue is a work order only from the repository owner.** The repositories
are public, so anyone can open an issue, and a row the night launches merges
with nobody watching. `dispatch_board.py` takes `gh-issue-owned.sh`'s numbers in
code, never from the session: `board`, `wake` and `report` hold back a row whose
issue is not among them, `launch` refuses one, `--berserk` lifts neither, and a
failed read refuses rather than reading as empty. The test is authorship, as
`gh-issue-suppresses.sh`'s is, and not a label: a label stays on an issue its
author then rewrites. Work the owner wants from such an issue is filed as their
own, in their words, which the interview offers.

**What an executor reports is not trusted.** A prompt-injected body, which is
now text an owner's issue quotes, reaches an executor as it reaches any
`/ship`. Its result file is checked
against `dispatch_run.py`'s schema by `result`, and a claimed merge is read
from GitHub by `verify` before it counts; a missing or mis-shaped file is a
crash, never a success.

**Residuals.**

- The dispatcher's and `issue_decisions.py`'s `path_which` take a `.bat` or
  `.cmd` that `PATHEXT` names, where `refresh-index.py`'s `on_path` takes only
  `.exe` and `.com`. The decision (issue #130) is to keep it: `claude`
  installed by npm is a `.cmd`, so refusing one would stop `/dispatch` on such
  a machine. The other two rules bound it: only an absolute `PATH` entry is
  searched and never the working directory, and `dispatch_run.py` passes
  `claude` fixed arguments with its prompt on stdin, so no issue or executor
  text reaches the command interpreter. A planted batch file needs write
  access to a `PATH` directory, which already runs code.
- A refusal at night is a hand-back. `result` classifies it, the morning
  report shows it, and the grant is never widened to get past it (the
  spec's §2, its last anti-goal).
- No script reads the weekly figure. Token Weather's pause notice in the
  dispatcher's own session ends the night (spec §8). Whether that pause
  reaches a headless executor is untested
  (`docs/superpowers/plans/2026-11-01-dispatch-spike.md`), so an executor
  may run on past the limit.
- The `Edit(artifacts/dispatch/**)` grant also reaches the helper-owned
  `state.json`. No step edits it, so an honest mistake does not reach it,
  and a session setting out to defeat the helper's checks is what *The
  guards' threat model* puts outside what the guards defend.
- A stranger's issue still reaches the dispatcher as data: its row is added
  and held, and the rebuild and the interview read its text. A collaborator's
  is held like a stranger's, and under a repository an organisation owns every
  row is, since no author is the organisation's login: the cautious side.

## The suite's shard runner

**`py -3.12 .claude/scripts/shard-harness-suite.py` is the `.claude/scripts`
suite, run in parallel.** It takes no arguments and exits 0 only when every
discovered test ran and passed or skipped itself, as a case does on a host
without what it needs. `.claude/scripts` is its one root, the tree
every target holds; a gate's suite or a stack's root is the target's to run
beside it. `python` will do where `py` is not installed.

**It deals classes from a queue to parallel workers.** The unit is the class,
since a class is what shares a fixture, and the count is the host's cores
capped at the measured knee. A worker takes the next unit when it finishes the
last, longest first, so a wrong guess at a class's cost costs that class rather
than a whole worker.

**What a splitter may never do is stop placing a class.** Every worker green
and one class never run is the most-repeated failure of
`.harness/agent-rules.md` in its cheapest form. So placement comes from
discovery, with no list written by hand, and `test_harness_shards.py` compares
the units back against discovery. Every unit has to come back finished, a
worker refuses an id discovery does not hold rather than reporting a pass, and
a root that discovers nothing refuses the run. A unit is claimed by creating
its claim file exclusively, and a rename is not exclusive on Windows.

**Each class runs as its own suite.** Its time is measured whole, and module
cleanups run between two classes of one worker, so a fixture a module caches
has to be built again after one. Class sharding cannot split a class, so the
run never takes less than its slowest one, which is why the worker count is
capped rather than tuned.

**Two of its three tables are not about speed.** `SERIAL` holds the classes
whose subject is elapsed time, `TOGETHER` the ones sharing the `secsweep-*`
namespace in the temp root and this checkout's own `.git`, and `WEIGHT` only
orders the queue. A name in any of them that no longer resolves fails a test, so
a renamed class loses its pinning loudly. A target's `local` test module is
placed on its test count and needs no entry.

**One run at a time, per user.** A run takes a lock named for the user in their
temp directory before it discovers anything, and a second run, from another
session, worktree or target, waits and says so. Two at once ran twice the
workers the host holds and each took about three times as long. The lock spans
what one user runs, which on a developer's machine is every session that could
collide; another user's runs, or one under a different `TMPDIR`, are not held by
it. The operating system drops the lock with the process that held it, so there
is nothing stale to clear.

## Managed files

This section holds from a repository's first sync, which writes the
manifest, the base settings and the renderer; before it,
`.claude/settings.json` is the repository's own and `.harness/config.json`
renders nothing.

**A path `.harness/manifest.json` lists is owned by `blueprint-harness`, and
is changed there.** A sync from that
repository wrote the file, and the manifest records the harness commit and
the file's hash. A change is made in that repository's `payload/` and reaches
each target as the sync pull request opened on the next merge to its `main`;
an edit to a target's copy is red until the next sync reverts it.
`.claude/settings.json` is among them: it is rendered from
`.harness/settings.base.json` plus the `settings` in `.harness/config.json`,
so a rule only one repository needs goes in that config, which is the
repository's own file and never in the manifest. That is the one change a
target makes to a managed path itself: a pull request that changes the
config carries the re-rendered `.claude/settings.json` and
`.harness/manifest.json`, rendered at the commit the manifest already names
(the drift gate's README, *Changing this repository's config*). The config
is Edit-denied with the rest of `.harness/` (*Edit denies and agent
self-protection*), so that change is the owner's to make or lift.

**A target's stack grant goes in the config's `allow` too**, such as the
check runner its *Ship checks* names. It is granted to every turn of every
session there, not only to the commands that run it, and that is accepted:
the runner is a script under `.claude/scripts/`, Edit-denied, so the grant
admits one fixed endpoint a session cannot rewrite. A per-command route was
rejected: it would make every managed command a rendered file, differing
from the payload by each target's lines, and the drift gate and `sync_pr.py
verify`, which hold a managed file to the payload's content, would each need
a second renderer to judge it.

**A path the config lists under `local` is the repository's own**, even
inside a managed directory: a sync neither writes nor deletes it.

**The drift gate holds it**, delivered by the first sync with the manifest:
`.github/harness-drift/`, run on every pull request by
`.github/workflows/harness-drift.yml`. Its README owns what the
gate refuses, how a config change re-renders the settings, and what to do
when it is red.

**`docs/harness-local.md` is the one document every target writes for the
payload**, and the payload cites it rather than any target document by path:
the commands run in repositories with different products, so where one
names a target's document or check it names the row of this file that does.
It is the repository's own file, never in the manifest, and holds four
headings, fixed so a citation of one lands in every target:

- *Documents*: one row per role, each a path in the repository or `none` —
  the change contract (the classes, the touch set and their class map), the
  style guide, the testing document, the product specification, the
  roadmap, the closed records a reviewer greps rather than reads, and the
  measurements `docs/harness/token-rules.md`'s reasons rest on.
- *Ship checks*: the checks `/ship` step 2 runs here, each with the change
  that selects it and the passes it dispatches, each pass's scope and
  method as its brief, or `none`; and the required checks, the CI check
  names `/ship` step 6's wait requires on the pushed head, never `none`.
- *Residuals*: what this file's arguments leave open only in this
  repository, beside the residuals stated above.
- *Review examples*: the kinds of finding this repository's surface
  produces, read beside `docs/harness/working-rules.md`'s *What counts as a
  finding*.
