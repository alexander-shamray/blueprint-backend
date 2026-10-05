# Delivery roadmap

**How long the blueprint takes to build, and what the schedule rests on.**

[Appendix C](backend-architecture/appendix-c-delivery-plan.md) sequences the
work into independently reviewable pull requests and fixes their dependencies.
It says what to build and in what order; it does not say how long. This roadmap
attaches a number to each pull request in the plan that appendix sequences, and
derives a calendar from them.

> **This roadmap is frozen.** It laid a calendar over a plan that is complete
> — every numbered row of Appendix C has landed — so no estimate here is
> revised, no row is added, and the *After the plan* rows keep the empty
> columns that section argues for. Under
> [`change-locality.md`](change-locality.md) this file is the record of what
> was forecast, kept beside what was built. Re-pricing the plan against
> delivered work, which *What these numbers are* names as the one thing that
> should move the total, would be a successor to this file and not an edit
> to it.

**The plan is not the whole of Appendix C, and the difference is the one thing
this file does not price.** The appendix has since grown an *After the plan*
section — rows for work no numbered row covered, written after the plan was
already complete — and those rows carry no estimate here. *After the plan*
below says why, and says it as a decision rather than leaving a hole.

**The count that used to open this paragraph is gone rather than corrected.**
It said 27, which is the number of rows Appendix C's own prose counts above
that section, and it was read here as a total for the appendix. A figure that
is true of one part of a document and false of the document is worse than
none. What a reader can check is that the rows below are those rows, title for
title and phase for phase, and that check needs no numeral in front of it.

It restates nothing. Every title below is Appendix C's title verbatim, every
dependency is Appendix C's dependency, and where the two disagree Appendix C is
right — this file is an estimate laid over a plan, not a second plan.

| | |
|---|---|
| **Scope** | PR-01 … PR-25 and PR-27. PR-26 is optional and conditional, and is priced separately below rather than counted. Appendix C's *After the plan* rows are outside this scope entirely and carry no estimate at all |
| **Total** | **99 ideal engineer-days** |
| **Calendar** | **29 weeks** — roughly six and a half months |
| **Critical path** | 54 ideal days, so no amount of staffing beats about 1.8× |

## What these numbers are

They were quoted before any of this code was written, against a specification
that was already finished — an unusually good position to estimate from. The
blueprint names the types, the tests, the failure modes and the migration
shapes, so most of what normally hides inside an estimate has already been
argued out in ~10,500 lines of prose — the blueprint's size when these
estimates were quoted. The figure here is deliberately the historical one,
because it is what the estimates were made against; no file carries a live
count, because [`change-locality.md`](change-locality.md) §2 forbids one, and
the measurement is a line count over the Markdown files under
`docs/backend-architecture/`, taken when it is wanted rather than recorded.

It is also the trap. A specification this precise makes an estimate feel
measured when it is still a guess, and the numbers below have the same standing
as any other pre-implementation estimate: they are an argument about relative
size that happens to be denominated in days. None has been re-priced against a
pull request that has actually landed. The ranking of the pull requests is the
durable part; the absolute total is the part that moves, and delivered work is
the only thing that should move it.

> **Estimate the specification, not the aspiration.** Each figure prices what
> Appendix C's *Delivers* column actually lists, including its tests. Where a
> pull request delivers documentation rather than code — PR-24's runbooks — it
> is priced as the writing job it is, because writing a runbook per §13.6
> condition, each checked in both directions (§13.9), is not faster than
> writing code.

## Basis

Three assumptions carry the whole schedule. Each is stated separately so it can
be argued with separately.

**Estimates are ideal engineer-days.** Uninterrupted focus on one pull request:
no meetings, no review latency, no context switching, no production
interruption. This is deliberately not a calendar figure. Mixing the two is how
an estimate stops being falsifiable — when a slipped week could mean either
"the work was larger than we thought" or "three days went to something else",
the estimate can never be shown wrong and so never improves.

**One engineer.** Chosen, not assumed. The dependency graph fans out
considerably after PR-07 — the Redis, messaging and security branches are
genuinely independent — and none of that parallelism is used here. The
*Critical path* section below prices what it would be worth.

**A solo engineer sustains about 3.5 ideal days per five-day week.** This one
ratio is everything standing between the day totals and the calendar. It is the
number to change first if the schedule looks wrong, because changing it
re-derives every date without re-estimating a single pull request. At 3.5,
99 ideal days is 29 weeks; at 3.0 it is 33; at 4.0 it is 25.

