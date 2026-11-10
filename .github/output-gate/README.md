# The output gate

**The claim: no `bin/` or `obj/` exists under `src/`, `tests/` or `tools/`, and
every project's output is under `artifacts/` instead.** It checks the rule
that those trees hold source and nothing a build wrote, which backend's
architecture states as its §4.1 and every target the gate is delivered to
holds. A target's `Directory.Build.props` makes the rule true, and its
`Output` comment argues how; this gate keeps the outcome true and carries
none of that reasoning.

## What it reads

- The directory trees under `src/`, `tests/` and `tools/`, for a `bin/` or
  `obj/` anywhere beneath any of them.
- `artifacts/obj/` and `artifacts/bin/`, where every project must be found.
  The negative half alone is weak — a checkout nobody has touched has no
  `obj/` under `src/` either — so the gate proves a restore and a build both
  ran: a restore alone creates every `obj` entry and no `bin` entry.
- The solution, the one `.slnx` at the repository's root, reconciled in both
  directions with the projects found on disk, because a walk that finds no
  project satisfies every assertion above. It is found rather than named,
  since each target calls its solution something of its own, and none or more
  than one at the root is refused before anything else is read: either leaves
  no single inventory to reconcile against.
  They are reconciled by path, and a duplicate `.csproj` stem is refused
  before anything is looked up by name, because `UseArtifactsOutput` keys a
  project's output on the stem and two projects sharing one share an entry.

## What it does not claim

The rule is wider than `bin/` and `obj/`: nothing a build wrote. Checking
that needs a before to compare against, so it is not this gate's. CI has one
— the checkout — so a target can ask git in a step beside this one; there is
no local equivalent, because run on a working tree it would report whatever
is in flight.

## How it runs

The suite and the gate run apart, a split the gate cannot avoid, because it
reads what a build left. The suite needs no build, so a target runs it in a
CI job that builds nothing; the gate runs behind a restore and a build of the
root solution, in whichever job of the target's CI does those, and a target
that runs only the suite has the gate's own cases proved and its own tree not
yet read. `py -3.12 .github/output-gate/output_gate.py` runs the gate on the
repository it sits in, or on the one `--repo` names.

`py -3.12 -m unittest discover -s .github/output-gate` runs the suite. A tree
this gate does not walk is invisible to every test that builds its own, and
the suite builds every tree it reads, so it never reads the target's real
one; a target that wants its real repository read keeps a suite of its own
beside it, as backend does.
