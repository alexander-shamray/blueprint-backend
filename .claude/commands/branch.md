---
description: Start a correctly named working branch — in its own sibling worktree from a clean main, in place when the tree is dirty or the parent is not writable
argument-hint: "[what the change does] — omit to derive it from the uncommitted work"
allowed-tools: Read, Grep, EnterWorktree, Bash(git status:*), Bash(git diff:*), Bash(git branch --list:*), Bash(git branch --show-current), Bash(git branch -a), Bash(git log:*), Bash(git fetch origin:*), Bash(bash .claude/scripts/git-branch-create.sh:*), Bash(bash .claude/scripts/git-worktree-fork.sh:*), Bash(bash .claude/scripts/git-switch-existing.sh:*), Bash(git rev-parse:*), Bash(git worktree list:*), Bash(ls:*)
---

Create a branch for: $ARGUMENTS — if empty, derive it from the uncommitted
work.

`main` is not a working branch. If work has already started on it, this command
is how it gets moved off — and uncommitted changes follow a `checkout -b`, so
nothing is lost and nothing needs stashing. That carry is also one of the two
cases that keep the branch in this checkout; otherwise the branch gets a
worktree of its own, which is the next section.

## A branch is a workspace, not a HEAD

**A new branch gets its own worktree by default, and the session moves into
it** — the exceptions are named in step 1 and step 5, and both branch in
place. One
checkout switching between branches is one directory whose contents mean
something different depending on state nobody can see from the files — and this
repo's chain leans on the working tree hard enough for that to matter:
`grok-review.sh` refuses a dirty tree, `suggestions.md` sits untracked at the
root as a review's working state, and `/commit`'s unscoped form sweeps
untracked files. Each of those is a rule about *the* tree, and each of them
gets safer when a PR owns one.

Worktrees are siblings of this checkout, never children of it:

```
C:/dev/ashamray                     main — stays clean, stays put
C:/dev/ashamray-groklimit           feat/grok-usage-limit-guard
C:/dev/ashamray-masstransit         feat(template)/masstransit-registration
```

`../<checkout-name>-<slug>` is the shape. **Outside the repository tree is the
load-bearing half**, not the naming: a worktree under `.claude/worktrees/` would
sit inside the checkout, show up as untracked in every `git status` the chain
reads, and put `grok-review.sh`'s clean-tree refusal in its blast radius.
Nothing has to be added to `.gitignore` for a sibling, because there is nothing
to ignore.

**The sweeps take the opposite path deliberately, and the difference is the
worktree's job.** `/security-sweep` and `/bug-sweep` each fork a *detached*
worktree under `mktemp -d` and remove it at the end; it carries no branch and
nothing returns to it, and they refuse a sibling *by name* — partly because a
root-level or container layout has no writable parent to put one in, which is a
layout they have to keep working under rather than one they require. This one
holds a branch that a PR, two review loops and a person all come back to, so it
wants a stable named directory beside the checkout rather than a temp path.
None is another's precedent — do not reconcile them by making one match.

**A writable parent is the precondition for the sibling worktree, not for this
command**, and it is the constraint the sweeps warn about: a root-level
or container layout cannot create `../<checkout-name>-<slug>` at all. `/branch`
still succeeds there — it branches in place and says so, exactly as it does on
a dirty `main`. Naming the case is what keeps it from surfacing as a raw `git`
error mid-`/ship`; step 5 has the handling.

The slug is the branch's kebab summary cut to the first word or two that name
the change — it is a directory name, not a branch name, so the `<type>/` prefix
and any parenthesised scope are dropped rather than spelled.
`feat(template)/masstransit-registration` gives `ashamray-masstransit` above
because one word is already unambiguous; take the second where it is not.
`ashamray-masstransit-registration` is fine, and `ashamray-feat(template)` is
not a path.

**A worktree carries committed files and nothing else.** Anything untracked
that a build needs would have to be copied across — today nothing is, and a
fresh worktree restores, builds and tests as it stands. Say so if that ever
stops being true rather than copying quietly.

