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
stress/
  stress.js               the k6 stress run: users rising in steps to a knee
  sample.mjs              Docker and SQL Server readings while it runs
  summarise.mjs           the per-step table and the knee, afterwards
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
| 8 | Every service with a Domain project hosts §9.4's dispatcher and one with none hosts none; every service hosting it publishes its gauges or is on a declared exemption |
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
§13.8's ownership split makes error rate two rules over one procedure, declared
with its reason in `SHARED_RUNBOOKS`.
That is why the chapter's *conditions* and the rule files' *alerts* are counted
by nobody here — only paired.

**Check 8 exists because checks 4 and 5 are about metric *names* and cannot see
a service missing from a series.** The four loaded outbox alerts group `by
(service_name)`; a service that runs a dispatcher and publishes no gauges is
"covered" by four alerts that can never fire for it. The exemption list is
empty, because §4.5's template registers the gauges and the scaffold renders
them into every new publishing service (§13.6); a service lands on it only
with a reason written down, and the check fails in **both** directions: a new
unexempted service, and a stale exemption for one that no longer needs it.
**Which services owe a dispatcher is read from the tree**: one with a Domain
project raises the events §9.4's outbox carries, and one with none — §4.1's
pure consumer — publishes nothing, so it hosts no dispatcher and is owed no
gauges. Both directions fail here too.

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
and it does **not** check that a rules file is valid to Prometheus. The Compose
smoke does, for `platform-alerts.yaml` alone:
[`compose.yml`](../../.github/workflows/compose.yml) loads it into the image's
Prometheus and fails unless the loaded alerting-rule count is the file's. It
does not evaluate C# preprocessor conditionals either:
a `Create` call inside `#if false` would still count as published, because
excluding one means implementing defined symbols, `#elif` and nesting — a
compiler rather than a scanner. `src/` contains no `#if` today. Named here as
not covered rather than implied, on `deploy/helm/smoke.sh`'s terms.

## Why there are two rule files

Three of §13.6's alerts read an instrument this platform does not publish
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
names all three by alert, so it evidences itself and cannot drift silently; a
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

**Locally, they are loaded at start.** §14.1's `grafana` service mounts
`platform-alerts.yaml` into the Prometheus its image bundles and provisions
both dashboards into a *Platform* folder; `deploy/compose/README.md` gives the
read that lists what it loaded, and the Compose smoke holds the loaded rule
count to this file's. **`awaiting-signal.yaml` is deliberately not mounted**:
its rules read instruments that do not exist, which is why it is a second
file at all.

No deployed environment's Prometheus or Grafana is deployed by this
repository — §15.3's charts cover the platform's own workloads and nothing
else. How these files reach a deployed Prometheus (a `PrometheusRule`, a
ConfigMap, a sidecar-discovered folder) is a decision for whoever owns the
observability stack, and no chapter has taken it. What this directory
guarantees is that the content is reviewed, versioned and internally
consistent.

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
  -e SLO_CURRENCY=... \
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

**One of §13.7's seven rows is not evaluated**, and it is named in
`teardown()` rather than quietly dropped: availability, because a three-minute
run cannot compute a monthly objective. Asserting it would fail every run on a
healthy platform, which is how a gate gets switched off. Event end-to-end is
asserted over the two events the run's own orders publish, `OrderPlaced` and
`OrderConfirmed`, as the request rows are scoped to their request types.

