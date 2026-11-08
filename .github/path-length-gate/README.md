# The path-length gate

**The claim: no tracked path is longer than `LIMIT` in
`path_length_gate.py`, so a worktree under `.claude/worktrees/` can check the
tree out on Windows with an ordinary slug.** A path past Windows' limit fails
late and expensively: `git-worktree-fork.sh` stops with `Filename too long`,
after `git worktree add -b` has made a branch that `git-branch-drop.sh` then
refuses to drop, and the comment gate's `git show` fails the same way on a
checkout that does hold the file.

## The arithmetic

A checkout's absolute path has to fit `WINDOWS_LONGEST_PATH`, the longest git
wrote with `core.longpaths` unset when measured on 2026-11-08, one character
more failing with `Filename too long`. In the main checkout, the path is
`WORKTREES_DIR`, the slug, a separator and the tracked path, so `LIMIT` is what
is left for the tracked path once a slug has room. The suite's
`test_a_thirty_character_slug_fits_in_the_main_checkout` holds a 30-character
slug inside that sum.

`core.longpaths` is not the answer: it is one machine's setting, and it leaves
a tree other tools on Windows still cannot read.

## What it reads

- Every path `git ls-files` lists, so a new long name in any tree is read
  without this gate learning where. That is the index rather than the working
  tree, so a staged file counts and a file that `.gitignore` covers does not.
- A listing with nothing in it fails, and a listing git refuses raises, so a
  gate that read nothing never reports a clean tree.

## What it does not claim

- A checkout that sits deeper than `WORKTREES_DIR` has less room, and nothing
  here knows where another machine keeps its checkout. A shorter slug is that
  machine's way out.
- It measures a tracked file's path, not the directories above it, whose own
  limit on Windows is shorter, and not the generated paths a build writes under
  `bin/` or `obj/`.
- It says nothing of the file name's words: the ADR form that keeps a name
  inside the limit is `.claude/commands/new-adr.md`'s.

## How it runs

Its suite, then the gate, in the fast job of the target's CI workflow.
Locally:

```bash
py -3.12 -m unittest discover -s .github/path-length-gate
py -3.12 .github/path-length-gate/path_length_gate.py
```
