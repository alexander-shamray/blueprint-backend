---
description: Start a correctly named working branch — in its own worktree under .claude/worktrees/ from a clean main, in place when the tree is dirty or that directory is not writable
argument-hint: "[what the change does] — omit to derive it from the uncommitted work"
allowed-tools: Read, Grep, EnterWorktree, Bash(git status:*), Bash(git diff:*), Bash(git branch --list:*), Bash(git branch --show-current), Bash(git branch -a), Bash(git log:*), Bash(git fetch origin:*), Bash(bash .claude/scripts/git-branch-create.sh:*), Bash(bash .claude/scripts/git-worktree-fork.sh:*), Bash(bash .claude/scripts/git-switch-existing.sh:*), Bash(git rev-parse:*), Bash(git worktree list:*), Bash(ls:*)
---

Create a branch for: $ARGUMENTS — if empty, derive it from the uncommitted
work. `main` is not a working branch; this command moves work off it, and
uncommitted changes follow a `checkout -b`.

## A branch is a workspace, not a HEAD

A new branch gets its own worktree under `.claude/worktrees/<slug>` and the
session moves into it; step 1 and step 5 name the exceptions, which branch in
place (why: docs/commands/branch.md, *A branch is a workspace, not a HEAD*).

```
C:/dev/ashamray/blueprint-backend                                main — stays clean, stays put
C:/dev/ashamray/blueprint-backend/.claude/worktrees/groklimit    feat/grok-usage-limit-guard
C:/dev/ashamray/blueprint-backend/.claude/worktrees/masstransit  feat(template)/masstransit-registration
```

Never put one elsewhere, sibling or temp path included; `.gitignore`
carries `.claude/worktrees/`. The slug is the summary's first word or two,
without `<type>/` or scope: `masstransit` above. Never copy untracked files
in quietly; say so if a build needs one. Never make this layout match the
sweeps' `mktemp -d` ones.

## Steps

0. **Ask whether this is already an isolated workspace**, before creating
   anything:

   ```bash
   git rev-parse --git-dir --git-common-dir --show-superproject-working-tree
   ```

   The first two differing, with no superproject, means a linked worktree:
   report its path and branch; step 5 forks nothing, but go on to step 1
   (why: docs/commands/branch.md, *Step 0*).
1. **Read the current state.** `git branch --show-current` and
   `git status --short`. Four cases:
   - **On `main`, clean** — fetch, then cut from `origin/main`, normally as a
     worktree.
   - **On `main`, dirty** — **branch in place, with no worktree**, from
     `HEAD`, carrying the changes; never stash or patch them. Say so, and
     that it lives in the main checkout. An in-place branch (here or step 5)
     forgoes the worktree for good; give the user the manual route, never a
     second `/branch` (why: docs/commands/branch.md, *Step 1*):

     ```bash
     git switch main
     git worktree add .claude/worktrees/<slug> <branch>
     ```

     then `EnterWorktree` on the new path.
   - **Detached — `git branch --show-current` prints nothing.** Make one here
     with `bash .claude/scripts/git-branch-create.sh <name> HEAD`, carrying
     any changes, and say that the workspace is this directory.
   - **Already on a branch** — stop and say so. Report the branch, its
     upstream, its worktree if any and whether the tree is dirty, then ask
     whether this is a second change or a continuation. Never branch off a
     feature branch unasked.
2. **Derive the description if none was given.** Read `git status --short`,
   `git diff`, `git diff --stat` and the untracked files; name the *change*
   from the diff, not the files.

   | | |
   |---|---|
   | `docs/**` only | `docs/` |
   | `.editorconfig`, `CLAUDE.md`, `.claude/**`, CI, `Directory.*.props` | `chore/` |
   | `src/**` or `tests/**` | `feat(<scope>)/`, `fix/` or `refactor/` — the diff decides which |

   A mixed tree takes the type of the change carrying the argument. Two
   unrelated changes: say so, name the dominant one, and flag that the other
   may want its own branch. A clean tree and no argument: stop and ask;
   never invent a name (why: docs/commands/branch.md, *Step 2*).
