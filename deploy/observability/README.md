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
