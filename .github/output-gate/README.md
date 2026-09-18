# The output gate

**The claim: no `bin/` or `obj/` exists under `src/` or `tests/`, and every
project's output is under `artifacts/` instead.** It checks
[§4.1](../../docs/backend-architecture/04-solution-structure.md)'s rule that
those trees hold source and nothing a build wrote. `Directory.Build.props`
makes the rule true, and its `Output` comment argues how; this gate keeps the
outcome true and carries none of that reasoning.

## What it reads

- The directory trees under `src/` and `tests/`, for a `bin/` or `obj/`
  anywhere beneath either.
- `artifacts/obj/` and `artifacts/bin/`, where every project must be found.
  The negative half alone is weak — a checkout nobody has touched has no
  `obj/` under `src/` either — so the gate proves a restore and a build both
  ran: a restore alone creates every `obj` entry and no `bin` entry.
- `Platform.slnx`, reconciled in both directions with the projects found on
  disk, because a walk that finds no project satisfies every assertion above.
  They are reconciled by path, and a duplicate `.csproj` stem is refused
  before anything is looked up by name, because `UseArtifactsOutput` keys a
  project's output on the stem and two projects sharing one share an entry.

## What it does not claim

§4.1's rule is wider than `bin/` and `obj/`: nothing a build wrote. Checking
that needs a before to compare against, so it is not this gate's. CI has one
— the checkout — and a step beside this one asks git instead; there is no
local equivalent, because run on a working tree it would report whatever is
in flight.

## How it runs

Its suite runs in the fast job of [`ci.yml`](../workflows/ci.yml) and the
gate runs behind the solution build — not behind `scaffold-build`'s, which
compiles a rendered service and takes no gate. The split is one the gate
cannot avoid, because it reads what a build left. Run on its own it needs a
restore and a build in front of it; `docs/testing.md` has the commands.