## Estimate per pull request

Phases and titles are Appendix C's. **Cum.** is the running total in ideal
engineer-days; **Week** is the calendar week that total completes in, at
3.5 days a week.

### Foundation

| PR | Title | Est. | Cum. | Week |
|---|---|---|---|---|
| **01** | `chore: solution structure, SDK pin, central package management, CI skeleton` | 3d | 3 | 1 |
| **02** | `feat(common): Result, Error, and domain primitives` | 3d | 6 | 2 |
| **03** | `feat(common): ProblemDetails, error catalogue, correlation middleware` | 2d | 8 | 3 |
| **04** | `feat(common): CQRS dispatcher and pipeline behaviours` | 4d | 12 | 4 |
| **05** | `feat(common): OpenTelemetry and structured logging defaults` | 3d | 15 | 5 |
| **06** | `feat(dev): Docker Compose — SQL Server, Redis, RabbitMQ, Keycloak, OTel` | 3d | 18 | 6 |

PR-01 is scaffolding except for the licence allow-list gate, which is a real
piece of CI work rather than a checkbox. PR-04 is the largest of the six
because the dispatcher is hand-rolled —
[ADR-004](backend-architecture/adr/ADR-004-no-mediator-library.md)
takes no mediator library — and the behaviour-ordering tests are the point of
it, not a garnish.

### Service template

| PR | Title | Est. | Cum. | Week |
|---|---|---|---|---|
| **07** | `feat(template): service skeleton and architecture test gate` | 4d | 22 | 7 |
| **08** | `feat(template): EF Core, repositories, IUnitOfWork, migrator host` | 5d | 27 | 8 |
| **09** | `feat(common): TransactionBehavior over IUnitOfWork` | 3d | 30 | 9 |
| **10** | `feat(catalog): first vertical slice — command, query, cursor pagination` | 4d | 34 | 10 |
| **11** | `feat(tooling): new-service scaffold script` | 3d | 37 | 11 |

PR-08 is the phase's largest and the estimate is mostly Testcontainers: the
first container-backed test in a repository costs a day that the fiftieth does
not. PR-09 looks small for what it proves, and it is — the behaviour is short.
Its three tests are not, particularly the one asserting that a handler which
writes through `ExecuteRawAsync` and then returns `Result.Failure` leaves no
row behind.

PR-11 has since landed, and it changes what PR-18 is an estimate *of* rather
than what it costs. The scaffold renders the wiring and none of the domain
([§4.5](backend-architecture/04-solution-structure.md)): a new service arrives
with its five projects, its migrator, its schema migration, both images, its
Compose pair and eighty-nine passing tests, and with no aggregate, no command,
no query and no endpoint. So PR-18's three days are now three days of Ordering's
*domain* — which is the shape the estimate always assumed, and was the first
thing on this page that could be checked against a delivered pull request rather
than argued about.

**PR-18 has since landed, and the check came out the way this paragraph
predicted.** Ordering was rendered by the scaffold and then given its
aggregate, with no reconciliation owed to the wiring — so the three days bought
domain work, which is what the estimate said they were for. The estimate itself
is left where it is: what a delivered PR settles here is the *shape* of the
prediction, not the schedule resting on it.

### Data, cache, messaging

| PR | Title | Est. | Cum. | Week |
|---|---|---|---|---|
| **12** | `feat(common): Redis helpers — HybridCache, key namespaces, distributed locks` | 4d | 41 | 12 |
| **13** | `feat(template): MassTransit RabbitMQ registration and harness smoke` | 2d | 43 | 13 |
| **14** | `feat(template): transactional outbox and allow-list event mapper` | 6d | 49 | 14 |
| **15** | `feat(messaging): Contracts, inbox consumers, inbox + outbox retention purge` | 5d | 54 | 16 |

**PR-14 is the heaviest pull request in the plan, tied with PR-21 and PR-24**,
and the 2d/6d split
between it and PR-13 is why Appendix C separates them. Getting a bus connection
working is an afternoon; getting an outbox transactionally correct, with
`MessageTypeMap` and `OutboxJson` both landing before any row exists in the
column they define, is most of a working week — and the integration tests
proving the aggregate row and the outbox row commit in one transaction are half
of that again.

### Edge and security

