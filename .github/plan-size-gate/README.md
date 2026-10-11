# The plan-size gate

**The claim: every plan file under `docs/superpowers/plans/` dated on or
after the day [`CLAUDE.md`](../../CLAUDE.md)'s *Plans* rule landed is within
that rule's size.** The rule owns the limit and the gate reads it as 50 KB of
1024 bytes; the day the rule landed is `RULE_LANDED` in `plan_size_gate.py`.

## What it reads

- Every file under the plans directory, recursively, dated by the
  `YYYY-MM-DD-` prefix of the first component of its path below it. A file
  with no such prefix, or an impossible date, fails: it cannot be placed
  before or after the rule, and passing it unjudged is the silent pass this
  repository's gates refuse.
- A missing or empty directory fails, so a gate that read nothing never
  reports a clean run.

## What it does not claim

- A plan dated before the rule is not judged: 20 of the 21 written in the
  fortnight before it are over the limit and stay as they are.
- The date is the one in the file's name, not git's, so a plan can be
  back-dated past the gate by whoever names it.
- It holds the size and nothing of the one-pass brief that
  `CLAUDE.md`'s *Subagents, review rounds and plans* gives the plan-writing
  agent.

## How it runs

Its suite, then the gate, in the fast job of [`ci.yml`](../workflows/ci.yml).
Locally:

```bash
py -3.12 -m unittest discover -s .github/plan-size-gate
py -3.12 .github/plan-size-gate/plan_size_gate.py
```
