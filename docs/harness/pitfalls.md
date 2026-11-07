# Pitfalls

Traps a guard cannot see from the command: they depend on the network, a
process tree or an interpreter's own decoding, or their shape is mostly
intended, so a refusal would stop right work too. A trap a guard can see
is that guard's refusal instead, which names the form that works. Each
entry is the trap, what it looks like, and the safe form. Read the section
when a step behaves unexpectedly.

## Shell and encoding

- **A backslash in an inline heredoc script.** `py -3.12 - <<'EOF'` in the
  Bash tool does not receive `\\` intact: `'\\n'` reached Python as `'\n'`
  and wrote a real newline into a string literal. Symptom: generated code
  split across lines. Safe: build the character from `chr(92)`, or write
  the script with `Write` and run it by path.
- **Python output read by Git Bash carries `\r`.** A `print` piped into
  Bash on Windows writes `\r\n`, so `read -r a b oid` leaves `\r` on the
  last field. Symptom: a helper refuses a 40-character sha, or
  `git log "$oid..HEAD"` reports an unknown revision. Safe: `| tr -d '\r'`
  after any Python step feeding Bash, and shape-check the value.
- **An emptiness test fails open on an error.** `test -z "$(git log …)"`
  passes when `git log` fails, since the error goes to stderr. Safe:
  capture into a variable under `set -e`, so a failing command stops the
  script before the test.
- **A `cd` persists.** A `cd` into a subdirectory makes the next relative
  `mkdir` or `git add -A` land there, and a line that both changes
  directory and writes a relative path is refused whole. Safe: run the
  `cd` as its own call, and check `pwd` before `git add`.
- **A file rewritten through Python's text mode gets CRLF on Windows.** A
  PR body written with `open(p, "w")` made a table cell end in `\r`, and a
  gate reading the cell refused it; `gh pr view` shows nothing. Safe:
  `newline="\n"`, or write bytes.
- **`git show origin/main:<path>` is mangled by Git Bash's path
  conversion**, into `origin\main;…`. Safe: read the file through
  `gh api repos/<owner>/<repo>/contents/<path>?ref=main`, or from a
  checkout of `main`.
- **A new file named `README.md` can be refused** by a directory deny no
  listed rule names, while a sibling file writes. Safe: name the file for
  what it is and say so in the PR.

## Git and worktrees

- **`EnterWorktree` outside `.claude/worktrees/` prompts**, and no
  permission rule suppresses it, so an unattended chain waits on a human.
  Safe: worktrees live under `.claude/worktrees/<slug>`, which `/branch`
  forks.
- **A worktree session refuses git it cannot follow.** Inside an
  `EnterWorktree` session, Claude Code's own isolation check refuses a
  Bash command whose git use it cannot prove stays in the worktree: a
  loop running a helper, `cd x && git …`, a `$(git …)` inside a loop,
  a variable set in a compound, even a path naming `.github/`. Symptom:
  *too complex to verify that it stays inside the worktree*. Safe: split
  the line into plain separate commands, or put a loop in a scratchpad
  script and run it by path; never obfuscate the path it refused.
- **`CLAUDE_PROJECT_DIR` is empty in the Bash tool.** Hooks get it, a
  session's own commands do not, so a fix that anchors a typed command to
  it cannot work. Safe: check `echo "$CLAUDE_PROJECT_DIR"` before proposing
  one; put the anchoring in a hook.
- **Hunks of one file are hard to split across commits**: `git add -p` is
  interactive, and something may refuse `git apply --cached` (a target's
  own deny, or the auto-mode classifier). Safe: group commits by file and
  say in the body which change rides along, or write the file down to the
  hunks the first commit wants, `git add` and commit it, then restore the
  rest.
- **A subagent cannot ship from a worktree.** Its working directory is
  pinned to its parent's, so its `EnterWorktree` is refused and the
  edit-target guard refuses its edits in a worktree it is not standing
  in. Safe: one session per worktree, launched with that worktree as its
  working directory, or ship sequentially.
- **A worktree removal on Windows can unregister and fail to delete.**
  `git worktree remove` exits non-zero with *Permission denied* when a
  process, the session's own included, holds the directory; git has
  already deleted the files and the registration. `git-worktree-remove.sh`
  reads that for you: it tries `rmdir` on what is left and exits 0 when
  that empties the path, and exits 4, naming the directory, when something
  is still in it or a process still holds it. Safe: remove last, from the
  main checkout, without `-f`, and trust the helper's exit, checking with
  `git worktree list` and `ls` after a raw `git worktree remove`, whose
  exit says nothing. A folder an exit 4 names is removed from a fresh
  terminal after the holder exits; a `!` line runs inside the session's
  own process tree and fails *busy*.

## Machine and network

- **`localhost` costs two seconds on Windows.** A request to `localhost`
  tries `::1` first and falls back after a timeout when the port is
  published on `127.0.0.1` only. Symptom: a loop over a local HTTP API
  crawls, while CI on Linux does not. Safe: `127.0.0.1`, or one reused
  connection.
- **`github.com:443` can drop while `api.github.com` stays up.** `gh` works
  and `git push` hangs about 21 seconds and exits 128 with *Failed to
  connect*. It reads like an auth problem and is not. Safe: probe
  `curl --max-time 20 https://github.com` and `https://api.github.com`;
  a failed push is a stop, with the commits safe on the local branch,
  never a cost to record.
- **`TaskStop` leaves grandchildren.** On Windows Git Bash it stops the
  outer shell, not the scripts that shell started, so a stopped series
  starts its next run beside the restarted one. Safe: after `TaskStop`
  on anything that spawns runs, list the leftover `bash` and native
  processes and kill them before starting anything new.
- **A queued runner is not a hung one.** A per-user harness runner queues
  behind other runs; CI's own jobs are then the practical check, and the
  PR says the local run was not completed.

## Auto mode and probes

- **The classifier refuses a probe of a harness boundary**, as
  self-modification or a bypass, since it cannot tell a measurement from
  an attempt. Safe: a runtime measurement of a deny list or hook runs
  outside auto mode, at the owner's word; never reword the prompt.
- **A probe that executes bypass candidates needs a dead remote.** A shim
  on `PATH` can silently fail to apply, and the payload under test is a
  push the guard exists to stop. Safe: run every probe with its working
  directory in the scratchpad, in a fresh repository whose only remote is
  a local bare one, and prove the shim fires on a harmless command first;
  afterwards check `git status` and the remote's `main`.
- **The classifier refuses some outward writes.** Closing a pull request,
  an `@dependabot ignore`, or a merge it reads as unreviewed can be
  refused where a comment is allowed. Safe: write the refused action into
  a script up front and hand it over (`.harness/agent-rules.md`,
  *Handing over to the owner*). A Dependabot pull request whose config
  entry was deleted cannot be rebased: check whether `main` already
  carries the version, then close it.