| PR | Title | Est. | Cum. | Week |
|---|---|---|---|---|
| **16** | `feat(security): JWT bearer with mandatory per-service re-validation` | 4d | 58 | 17 |
| **17** | `feat(gateway): YARP routing, JWT, rate limiting, CORS` | 4d | 62 | 18 |
| **18** | `feat(ordering): second service from the scaffold` | 3d | 65 | 19 |
| **27** | `feat(gateway): response compression and request size limits` | 1d | 66 | 19 |

**PR-27 sits in this phase because Appendix C puts it here**, and for one
round it did not: the row was filed under *Integration and operations* while
this file said in prose that it belonged to no phase at all. Appendix C is
where a phase is decided and this file restates it, so a disagreement between
them is this file's defect however reasonable the prose sounded. It is gateway
work depending on PR-17 alone, which is what *Edge and security* means.

Its day was the one on this page that is mostly not code, and it held. The two
capabilities are four lines together, and the day priced the decisions in
front of them: a body-size limit needed a number nobody had chosen — one
mebibyte — and turning compression on at the edge needed the `EnableForHttps`
argument made where an ADR could hold it, which is
[ADR-020](backend-architecture/adr/ADR-020-the-edge-compresses-over-tls-and-says-so.md).
**A day is what an argument costs when the code is already obvious**, and
pricing it at zero because the diff is small is how design decisions end up
taken by whoever types first.

What the day did not price, and what a later small-diff PR should, is the
**tests**: the four lines needed thirteen, a whole suite of which cannot run on
`TestServer` at all, and finding that out is most of what the day actually
bought. An
estimate derived from the diff is wrong in the same direction each time — and
six of those thirteen exist only because a review found something wrong: two
where the *argument* was wrong and the code was right, and four where the code
was, which is the half of a small PR an estimate is least equipped to see. The
line count moving from three to four says the same thing about the code: the
reviews turned up a specification the edge was violating, and a conformance fix
is not something a diff-shaped estimate anticipates either.

PR-18 is the cheapest service in the plan and that is the whole point of it —
it is priced at three days *because* PR-11 exists, and if it turned out to cost
more, the finding would be about the scaffold rather than about Ordering. **Both
have now landed, and the finding was not against the scaffold**: Ordering is the
scaffold's own output plus one domain, with nothing about the wiring
hand-written. With PR-11
landed, that test was sharp: the scaffold's output is a service that builds
and passes its tests with no domain in it, so the three days buy Ordering's
aggregate, its first command and its endpoint. They no longer buy a gateway
route: PR-17 shipped §10.2's file whole, `ordering` included, so what PR-18
adds at the edge is the Compose pair that makes an existing route stop
answering 502. The route was always gateway configuration rather than
something in the service's tree — which is why the scaffold does not write
one — and after PR-17 it is not work at all. What the three days do **not**
buy is project wiring: a day spent there is a defect report against the
scaffold.

### Integration and operations

| PR | Title | Est. | Cum. | Week |
|---|---|---|---|---|
| **19** | `feat(bff): the BFF host, its gRPC client and the one permitted sync hop` | 4d | 70 | 20 |
| **20** | `feat(ordering): consume Catalog events into a local projection` | 4d | 74 | 22 |
| **21** | `feat(ordering): order fulfilment saga` | 6d | 80 | 23 |
| **22** | `test: expand architecture rules and document the test strategy` | 3d | 83 | 24 |
| **23** | `feat(deploy): Helm charts, migration hooks, probes` | 5d | 88 | 26 |
| **24** | `docs(ops): runbooks, secrets, dashboards-as-code, the SLO run` | 6d | 94 | 27 |
| **25** | `ci: integration categories, canary deploy, quality gates` | 5d | 99 | 29 |

This phase is a third of the total, which surprises people who read a plan as a
list of features. PR-21's saga carries a timeout on every wait state and a
compensation path per failure, and each of those is a harness test. PR-24 is
six days of writing: a runbook per §13.6 condition, each checked from the
alert to the procedure and from the procedure back to the alert.

**PR-24 has landed, and the estimate was right about the size for the wrong
reason.** Six days of writing was the guess; what it actually cost was the
writing *plus* the code the alerts turned out to need — §13.6's `OutboxMetrics`
had never been built, so four of §13.6's alerts had no signal and four more
were found to have none either. **An estimate that prices a documentation PR as
documentation is an estimate that has not asked what the documents point at.**
The same reading applies to PR-25 below, which is priced as pipeline work and
inherits whatever the SLO run turns out to need from a staging environment that
does not exist yet.

