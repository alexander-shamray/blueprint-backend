# Canary rollout

§15.5's progressive delivery, as data and one decision function.
[ADR-022](../../docs/backend-architecture/adr/ADR-022-the-canary-is-a-second-release-weighted-by-replicas.md)
records the mechanism.

**Nothing here has ever reached a cluster.** There is no dev, staging or
production environment for this repository, no kubeconfig and no registry.
`.github/workflows/deploy.yml` is `workflow_dispatch` only for that reason — a
canary on `push` would fail on every merge for want of a cluster, and a
pipeline that is red by design trains everybody to ignore it. This is the third
artefact of that kind: PR-23 shipped charts nothing installs and PR-24 alert
rules no Prometheus loads.

What is asserted, and by what, is the whole of the next two sections.

## What runs

```bash
py -3.12 -m unittest discover -s deploy/canary   # the arithmetic and the verdict
py -3.12 deploy/canary/canary.py check           # the plan against this repository
```

Stdlib Python, no dependencies, no SDK, no `helm`, no Docker — the licence
gate's terms, which is why the gate can run before anything is built.
`deploy/helm/smoke.sh` covers the other half: it renders the canary track of
every chart and asserts what comes out.

| File | What it is |
|---|---|
| `canary.json` | §15.5's ladder, the thresholds, each signal's PromQL, and the workload map with the signals each workload is judged on |
| `canary.py` | The weight arithmetic, the promote/rollback verdict, and the gate over `canary.json` |
| `read_prometheus.py` | The one file that talks to anything. Runs the queries of the signals a workload declares and writes what came back |
| `test_canary.py` | The suite. It is the whole of the assurance the rollout has |

## What it asserts

`canary.py check` is ten checks, and the sixth is about itself:

1. The ladder climbs, ends at 100%, and every rung but the last has a dwell.
2. Every threshold `analyse` reads is present — it indexes them, so a missing
   key would be a `KeyError` with a canary already serving traffic.
3. The absolute thresholds **are** §13.6's alert thresholds, read out of
   `platform-alerts.yaml` rather than restated. A canary tuned looser than the
   alert promotes a release and then pages about it.
4. Each workload's key is a Helm release name, its `serviceName` is an entry
   assembly this solution builds, and its `chart` is a chart under
   `deploy/helm`.
5. Every metric a signal's queries read is vouched for: either a loaded alert
   reads it — and `deploy/observability/check.py` has already established
   that something publishes it — or it is an instrument of a meter
   `Common.Web`'s `ObservabilityExtensions` registers, which is how
   MassTransit's series qualify — and those only as exact entries in
   `EXPORTED_SERIES`, verified against the MassTransit pin in
   `Directory.Packages.props`, which fails the check when it moves. Deleting
   the registration fails it too.
6. The parser found host assemblies at all, so checks 4 and 5 cannot pass
   vacuously.
7. Both of `deploy.yml`'s triggers cover every path in `SOURCE_INPUTS`.
8. `deploy.yml`'s dispatch menu is exactly the plan's workload set.
9. Every signal carries the three queries `analyse` reads and holds its fault
   rate to an absolute threshold; every workload declares at least one signal
   the plan defines; and a service whose tree registers a MassTransit
   consumer declares `consume`, and one that registers a saga declares `saga`,
   or carries a non-empty `consumeExemption` or `sagaExemption` — which fails
   on a service with nothing to exempt, or beside the signal it exempts.
10. Every selector in the `http` signal's queries excludes the probe routes,
    and the exclusion matches every route `MapHealthChecks` maps in `src/` —
    found by scanning, so a fourth probe route fails the plan rather than
    counting as traffic again.

## What a workload is judged on

[ADR-047](../../docs/backend-architecture/adr/ADR-047-the-canary-judges-each-workload-on-the-signals-it-receives.md)
is the decision; this is where it lives. A workload declares `signals` in
`canary.json`, and `read_prometheus.py` fetches those and no others:

- **`http`** is ASP.NET Core's request histogram, less the probe routes. It is
  held to both of §13.6's absolute numbers.