**The run must reach the happy path, and two checks hold that.** `placeOrder`
asserts only the synchronous answer, and the stock reservation fails later, in
the saga, so a product without stock leaves every check green while every order
is cancelled `out_of_stock` ([#993](https://github.com/alexander-shamray/blueprint-backend/issues/993)).
`setup()` reads the product's stock through the gateway, which needs the
`inventory:admin` permission on the SLO client, and refuses a product with
fewer available than the run places (540). `teardown()` waits up to 50 seconds
(`SETTLE_ATTEMPTS` and `SETTLE_SLEEP_SECONDS`) for the tail of the saga, then
fails the run when fewer than 90% of the orders `orders.placed` counted ended
confirmed (`orders.fulfilment.duration`). Both decisions are `slo/gate.js`,
tested by `slo/gate.test.js`.

**An absent series fails the run, for every row it asserts.** It is not treated
as "no problem observed", for the reason the two rule files exist.

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

**The two overdue gauges are recorded, never asserted.** `teardown()` prints
`shipping.shipments.overdue` and `notifications.overdue` once the run has
settled, because [ADR-063](../../docs/backend-architecture/adr/ADR-063-a-worker-that-waits-on-a-third-party-is-scaled-by-hand.md)
waits on a baseline under load for both and this run is the only load the
platform has. No rule pages on either yet, so there is no limit to fail the
run on, and an absent series or a refused read is printed rather than failed.

### Running it locally

This repository has no staging, so the run has only been made against the
Compose stack on a developer machine. **That number is a local run, never a
staging baseline** ([§13.7](../../docs/backend-architecture/13-observability.md)
says why), and a result is reported with the machine's processor, memory and
operating system, the Docker engine's limits, the commit and the k6 version.
k6 is a machine tool like Docker: install it with `winget install k6 --source
winget`, or `choco install k6`, and add it to no manifest.

The blocks run in order in one Git Bash shell from the repository root, with
nothing to fill in, and running them all again is safe: each one finds what an
earlier run left and reuses it. `MSYS_NO_PATHCONV=1` stops Git Bash rewriting
the `/opt/...` paths the containers are handed.

**1. Reach Prometheus through Grafana.** The `grafana` service runs
grafana/otel-lgtm, whose bundled Prometheus listens on 9090 inside the
container and is published nowhere. Grafana's datasource proxy reaches it with
no login, the image's default, so `PROM_URL` is the proxy's route and `slo.js`
appends `/api/v1/query` to it, as `tools/game-day` reads alerts. No overlay
publishes 9090: one would recreate `grafana`, and with no data volume its
Prometheus would start empty. Bring the stack up with `--build`, since the
services are built from source and the result is reported with this commit,
and let it idle a minute, so the outbox and overdue gauges `teardown()` reads,
which the services and workers export on a timer, have reported once; the
run's own traffic makes the other series:

```bash
export MSYS_NO_PATHCONV=1
docker compose -f deploy/compose/docker-compose.yml up -d --build --wait
curl -fsS http://localhost:3000/api/datasources/proxy/uid/prometheus/-/ready \
    && echo 'prometheus ready: yes' || echo 'prometheus ready: no'
```

**2. Create the SLO client at runtime, once.** The Compose realm has no client
with `orders:write` and a GUID `sub`, which `slo.js`'s header states as an
obligation, and whether the realm should carry one is not decided here. The
`commerce-api` client scope already maps a client's `commerce-api` roles into
the `permission` claim, so a service account holding the roles is enough. The
list of default scopes names `basic` as well: naming the list replaces the
realm's, and `basic` carries the mapper that writes `sub`, which step 3 checks.
The block reads the client before creating it, grants both roles either way,
since granting a held role changes nothing, and exports the secret for step 5:

```bash
kcadm="docker exec commerce-keycloak-1 /opt/keycloak/bin/kcadm.sh"
$kcadm config credentials --server http://localhost:8080 --realm master \
    --user admin --password admin
id=$($kcadm get clients -r commerce -q clientId=slo-run --fields id --format csv --noquotes | tr -d '\r')
[ -n "$id" ] || id=$($kcadm create clients -r commerce -i -s clientId=slo-run -s enabled=true \
    -s publicClient=false -s serviceAccountsEnabled=true \
    -s standardFlowEnabled=false -s directAccessGrantsEnabled=false \
    -s 'defaultClientScopes=["basic","commerce-api"]' | tr -d '\r')
for role in orders:write inventory:admin; do
    $kcadm add-roles -r commerce --uusername service-account-slo-run \
        --cclientid commerce-api --rolename "$role"
done
export CLIENT_SECRET=$($kcadm get "clients/$id/client-secret" -r commerce \
    --fields value --format csv --noquotes | tr -d '\r')
```

Keycloak runs `start-dev` with no volume, so its database lives in its
container: a `down`, or an `up` that recreates `keycloak`, loses the client,
and this block makes it again with a new secret.

**3. Check the token before loading anything.** Request it from
`localhost:8080`, the host the gateway's authority accepts. The check prints
`token check: pass` and exits 0 when `permission` holds both roles, `sub` is a
GUID and `iss` is `http://localhost:8080/realms/commerce`, and `fail` with
exit 1 otherwise:

```bash
token=$(curl -fsS http://localhost:8080/realms/commerce/protocol/openid-connect/token \
    -d grant_type=client_credentials -d client_id=slo-run --data-urlencode client_secret="$CLIENT_SECRET" \
    | sed -n 's/.*"access_token":"\([^"]*\)".*/\1/p')
echo "$token" | py -3.12 -c '
import base64, json, sys, uuid
try:
    part = sys.stdin.read().strip().split(".")[1]
    claims = json.loads(base64.urlsafe_b64decode(part + "=" * (-len(part) % 4)))
except (IndexError, ValueError):
    claims = {}
perm, sub, iss = claims.get("permission", []), str(claims.get("sub", "")), claims.get("iss", "")
try:
    guid = bool(uuid.UUID(sub))
except ValueError:
    guid = False
ok = {"orders:write", "inventory:admin"} <= set(perm) and guid and iss == "http://localhost:8080/realms/commerce"
print("permission:", ",".join(perm), "| sub:", sub, "| iss:", iss)
print("token check:", "pass" if ok else "fail")
sys.exit(0 if ok else 1)'
```

**4. Choose the product and the currency.** The scenario orders one product in
one currency, and without a matching row in `ordering.ProductPrices` every
order is a 422 (§6.6). The read names no id. It takes the first seeded
product, from the `5eed0000-` ids
[`deploy/compose/README.md`](../compose/README.md#seeded-products)'s table
owns, that Ordering holds as available. Its price must end in none of the
pence the payment simulator scripts as a decline or a timeout
([`psp-simulator/README.md`](../compose/psp-simulator/README.md)), because
either would cancel the order. The currency comes from the same row, so a
change to the seed moves both. `none` means Ordering has received no usable
product yet. The password is read inside the container, as its healthcheck
reads it, so the command carries none:

```bash
row=$(docker exec commerce-sql-1 sh -c 'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" exec "$@"' sh \
    /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -d Ordering -h -1 -W -s , \
    -Q "SET NOCOUNT ON; SELECT TOP 1 LOWER(CONVERT(char(36), ProductId)), Currency FROM ordering.ProductPrices
        WHERE IsAvailable = 1 AND CONVERT(char(36), ProductId) LIKE '5EED0000-%'
        AND CAST(ROUND(Amount * 100, 0) AS bigint) % 100 NOT IN (1, 2, 5, 9) ORDER BY CONVERT(char(36), ProductId)" \
    | tr -d '\r')
IFS=, read -r product currency <<< "$row"
echo "product: ${product:-none}, currency: ${currency:-none}"
```

**4a. Restock the product.** The run places 540 single-unit orders and
`setup()` refuses a product with fewer available. The SLO client holds
`inventory:admin`, so its token sets an absolute count, and a repeat sets the
same one; `available` is that count less what earlier runs still reserve:

```bash
curl -fsS -X PUT "http://localhost:5000/api/v1/inventory/stock/$product" \
    -H "Authorization: Bearer $token" -H "Content-Type: application/json" -d '{"onHand": 100000}'
curl -fsS "http://localhost:5000/api/v1/inventory/stock/$product" -H "Authorization: Bearer $token"
```

**5. Run it.** `BASE_URL` is the gateway on 5000, never a service port. The
secret reaches k6 through the environment step 2 exported, never `-e`. The run
takes three to four minutes: about 190 seconds of load, because the orders
start ten seconds in, then `teardown()` waits up to 50 seconds for the saga's
tail before it asks Prometheus:

```bash
k6 run \
  -e BASE_URL=http://localhost:5000 \
  -e PROM_URL=http://localhost:3000/api/datasources/proxy/uid/prometheus \
  -e TOKEN_URL=http://localhost:8080/realms/commerce/protocol/openid-connect/token \
  -e CLIENT_ID=slo-run \
  -e SLO_PRODUCT_ID="$product" \
  -e SLO_CURRENCY="$currency" \
  deploy/observability/slo/slo.js
echo "k6 exit: $?"
```

**The exit code is the verdict.** 0 is a pass: every threshold met and
`teardown()` silent but for its `SLO run: not evaluated` and `SLO run:
recorded, not asserted` lines. 99 is a k6 threshold crossed, with `teardown()`
passing. 107 is a `fail()`: from `teardown()`, `SLO run failed:` and one line
per breached row; from `setup()`, the configuration or stock refusal, with no
load driven. It wins over 99 when both happen. 105 is an interrupt: on Ctrl+C
k6 stops the load and still runs `teardown()` over the shortened window, but
does not report its verdict, so an interrupted run is neither a pass nor a
fail.

**Nothing has to be undone afterwards.** The client stays in the running realm
for the next run, the product keeps its count, and the orders a run placed,
an interrupted one's included, go on through the saga with their stock
reserved; the next run's step 4a sets the count again.

**A run does leave a backlog, so wait for it to clear before the next.** It
places orders faster than one `shipping-worker` books them or one
`notifications-worker` sends their notices, so both are still working through
them long after k6 exits: on one machine a run left about 45 minutes of
bookings ([#1130](https://github.com/alexander-shamray/blueprint-backend/issues/1130)),
and a second run inside that window adds to the same queue. That run starts
on a target that is not quiescent, and its recorded readings include the
first run's backlog. Wait until both gauges are back near zero:

```bash
for gauge in shipping_shipments_overdue_seconds notifications_overdue_seconds; do
  curl -fsS -G http://localhost:3000/api/datasources/proxy/uid/prometheus/api/v1/query \
    --data-urlencode "query=max($gauge)"
  echo
done
```

A failed run is a finding, not a reason to loosen anything: thresholds, rate
budgets and checks stay as they are. A defect in `slo.js` is fixed; a defect in
the platform is filed, and the output goes in the pull request either way.

## The stress run

`stress/stress.js` answers a different question from the SLO run: not whether
latency regressed, but which part of the platform gives out first when
hundreds of people use it at once, and at roughly what load. Distinct
signed-in users walk the domain's typical journey through the gateway, and
their number rises in steps. Each step is measured, and the first step at
which the platform degrades is the knee.

**It reports and never gates.** No threshold is an SLO. It asserts nothing,
and it stops early by one of two rules, which `STRESS_STOP` chooses, because
the steps past a clear break measure nothing new and, driven far enough,
take every service down at once, so the first cannot be told:

- `knee`, the default, ends the run at the end of the first hold whose
  figures cross the knee below, so that step is measured whole and nothing
  past it runs. `stress.js`'s `judge` scenario wakes at each hold's end,
  reads the step's client figures from k6's own REST API and the backlog
  from Prometheus at the hold's middle and end, and holds them against the
  step before with `stress/report.js`'s `crossings()`, the function
  `summarise.mjs`'s knee calls afterwards. A request still in flight when
  the hold ends is in the summary's figures and not the judge's.
  `setup()` refuses the run when either source does not answer.
- `errors` drives on until a step's requests have failed at more than
  `STRESS_ABORT_ERROR_RATE`, whose default `stress.js` sets, over its hold,
  to see what falls over rather than what slows down. k6 times that
  judgement from before `setup()`, so it can begin up to `setup()`'s length
  before the hold ends.

It runs on a developer's machine against the Compose
stack. Its numbers describe that machine as much as the platform, so a result
is relative: which service, queue or table gives out first. It is never a
capacity figure for production, and
[§13.7](../../docs/backend-architecture/13-observability.md)'s point that a
local run is an order of magnitude for that machine at that commit, and not a
staging baseline, holds here too.

**It runs only with the stress override.**
`deploy/compose/docker-compose.stress.yml` raises the gateway's budgets and
stops the workers pacing themselves to the carrier, the relay and the push
providers ([ADR-157](../../docs/backend-architecture/adr/ADR-157-the-gateways-budgets-and-the-workers-third-party-pacing-are-options-only-a-stress-override-raises-1488.md)).
Every k6 client shares one address, so against the baseline stack the
`anonymous` window refuses every request past
`GatewayRateLimitOptions.AnonymousPermitLimit` a minute, and the run measures
only the limiter. The simulators answer at once, so the workers'
pacing models a capacity the run has no reason to measure. With both lifted,
what gives out first is the software itself or the host's resources, and the
resource readings tell those two apart. The SLO run never uses the override.

**The users.** `setup()` makes a `stress-run` client and one user per VU at
the highest step, `stress-0001` upwards, through Keycloak's admin API, as the
SLO client is made at runtime and never in `realm-export.json`. It gives each
user a fresh password for the run, held in memory and never written, and
grants `orders:write`. Each VU signs in once and refreshes its token before it
lapses. Keycloak's calls are reported as their own operation, `token`, and
left out of the journey's figures. A Keycloak user's id is a GUID, which is
the `sub` an order's owner is bound from. The client's service account holds
`inventory:admin`, and `setup()` uses it to restock a product that cannot
cover its share of every order the staircase could place.

**The journey.** Each iteration is one visit: the anonymous product listing,
up to `PRODUCT_VIEWS_MAX` product pages, the saved address (saved on a first
visit), a checkout quote, and in an `ORDER_SHARE` of visits an order followed
by *my orders* through the BFF. Users pause between `THINK_MIN_SECONDS` and
`THINK_MAX_SECONDS` between steps. Each is a constant at the top of
`stress.js`, with the reason for its value.
Orders are spread over every product `STRESS_PRODUCTS` names, which loads the
saga and the queues; one id there is the hot-row variant, which loads
Inventory's reservation of that one row instead.

**The staircase.** `STRESS_PROFILE` names a level, one of `steps.js`'s
`LEVELS` (`low`, `medium`, `high` and `xhigh`), each starting where the one
below ends, reached by `LEVEL_RAMP_SECONDS` ramps and held for
`LEVEL_HOLD_SECONDS`. Every level keeps doubling past its last step while the
next stays within `STRESS_MAX_USERS`, whose default is `DEFAULT_MAX_USERS`,
so a level that holds throughout ends on the cap and never on its own top.
A cap below a level's own steps drops them, and one below its first refuses
the level. Unset, the staircase is `DEFAULT_STEPS`, with
`DEFAULT_RAMP_SECONDS` ramps and `DEFAULT_HOLD_SECONDS` holds.
`STRESS_STEPS` replaces either as written and is never doubled, and
`STRESS_RAMP_SECONDS` and `STRESS_HOLD_SECONDS` override the ramps and holds;
a staircase above the cap is refused. A
step is measured over its hold alone, since a ramp mixes two loads. A hold
shorter than `summarise.mjs`'s `MINIMUM_RANGE_SECONDS` is read over that span,
reaching into its ramp, because the services export their meters once a
minute and a rate needs two exports.

**What each step records.** From k6: p50, p95, p99 and max per operation,
and per step the request rate and the failures, split into 429s, other 4xx,
5xx, timeouts and refused connections. From Prometheus, read through Grafana
as the SLO run reads it and evaluated at the step's end: the slowest request
types' `request.duration`, the oldest outbox row per service, the deepest
queues, the consumed queues with the lowest consumer utilisation, the
`_error` queues, `messaging.delivery.lag` overall and for the BFF's consumers
(the BFF's read model is their projection, so that lag is its staleness),
`orders.fulfilment.duration` and the share of orders confirmed, both overdue
gauges, and, from the runtime meter every service exports, the work waiting
for a thread-pool thread and the time collections paused it. From
`stress/sample.mjs`, every `INTERVAL_MS`: the machine's CPU as a share of
all its processors, each container's CPU and memory from
`docker stats`, where a CPU figure is a share of one core and passes 100 on
several, and SQL Server's deadlocks and lock, page-latch and log-flush waits.
From the `judge`, the VUs k6 ran at each hold's end.
Nothing is added to the stack to read any of them. What could only be read by
adding a component is listed in the summary under *Not measured*.

**The knee** is the first step at which one of three signals crosses:
requests failing at more than `ERROR_RATE_LIMIT`; the journey's p95 growing
by `P95_JUMP_RATIO` on the step before, and by more than `P95_JUMP_FLOOR_MS`;
or the oldest outbox row or the queued messages still growing by
`GROWTH_RATIO` over the second half of the hold. The thresholds are
`stress/report.js`'s constants, held by `stress/report.test.js`. Each signal
is read once a step, so the order in which two crossed within one step cannot
be seen, and the summary names every signal that crossed at the knee.

**Why it ended** is read at the last step that carried requests, by
`stress/report.js`'s `ending()`. k6 runs on the machine the stack runs on, so
at high counts the client can give out first: a step at which k6 ran fewer
VUs than it asked for, or the machine's CPU stayed at `HOST_CPU_LIMIT` over
the hold, measured the workstation, and the run ended on the `generator`,
whatever stopped it. Otherwise it ended on the `knee` stop, the `errors`
abort, the `cap` at a level's top, or `held` at the top of a fixed staircase
whose every step held; `interrupted` is a run cut short with no rule firing,
and `not measured` one that ran no step.

**The output** goes to `artifacts/stress/<run>/`, which git ignores:
`raw.json` from k6, `samples.jsonl` from the sampler, and `summary.json` and
`summary.md` from `stress/summarise.mjs`. The summary records the machine's
processor, memory and operating system, Docker's processors and memory, the
commit and the k6 version, since a local number means nothing without them.

### Running it

The blocks run in order in one Git Bash shell from the repository root, as
the SLO run's do. Node is the one the frontend already needs; `node --test`
runs the run's own suites.

**1. Bring the stack up with the override**, built from this commit, and let it
idle a minute, as the SLO run's step 1 does:

```bash
export MSYS_NO_PATHCONV=1
docker compose -f deploy/compose/docker-compose.yml -f deploy/compose/docker-compose.stress.yml \
    up -d --build --wait
```

**2. Choose the products.** Every seeded product Ordering holds as available,
in the first one's currency, whose price ends in none of the pence the payment
simulator scripts, as the SLO run's step 4 chooses one:

```bash
rows=$(docker exec commerce-sql-1 sh -c 'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" exec "$@"' sh \
    /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -d Ordering -h -1 -W -s , \
    -Q "SET NOCOUNT ON; SELECT LOWER(CONVERT(char(36), ProductId)), Currency FROM ordering.ProductPrices
        WHERE IsAvailable = 1 AND CONVERT(char(36), ProductId) LIKE '5EED0000-%'
        AND CAST(ROUND(Amount * 100, 0) AS bigint) % 100 NOT IN (1, 2, 5, 9) ORDER BY Currency, ProductId" \
    | tr -d '\r')
currency=$(echo "$rows" | head -1 | cut -d, -f2)
products=$(echo "$rows" | grep ",$currency\$" | cut -d, -f1 | paste -sd, -)
echo "currency: ${currency:-none}, products: ${products:-none}"
```

**3. Run it, with the sampler beside it, then summarise.** The admin password
is §14.1's local default and reaches k6 through the environment, never `-e`.
`--address` turns on k6's REST API, which the knee stop reads; a level, a
stop rule or a cap is one more `-e` line, such as
`-e STRESS_PROFILE=medium`, `-e STRESS_STOP=errors` or
`-e STRESS_MAX_USERS=6400`. The sampler stops itself once k6 has written its
results:

```bash
out=artifacts/stress/$(date +%Y%m%d-%H%M%S)
mkdir -p "$out"
node deploy/observability/stress/sample.mjs "$out" &
export KEYCLOAK_ADMIN_PASSWORD=admin
k6 run --address 127.0.0.1:6565 \
  -e BASE_URL=http://localhost:5000 \
  -e KEYCLOAK_URL=http://localhost:8080 \
  -e STRESS_PRODUCTS="$products" \
  -e STRESS_CURRENCY="$currency" \
  -e STRESS_OUT="$out" \
  deploy/observability/stress/stress.js
echo "k6 exit: $?"
wait
node deploy/observability/stress/summarise.mjs "$out"
```

k6 exits 0 when no rule stopped the run; 108 when the knee stop did, which
`summary.md` marks `(knee stop)`; and 99 when a step's errors passed the abort
rate under `errors`, which `summary.md` marks `(past the abort rate)`. Either
stop ends the run there unless it was the last step. 107 is a refusal, of a
variable at start or from `setup()`, with no load driven. `summarise.mjs`
prints three machine-readable lines: `Knee:`, then `Ended:`, the reason
first and then the step, the level and the stop rule, as in
`Ended: knee at step 4 (800 VUs); level low, stop rule knee`, then
`Summary:` and the summary's path.

**4. Go back to the normal profile** by bringing the stack up without the
override. Nothing else needs undoing:

```bash
docker compose -f deploy/compose/docker-compose.yml up -d --wait
```

**What a run leaves.** The `stress-run` client and its users stay in the
running realm until Keycloak's container is recreated, and the next run finds
them and resets their passwords. The orders stay, owned by the users, and go
on through the saga, and their bookings and notices are still draining when
k6 exits. Wait until both overdue gauges are near zero, as the SLO run's last
block reads them, before the next run; after step 4 the workers drain at their
paced rate again.
