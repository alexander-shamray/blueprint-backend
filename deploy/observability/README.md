# `deploy/observability/`

Alerts, dashboards and the SLO run, as code.

[§13.8](../../docs/backend-architecture/13-observability.md) requires it:
*"Dashboards are **code**, checked into `deploy/observability/` as Grafana JSON
or equivalent. A dashboard clicked together in a UI is lost with the instance
and cannot be reviewed."* The alert rules and the k6 run are here on the same
argument.

```
alerts/
  platform-alerts.yaml    LOADED
  awaiting-signal.yaml    NOT loaded — their instrument does not exist yet
dashboards/
  golden-signals.json     RED per service, plus §13.7's two request rows
  outbox.json             the two lanes of §9.4, kept apart
slo/
  slo.js                  the k6 SLO run of §13.7 and §15.1
check.py                  the gate — run it before pushing
```

## The gate

```bash
py -3.12 deploy/observability/check.py
```

Stdlib Python, no dependencies, no restore — the licence gate's terms, and for
the same reason: it reads text, so it can run before anything is built. CI runs
it in `.github/workflows/observability.yml`, path-filtered.

It asserts the following. The first two are §13.9's pairing in both directions;
the middle ones are §13.6's third leg — *"an alert has three parts: a condition,
a signal and a procedure"* — and the last is the gate watching its own inputs.

| | |
|---|---|
| 1 | Every alert names a runbook, and that runbook exists |
| 2 | Every runbook is named by an alert |
| 3 | No alert is defined in both rule files |
| 4 | Every metric a **loaded** rule reads is published by something |
| 5 | Every metric an **awaiting-signal** rule reads is published by **nothing** |
| 6 | Every dashboard panel's metric is published |
| 7 | The workflow's triggers cover every path outside this tree that the gate reads |
| 8 | Every service hosting §9.4's dispatcher publishes outbox gauges, or is on a declared exemption |
| 9 | §13.6's and §13.9's tables name exactly the runbooks on disk, both ways |