`.claude/` is tracked, so it comes with it: the commands, the helper scripts
the review loops invoke by name, and `settings.json` with its allow and deny
rules all arrive in the new directory, and the relative paths in every
`Bash(bash .claude/scripts/…)` grant mean the same thing there as here.
`.remember/` is ignored and does not, which is correct — it is session state,
not content.

## Steps

0. **Ask whether this is already an isolated workspace**, before creating
   anything:

   ```bash
   git rev-parse --git-dir --git-common-dir --show-superproject-working-tree
   ```

   The first two differing — with no superproject, which would make it a
   submodule instead — means this session is already in a linked worktree.
   **That worktree is this change's workspace, so step 5 forks nothing** —
   a second worktree from inside the first is how a chain ends up with two
   directories and one branch's work split across them. Report its path and
   its branch.

   **What this step switches off is the `git worktree add`, and nothing else.**
   Carry on into step 1 and let it read the branch and the tree as usual: a
   linked worktree says where the session is, never that the branch under it is
   the one this change wants. Entering `/branch` from a previous PR's worktree
   is the case that matters, and stopping here would silently adopt that
   branch — where step 1's **already on a branch** case stops and asks, which
   is exactly the guard wanted.

   The branch may also still have to be *created*: a linked worktree sitting on
   `main`, or a detached one, needs one exactly as the main checkout would, and
   simply needs it here rather than in a new directory. Step 5's table says
   which half each case skips.
1. **Read the current state.** `git branch --show-current` and
   `git status --short`. Four cases, and the first question is whether that
   first command printed anything at all:
   - **On `main`, clean** — fetch, then cut from `origin/main`. Normally that
     means a worktree; where step 5's table says otherwise the base is the
     same, because the fetch is what makes it right and not the fork. This is
     the path the next four steps are written for.
   - **On `main`, dirty** — **branch in place, with no worktree**, from `HEAD`,
     carrying the changes across. Say that you did, and say that this branch
     lives in the main checkout rather than in one of its own.

     The exception is not a shortcut, and it is worth knowing why it exists: a
     worktree is a fresh checkout of committed state, so uncommitted work does
     not follow it. Moving the work across would take a stash or a patch, and
     both are refused here — stashing hides work the user can see right now,
     and a patch is lossy about untracked files and line endings in a
     repository that forces `*.cs text eol=crlf` and leaves everything else to
     the platform. The honest answer is to keep the work where it is and name
     the cost.

     A clean `main` is the normal state at the start of a PR, so this is the
     edge and not the rule. **An in-place branch forgoes the worktree for good
     — this one and step 5's alike — and re-entering `/branch` will not get one
     back:** step 1 stops on an existing branch, step 4 refuses a name already
     taken, and step 5 cuts from `origin/main`, which would not carry the
     commits anyway. The two paths differ in their reason and not in this
     consequence, which is why it is stated once, here. Attaching a worktree
     afterwards is three commands the user runs, not a mode this command has,
     and the middle one is the reason it cannot be automated from here — a
     branch cannot be checked out in two worktrees at once, so the main
     checkout has to let go of it first:

     ```bash
     git switch main
     git worktree add ../<checkout-name>-<slug> <branch>
     ```

     then `EnterWorktree` on the new path. Say that, rather than promising a
     second `/branch` run that lands somewhere else.
   - **Detached — `git branch --show-current` prints nothing.** There is no
     branch to continue and no `main` to move off, so make one here from
     `HEAD` with `bash .claude/scripts/git-branch-create.sh <name> HEAD`,
     carrying any changes, and
     say that the workspace is this directory. A sweep's worktree —
     `/security-sweep`'s or `/bug-sweep`'s — has exactly this shape, and so
     does a checkout parked on a tag or a commit; leaving
     the case unnamed would drop it through every other branch of this step.
   - **Already on a branch** — stop and say so. Report the branch, its
     upstream, its worktree if it has one and whether the tree is dirty, then
     ask whether this is a second change wanting its own branch or a
     continuation of the current one. Do not branch off a feature branch on
     your own initiative.