### Optional

| PR | Title | Est. | Cum. | Week |
|---|---|---|---|---|
| **26** | `chore(optional): consumer-driven contract tests` | 4d | — | — |

Not counted. Appendix C makes it conditional on a consumer relationship
becoming contentious, and pricing a conditional into a total is how a total
stops meaning anything. If it is taken, it adds a week.

**It was taken, and the estimate stays out of the total anyway.** The scope
line at the top of this file quotes a figure that was priced before any code
existed, and folding a conditional into it after the fact would make the number
mean two different things depending on when it was read. So the 99 days still
describe the plan as estimated; the week PR-26 actually cost is beside it, not
in it.

The estimate said Pact and the delivery is not — [ADR-023](backend-architecture/adr/ADR-023-the-consumer-driven-contract-is-a-linked-file-not-pact.md)
records why, and Appendix C's row carries what was found. That is worth a line
here only because it moved the estimate's shape: no broker to stand up and no
package to adopt, against a stub that had to be corrected in four places before
anything could be verified against it.

### After the plan

Appendix C's plan is complete, and it has since grown rows for work no numbered
row covered. They are listed here so that a reader comparing the two documents
finds nothing missing — **and they carry no estimate, which is a stated
decision rather than a gap.**

| PR | Title | Est. | Cum. | Week |
|---|---|---|---|---|
| **28** | `feat(common): §8.5's idempotency behaviour and Redis store` | — | — | — |
| **29** | `feat(ci): §15.1's secret scan, the other half of the first node` | — | — | — |
| **30** | `fix(ordering): the saga's transactional outbox` | — | — | — |
| **31** | `fix(messaging): the broker has a per-service identity` | — | — | — |
| **32** | `fix(common): §8.5's durable idempotency marker` | — | — | — |
| **33** | `fix(common): the marker and its claim are ordered by construction` | — | — | — |
| **34** | `fix(common): the marker's purge asks the claim rather than out-counting it` | — | — | — |
| **35** | `fix(common): the marker purge identifies a row by a rowversion` | — | — | — |
| **36** | `feat(ci): the deployed realm is checked at deploy time` | — | — | — |
| **37** | `feat(ci): the deployed realm is checked between rollouts` | — | — | — |

**Nothing in this file would produce a number for them.** Every estimate above
was quoted before any code existed, against a specification that was already
finished, and *What these numbers are* says outright that none has since been
re-priced against a pull request that has actually landed. **Not one of them
was priced before it was built** — PR-28 and PR-29 were rowed after they
landed, and every row after them is written by the pull request doing the
work — so a figure
here would be either invented, which the *Basis* section's own terms forbid
because an invented day is not an argument about relative size, or an actual
restated as a forecast, which makes the column mean two different things
depending on which row is being read. There is no rule in this file that
converts one into the other, and writing one for the occasion would be the
invention wearing a procedure.

**PR-26 is the near case and does not supply that rule.** Its four days are a
real estimate that stayed out of the total, and they are legitimate for the one
reason these rows cannot borrow: it was priced before it was taken, like every
other row on this page. An estimate quoted after delivery is a different kind
of claim, whatever column it is written in.

**They still cost days, and the days were real.** What is claimed here is that
this file cannot say how many — not that the work was free, and not that the
total is right in spite of them. Whoever re-prices this roadmap against
delivered work is the person who could fill this table in — in the successor
the note at the top of this file names, since this one is frozen — and *What
these numbers are* already names that as the one thing that should move the
total.

## Milestones

Six, each named for a capability the platform gains rather than for a range of
pull request numbers. A milestone that reads "PR-12 through PR-15 complete"
tells nobody outside the branch anything.

| | Milestone | Through | Cum. | Week |
|---|---|---|---|---|
| **M1** | The foundation compiles and CI is green | PR-06 | 18d | 6 |
| **M2** | One service serves one request, end to end | PR-11 | 37d | 11 |
| **M3** | Events cross a service boundary transactionally | PR-15 | 54d | 16 |
| **M4** | Authenticated traffic reaches two services through the gateway | PR-18 | 65d | 19 |
| **M5** | The full async path: projection and saga | PR-21 | 80d | 23 |
| **M6** | Deployable, observable and gated | PR-25 | 99d | 29 |

**M2 is the one to watch.** It is the first point at which the platform does
something a person outside the team can see, and it arrives at week 11 — nearly
40% of the schedule — because everything before it is the machinery that makes
the second service cost three days instead of thirty. That is the correct
trade, and it is also the trade that gets abandoned around week 8 by anyone who
has not agreed to it in advance.

