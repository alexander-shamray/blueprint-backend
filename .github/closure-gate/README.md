# The closure gate

**The claim: what a pull request says it closes is what merging it will
close.** A pull request says so in three places, and nothing but this gate
compares them:

1. the `| Closes |` row of the house body form, which GitHub's linker does
   not read, because a table pipe leaves it no keyword-reference pair;
2. `closingIssuesReferences`, GitHub's own parse of the description;
3. a closing keyword in a commit message, subject or body, honoured on merge
   whatever the description says, and not editable after the push.

## What it compares

**Two comparisons, not three.** What the merge will do — GitHub's parse
together with the commit keywords — must match what the pull request says,
in the table and in the description, in both directions. An issue the
description closes and no commit repeats is the ordinary case, not a
disagreement: comparing that direction would make a commit keyword
mandatory, which no rule here states. A test pins that absence, because the
symmetry argument is what produces the missing comparison.

## Where it fails open, and what closes it

Half of what is compared is GitHub's parse and half is this gate's regex, so
a regex that matches too little is the fail-open direction: a keyword it
drops from the commits, and that the description also omits, is missing
from both sides and the comparison agrees. The parser is therefore literal —
it matches inside backticks and quoted prose, as GitHub's linker does — and
a keyword-shaped token it cannot resolve is a finding rather than a skip.
The suite is mostly that parser.

A commit list at or above `gh`'s page size is **refused** rather than judged,
because a prefix of one page and a complete list of one page read alike.
`closingIssuesReferences` needs no such guard: `gh` preloads it through
every page. A cross-repository reference is refused by name, because every
issue this repository tracks lives in it.

## What it reads

One JSON document on stdin — the pull request's number, URL, body, commits,
closing references and head, each one of `REQUIRED_FIELDS`; the repository
is read from the URL — fetched by the workflow, so the deciding is
testable without a network. `docs/testing.md` has the live invocation.

## How it runs

[`closure-gate.yml`](../workflows/closure-gate.yml), on every pull request
with no path filter, and on `edited` as well as pushes, because a
description edit with no push behind it can break the claim. **The gate that
judges is read out of the base commit**, while the suite runs the branch's
copy, so a pull request cannot supply its own judge; a base carrying no gate
fails rather than falling back. The workflow file itself is still the
branch's copy, because `pull_request` runs the head definition, and closing
that needs a required status check on `main`.