**Check 9 is checks 1 and 2 aimed at the chapter instead of the rule files.**
Those two pair `alerts/*.yaml` with `docs/runbooks` and never open
`13-observability.md`, so the prose the rule files were written *from* was the
one inventory here nothing read — and it drifted: a thirteenth condition landed
and five prose sites went on saying twelve, with this gate green throughout
because it has never counted anything ([#155](https://github.com/alexander-shamray/blueprint-backend/issues/155)).

**Counting is still not the fix, and the numerals were dropped rather than
corrected.** A total in front of a table only records how stale the sentence
is. What check 9 pins is the table, which is the claim a reader can check —
§13.9 names each runbook with its `docs/runbooks/` prefix, §13.6's alert rows
name it bare in the Runbook column, and both sets must equal the directory.

**The pairing is not one-to-one and this check does not require it to be.**
Fourteen rules name thirteen runbooks: §13.8's ownership split makes error rate
two rules over one procedure, declared with its reason in `SHARED_RUNBOOKS`.
That is why the chapter's *conditions* and the rule files' *alerts* are counted
by nobody here — only paired.

**Check 8 exists because checks 4 and 5 are about metric *names* and cannot see
a service missing from a series.** The four loaded outbox alerts group `by
(service_name)`; a service that runs a dispatcher and publishes no gauges is
"covered" by four alerts that can never fire for it. Catalog is that service
today — §13.3 places `OutboxMetrics` in `Ordering.Infrastructure`, so closing
it means lifting the type into common code and teaching §4.5's scaffold to emit
it, which is a larger decision. It is on the exemption list with that reason,
and the check fails in **both** directions: a new unexempted service, and a
stale exemption for one that no longer needs it.

**Checks 4, 5 and 6 read C# with comments removed first, and the direction of
that failure is why.** The instrument scan is a regex over source, so a
commented-out `CreateCounter(…)` was counted as a live publisher — which meant
check 4 could certify a loaded alert whose instrument does not exist at
runtime, the one silent gap this gate exists to close. Measured before it was
fixed: block-commenting Ordering's `outbox.oldest.age` gauge left the gate
reporting **OK** with two loaded alerts and two dashboard panels reading a
metric nothing published.

Only comments are removed. An instrument's name is a string literal, so
blanking strings as well would find nothing at all — and a `//` inside a string
is not a comment, which the gate's own probe asserts in both directions.

**What it does not do.** It reaches no Prometheus, no Grafana and no cluster,
and it does **not** check that a rules file is syntactically valid to
Prometheus — `promtool` would be the tool for that, and adding it is a decision
no chapter has taken. It does not evaluate C# preprocessor conditionals either:
a `Create` call inside `#if false` would still count as published, because
excluding one means implementing defined symbols, `#elif` and nesting — a
compiler rather than a scanner. `src/` contains no `#if` today. Named here as
not covered rather than implied, on `deploy/helm/smoke.sh`'s terms.

## Why there are two rule files

Four of §13.6's alerts read an instrument this platform does not publish
yet. Loading them would be the exact defect that chapter spends a callout on:

> Two of the alerts in this document were written against signals that did not
> exist, and both looked correct: the dashboard is empty either way, whether the
> system is healthy or the metric was never published.

A rule that cannot fire is not a weak alert, it is a silent one — and silence
reads as health. So they sit in `awaiting-signal.yaml`, unloaded and visible,
and each names the instrument it is waiting for:

| Alert | Owed |
|---|---|
| `StuckSaga` | a gauge over `ordering.OrderFulfilmentStates` |
| `OrdersAwaitingReview` | a gauge over `ordering.OrderReviews` |
| `CacheHitRatioCollapse` | an instrument — see below |
| `BusinessVolumeDrop` | `OrderMetrics`, which arrives with §6.6's `OrderSummaries` projection |

**Check 5 is what keeps that list honest.** It asserts the metrics named there
are published by *nothing*, so the day somebody ships one of these instruments
the gate goes red and names the rule to move. An "awaiting signal" list that
nothing re-checks would quietly become a list of alerts nobody ever turned on.

**The cache row is the one check 5 cannot keep honest, and that is a measured
residual rather than an oversight.** `Microsoft.Extensions.Caching.Hybrid`
10.0.0 references `System.Diagnostics.Tracing` and not
`System.Diagnostics.Metrics` — it reports through `HybridCacheEventSource` with
`PollingCounter`, so there is **no `Meter`** behind the name §13.2 registers,
and wiring Redis would not change that. An earlier version of this gate treated
a call to `AddRedisConnections` as the signal arriving; that was written, tested
red and removed, because it would have gone red on a consumer while the alert
stayed silent — moving a rule into the loaded file where it cannot fire.
**That removal is now load-bearing rather than hypothetical**: §8.5's PR gave
`AddRedisConnections` its first callers, so the gate that was removed would have
fired on this branch and turned an alert with no signal on.

What the row is owed is an instrument: a bridge written here, which check 5
would see, or a package that publishes a meter — **which no gate in this
repository can observe**. The metric names in the rule are what such a bridge
would plausibly export, not names read off a running system.

**Neither file's rule count is written down here on purpose.** The table above
names all four by alert, so it evidences itself and cannot drift silently; a
bare "eight loaded" would be a number nothing recomputes, and the first rule
that moves between the files would make it wrong. `CLAUDE.md` makes the same
argument about its own line count.

Every runbook exists either way — §13.9 asks for the procedure to be
written *when the alert is created*, not when it first fires.

## Metric names

Expressions use the OpenTelemetry-to-Prometheus spelling, not the C# one: dots
become underscores and a UCUM unit becomes a suffix. So `outbox.oldest.age`
with unit `s` is `outbox_oldest_age_seconds`, while the annotation unit
`{message}` is dropped and `outbox.pending.count` stays `outbox_pending_count`.

`check.py` applies the same transformation when matching an expression against
the instruments declared in C#, which is what stops the two spellings drifting
apart in silence.

**`service_name` assumes the collector promotes the `service.name` resource
attribute to a label.** That is a collector-config decision rather than
something this repository can assert, and it is stated rather than assumed:
without the promotion, every `by (service_name)` groups everything into one
series and the per-service alerts stop discriminating.

## Loading them

Neither Prometheus nor Grafana is deployed by this repository — §15.3's charts
cover the platform's own workloads and nothing else. How these files reach a
running Prometheus (a `PrometheusRule`, a ConfigMap, a sidecar-discovered
folder) is a decision for whoever owns the observability stack, and no chapter
has taken it. What this directory guarantees is that the content is reviewed,
versioned and internally consistent.

## The SLO run

```bash
# From the runner's masked environment, never `-e`: that puts the real value in
# k6's process arguments and CI logs the command line. k6 reads system
# environment variables through __ENV by default, so no flag is needed.
export CLIENT_SECRET="$SLO_RUN_CLIENT_SECRET"

k6 run \
  -e BASE_URL=https://staging.example.com \
  -e PROM_URL=http://prometheus.observability:9090 \
  -e TOKEN_URL=https://id.staging.example.com/realms/commerce/protocol/openid-connect/token \
  -e CLIENT_ID=slo-run \
  -e SLO_PRODUCT_ID=... \
  deploy/observability/slo/slo.js
```

It is the first real gate after the staging deploy (§15.1), and it is **not** a
smoke test — §15.1 declines to have one, because a gate nobody has defined is a
gate that gets configured to pass.

k6 drives the traffic and Prometheus adjudicates: k6's own thresholds are a
coarse guard, because a client's wall-clock includes the edge, TLS and the
network, while §13.7's command and query rows read `request.duration`, which is
dispatcher entry to result. The authoritative assertions are in `teardown()`,
one per §13.7 row this run's own traffic can actually produce.

**Three of §13.7's seven rows are not evaluated**, each named in `teardown()`
rather than quietly dropped: availability, because a three-minute run cannot
compute a monthly objective; `projection.lag`, because no service registers an
`IProjectionHandler<T>` and so nothing writes to it; and
`messaging.delivery.lag`, because the consumer that records it handles
Catalog's product events and neither scenario produces one. Asserting any of
the three would fail every run on a healthy platform, which is how a gate gets
switched off.

**An absent series fails the run.** It is not treated as "no problem observed",
for the reason the two rule files exist.

**Run it against a quiescent target, and treat that as a precondition rather
than a preference.** `teardown()` reads the *server's* histograms, which have no
way to tell this run's requests from anyone else's: the queries are scoped to
the exact request types the scenarios drive, and concurrent traffic of those
same types still lands in the same series. Enough fast ambient load dilutes a
regression the run generated. Fencing it would need a run identifier on the
server metric, and §13.3's cardinality rule rules that out — so the isolation
has to come from the environment. A staging deployment nobody else is driving
is what makes the authoritative half of this gate mean anything.

**Availability (99.9% monthly) is deliberately not evaluated.** A three-minute
run cannot; `http_req_failed` bounds the error rate *during the run*, which is a
much weaker claim, and the run says so rather than reporting a pass.
