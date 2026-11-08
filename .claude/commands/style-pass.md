---
description: Apply one code-style form corpus-wide, then record it in the style guide and .editorconfig
argument-hint: "<the corrected form — paste the code as it should read>"
allowed-tools: Read, Grep, Glob, Edit, Bash(git diff:*), Bash(wc:*), Bash(ls:*)
disallowed-tools: Bash(git push:*), Edit(~/.claude/projects/**), Edit(~/.claude/output-styles/**)
---

A corrected code form has been given:

$ARGUMENTS

It is an instance of a rule, not a one-off fix. Four things follow, and a pass
that does fewer than four is not done.

## 1. Name the rule

State it in one sentence before touching anything, and say what it implies for
cases the example does not show. `query` / `.where(…)` /
`.orderBy(…)` is not "indent that chain four" — it is *a broken
fluent chain puts every call on its own line at head + 4, the first call
included*, which then decides what happens to a chain whose head is a wrapped
initialiser line.

If the rule as stated would contradict something the style guide
`docs/harness-local.md` *Documents* names, or `CLAUDE.md`, already says,
stop and say which. That is a decision about the
whole corpus and it is the user's.

## 2. Sweep the corpus

The named site is one of many. Find the rest across every fenced block under
`docs/`, the product specification among them, except the closed records
`docs/harness-local.md` *Documents* names and the paths
`.harness/manifest.json` manages, which a target's pass does not edit — and the
source too, because one dialect governs all of it. A rule applied to the source
and not to the samples leaves the samples teaching the form the pass just
removed.

Grep gets you candidates; reading them gets you the shape. Anything structural
— bracket depth, chain heads, continuation columns, raw-string boundaries —
has to track whether it is inside a fence of one language or another, a
string literal holding a second one, or prose, because the rules differ per
context, and that tracking is done by reading the file rather than by a
script this command can run unattended.

**This command holds no interpreter grant, and the absence is deliberate.**
`Bash(python:*)` would be a prefix grant on a general-purpose interpreter:
`python -c "<anything>"` would be auto-approved, and one `open(…,'w')`
reaches every path the `Edit(…)` denies in `.claude/settings.json` cover,
while one `subprocess.run` reaches every `git push --force` and `git switch -fC`
the deny list and the `git-*.sh` helpers exist to keep out. A rule that
matches `python -c` cannot see what the interpreter then does, so the whole
control structure this repository documents would sit downstream of one line of
frontmatter. That matters here more than in most commands because the input is
attacker-influenced twice over: `$ARGUMENTS` is a pasted code form, and the
sweep reads `docs/**` and `src/**`, which a PR author controls.

A throwaway script is still the right tool for a genuinely structural sweep.
Write it into the scratchpad and run it — under the default permission mode the
run prompts, and one approval per pass is the whole cost.

**The prompt is not the boundary.** Withholding the grant removes
*command-level auto-approval*; what an unlisted tool then does is the active
permission mode's decision, and under a bypassing mode it runs silently — the
same premise the sweeps state for their denies. So what the absence buys is
that the interpreter is not pre-approved by this command, not that a human
sees every run. A command
that needed the stronger property would have to refuse those modes, and none
here does.

**Confirm each hit by reading it.** The recurring false positives are real and
they repeat: `})` closing a lambda mid-chain is a continuation, not a chain
head; `(await fetch(` opens two brackets on one line legitimately;
a one-line function body is not a lambda; commas inside a generic argument
list are not list elements.

## 3. Record it in the style guide

The rule goes in the style guide's section for its language, in the
established voice: the rule, an example, and **the reason it is not
arbitrary**. State the exceptions with them — half that file's value is the
carve-outs, and a rule recorded without its exceptions gets applied to the
exceptions next time.

**Where the target's `CLAUDE.md` keeps a short list of these rules, the
guide is the master copy.** If the form you corrected is one of the ones it
names, the change has to reach both in this pass — a rule in two places that
moves in one of them is the drift the split was accepted in order to buy.

Where the change contract `docs/harness-local.md` *Documents* names forbids a
document stating a number of sites in the present tense, remove a site count
you find and write none: a count belongs in the commit or PR body, as a
measurement taken at a named commit. Where it does not, update any count the
change invalidates; a stale one is a defect.

## 4. Reconcile `.editorconfig`

**Every time. Without being asked.** It is a build input, not a
documentation convenience.

Three outcomes, and the third is as important as the others:

- **A setting expresses the rule** — set it, with a comment saying what it
  costs and which sites depend on it.
- **A setting would fight the rule** — a formatter's defaults can undo
  several of these. Pin the setting that keeps a format run idempotent.
- **No setting expresses it** — a formatter with no wrapping engine cannot
  enforce most of these rules at all. Add it to the "no setting expresses
  these" list in the file, so a reviewer does not go hunting for a key that
  does not exist. Never invent a key.

The extreme case is a language held inside another's string literals, which
no formatter reads: it gets no section of its own, and the style guide says so.

## 5. Verify

Re-scan for the invariants the corpus holds. Each is a regression a pass can
introduce. Take the limits from the style guide and `.editorconfig`, never from
this file: the width, the indent unit and the line ending are the target's.

- no line over the guide's width inside a code fence
- no leading `&&`, `||`, `??` or `=>` on a continuation line
- every continuation indent a multiple of the guide's indent unit
- no ragged list — one line, or one element per line
- fences balanced, the target's line ending intact, no trailing whitespace

## Report

The rule as stated, sites changed per file, the style guide, `CLAUDE.md`
and `.editorconfig` edits, and the invariant scan results as
numbers. Flag every judgement call where two of the rules both applied and
one had to win — do not bury it.
