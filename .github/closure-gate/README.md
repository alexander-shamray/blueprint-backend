# The closure gate

**The claim: what a pull request says it closes is what merging it will
close.** A pull request says so in three places, and nothing but this gate
compares them:

1. the `| Closes |` row of the house body form, which GitHub's linker does
   not read, because a table pipe leaves it no keyword-reference pair;
2. `closingIssuesReferences`, GitHub's own parse of the description;
3. a closing keyword in a commit message, subject or body, honoured on merge
   whatever the description says, and not editable once merged.

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
issue this repository tracks lives in it, and so is a
`closingIssuesReferences` entry whose `url` names another one, since its
number would otherwise cover a local issue with the same number. `GH-n` is
read as `#n`.

## The Dependabot description

Dependabot copies the upstream release notes into its description, inside
`<details>`, and a note there reads "Fixes" and an upstream issue number,
which Dependabot breaks with a zero-width space after the hash. That leaves a
keyword-shaped token naming no issue here, so the gate would refuse the pull
request, and Dependabot writes the description again on every update, so a
hand edit does not last. The description's `<details>` blocks are therefore
not read for unresolvable keywords, and only when `author` is the Dependabot
bot. Commits and every other author are read whole, because the exemption is
for text this repository did not write. `closingIssuesReferences` is still
compared in full, so a keyword GitHub links in this repository is still
caught, bar the foreign entries excused below.

Upstream notes may also link their closures as full issue URLs
(`closes [#3227](https://github.com/o/r/issues/3227)`): GitHub still parses
the keyword and the URL and reports `o/r#3227` in `closingIssuesReferences`.
For the Dependabot bot alone, a foreign entry whose issue URL appears inside
the `<details>` blocks and nowhere outside them is excused. A foreign entry
from any other author, or whose URL is also written outside the blocks, is
still refused.

## What it reads

One JSON document on stdin — the pull request's number, URL, body, commits,
closing references and head, each one of `REQUIRED_FIELDS`, and its `author`,
which is optional and read only for the exemption above; the repository
is read from the URL — fetched by the workflow, so the deciding is
testable without a network. The live invocation is in
[`gates.yml`](../workflows/gates.yml) and in `/pr`.

## How it runs

The `gates` job in [`gates.yml`](../workflows/gates.yml), which runs every
gate of the pull request in one job, while the `closure-gate` leg of its
`report` job, which checks nothing out, concludes as this gate did and is the
`closure-gate` check, on every pull request with no path filter, and on
`edited` as well as pushes, because a description edit with no push behind
it can break the claim. **The gate that judges is read out of the base
commit**, while the suite runs the branch's copy, so a pull request cannot
supply its own judge; a base carrying no gate fails rather than falling back.
The suite runs only after every verdict is fixed, so head test code cannot
alter the run ahead of it; a red suite fails the `gates` job rather than the
`closure-gate` check. The workflow file itself is still the
branch's copy, because `pull_request` runs the head definition, so a required
check holds against accident only and the owner's review bounds an edit made
on purpose (`docs/harness-boundaries.md`, *Managed files*).