**M2 is complete.** PR-11 is its last pull request, and the milestone reads
the way it was written to: Catalog serves a request end to end, and the
machinery behind it now renders the second service in one command. Whether the
calendar held is not something this file can answer — it prices ideal
engineer-days and the *Basis* section says why that is deliberately not a date.

**M3 and M5 were the two that could slip quietly, and both have landed.** Each
was dominated by a single large pull request whose difficulty is invisible from
outside — PR-14's outbox and PR-21's saga — and each was a place where "nearly
done" can last a week. The warning is kept in the past tense rather than
deleted, because it was right: the saga's own PR spent most of its work on
things §9.6 did not say it needed, none of which is visible in a one-line
milestone row.

**M6 inherits the shape**, and PR-23 has now shown it: the charts are the same
"invisible from outside" kind, and most of that PR's work went on things §15.3
did not say it needed — a naming rule the platform's own routing already
depended on, a grace period whose number had to be measured, and a render-time
gate to keep any of it from drifting. PR-24 has since done the same thing
again — the runbooks, and beside them the metrics type §13.6 had specified
and nobody had built, plus a second render-time gate.

**PR-25 has landed and M6 with it, and the prediction above was right about
where the work would be.** "A canary nobody has run" is still a fair
description of what shipped — no cluster exists — but the invisible work was
not the running, it was the deciding. §15.5 named a behaviour and no chapter
had chosen a mechanism, so the pull request had to take
[ADR-022](backend-architecture/adr/ADR-022-the-canary-is-a-second-release-weighted-by-replicas.md);
the attribute the analysis compares on turned out to be constant across every
build in the platform and needed replacing; §15.5's first rung turned out to be
inexpressible at the replica count §15.3 ships; and the arithmetic that
computes the weight was wrong in floating point at exactly that input. Four
decisions, none of them in the estimate, and all four found by writing the
thing rather than by reading about it.

**Which makes M6 complete, and the estimate's lesson the same one PR-24
taught.** An estimate that prices a pipeline pull request as pipeline work has
not asked what the pipeline is being asked to decide.

## Critical path

With one engineer the schedule is the sum of the parts, so the dependency graph
does no work. It is still worth pricing, because it bounds what any staffing
decision could ever buy.

The longest chain through Appendix C's graph is 54 ideal days:

```
01 → 02 → 03 → 05 → 07 → 08 → 09 → 10 → 14 → 15 → 20 → 21 → 24
 3    3    2    3    4    5    3    4    6    5    4    6    6
```

Against a 99-day total, that is a **ceiling of about 1.8×** on any team of any
size. An infinite number of engineers finishes in 54 days, and the second
engineer captures most of the available gain; the fourth captures almost none,
because by then the schedule is the chain above and the chain is serial by
construction.

Two things follow. First, the honest answer to "can we go twice as fast with
twice the people" is no, and the graph says so before anyone is hired. Second,
the chain names exactly which pull requests are worth protecting from
interruption: a day lost on PR-14 is a day on the end date, while a day lost on
PR-12 — which is not on the chain — is free until it isn't.

The chain also explains the shape of the *Integration and operations* phase.
Three of its seven pull requests sit on the critical path, which is why a phase
that reads like clean-up is a third of the plan.

## What moves these numbers

Six things, ordered by how much of the total they can move.

**The domain question is settled: the domain is a reference implementation**
([ADR-062](backend-architecture/adr/ADR-062-the-domain-is-a-reference-implementation.md)).
The six services [§4.1](backend-architecture/04-solution-structure.md) and
Appendix C name are the worked example an adopter replaces, so the 21 of the
99 days PR-10, PR-18, PR-19, PR-20 and PR-21 took are the example's cost, not a
rework this plan owes. This item priced the question as open and the largest
risk on the page; settled, it moves no total.

PR-11 and PR-12 are the delivered pull requests that do **not** move if the
domain changes, and both were built that way deliberately: the scaffold
copies the service template and excludes Catalog's slice, so it names no
aggregate, no command and no endpoint (§4.5) — and the Redis helpers are
shared mechanism in `Common.Infrastructure`, naming no aggregate, no key and
no cache entry that belongs to any one service
([§8](backend-architecture/08-caching-redis.md)). That is seven of the 99 days
an adopter keeps when they replace the domain, which is the split ADR-062
draws between the example and what is kept.

