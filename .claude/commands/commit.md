---
description: Split the working tree into semantic commits with bodies that argue the change
argument-hint: "[what to commit — omit for everything staged and unstaged]"
allowed-tools: Read, Grep, Glob, Bash(git status:*), Bash(git diff:*), Bash(git log:*), Bash(git branch --list:*), Bash(git branch --show-current), Bash(git branch -a), Bash(git add:*), Bash(git commit:*), Bash(bash .claude/scripts/git-unstage.sh:*), Bash(wc:*)
---

Commit the working tree. Scope: $1 — if empty, everything modified and
untracked.

## Before anything

**Check the branch.** `git branch --show-current`. If it is `main`, stop and run
`/branch` first — nothing lands on `main` directly. Carry on only once the
work is on its own branch.

**Never touch `.remember/`.** It is session state, denied in
`.claude/settings.json`, and must not appear in a commit.

## Split before you write

Read the full diff, then group the hunks into commits that each stand alone.
The test is whether a reviewer could accept one and reject the next: if
reverting commit 3 would break commit 2, they are one commit.

The splits this repo has actually wanted:

- **Config apart from content.** `.editorconfig` and `CLAUDE.md` are one
  change; the chapters they govern are another.
- **One mechanical sweep per commit.** A corpus-wide reformat is its own
  commit — mixing it with a substantive edit makes the substantive edit
  invisible in the diff.
- **Findings apart from the work that surfaced them.** A licence-register gap
  found while doing something else is its own commit, so its rationale survives
  in `git log` rather than being buried in an unrelated body.

Do not manufacture splits. Three commits that each say something beat six that
divide one idea.

## Message form

```
<type>(<scope>): <what changed, and where>

<Body. Why, not what — the diff already says what. Wrapped at 80 columns,
British spelling in prose, identifiers left alone.>
```

- Types are `docs:`, `feat(<scope>):`, `fix:`, `chore:`, `refactor:`, `test:`.
  **Implementing a delivery-plan PR? Use Appendix C's title verbatim** — it is
  already written in this form, and matching it is what lets the plan be traced
  against the log.
- **The subject names what changed and where** — a symbol, a file or a
  behaviour a reviewer could search for — lower case, with no full stop; the
  aphorism goes in the body. The primer's *Commit messages* line is the rule.
  `fix(ci): comment-gate honours a block scalar's indentation indicator` can
  be searched for; a subject that states a principle cannot.
- The body carries the argument and the honest cost. `docs: use explicit types
  for locals in the C# samples` earns its keep with *"a reader of a fenced code
  block has no hover and no go-to-definition"* — the reason, not the count.
- State the counts where they are the evidence (`211 declarations become 27`),
  and say plainly where nothing changed (`no behaviour changes; every edit is a
  declaration`).
- Tables are fine in a body when they carry the summary data.
- **A `Closes #n` in a body closes that issue on merge**, out of history rather
  than out of the pull request description — and unlike the description, a
  commit message cannot be edited afterwards. So write one only where this
  commit genuinely resolves that issue, and never as a remark *about* a
  closure: GitHub's linker does not read markdown, so a `` `Closes #30` ``
  quoted inside an argument links exactly as hard as a real one, and a
  description rewritten later does not stop it. `/pr` carries the other half of
  this rule, and `.github/closure-gate/` gates the two against each other.
- Keep the `Co-Authored-By:` and `Claude-Session:` trailers.

## Steps

1. `git status --short` and `git diff` — read all of it before staging
   anything.
2. Stage each group explicitly by path. Never `git add -A` when the tree holds
   more than one commit's worth.
3. Commit with a heredoc so the body keeps its line breaks.
4. Repeat, then `git log --oneline` the new commits to confirm the order reads
   as an argument.

## Report

The commits made, one line each, and anything left uncommitted with the reason.
Do not push. `/pr` owns that step and reports which of its three states it
found; a push issued from here reaches the remote out of a command whose report
says nothing about having done so.