- **`consume`** is MassTransit's consume counters and duration histogram. Its
  fault rate is held to §13.6's error threshold; its duration is compared with
  the stable track only, because no alert owns a consume-duration number.
- **`saga`** is MassTransit's saga instruments, judged on the same terms as
  `consume`. A state machine's messages are counted there and not on the
  consume series, so healthy consumers cannot carry a failing saga.

Every declared signal is compared with the stable track on both metrics, and
every declared signal must reach `minimumRequests` on its own: a workload is
promoted only when each thing it does was observed doing it.

## What it does not

- **It reaches no cluster and no Prometheus.** Every function in `canary.py` is
  pure over its arguments; the workflow fetches and acts.
- **It does not validate PromQL.** The queries are strings here. A syntax error
  in one surfaces as a failed query at the end of a ten-minute dwell — which
  the verdict reads as an absent series and therefore as a rollback, so it
  fails safe and slowly rather than unsafely.
- **It does not hold the weight against a voluntary disruption.** The
  PodDisruptionBudget belongs to the stable release and its selector matches
  both tracks, so it constrains the total rather than the stable count: a node
  drain during a dwell can evict stable pods and leave the canary serving more
  than the rung asked for. The verdict is still measured rather than assumed —
  `analyse` reads both tracks' real numbers — so the cost is exposure for one
  dwell, not a wrong decision. ADR-022 records why the temporary stable-track
  budget that would fix it is deferred.
- **It does not establish that a replica ratio is a traffic ratio.** kube-proxy
  spreads *connections*, not requests. Keep-alive, HTTP/2 multiplexing to the
  gRPC listener, or a client that opens one connection and holds it will all
  under-deliver the weight, and nothing short of a cluster can measure that.
  ADR-022 names it as owed.
- **It does not establish the exported spelling of MassTransit's series.**
  The instrument names are MassTransit's defaults at the pinned version and the
  meter registration is checked, but the unit suffixes — `ea` on the counters,
  `ms` on the histogram — become `_ea` and `_milliseconds` under the
  OTLP-to-Prometheus mapping the deployed backend applies, which nothing here
  reaches. A wrong spelling matches nothing and rolls back, like the
  `deployment_track` requirement below.
- **It does not establish that the consume share is the replica share.** The
  tracks are competing consumers on one queue, so the canary's share of the
  messages follows prefetch and processing speed rather than the pod ratio
  `plan` computed.
- **It cannot see an ad-hoc `--set` at deploy time**, the same reach
  `deploy/helm/README.md` states for the chart gate.
- **It does not establish that `deployment_track` is a label on the deployed
  backend's series.** `deployment.track` is a *resource* attribute, and the
  standard OTLP-to-Prometheus mapping puts only `service.name`,
  `service.namespace` and `service.instance.id` on each series — the rest go to
  `target_info`. §14.1's collector copies this one attribute onto the datapoint
  with a `transform` processor, and **the deployed collector must do the
  same**. Without it every query matches nothing, which this reads as an absent
  series and rolls back: every rung, on a healthy canary. The requirement is in
  ADR-022; nothing here can check it, because the cluster's collector is not in
  this repository.

## The two things worth knowing before reading the code

**The requested weight is a ceiling, not a target.** `plan` returns the largest
canary that stays *within* it and refuses where even one pod overshoots, naming
the stable replica count that would satisfy the step. A replica ratio is
quantised, so §15.5's 5% needs 19 stable replicas; at the chart's default of 3,
one canary pod is already 25%. The refusal is the deliverable — rounding to the
nearest expressible weight is how a step labelled 5% comes to serve five times
the blast radius anybody authorised.

**There is no third verdict.** Promote or roll back, and every doubt resolves to
the second: an absent series, a declared signal nobody fetched or one too quiet
to judge, a breach, or a regression against the stable track. That is
affordable because of what the mechanism is — the canary is a second
Deployment and the stable release is never touched, so a rollback costs the
canary's own pods and nothing else. When rolling back is cheap, "inconclusive"
is not caution, it is a canary left serving traffic on nobody's authority.