2. **Derive the description if none was given.** Read `git status --short` and
   then the diff itself — `git diff` and `git diff --stat`, plus the untracked
   files. The stat names files; the branch has to name the *change*, and only
   the diff says what it is.

   The type falls out of what was touched, and maps onto the same table below:

   | | |
   |---|---|
   | `docs/**` only | `docs/` |
   | `.editorconfig`, `CLAUDE.md`, `.claude/**`, CI, `Directory.*.props` | `chore/` |
   | `src/**` or `tests/**` | `feat(<scope>)/`, `fix/` or `refactor/` — the diff decides which |

   **A mixed tree takes the type of the change that carries the argument, not
   the one with the most files.** `chore/repo-guidance-and-explicit-types`
   touched `CLAUDE.md`, `.editorconfig` and nine chapters, and is `chore/`
   because the guidance change was the point and the chapter edits followed
   from it.

   Two genuinely unrelated changes cannot both be in the name. Say so, name the
   dominant one, and flag that the other may want its own branch — `/commit`
   will split them into separate commits either way, but the branch can only
   describe one of them.

   A clean tree and no argument leaves nothing to derive from. Stop and ask
   what the branch is for rather than inventing a name.
3. **Name it `<type>/<kebab-summary>`.** The type is the one the commit will
   carry, so the branch and its commits agree:

   | | |
   |---|---|
   | `docs/` | Blueprint prose, structure, cross-references |
   | `chore/` | Repo config — `.editorconfig`, `CLAUDE.md`, commands, CI |
   | `feat(<scope>)/` | Solution code that adds behaviour |
   | `fix/` | A defect in either |
   | `refactor/` | Shape change, no behaviour change |

   Established names to match: `docs/split-by-chapter`,
   `chore/repo-guidance-and-explicit-types`. Summarise the change, do not
   describe the files — `docs/sql-sample-indentation`, not `docs/update-docs`.

   **Implementing a delivery-plan PR?** Grep `appendix-c-delivery-plan.md` for
   the PR and derive the name from its title, so the branch, the commit and the
   plan all read the same. PR-01's title is
   `chore: solution structure, SDK pin, central package management, CI skeleton`
   → `chore/solution-structure`.
4. **Check the name and the directory are both free** — `git branch --list`,
   `git branch -a` and `git worktree list`. A name that already exists locally
   or on the remote means the work may already be underway; say so rather than
   picking a variant.

   **The directory check runs only on the path that uses the directory** —
   step 1's clean-`main` case in the main checkout, the one row of step 5's
   table that reaches `git worktree add`. The other three branch in place and
   never touch the sibling path, so a stranger sitting at
   `../<checkout-name>-<slug>` must not stop them: refusing to carry dirty work
   off `main` because an unrelated directory shares a two-word slug is a
   blocked command with no defect behind it.

   **On that one path the directory takes a second check, because
   `git worktree list` cannot see most of what could be in the way** — it
   reports registered worktrees and nothing else, so an ordinary file or
   directory at `../<checkout-name>-<slug>` passes it silently and only fails
   inside step 5's `git worktree add`. Look at the path itself:

   ```bash
   ls -d ../<checkout-name>-<slug>
   ```

   **Anything already there stops this command**, whatever it is — a
   registered worktree of this repository, or something unrelated. Report
   which of the two and ask.

   It is tempting to read a registered worktree as "then that is the
   workspace", and that is wrong: step 0 answers for the directory the session
   is **inside**, and this is a different directory. The slug is one or two
   words, so a previous branch can easily own it, and adopting it would either
   take an unrelated branch as this change's or hand step 5 an occupied path.
   Two directories with a claim to the same slug is a question, not a
   collision to resolve by guessing.

   **Stopping here is what keeps step 5's fallthrough honest.** That
   fallthrough reads a failed `git worktree add` as an unwritable parent and
   branches in place; an occupied path fails the same command for a completely
   different reason, and would be silently absorbed as though the layout were
   at fault. One is a case to handle, the other is a question for the user, and
   the only thing that tells them apart is having looked first.
