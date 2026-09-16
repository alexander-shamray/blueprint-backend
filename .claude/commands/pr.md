---
description: Open a pull request with a body in the house form
argument-hint: "[title — omit to derive it from the commits]"
allowed-tools: Read, Grep, Glob, Write, Bash(git status:*), Bash(git log:*), Bash(git diff:*), Bash(git branch --list:*), Bash(git branch --show-current), Bash(git branch -a), Bash(git push -u origin:*), Bash(git push origin:*), Bash(gh pr create:*), Bash(bash .claude/scripts/pr-closure-input.sh:*), Bash(bash .claude/scripts/pr-for-branch.sh:*)
---

Open a PR for the current branch. Title: $1 — if empty, derive it from the
commits.

## Push the branch first

Read `git status -sb` and do the least that is needed:

| State | Action |
|---|---|
| No upstream | `git push -u origin <branch>` |
| Tracking, ahead | `git push origin <branch>` |
| In sync | Nothing |

Name the remote and the branch in both cases. A bare `git push` relies on the
branch's tracking config to say where it goes, and it matches neither allow
rule in `.claude/settings.json` — so it prompts, which is the one thing an
unattended chain must not do.

Say which of the three it was. A push is the first thing in this command that
another person can see, so it is worth one line of report rather than
happening silently.

**Two kinds of push are denied in `.claude/settings.json`, and neither is a
step to work around:** rewriting history (`--force`, `-f`, `--force-with-lease`,
`--delete`) and anything landing on `main`. A branch wanting either is raising
a decision — stop and say so. Do not reach for `gh pr create`'s own offer to
push either: it is the same action by a route that skips the upstream check
above, so it pushes without ever reporting that it did.

**The deny list is defence in depth, not the guard.** It matches on command
prefixes, and a refspec has more spellings than a prefix list can hold —
`origin main`, `origin HEAD:main` and `origin <branch>:main` are three ways to
say one thing, and only the first two are enumerated. What actually keeps this
safe is the rule above: push the **current branch, by name**, and nothing else.
A push whose destination is not the branch you are on is not this step.

## Title

One line, semantic prefix, the same form as the commits. It names the change,
not the file count. Where the branch implements a delivery-plan PR, use
Appendix C's title verbatim.

A branch carrying several related commits takes a title that covers them
honestly — `chore: Claude Code guidance, explicit types in samples, and the
licence-register gaps it surfaced` names the third thing rather than hiding it
under the first two.

## Body

Wrapped at 80 columns, British spelling, no emoji. Structure:

1. **An opening sentence or two** saying what the branch is and how it is
   organised — `Five commits, each self-contained.`
2. **`## What changed`**, then one block per commit, each led by a bold line
   naming that commit:

   ```markdown
   **`docs:` explicit types for locals**

   211 `var` declarations become 27. A reader of a fenced code block has no
   hover and no go-to-definition…
   ```

   The block argues the change the way the commit body does; it does not
   summarise the diff. Reuse the commit bodies where they already say it well.
3. **The honest cost**, wherever there is one. A PR that says what it
   deliberately did *not* do — and why — is the one a reviewer can trust.
   Name anything left undecided, and anything a later change will have to
   revisit.
4. **Tables** for summary data. The two-column borderless form (`| | |`) is the
   established shape for metadata.
5. The `🤖 Generated with [Claude Code]` footer and session link.

### The class and the touch set

The metadata table opens with two rows. The class and the touch set are
decided before the first edit — in the issue, or in the first commit's body
when there is none, per `docs/change-locality.md` §5 — and copied here when
the PR opens, with any file added mid-work and the reason it was:

```markdown
| | |
|---|---|
| Class | A |
| Touch set | `src/Services/Ordering/**`, `tests/Ordering.*` |
```

`Class` is one letter from that file's table — A local, B shared mechanism,
C a rule moved, D docs or harness, E the dependency graph — or two joined
with `+`, as `C+E`, for a change the contract's §3 says needs both, with the
touch set as the union. `Touch set` is the
paths that class allows and this branch actually edited, as globs or files on
one line. A file in the diff outside the set is a finding for
`/review-branch`, and the answer is a narrower diff, a row widened with its
reason beside it, or a different class — never a row widened silently.

### What the branch closes

**A closing keyword inside a table cell links nothing.** The metadata row —
`| Closes | #88 (high), #81 (high) |` — is a summary for a reader and that is
all it is: a cell boundary sits between `Closes` and `#88`, so GitHub is never
handed a keyword-reference pair, and `closingIssuesReferences` for a body
whose only keywords sit in that row reads `[]`.

So the body carries **both**: the row as the human-readable summary, and a
bare `Closes #n` line for each issue below it.

**A `Closes #n` in a commit body fires on merge whatever the description
says.** That is the opposite failure: a description rewritten to say an issue
stays open does not stop a commit written earlier from closing it.

**The commits are the half that cannot be taken back, so the description is
reconciled to them** — never the other way round. A description is editable
and a commit message is not, which is why withdrawing a closure from the body
reads as sufficient and is not. If a commit already closes an issue the branch
has since decided to leave open, name it in the description, let it close, and
reopen it with the reason on the issue itself.

**A keyword the body only *discusses* still links.** GitHub's linker does not
read markdown, so a `` `Closes #30` `` quoted inside an argument about closures
closes #30 — which makes this section's own examples a hazard for any PR that
edits it. Write the number away from the keyword when the point is the keyword.

`.github/closure-gate/` compares what this body *says* against what the merge
*will do*, on every push **and on every description edit**, so this is checked
rather than remembered. It does **not** ask a commit to repeat a closure the
description makes — a bare `Closes` line under the table is enough on its own.

Run it yourself as soon as the pull request exists, if you want the answer
before the workflow reports it — not before opening:
`pr-closure-input.sh` needs a pull request to read, and
`closingIssuesReferences` is GitHub's parse of a body it has not been given
yet, so there is nothing to ask about until the PR is open:

```bash
bash .claude/scripts/pr-closure-input.sh <n> |
    py -3.12 .github/closure-gate/closure_gate.py
```

Do not include a test-plan section while the repo is in its documentation
phase — there is nothing to run. Once `Platform.slnx` exists, state the
`dotnet build` / `dotnet test` result instead, and report it as it actually
came out.

## Before opening

- `bash .claude/scripts/pr-for-branch.sh` — an OPEN row means you are
  updating, not creating. Say so and stop.
- Confirm `/validate-blueprint` and `/check-links` have run over the change, or
  say plainly that they have not.

## Steps

Write the body to a scratchpad file and pass it with `--body-file`; heredocs
through `gh` mangle the wrapping. Then:

```bash
gh pr create --base main --title "<title>" --body-file <path>
```

## Report

The PR URL, its title, and the commits it carries. If any check you named above
was skipped, say which.