3. **Name it `<type>/<kebab-summary>`**, with the type the commit will carry:

   | | |
   |---|---|
   | `docs/` | Blueprint prose, structure, cross-references |
   | `chore/` | Repo config — `.editorconfig`, `CLAUDE.md`, commands, CI |
   | `feat(<scope>)/` | Solution code that adds behaviour |
   | `fix/` | A defect in either |
   | `refactor/` | Shape change, no behaviour change |

   Match `docs/split-by-chapter`, `chore/repo-guidance-and-explicit-types`.
   Summarise the change: `docs/sql-sample-indentation`, not `docs/update-docs`.
   For a delivery-plan PR, derive it from the title in
   `appendix-c-delivery-plan.md`: PR-01's
   `chore: solution structure, SDK pin, central package management, CI skeleton`
   → `chore/solution-structure` (why: docs/commands/branch.md, *Step 3*).
4. **Check the name and the directory are both free** — `git branch --list`,
   `git branch -a` and `git worktree list`. A name taken locally or on the
   remote: say so rather than picking a variant.

   Only on the path that reaches `git worktree add` (step 5's first row),
   also look at the path itself; never let it stop the in-place paths:

   ```bash
   ls -d .claude/worktrees/<slug>
   ```

   **Anything already there stops this command**; report whether it is a
   registered worktree and ask, never adopt it
   (why: docs/commands/branch.md, *Step 4*).
5. **Create the workspace, then move into it.**

   | Step 1 said | This step does |
   |---|---|
   | On `main`, clean, in the main checkout with a writable `.claude/worktrees/` | Both halves — fork the worktree, enter it |
   | On `main` clean, but **already in a linked worktree** (step 0) | `bash .claude/scripts/git-branch-create.sh <name> origin/main` here. The workspace exists; forking a second is what step 0 refused |
   | On `main` clean, `.claude/worktrees/` not writable | `bash .claude/scripts/git-branch-create.sh <name> origin/main` where you are |
   | On `main` dirty, or **detached** | `bash .claude/scripts/git-branch-create.sh <name> HEAD` — the point is to carry what is in this tree |
   | Already on a branch | Nothing — step 1 stopped |

   A skipped fork is never a skipped branch; every clean-`main` row cuts from
   `origin/main`, never local `main` (why: docs/commands/branch.md,
   *Step 5*). From a clean `main` in the main checkout:

   ```bash
   bash .claude/scripts/git-worktree-fork.sh .claude/worktrees/<slug> <name>
   ```

   Whichever row ran, before any `EnterWorktree`, add the change to *In
   progress* in the main checkout's `TODO.md` (`#n`, what, branch and
   worktree, `started`); from a linked worktree, carry it in the report
   as owed (why: docs/commands/branch.md, *Step 5*).

   Then **`EnterWorktree`** with the new directory as `path`. Git writes go
   through the helpers only (why: `docs/harness-boundaries.md`, *Prefix
   grants and wildcard denies*).

   **If the fork fails, read the failure first.** Fall back in place only
   when it establishes that the path could not be created for permission
   reasons, as in `fatal: could not create leading directories of
   '<path>/.git': Permission denied`; report it and that the worktree could
   not be created. Any other failure stops the command, reported verbatim.
   The branch usually survives:

   | After the failed fork | Take |
   |---|---|
   | `git branch --list <name>` prints it | `bash .claude/scripts/git-switch-existing.sh <name>` — it is already cut from `origin/main` and untracked, which is what the fork asked for |
   | It prints nothing | `bash .claude/scripts/git-branch-create.sh <name> origin/main` |

   Continue in place; step 1's forgone-worktree rule applies.

   **If the session cannot enter a worktree that was created, stop and report
   the path**; never drive it with `git -C`, and leave it standing.

   No upstream is set either way — `/pr` does that on the first push.

## Report

The branch, its base, **the worktree it lives in and whether the session is
now inside it** (own line; in place says why), and any dirty files carried.
**Nothing here removes a worktree**: keeping or removing it after the PR
lands is the user's call (`git worktree remove`). **A derived name is a
guess, so show your work**: say no description was passed, give the diff
reading, and that `git branch -m <better-name>` is free until `/pr` pushes.