5. **Create the workspace, then move into it.** This step has two halves, and
   three of step 1's four cases skip the first of them:

   | Step 1 said | This step does |
   |---|---|
   | On `main`, clean, in the main checkout with a writable parent | Both halves — fork the worktree, enter it |
   | On `main` clean, but **already in a linked worktree** (step 0) | `bash .claude/scripts/git-branch-create.sh <name> origin/main` here. The workspace exists; forking a second is what step 0 refused |
   | On `main` clean, parent not writable | `bash .claude/scripts/git-branch-create.sh <name> origin/main` where you are |
   | On `main` dirty, or **detached** | `bash .claude/scripts/git-branch-create.sh <name> HEAD` — the point is to carry what is in this tree |
   | Already on a branch | Nothing — step 1 stopped |

   **A skipped fork is never a skipped branch.** Every row above except the
   last ends with a branch that exists; only the first ends with a new
   directory. Reading step 0 as "step 5 is off" would leave a session in a
   linked worktree on `main` with nowhere for the change to go, which is a
   state this command must not produce.

   **Every clean-`main` row cuts from `origin/main`, not from local `main`.**
   Step 1 fetched for the reason spelled out under the fork below — local
   `main` is whatever it was when it was last pulled — and that reason does not
   weaken because the branch is being made in place. The two rows that carry
   no worktree are still starting a PR from a base, and a stale one costs the
   same there as anywhere. `--no-track` travels with `origin/main` for the same
   reason it does on the fork: it is a remote-tracking ref, so without it the
   upstream lands on `origin/main` and `/pr` never sets the right one.

   The dirty and detached rows are the exception and stay on `HEAD`, because
   the whole point of those paths is to carry the state that is already in the
   tree. A base is not what they are short of.

   From a clean `main` in the main checkout, both halves happen in one
   command:

   ```bash
   bash .claude/scripts/git-worktree-fork.sh ../<checkout-name>-<slug> <name>
   ```

   **The helper is the whole command, and it takes two arguments because
   everything else about it is fixed.** It runs
   `git worktree add --no-track -b <branch> <path> origin/main` and nothing
   else — a `Bash(git worktree add:*)` grant would also buy `-B`, which does
   not create a branch but **resets** an existing one, the operation
   `.claude/settings.json` denies as `git branch --force` and `-M`. It refuses
   a branch that already exists, which is what makes the missing `-B` harmless
   rather than merely unavailable.

   **`--no-track` inside it is load-bearing, not tidiness.** The start point is
   a remote-tracking ref, so without it git sets the new branch's upstream to
   `origin/main` — "branch '<name>' set up to track 'origin/main'". `/pr` then
   reads `git status -sb`, finds an upstream, classifies the branch as
   *tracking, ahead*, and pushes without `-u`, leaving the PR branch pointed at
   `origin/main` for every later status and resume read. With `--no-track` there
   is no upstream, `/pr` takes its **no upstream** row, and
   `git push -u origin <branch>` sets the right one.

   `origin/main` is the base rather than `HEAD`, for the reason step 1 fetched:
   local `main` here is whatever it was when it was last pulled, and
   `grok-review.sh` already carries a paragraph about a review diffed against a
   stale base. Cutting the worktree from the remote-tracking ref is where that
   is cheapest to get right.

   Then switch the session into it with **`EnterWorktree`**, passing the new
   directory as `path` — the worktree exists and `git worktree list` reports
   it, which is what that form of the tool requires. When both steps succeed,
   everything after this command — `/commit`, `/pr`, both review loops,
   `dotnet test` — runs there. The two paragraphs below are what happens when
   either does not.

   **If the parent directory is not writable, `git worktree add` fails and the
   answer is the in-place branch, not a temp path.** A root-level or container
   layout has no `..` to write into — the case the sweeps name — and
   a workspace somewhere unrelated to the checkout would be worse than none: it
   is a directory the user has to be told about and return to, where the
   in-place branch is where they already are. So report the failure and say the
   sibling could not be created.

   **Read the failure before falling back, because only one kind of failure
   means this.** The layout case has a recognisable shape — git prints
   `fatal: could not create leading directories of '<path>/.git': Permission
   denied`, a refusal to create the path at all. A ref lock it could not take,
   corrupt repository metadata, a full filesystem, a path the platform will not
   accept: each of those also fails `git worktree add`, and none of them says
   anything about the parent being unwritable. Falling back on all of them
   alike would quietly downgrade the PR to the shared checkout and **report a
   layout exception that did not happen** — a wrong reason recorded as a
   handled case, which is worse than the raw error this handling exists to
   replace.

   So: fall back only when the message establishes that the path could not be
   created for permission reasons. Anything else stops the command and is
   reported verbatim, unnamed rather than misnamed.

   **Then check whether the branch survived, because it usually does.**
   `git worktree add -b` creates the branch *before* it creates the directory,
   so a failure at the directory leaves the branch behind — against an
   unwritable parent it prints `branch '<name>' set up to track …` and then
   `fatal: could not create leading directories`, and the branch stays in
   `git branch --list`. A blind
   create there fails with *branch already exists* — `git-branch-create.sh`
   refuses it on purpose — which would turn a handled fallback into a stop.
   Both post-failure states are ordinary and each has one command:

   | After the failed fork | Take |
   |---|---|
   | `git branch --list <name>` prints it | `bash .claude/scripts/git-switch-existing.sh <name>` — it is already cut from `origin/main` and untracked, which is what the fork asked for |
   | It prints nothing | `bash .claude/scripts/git-branch-create.sh <name> origin/main` |

   **All three git operations in this command go through helpers, because a
   prefix grant buys more than its operation.** A
   `Bash(git switch:*)` grant buys the one
   operation above and also licenses `--discard-changes` and `-C` — discarding
   work and force-moving a branch, both of which `.claude/settings.json` denies
   in their other spellings. Deny rules cannot claw that back, because the
   flags **combine**: `git switch -fC <name> <start>` switches, and a
   `Bash(git switch -C:*)` rule matches none of it. That is the refspec argument
   `/pr` already makes about pushes, one command over. The helper takes one
   shape-checked argument, requires the branch to exist, and passes no flags to
   git at all.

   The other two are the same finding in different clothes:
   `git worktree add -B` resets an existing branch, and
   `git checkout -b <name> -f origin/main` is accepted with the flag *after*
   the name, discarding tracked modifications on the one path whose whole
   purpose is carrying them. So `git-worktree-fork.sh` and
   `git-branch-create.sh` fix their commands the same way — every flag
   decided in the file, both arguments shape-checked, and creation only, so a
   name that already exists is refused rather than reset. `git worktree list`
   stays a raw grant because it reads and nothing else.

   The second row spells the base out for the reason the table above gives:
   this path starts from a clean `main`, so it takes the fetched
   `origin/main` rather than whatever local `main` happens to be. Either way
   the run continues on the branch, in place, exactly as the
   dirty-`main` path does. Both no-worktree states then look the
   same to everything downstream, and there is only one of them to describe —
   **including step 1's consequence, which is this path's too**: the branch
   forgoes the worktree for good, and the manual attachment stated there is the
   only route to one afterwards.

   **If the session cannot enter a worktree that was created, stop and report
   the path** — this is the other half and it fails the other way. Do
   not carry on issuing `git -C` commands against a directory the session is
   not in: the chain behind this command reads `git status`, writes
   `suggestions.md` and shells a helper that resolves paths from the working
   directory, and half of it in one tree and half in another is the failure
   this whole section exists to prevent. Leave the worktree standing — it costs
   nothing and it holds the branch.

   On the dirty-`main` path of step 1 there is no worktree, and the branch is
   made in place with
   `bash .claude/scripts/git-branch-create.sh <name> <base>`.

   No upstream is set either way — `/pr` does that on the first push.

## Report

The branch created, what it was cut from, **the worktree it lives in and
whether the session is now inside it**, and — if the tree was dirty — the files
carried across, so the user can confirm they belong on this branch and not the
next one.

The workspace gets its own line whether or not one was created, and a branch
made in place says so in the same breath as why. A reader who cannot tell which
directory the next command will run in has to go and look, and the whole point
of moving the session is that nobody should have to.

**Nothing here removes a worktree.** The branch has a PR to open and two review
loops to survive, so the directory outlives this command by design; whether it
is kept or removed afterwards is the user's call, made with `git worktree
remove` once the PR has landed.

**A derived name is a guess, so show your work.** State that no description was
passed, give the one-line reading of the diff the name came from, and say the
name is still free to change: no upstream exists until `/pr` pushes, so
`git branch -m <better-name>` costs nothing until then.
