# The commit gate

**The claim: every commit a pull request carries names that pull request,
and keeps `/commit`'s form.** Pull requests land by rebase, so the commits
reach `main` exactly as pushed and no merge commit records which pull
request they belong to. The `PR: #<n>` trailer is that record.

## What it refuses

A commit in `base..head` that:

- is a merge commit;
- has a subject outside `/commit`'s form, or one starting `fixup!`,
  `squash!`, `amend!` or `wip` — a subject git wrote for a revert,
  `Revert "<subject>"`, is judged by the subject inside the quotes;
- carries no `PR: #<this pull request>` trailer, or carries one naming
  another pull request.

A range of no commits is refused rather than passed.

## Why the trailer is read by git

`git interpret-trailers --parse` is the reader, so a `PR: #12` line quoted in
the middle of a body is not a trailer. A substring match would accept it,
and `git-revert-pr.sh` would then act on a commit that does not name the
pull request.

## What it reads

`check` takes parsed commits and is pure; `collect` is the only part that
runs git. The suite tests the judgement from dictionaries and the reading
against a throwaway repository. The testing document `docs/harness-local.md`
*Documents* names has the live invocation.

## How it runs

[`commit-gate.yml`](../workflows/commit-gate.yml), on every pull request with no
path filter. Dependabot's commits carry no trailer, so the gate stands down only
where Dependabot opened the pull request and every commit's author and committer
are Dependabot's. The opener alone is not enough, and the gate judges by the
identities a commit records, not by who pushed it: a local amend or rebase
keeps Dependabot as author but records the pusher as committer. Once the gate
judges, it walks the whole range, so every commit must carry the trailer and
the form, Dependabot's included, and the commits are rewritten or the branch
replaced. **The gate that judges is read out of the base commit**, while the
suite runs the branch's copy, so a pull
request cannot supply its own judge. The pull request that introduces the gate
has no base copy and is judged by its own, with a notice; a base without the
gate and a head without it fails. The workflow file itself is the branch's copy,
as in [`closure-gate`](../closure-gate/README.md), and closing that needs the
required status check the pull request that added this gate lists as an
owner-only setting.

The other half, where a target's own CI workflow has it, is a job that
builds every commit before the head, which the build of the head already
covers.
