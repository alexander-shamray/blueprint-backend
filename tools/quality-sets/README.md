# The quality sets

[`docs/token-plan.md`](../../docs/token-plan.md) step 1 owes two sets, because
the exits of steps 4 and 5 and the cheaper-model trial of step 2 each hold
*recall* and recall needs something to be measured against. This directory
holds them and the script that scores a run against them.

```bash
py -3.12 tools/quality-sets/quality_sets.py build-sweeps    # rewrites sweeps.json from the issue tracker
py -3.12 tools/quality-sets/quality_sets.py build-reviews   # rewrites reviews.json from the PR review comments
py -3.12 tools/quality-sets/quality_sets.py run review --pr 187 [--head <sha>]
py -3.12 tools/quality-sets/quality_sets.py run review --sample 15 --repeat 2   # heads spread over the set
py -3.12 tools/quality-sets/quality_sets.py run sweep --kind bug --commit 5bea8b26
py -3.12 tools/quality-sets/quality_sets.py score --set reviews.json --findings found.json
```

Both builds need `gh` signed in; the runs need `claude` on the path and a
checkout that holds the commits, which `git fetch` of `main` and the PR heads
gives. The suite needs neither: `cd tools/quality-sets && python -m unittest`.

## `sweeps.json`: what earlier sweeps filed

One entry per issue that carries the sweeps' trailer, so a hand filing is not
one. Each names the issue, its kind and severity from the labels, the
**commit it was found on**, and the **files** its *Affected lines* section
names that exist at that commit. The commit is the issue's pinned sha or its
*found by … at <sha>* abbreviation; an issue that names none, or one this clone
does not hold, keeps `commit: null` and is listed but never scored.

## `reviews.json`: what earlier review rounds raised

One entry per top-level inline comment by Copilot on a merged pull request: the
pull request, its base, the **head the comment was raised on** (one head is
one round), the file, the line and the first 400 characters of the text. The
external reviewers are archived (`token-plan.md` step 4), so these comments are
the whole of what they found; the local reviewer's own findings are not
recorded anywhere a set could read, because a round's JSON is not posted.

## What a score is

`run review` writes the pull request's diff at one recorded head, in a
throwaway worktree at that head, and hands it to `branch-reviewer`. `run sweep`
cuts a full first round with `sweep-slices.sh` over a throwaway worktree at the
commit and runs one auditor per slice. **Both run the checkout's own agent
definition and slicer on the older tree**, because a commit older than the agent
has no file for it and the question is whether today's reviewer would have found
what was found then.

An entry counts as **found** when a reported finding names its file, and for a
review entry also a line within ten of it. That is a proxy, not a judgement that
the two describe the same defect: a sweep entry is hit by any finding in one of
its files, so recall is an upper bound, and a finding on the right line about
something else is a hit. A run reports `reported`, `entries`, `found`, `recall`
and the `missed` ids, scored over the entries pinned to the run's commit only.

It also reports `tokens`: the input, cache-creation, cache-read and output counts
that each `claude -p` result carries, and their `total`, summed over the run, a
retried or failed slice's included. A review prints them per attempt; a sweep's
progress line gives each slice's total. A run that ends on a usage-limit notice
reports none.

Recall is noisy: the same head scored 0 of 12 and 2 of 12 on two runs
(`docs/token-usage.md`, 2026-10-11). Read a score as a sample, and run a head
more than once before comparing two models.

A slice whose auditor returns no JSON is retried once and then listed in
`failed`; a usage-limit notice ends the run unscored, since every later slice
would stop the same way and a score over the rest measures the limit.

An auditor's run is long and the sweeps' ceiling is seven rounds; `run sweep` is
round 1 only, which is what *a first sweep finds at least what the earlier
sweeps filed* asks of it.