**That parenthetical used to read "wired to no service at all", and PR-28
falsified it.** Both `Catalog.Infrastructure` and `Ordering.Infrastructure`
now call `AddRedisConnections(configuration)`; Appendix C's PR-28 row names
that wiring as a deliverable, because PR-12 had built the whole stack and left
it with no caller anywhere in `src/`. **The seven days do not move, which is
why the sentence is reworded rather than deleted.** What makes PR-12
domain-proof is that its helpers name nothing a domain owns — the namespaces
are parameterised by service and the TTL rule is arithmetic — not that nothing
had called them. Being wired into two services it knows nothing about
demonstrates the property rather than costing it, and the state claim was
never the argument.

**Testcontainers and Docker on the build agent.** Priced into PR-08 as one day
of first-time cost, which assumes the agent can run containers at all. If it
cannot, PR-08, PR-12, PR-14 and PR-25 each grow and the schedule acquires an
infrastructure dependency it does not currently have.

**The Keycloak realm import.** PR-16's four days assumed the realm is imported
from a checked-in file that works the first time. Realm imports are reliably
worse than expected, and PR-16 sits one hop off the critical path — close
enough that a bad week there reaches the end date through PR-17 and PR-18.

The risk landed, and cheaply. A hand-written import cost one wrong turn:
Keycloak treats a `clientScopes` array as the complete set, so the readable
thirty-line file it started as silently dropped the built-in scopes and with
them the `sub` claim. Found in an afternoon by importing it into a container
and reading a token, which is the mitigation this entry should have named —
build the realm through the admin API, export it, and re-import the export.
The remaining exposure was the same shape one service further on — PR-19's
client-credentials audience, the half no file test can reach — and **it landed
the way this entry recommended rather than the way PR-16's did**: the `web-bff`
client was created through the admin API against a running Keycloak, read back,
and spliced into the export, then the whole file re-imported into a fresh
container and a token read out of it. Eleven claims checked, including the two
negative ones that matter most — a service account carrying no `permission`
claim, and every existing login keeping `sub`, `email` and `realm_access`.
**What stands afterwards is four of those eleven**, and the difference is worth
recording where the risk was: `KeycloakIdentityTests` pins the audience on the
BFF's token, the absent `permission` claim, that a host running the real
`AddJwtAuthentication` accepts it, and that a client without the scope is
refused. The login half is `RealmImportTests`, which reads the export and
starts nothing — so no standing test mints a password-grant token for `demo` or
`browser`, and the claims those logins carry were verified once, by hand. It is
still the only suite in the solution that starts an identity provider.

**The `Directory.Build.props` analyser policy is settled, and it is a tax.**
[ADR-019](backend-architecture/adr/ADR-019-warnings-are-errors-and-the-editorconfig-is-a-build-input.md)
takes `TreatWarningsAsErrors` and `EnforceCodeStyleInBuild` from PR-01 and
declines StyleCop. The half day it adds to PR-01 is inside that estimate. What
is **not** priced anywhere below is the per-pull-request cost — small each
time, and paid by every pull request after PR-01 that touches C# at all —
which lands as slippage spread thin rather than as a line item. The
alternative was a sweep, which at least would have had a number.

**The width used to be written as a count and is not any more.** It said 24,
which works out only as PR-02 … PR-25 and was already false against the Scope
row's own PR-27; PR-26 was taken, and Appendix C has since added the *After
the plan* rows, none of which the sweep alternative would have skipped either.
The predicate is checkable against the tables above and the count was
checkable against nothing that stays still — which is the fix this file
reaches for elsewhere, and the reason the opening paragraph no longer counts
pull requests either.

**Aspire.** Not adopted; Compose is the baseline
([§14.1](backend-architecture/14-local-development.md)) and no `Aspire.*`
package is pinned. If it is adopted, `src/AppHost` is new work and every
service picks up the client integrations for the resources it consumes, so the
cost is a line per resource per service in both directions (§14.2). Not priced
here.

**Review latency, which this roadmap does not model at all.** Ideal
engineer-days exclude it by definition, and for a solo engineer with
self-review it is genuinely near zero. Add a reviewer and the calendar changes
without any estimate changing — which is precisely the separation the *Basis*
section exists to preserve.

---

[Blueprint index](backend-architecture/README.md) · [Delivery plan](backend-architecture/appendix-c-delivery-plan.md)
