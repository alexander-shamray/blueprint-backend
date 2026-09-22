# Shipping PR-7 — chart, deploy target and canary — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Shipping deployable, and give the platform the two alerts a
worker's lateness shows up in. `deploy/helm/shipping` is Ordering's chart with
the Service and the Ingress declared off and the autoscaler with them; the
library chart grows the three capabilities Shipping's host reads eagerly and
admits a second chart to the one it already had; `smoke.sh`, the umbrella, the
canary map and the deploy menu each gain an entry with a gate that reads it;
and §13.6 gains its first queue-backlog and delivery-lag rules with the one
runbook they share.

**Architecture:** `deploy/helm/shipping` is `deploy/helm/ordering` with the
workload renamed and four values decided rather than copied — `service.enabled:
false`, `ingress.enabled: false`, `redis.enabled: false` and `autoscaling.
enabled: false` with `replicaCount: 3`. The library chart has no generic
variable — every capability is a guarded block — so Shipping's four eagerly
read key groups arrive as blocks on `paymentProvider`'s pattern: `carrier`
(address plus credential), `addressSource` (one address), `jurisdiction` (two
windows) and the existing `identity.clientCredentials`, whose chart comparison
stops naming one chart and names two. Each is required when its block is on and
refused when its settings are present and the block is off, in both directions.
The alerts are platform-wide and grouped the way §13.6 groups its others:
Shipping is why they arrive, not what they select on.

**Tech Stack:** Helm 3 templates, bash (`smoke.sh`), Prometheus rules, Python
3.12 (`canary.py`, `check.py`), GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-09-22-shipping-service-design.md`,
sections 3 (PR-7's row), 10 (the chart, its capabilities, the scaling decision,
the canary row and the readiness set), 11 (the two rules and the shared
runbook) and 13 (§13.6 and §15.3).

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class D.** Touch set: `deploy/helm/**`, `deploy/canary/**`,
  `deploy/observability/**`, `docs/runbooks/queue-backlog.md`,
  `docs/backend-architecture/13-observability.md`,
  `docs/backend-architecture/15-cicd-deployment.md`, `docs/secrets.md`,
  `.github/workflows/deploy.yml`, `.github/workflows/helm.yml`.
  Reasons, since the row above is paths only: the chart, the library chart and
  the smoke gate are `deploy/helm`; the workload map and its suite are
  `deploy/canary`; the two rules and `SHARED_RUNBOOKS` are
  `deploy/observability`; the runbook is the procedure those two rules share;
  §13.6 and §13.9's tables are the chapter `check.py` reads; §15.3 and §15.4
  are the chart chapter and the inventory; `docs/secrets.md` is where the
  Helm place of a required key is stated; and the two workflows are the gate
  and the deploy menu.
- **No `src/**` and no `tests/**`, and that is a decision rather than an
  omission.** Spec section 10 asks for a test that the carrier, Ordering and
  Keycloak are not in the readiness set, and that test is **PR-5's**:
  `tests/Shipping.Worker.Tests/HostSmokeTests.cs`'s
  `Ready_probe_reports_the_sql_and_bus_checks_and_no_shared_dependency`, which
  that plan's Task 5 step 3 writes. The readiness set is the host's
  registration, and the pull request that adds the last dependency somebody
  would be tempted to put in it is the one that must refuse it — not the one
  that writes the probe's chart values three PRs later. So this PR stays a
  clean Class D, `classes.yml` needs no change, and the class row is one
  letter.
- Depends on **PR-6 having merged**, and therefore on PR-1 through PR-5: the
  two images exist in CI's matrix, the host reads `ConnectionStrings__Shipping`,
  `ConnectionStrings__RabbitMq`, `Identity__Authority`,
  `OTEL_EXPORTER_OTLP_ENDPOINT`, `Carrier__BaseUrl`, `Carrier__ApiKey`,
  `AddressSource__BaseUrl`, the three `Identity__Client__*` keys and the two
  `Jurisdiction__*` windows, `shipping-events` is bound, and both integration
  events are published. A chart that named a key before its host read it would
  be the failure §15.3's own rule forbids.
- **Every key this chart renders is one the host refuses to start without**,
  which is why each is required at render rather than defaulted. The spec's
  section 10 names two of the four groups; the other two are §15.4's own rows,
  written by PR-5 and PR-6, and the blueprint beats the spec where they differ.
- Every list touched has a check that reads it: `smoke.sh` for the chart lists
  and the two new partitions, `canary.py check` for the workload map and the
  dispatch menu, `check.py` for the runbook pairing and the chapter's tables,
  and `deploy.yml`'s own `pull_request` run for the menu.
- **The comment gate reads `.yaml` and `.sh` and does not read `.tpl`.** So
  every comment block this PR adds to `values.yaml`, `smoke.sh`,
  `platform-alerts.yaml` and the workflows is at most ten lines, carries no
  emphasis, no issue or PR number and no history — and a block already past the
  limit that this PR edits is cut and rewritten rather than appended to.
- **ADR-052 is not edited.** Its closing table names the places that say the
  BFF is alone and says the pull request that builds each service amends **the
  places**; the two Helm rows in it are `_helpers.tpl` and `smoke.sh`, and this
  PR amends those. An ADR is superseded, never rewritten.

---

### Task 1: The library chart's capabilities, and a second credentialed chart

**Files:**
- Modify: `deploy/helm/common/templates/_helpers.tpl` — `commerce.config` gains
  `Carrier__BaseUrl`, `AddressSource__BaseUrl` and the two `Jurisdiction__*`
  windows; `commerce.env` gains `Carrier__ApiKey` from a `secretKeyRef`, three
  coherence guards and one upward guard, and its `identity.clientCredentials`
  comparison names two charts
- Modify: `deploy/helm/smoke.sh` — the capability section, and the four
  credential assertions ADR-052 marks **asserted**

- [ ] **Step 1: Write the failing smoke assertions**

Two edits, both red until Task 2 creates the chart.

First, `refuses_chart` learns the per-chart overlay, because every negative
test below renders a chart whose capabilities are required and would otherwise
fail on the wrong value. The helper's own `--set` arguments come after the
overlay, so a case overriding one of them still wins — Helm takes the last
assignment:

```bash
refuses_chart() {
    # refuses_chart <chart> <label> <needle> <helm args...>
    local chart="$1" label="$2" needle="$3"
    shift 3
    if "$HELM" template "$chart" "$CHARTS_DIR/$chart" --set-string "image.tag=$TAG" \
        $(overlay_for "$chart") "$@" >"$OUT/cap.txt" 2>&1; then
        fail "$label — it rendered instead"
    else
        check "$label" grep -q "$needle" "$OUT/cap.txt"
    fi
}
```

Second, a new section after the `paymentProvider` one, inside `Rendering`:

```bash
# --------------------------------------------------------------------------
section 'The worker chart declares four capabilities, and each is required'
# --------------------------------------------------------------------------
# Shipping's host reads every key below before it will start (§15.4), so each
# state that renders cleanly here is a pod that never starts. Asserted by
# PLACEMENT and not by presence: §15.4 puts the credential and the three
# addresses in different Kinds, and a global grep proves neither.
SHIPPING_RENDER=$("$HELM" template shipping "$CHARTS_DIR/shipping" \
    --set-string image.tag="$TAG" $(overlay_for shipping))
printf '%s\n' "$SHIPPING_RENDER" >"$OUT/shipping-capability.yaml"

check 'shipping: Carrier__BaseUrl is in the ConfigMap' \
    awk '/^kind: ConfigMap$/ { in_cm = 1 }
         /^---$/ { in_cm = 0 }
         in_cm && /^ *Carrier__BaseUrl: "https:\/\/carrier\.example\.invalid\/"$/ { found = 1 }
         END { exit found ? 0 : 1 }' "$OUT/shipping-capability.yaml"
check 'shipping: AddressSource__BaseUrl is in the ConfigMap' \
    awk '/^kind: ConfigMap$/ { in_cm = 1 }
         /^---$/ { in_cm = 0 }
         in_cm && /^ *AddressSource__BaseUrl: / { found = 1 }
         END { exit found ? 0 : 1 }' "$OUT/shipping-capability.yaml"
check 'shipping: both jurisdiction windows are in the ConfigMap' \
    awk '/^kind: ConfigMap$/ { in_cm = 1 }
         /^---$/ { in_cm = 0 }
         in_cm && /^ *Jurisdiction__[A-Za-z]+Retention: / { n++ }
         END { exit n == 2 ? 0 : 1 }' "$OUT/shipping-capability.yaml"
check 'shipping: Carrier__ApiKey comes from a secretKeyRef, not a literal' \
    awk '/^ *- name: Carrier__ApiKey$/ { at = NR }
         at && NR == at + 1 && /^ *valueFrom:$/ { vf = 1 }
         vf && NR == at + 2 && /^ *secretKeyRef:$/ { found = 1 }
         END { exit found ? 0 : 1 }' "$OUT/shipping-capability.yaml"
check 'shipping: no ConfigMap carries Carrier__ApiKey' \
    awk '/^kind: ConfigMap$/ { in_cm = 1 }
         /^---$/ { in_cm = 0 }
         in_cm && /Carrier__ApiKey/ { found = 1 }
         END { exit found ? 1 : 0 }' "$OUT/shipping-capability.yaml"

refuses_chart shipping 'a cleared carrier address fails the render' \
    'carrier.baseUrl is required' --set-string 'carrier.baseUrl='
refuses_chart shipping 'a plain-HTTP carrier address fails the render' \
    'HTTPS address this chart will accept' \
    --set-string 'carrier.baseUrl=http://carrier.example.invalid/'
refuses_chart shipping 'a carrier setting with the capability off fails the render' \
    'carrier.enabled is false' --set carrier.enabled=false
refuses_chart shipping 'the carrier capability off and cleared fails the render' \
    'carrier.enabled is false on the shipping chart' \
    --set carrier.enabled=false --set carrier.apiKeySecretRef=null \
    --set-string 'carrier.baseUrl='
refuses_chart shipping 'a cleared address source fails the render' \
    'addressSource.baseUrl is required' --set-string 'addressSource.baseUrl='
refuses_chart shipping 'a cleared retention window fails the render' \
    'jurisdiction.addressRetention is required' \
    --set-string 'jurisdiction.addressRetention='
refuses_chart shipping 'a jurisdiction the capability is off for fails the render' \
    'jurisdiction.enabled is false' --set jurisdiction.enabled=false
pass 'the worker chart renders four capabilities and refuses each half state'
```

The plain-HTTP case is the one negative shape repeated from the provider's
matrix, and only one: `commerce.requireUrl` is a single helper and
`refuses_payments` already exercises every shape it rejects. What is new here
is that the carrier's address goes through it at all, which is what that one
case asserts.

- [ ] **Step 2: Run it, expect red**

```bash
bash deploy/helm/smoke.sh
```

Expected: the run aborts before the new section — `SERVICE_CHARTS` still
matches the directories, but `helm template shipping` fails under `set -e` in
the section above, because `deploy/helm/shipping` does not exist. That is the
red step; the section turns green at the end of Task 2.

- [ ] **Step 3: Write the library templates**

In `commerce.config`, after the `paymentProvider` block:

```yaml
{{- if (.Values.carrier).enabled }}
{{- /*
§3.2's carrier, on the provider's pattern one service over and for the same
reason: an address is not a credential, so it is Config, and it is required
because the host parses it before it will start (§15.4). HTTPS unconditionally,
which `commerce.requireUrl` argues — a chart is how a cluster is deployed and
sets no environment, so Production is what runs.
*/}}
Carrier__BaseUrl: {{ include "commerce.requireUrl" (list .Values.carrier.baseUrl "carrier.baseUrl is required when carrier.enabled: Shipping's carrier registration reads it eagerly and throws naming the key, so the host does not start (§15.4).") | quote }}
{{- end }}
{{- if (.Values.addressSource).enabled }}
{{- /*
ADR-052's address read, and the one required address here that `requireUrl`
must NOT see. TLS terminates at the Ingress (§10.1) and every hop past it is
plain http, so Ordering's HTTP/2-only endpoint is dialled over cleartext
exactly as `PricingHop.cs` dials Catalog's — and the host's own guard says so,
refusing user information and accepting either scheme.
*/}}
AddressSource__BaseUrl: {{ include "commerce.require" (list .Values.addressSource.baseUrl "addressSource.baseUrl is required when addressSource.enabled: the worker resolves the address owner eagerly (ADR-052) and does not start without it (§15.4).") | quote }}
{{- end }}
{{- if (.Values.jurisdiction).enabled }}
{{- /*
ADR-053's two statutory windows, required and never defaulted: a window is a
fact about where a deployment runs, and a chart that guessed one would pick
somebody's statute for them. The record says refused rather than clamped, and
this is the render-time half of that — the host's own refusal is at start.
*/}}
Jurisdiction__AddressRetention: {{ include "commerce.require" (list .Values.jurisdiction.addressRetention "jurisdiction.addressRetention is required when jurisdiction.enabled: ADR-053 makes the window a value the deployment is given, and ShippingJurisdictionOptions refuses to boot without it.") | quote }}
Jurisdiction__TrackingRetention: {{ include "commerce.require" (list .Values.jurisdiction.trackingRetention "jurisdiction.trackingRetention is required when jurisdiction.enabled: ADR-053's second window, on the same terms.") | quote }}
{{- end }}
```

In `commerce.env`'s coherence block, after the `paymentProvider` guard:

```yaml
{{- if and (or (.Values.carrier).apiKeySecretRef (.Values.carrier).baseUrl) (not (.Values.carrier).enabled) }}
{{- fail "carrier.enabled is false but a carrier setting is set. Shipping reads both carrier keys eagerly (§15.4), so this renders cleanly and the host does not start. A capability is a fact about the code, not an environment setting." }}
{{- end }}
{{- if and (.Values.addressSource).baseUrl (not (.Values.addressSource).enabled) }}
{{- fail "addressSource.enabled is false but addressSource.baseUrl is set. The worker resolves ADR-052's address owner at startup, so this renders cleanly and the host does not start." }}
{{- end }}
{{- if and (or (.Values.jurisdiction).addressRetention (.Values.jurisdiction).trackingRetention) (not (.Values.jurisdiction).enabled) }}
{{- fail "jurisdiction.enabled is false but a jurisdiction window is set. ShippingJurisdictionOptions is validated at start (ADR-053), so this renders cleanly and the host does not start." }}
{{- end }}
```

and, beside the upward guards that keep a credential inside its own chart:

```yaml
{{- if and (.Values.carrier).enabled (ne .Chart.Name "shipping") }}
{{- fail (printf "carrier.enabled is true on the %s chart, and only shipping books with a carrier (§3.2). This would mount the carrier's Secret into a pod that never reads it — a credential crossing a service boundary, which no value in an environment file may do." .Chart.Name) }}
{{- end }}
```

The `identity.clientCredentials` upward guard stops naming one chart. Before:

```yaml
{{- if and .Values.identity.clientCredentials (ne .Chart.Name "web-bff") }}
{{- fail (printf "identity.clientCredentials is true on the %s chart, and the BFF is the one host that calls a peer synchronously (§9.7, ADR-017). This would mount the BFF's client secret into a pod that never presents it — a credential crossing a service boundary, which no value in an environment file may do." .Chart.Name) }}
{{- end }}
```

After:

```yaml
{{- if and .Values.identity.clientCredentials (not (has .Chart.Name (list "web-bff" "shipping"))) }}
{{- fail (printf "identity.clientCredentials is true on the %s chart, and the two hosts that call a peer synchronously are the BFF (§9.7, ADR-017) and Shipping's worker (ADR-052). This would mount one of their client secrets into a pod that never presents it — a credential crossing a service boundary, which no value in an environment file may do." .Chart.Name) }}
{{- end }}
```

A list rather than a second `ne`: the set is the thing ADR-052 moved, and a
third chart joining it edits one literal. The comment above the
`clientCredentials` block in `commerce.config` says "A second chart growing
these is a design change, not a configuration change." — the sentence survives
and its subject does not, so it becomes "A third chart growing these is a
design change, not a configuration change; ADR-052 is the record that made it
two."

`commerce.env`'s secret half gains, after `PaymentProvider__ApiKey`:

```yaml
{{- if (.Values.carrier).enabled }}
- name: Carrier__ApiKey
  valueFrom:
    secretKeyRef:
      name: {{ include "commerce.require" (list .Values.carrier.apiKeySecretRef.name "carrier.apiKeySecretRef.name is required when carrier.enabled. The key is a reference, never a value (§15.3).") | quote }}
      key: {{ include "commerce.require" (list .Values.carrier.apiKeySecretRef.key "carrier.apiKeySecretRef.key is required when carrier.enabled.") | quote }}
{{- end }}
```

`(.Values.carrier).enabled` rather than the dotted form, and the same for the
other two blocks: the six other charts carry no such map, and the
parenthesised form reads a missing one as empty where the dotted form fails
the render.

- [ ] **Step 4: The four credential assertions ADR-052 marks asserted**

In `smoke.sh`'s first section, the count becomes a set. Before:

```bash
credentialed="$(grep -l 'clientCredentials: true' "$CHARTS_DIR"/*/values.yaml | wc -l | tr -d ' ')"
check "exactly one chart declares client credentials (found $credentialed)" \
    test "$credentialed" -eq 1
```

After:

```bash
# ADR-052 made ADR-017's budget two: the BFF's pricing hop and Shipping's
# address read. Named rather than counted — a count of two is satisfied by the
# wrong two charts, and which host holds a grant is the whole claim. Read from
# the values files rather than from a render, because a second chart setting it
# renders nothing at all and the run would abort before this reported.
CREDENTIALED_CHARTS="shipping web-bff"
credentialed="$(grep -l 'clientCredentials: true' "$CHARTS_DIR"/*/values.yaml |
    sed -E 's|.*/([^/]+)/values\.yaml|\1|' | sort | tr '\n' ' ' | sed 's/ *$//')"
want_credentialed="$(printf '%s\n' $CREDENTIALED_CHARTS | sort | tr '\n' ' ' | sed 's/ *$//')"
if [ "$credentialed" = "$want_credentialed" ]; then
    pass "exactly the charts whose host calls a peer declare client credentials ($credentialed)"
else
    fail "charts declaring client credentials ($credentialed) are not ($want_credentialed)"
fi
```

Beside the BFF's source assertion, Shipping's:

```bash
check "the worker attaches ClientCredentialsHandler in src/, so its chart declares credentials" \
    grep -rq 'ClientCredentialsHandler' "$ROOT/src/Services/Shipping"
```

The BFF's own line still names `ServiceIdentityOptions`, because that is what
that host binds; Shipping's names the handler, because since PR-3b the options
type lives in `Common.Infrastructure` and finding it under `src/Services/` is
what the pipeline attaches rather than what the host declares.

In the client-credentials section, the two workload assertions and the loop:

```bash
check 'exactly two workloads in the platform hold a client secret' \
    test "$(count 'Identity__Client__ClientSecret' "$OUT/platform.yaml")" -eq 2
check 'one of them is the BFF' \
    test "$(count 'Identity__Client__ClientSecret' "$OUT/web-bff.yaml")" -eq 1
check 'and the other is the worker' \
    test "$(count 'Identity__Client__ClientSecret' "$OUT/shipping.yaml")" -eq 1
# And they read DIFFERENT Secrets, which the counts above cannot see: a values
# file copied from the BFF's leaves Shipping's pod mounting the BFF's grant,
# renders cleanly, passes all three counts, and gives one host another's
# identity (§11.5).
check 'and the two read different Secrets' \
    test "$(awk '/- name: Identity__Client__ClientSecret/ { want = 1; next }
                 want && /name: / { print $2; want = 0 }' "$OUT/platform.yaml" |
        sort -u | wc -l)" -eq 2

for chart in $SERVICE_CHARTS; do
    [ "$chart" = payments ] || refuses_foreign "$chart" \
        "the provider capability is refused on $chart" 'only payments registers a provider' \
        --set paymentProvider.enabled=true \
        --set-string paymentProvider.baseUrl=https://psp.example.invalid/ \
        --set-string paymentProvider.apiKeySecretRef.name=payments-provider \
        --set-string paymentProvider.apiKeySecretRef.key=api-key
    [ "$chart" = shipping ] || refuses_foreign "$chart" \
        "the carrier capability is refused on $chart" 'only shipping books with a carrier' \
        --set carrier.enabled=true \
        --set-string carrier.baseUrl=https://carrier.example.invalid/ \
        --set-string carrier.apiKeySecretRef.name=shipping-carrier \
        --set-string carrier.apiKeySecretRef.key=api-key
    case "$chart" in
        web-bff|shipping) ;;
        *) refuses_foreign "$chart" \
            "client credentials are refused on $chart" 'the two hosts that call a peer' \
            --set identity.clientCredentials=true \
            --set-string identity.clientId=x --set-string identity.scope=y \
            --set-string identity.clientSecretRef.name=web-bff-identity \
            --set-string identity.clientSecretRef.key=secret ;;
    esac
done
```

The section's heading and its opening comment follow: `Client credentials:
exactly one chart (§11.5, §15.3)` becomes `Client credentials: the hosts that
call a peer (§11.5, §15.3, ADR-052)`, and "A second chart growing an
identity.clientId is a design change" becomes "A third chart growing an
identity.clientId is a design change, not a configuration change: it means a
host started calling a peer synchronously, which is ADR-017's budget being
spent a third time."

- [ ] **Step 5: Commit with Task 2** — the section needs the chart.

---

### Task 2: The chart

**Files:**
- Create: `deploy/helm/shipping/Chart.yaml`, `values.yaml`
- Create: `deploy/helm/shipping/templates/{configmap,deployment,hpa,ingress,migrate-job,pdb,service}.yaml`
- Create: `deploy/helm/shipping/templates/capabilities.yaml`

- [ ] **Step 1: Copy Ordering's chart and rename the workload**

```bash
cp -r deploy/helm/ordering deploy/helm/shipping
rm -rf deploy/helm/shipping/charts deploy/helm/shipping/Chart.lock
```

`Chart.yaml`:

```yaml
apiVersion: v2
name: shipping
description: >-
  Shipping (§4.1) — the platform's first worker: a migrator hook, no Service
  and no Ingress (§3.2, §15.3), and the two outbound calls §9 gives it.
type: application
version: 0.1.0
dependencies:
  - name: commerce-common
    version: 0.1.0
    repository: file://../common
```

**The seven one-line templates are unchanged**, `service.yaml`, `ingress.yaml`
and `hpa.yaml` included. §15.3 gives a worker the same chart minus the Service
and the Ingress **by value**, and `_service.tpl`'s own comment argues the key
is `false` rather than absent; deleting the templates instead would make
`service.enabled: true` a value that renders nothing, which is the shape every
guard in the library refuses.

- [ ] **Step 2: Write `values.yaml`**

Every value and comment naming Ordering is replaced, and then every comment is
read again rather than only those that name it: Ordering's file recounts how a
value was arrived at, names issues and stresses words, and the comment gate
fails an added line inside such a block. Each comment here says one reason and
cites its owner — `_helpers.tpl`, §13.6, §15.3, ADR-052, ADR-053 — or is cut.
When done,
`grep -n -iE "ordering|PR-|#[0-9]|\*\*" deploy/helm/shipping/values.yaml`
prints nothing.

```yaml
# Shipping, and the first chart in this tree with no front door. §3.2 gives it
# no API, so §15.3's worker shape is the chart: no Service, no Ingress, and
# probes that address the container port directly.

workload:
  # Nothing dials this workload — it is here because every object a chart
  # renders is named from it (_helpers.tpl), not because a peer resolves it.
  name: shipping-worker

# Three, and a decision rather than a copy. CPU is the wrong signal for a host
# that waits on a queue and a third party: it idles through a carrier outage
# and through a backlog alike, so an autoscaler would scale on noise. Three is
# for availability across a node drain, and §13.6's queue-backlog rule is how
# anyone finds out it is too few.
replicaCount: 3

# §15.5's canary track, and OFF is what every ordinary release means. Turned
# on, this chart renders the second of two Deployments; the Service, Ingress,
# HPA and PodDisruptionBudget are suppressed because the stable release owns
# those names (ADR-022). Set by the rollout, not by an operator.
canary:
  enabled: false

image:
  # Registry namespace only. Each workload appends its own name, so the chart
  # can reference both images (§7.4) from one tag.
  registry: registry.example.com/commerce
  # `-worker`, not `-api`: the image is the host, and this host is a worker.
  api: shipping-worker
  migrator: shipping-migrator
  # Supplied by CI, never "latest". Deliberately empty rather than a default: a
  # deploy that cannot name its tag must fail, not roll something (§15.3).
  tag: ""
  pullPolicy: IfNotPresent

ports:
  # §13.5's health endpoint and nothing else. Declared because the kubelet
  # reaches a container port without a Service in front of it (§15.3).
  - name: http
    containerPort: 8080

resources:
  requests: { cpu: 100m, memory: 256Mi }
  limits:   { memory: 512Mi }          # No CPU limit — §15.3.

migrationJob:
  resources:
    requests: { cpu: 50m, memory: 128Mi }
    limits:   { memory: 256Mi }

# Off, which is the other half of replicaCount above. The key is written down
# rather than omitted, on service.enabled's terms (§15.3): a capability is a
# claim a chart makes, not one to infer from a missing key.
autoscaling:
  enabled: false

podDisruptionBudget:
  enabled: true
  minAvailable: 2

# Two receive endpoints, an outbox dispatcher and two workers to drain, each
# with a call in flight bounded by CarrierHop.TotalRequestTimeout and
# AddressHop.TotalRequestTimeout. No number is written here: the value must
# exceed HostOptions.ShutdownTimeout, and _deployment.tpl carries the argument.
terminationGracePeriodSeconds: 45

probes:
  probePort: http
  liveness:  { path: /health/live,  initialDelaySeconds: 10, periodSeconds: 10 }
  readiness: { path: /health/ready, initialDelaySeconds: 5,  periodSeconds: 5 }
  startup:   { path: /health/startup, failureThreshold: 30,  periodSeconds: 2 }

service:
  # False, and the key §15.3's callout is about: a worker's safety comes from
  # having no route, so the absence of a route is the thing to assert.
  enabled: false

ingress:
  # False, and refused with the Service off anyway (_ingress.tpl): an Ingress
  # whose backend does not exist installs cleanly and answers 503.
  enabled: false

identity:
  # The authority, to validate incoming JWTs (§11.2) — and, since ADR-052, the
  # issuer this host asks for its own token.
  authority: https://id.example.com/realms/commerce
  # True, and the second chart in the platform to say so (ADR-052). The worker
  # presents this grant to read a delivery address from its owner; all three
  # values are required by ValidateOnStart (§15.4).
  clientCredentials: true
  clientId: shipping-worker
  scope: commerce-api
  clientSecretRef:
    # Its own Secret, never web-bff-identity: two hosts holding one grant is
    # one host able to act as the other (§11.5).
    name: shipping-identity
    key: client-secret

database:
  enabled: true
  # GetConnectionString("Shipping") at runtime, "ShippingMigrator" in the hook.
  connectionName: Shipping
  runtimeSecretRef:
    name: shipping-database
    key: connection-string
  migratorSecretRef:
    name: shipping-migrator-secret
    key: connection-string

# The bus (§9). One Secret per service, never a shared one: a single broker
# principal is what makes queue arrival a boundary in name only (§9.4,
# ADR-036). The Secret this names must carry a connection string for the
# `shipping-svc` account, whose permissions
# deploy/compose/rabbitmq/definitions.json declares and check_permissions.py
# holds to the code. Provisioning it on a deployed broker is an obligation
# stated and not checked — §15.4's division.
broker:
  enabled: true
  secretRef:
    name: shipping-rabbitmq
    key: connection-string

# No Redis (§8.1): the two workers claim rows under a lease in SQL, nothing is
# cached, and no HTTP write command takes a §8.5 key. Declared rather than
# omitted, as the gateway, the BFF and Payments declare it (§15.3).
redis:
  enabled: false

# §3.2's carrier. The address is an environment value with no default a cluster
# could use — the simulator is Compose's, never a cluster's — so the capability
# refuses to render without one. Both keys are read eagerly, which is why the
# chart beside this file refuses the capability being switched off at all.
carrier:
  enabled: true
  baseUrl: ""
  apiKeySecretRef:
    name: shipping-carrier
    key: api-key

# ADR-052's address owner, and the one address here with a default: the host
# and the port are routing configuration, literal in src/, and the same in
# every cluster — `ordering-api`'s HTTP/2-only endpoint. Plain http, because
# TLS terminates at the Ingress (§10.1) and every hop past it says so.
addressSource:
  enabled: true
  baseUrl: http://ordering-api:8081

# ADR-053's two statutory windows, and neither has a default on purpose: a
# window is a fact about the jurisdiction a deployment runs in, and a chart
# that shipped a plausible one would pick somebody's statute for them. Refused
# rather than clamped, at render and again at start.
jurisdiction:
  enabled: true
  addressRetention: ""
  trackingRetention: ""

observability:
  otlpEndpoint: http://otel-collector.observability:4317

extraConfigMaps: []
```

- [ ] **Step 3: Write `templates/capabilities.yaml`**

It renders nothing and refuses four states, on Payments' template's terms:

```yaml
{{- /*
Shipping registers each of these unconditionally, so on this chart every one is
a fact about the code: switched off with its settings cleared, the library
chart's coherence guard has nothing to refuse and the pod does not start. So
this chart refuses the switches themselves (§15.4).
*/}}
{{- if not (.Values.carrier).enabled }}
{{- fail "carrier.enabled is false on the shipping chart. Shipping registers its carrier unconditionally and does not start without both keys (§15.4)." }}
{{- end }}
{{- if not (.Values.addressSource).enabled }}
{{- fail "addressSource.enabled is false on the shipping chart. The worker resolves ADR-052's address owner at startup and does not start without it." }}
{{- end }}
{{- if not (.Values.jurisdiction).enabled }}
{{- fail "jurisdiction.enabled is false on the shipping chart. ShippingJurisdictionOptions is bound and validated at start (ADR-053), so the host does not start without both windows." }}
{{- end }}
{{- if not .Values.identity.clientCredentials }}
{{- fail "identity.clientCredentials is false on the shipping chart. The worker binds ServiceIdentityOptions with ValidateOnStart (ADR-052) and does not start without all three values (§15.4)." }}
{{- end }}
```

The BFF has no such template and this PR does not give it one: `smoke.sh`'s
`refuses_bff` covers its case through `identity.clientId` staying set, and
widening that is the BFF's chart's own change.

- [ ] **Step 4: Render it, then run the smoke script**

```bash
helm dependency update deploy/helm/shipping
helm template shipping deploy/helm/shipping --set-string image.tag=test \
    --set-string carrier.baseUrl=https://carrier.example.invalid/ \
    --set-string jurisdiction.addressRetention=30.00:00:00 \
    --set-string jurisdiction.trackingRetention=90.00:00:00 |
    grep -E "^kind:|name: shipping|image:|Carrier|AddressSource|Jurisdiction|Identity__Client|Redis"
bash deploy/helm/smoke.sh
```

Expected from the render: `kind: ConfigMap`, `kind: Deployment`, `kind: Job`
and `kind: PodDisruptionBudget` and **no** `Service`, `Ingress` or
`HorizontalPodAutoscaler`; `shipping-worker` as the workload;
`shipping-worker-migrate-test` as the Job; `Carrier__BaseUrl`,
`AddressSource__BaseUrl` and both `Jurisdiction__*` keys in the ConfigMap;
`Carrier__ApiKey` and `Identity__Client__ClientSecret` as `secretKeyRef`s; no
`Redis` line. `smoke.sh` now passes the capability and credential sections and
fails on the chart lists, which is Task 3.

- [ ] **Step 5: Commit**

```bash
git add deploy/helm/common deploy/helm/shipping deploy/helm/smoke.sh
git commit -m "feat(deploy): Shipping's chart, and the library chart's carrier, address and jurisdiction capabilities"
```

The body argues the four decided values, names ADR-052's two Helm rows as the
`fail` and the four assertions this commit moves, and says why a worker's
chart keeps the three templates whose values are off.

---

### Task 3: `smoke.sh`'s lists, and the two partitions

**Files:**
- Modify: `deploy/helm/smoke.sh` — `SERVICE_CHARTS`, `MIGRATOR_CHARTS`,
  `SOURCE_INPUTS`, `overlay_for`, the two umbrella override lists, the
  autoscaling section and its new partition, and the worker-shape section
- Modify: `.github/workflows/helm.yml` — both `paths:` lists

- [ ] **Step 1: Run it to see the list fail**

```bash
bash deploy/helm/smoke.sh
```

Expected: the first section fails — `SERVICE_CHARTS` does not match the chart
directories on disk, which now include `shipping`.

- [ ] **Step 2: Edit every list**

```bash
SERVICE_CHARTS="catalog ordering inventory payments shipping gateway web-bff"
MIGRATOR_CHARTS="catalog ordering inventory payments shipping"
```

`DATABASELESS_CHARTS` is unchanged: Shipping owns a database and a migrator.

`overlay_for` gains the worker's required values, beside Payments':

```bash
overlay_for() {
    case "$1" in
        payments) printf '%s' "--set-string paymentProvider.baseUrl=https://psp.example.invalid/" ;;
        shipping) printf '%s' "--set-string carrier.baseUrl=https://carrier.example.invalid/ --set-string jurisdiction.addressRetention=30.00:00:00 --set-string jurisdiction.trackingRetention=90.00:00:00" ;;
    esac
}
```

`addressSource.baseUrl` is deliberately absent from that overlay: it has a
default the chart ships, because the address owner's Service name and port are
routing configuration rather than an environment's choice.

`SOURCE_INPUTS` gains `src/Services/Shipping` after `src/Services/Payments`,
and `.github/workflows/helm.yml` gains `'src/Services/Shipping/**'` in both its
`pull_request` and `push` `paths:` lists after Payments' line. Left out, a
Shipping source change would skip the gate that reads it, and `smoke.sh`'s own
two-directional check is what refuses that.

Both places that override every subchart's tag for the umbrella — the `helm
lint` loop and the `platform` render — gain four lines beside Payments' two:

```bash
        --set-string "shipping.image.tag=$TAG" \
        --set-string "shipping.carrier.baseUrl=https://carrier.example.invalid/" \
        --set-string "shipping.jurisdiction.addressRetention=30.00:00:00" \
        --set-string "shipping.jurisdiction.trackingRetention=90.00:00:00" \
```

Without them the umbrella fails the capabilities' own guards on the first run,
which is the guards working.

- [ ] **Step 3: The autoscaling partition**

Beside the migrator/databaseless partition at the top, a second one:

```bash
# One chart sets a replica count instead of an autoscaler (§15.3), so the
# assertions below branch — and the branch is driven by a declared list rather
# than by each chart's own values. Read from the values alone, a file flipped
# by itself would change what is asserted rather than fail it, which is this
# repository's most-repeated failure pointed at its newest surface.
AUTOSCALED_CHARTS="catalog ordering inventory payments gateway web-bff"
FIXED_REPLICA_CHARTS="shipping"

scaled="$(printf '%s\n' $AUTOSCALED_CHARTS $FIXED_REPLICA_CHARTS | sort | tr '\n' ' ' | sed 's/ *$//')"
if [ "$scaled" = "$listed" ]; then
    pass 'every chart is classified as autoscaled or fixed-replica'
else
    fail "AUTOSCALED_CHARTS + FIXED_REPLICA_CHARTS ($scaled) do not partition SERVICE_CHARTS ($listed)"
fi
```

The `Autoscaling owns the replica count` section becomes two loops and one,
with each chart's values held to its classification so the list and the file
cannot part:

```bash
# With the HPA on, `replicas` must be absent from the Deployment: present,
# every helm upgrade writes the chart's value and the autoscaler writes it
# back, so a config-only deploy (§15.1) silently scales the service down and it
# climbs out again over the following minutes.
for chart in $AUTOSCALED_CHARTS; do
    check "$chart declares autoscaling in its values" declares "$chart" autoscaling
    check "$chart leaves replicas to its HPA" test "$(count '^ *replicas:' "$OUT/$chart.yaml")" -eq 0
    check "$chart renders an HPA" test "$(count '^kind: HorizontalPodAutoscaler$' "$OUT/$chart.yaml")" -eq 1
done

# And the other side of the branch, which is the whole of the claim for a host
# that waits on a queue: no autoscaler, and therefore a replica count the chart
# owns. Both halves, because either alone is a Deployment defaulted to one pod.
for chart in $FIXED_REPLICA_CHARTS; do
    check "$chart declares no autoscaling in its values" lacks "$chart" autoscaling
    check "$chart renders no HPA" test "$(count '^kind: HorizontalPodAutoscaler$' "$OUT/$chart.yaml")" -eq 0
    check "$chart carries a replica count of its own" test "$(count '^ *replicas:' "$OUT/$chart.yaml")" -eq 1
done

for chart in $SERVICE_CHARTS; do
    check "$chart renders a PodDisruptionBudget" \
        test "$(count '^kind: PodDisruptionBudget$' "$OUT/$chart.yaml")" -eq 1
done
```

- [ ] **Step 4: The worker-shape section**

`Branches no chart takes yet, exercised anyway` rendered Ordering under a name
nothing dials, because no worker chart existed. One does. The surrogate render
is replaced rather than kept beside the real one — a second render of a chart
that never sets the key proves nothing this one does not — and the section's
other three refusals, which are the gateway's, stay exactly where they are:

```bash
# --------------------------------------------------------------------------
section 'The worker shape, on the chart that has it'
# --------------------------------------------------------------------------
# §15.3 specifies `service.enabled: false` and `ingress.enabled: false` for
# Shipping and Notifications. Shipping's chart is on disk, so these read it
# rather than a stand-in rendered under a name nothing dials.
check 'shipping renders no Service' \
    test "$(count '^kind: Service$' "$OUT/shipping.yaml")" -eq 0
check 'and no Ingress' \
    test "$(count '^kind: Ingress$' "$OUT/shipping.yaml")" -eq 0
check 'and the workload survives' \
    test "$(count '^kind: Deployment$' "$OUT/shipping.yaml")" -eq 1
check 'and so does its migration hook' \
    test "$(count '^kind: Job$' "$OUT/shipping.yaml")" -eq 1
check 'and the probes still address the container port directly' \
    test "$(count 'path: /health/ready$' "$OUT/shipping.yaml")" -eq 1
# The pair is not independent: an Ingress backend IS this workload's Service,
# so a values copy that turned the route on would install cleanly and answer
# 503 for every request (_ingress.tpl).
refuses_chart shipping 'an Ingress on the worker chart fails the render' \
    'ingress.enabled requires service.enabled' --set ingress.enabled=true
```

Then, in the canary loop, one comment beside the four suppressed kinds, because
three of them are vacuous on this chart and a reader must not read the pass as
coverage:

```bash
    # On a chart whose stable release renders no Service, Ingress or HPA, three
    # of these four pass by construction; the PodDisruptionBudget is the live
    # one there. Left in the loop rather than special-cased: the claim is about
    # what a canary release must not own, and it is true of every chart.
```

- [ ] **Step 5: Run every gate that reads a list**

```bash
bash deploy/helm/smoke.sh
```

Expected: every section green for all seven charts and the umbrella, including
the new capability section, the two credential set assertions, both autoscaling
loops and the worker-shape section.

- [ ] **Step 6: Commit**

```bash
git add deploy/helm/smoke.sh .github/workflows/helm.yml
git commit -m "feat(deploy): the smoke gate covers the worker chart, its capabilities and its fixed replica count"
```

---

### Task 4: The umbrella, the canary map and the deploy menu

**Files:**
- Modify: `deploy/helm/platform/Chart.yaml` — a `shipping` dependency
- Modify: `deploy/helm/README.md` — the tree fence, the umbrella command, the
  required-values paragraph and the environment example
- Modify: `deploy/canary/canary.json` — the `shipping-worker` workload
- Modify: `deploy/canary/test_canary.py` — one docstring's claim about
  `maxReplicas`
- Modify: `.github/workflows/deploy.yml` — the dispatch `options`, and one
  sentence in the first-rung step

- [ ] **Step 1: The umbrella and the README**

`platform/Chart.yaml`, after `payments`:

```yaml
  - name: shipping
    version: 0.1.0
    repository: file://../shipping
```

`platform/values.yaml` needs nothing: it holds `{}` and argues why, and the
per-environment overlay is the caller's.

`README.md`: `shipping/` joins the bracketed group in the tree fence, and the
group's note becomes "Payments and Shipping each carry one template more: the
guard on the capabilities their hosts register unconditionally." The umbrella
`helm upgrade` example gains

```bash
    --set-string shipping.image.tag="$SHIPPING_SHA" \
```

after Payments' line, because that command as printed fails the umbrella's
required-tag check the moment it has a seventh dependency. The paragraph
beginning "**Payments' provider address is required and is deliberately NOT on
that command line**" gains a sentence: "Shipping's carrier address and its two
retention windows are the same case for the same reason — a third party's
address and a jurisdiction's statute are facts about one deployment, and
ADR-053 says a window is refused rather than guessed." The
`environments/staging.yaml` fence gains:

```yaml
shipping:
  carrier:
    # §3.2's carrier, per cluster. Compose's simulator is never a cluster's.
    baseUrl: https://carrier.staging.example.com/
  jurisdiction:
    # ADR-053: the windows are the deployment's to state, and the chart ships
    # neither, so a render without both is refused.
    addressRetention: "30.00:00:00"
    trackingRetention: "90.00:00:00"
```

- [ ] **Step 2: The canary map**

`canary.json`, after `payments-api`:

```json
    "shipping-worker": {
      "serviceName": "Shipping.Worker",
      "chart": "shipping",
      "signals": ["consume"],
      "httpExemption": "Shipping has no HTTP surface at all (§3.2, ADR-051): its chart renders no Service, no route reaches it, and its only listener is §13.5's health endpoint, which the http templates exclude by route. A declared http signal would read a series that is empty by construction, and an empty result rolls every rung back, so the rollout could only ever fail. Its events arrive on the broker and are judged by consume. The two workers are where this service's risk sits and neither is a consumer, so the carrier and address calls carry no canary signal of their own — §13.6's delivery-lag and queue-backlog rules and shipping.shipments.waiting are what watch them, and docs/runbooks/queue-backlog.md says so."
    },
```

`serviceName` is the entry assembly, which §13.2 takes `service.name` from —
`Shipping.Worker`, never the chart's `shipping` or the workload's
`shipping-worker`. No `consumeExemption`, because the signal is declared; no
`sagaExemption`, because §9.6's saga is Ordering's and an exemption for a
service that registers none is refused by check 9.

**The first rung is not expressible at three replicas, and that is the tool
working rather than a gap this PR closes.** `canary.py plan` refuses 5% against
three stable pods and names the count that would work, exactly as it does for
any chart at its default; the rollout's own step then scales the stable track
to that count before anything rolls, and with no HPA there is no floor to
raise — `deploy.yml`'s `if [ -n "$FLOOR" ]` already states
`autoscaling.enabled: false` as a supported configuration and the cleanup
restores the Deployment's own count. Nothing here changes.

`test_canary.py`'s `test_five_percent_is_expressible_at_nineteen` docstring
says 20 is "the service charts' maxReplicas". It becomes "the maxReplicas of
every chart that autoscales — not the gateway's, which is 30 because every
external request passes through it, and not the worker's, which sets a replica
count instead (§15.3)."

- [ ] **Step 3: The deploy menu**

`deploy.yml`'s `options` becomes

```yaml
        options: [catalog-api, ordering-api, inventory-api, payments-api, shipping-worker, gateway, web-bff]
```

and the first-rung step's comment, which says `maxReplicas` "is exactly this 19
plus one canary on the service charts", becomes "…on the charts that
autoscale, higher on the gateway, and not a bound at all on a worker, which
sets its replica count directly". The block stays inside the gate's limit.

- [ ] **Step 4: Run every gate that reads a list**

```bash
bash deploy/helm/smoke.sh
py -3.12 -m unittest discover -s deploy/canary
py -3.12 deploy/canary/canary.py check
```

Expected: `smoke.sh` green, including `shipping appears in
deploy/canary/canary.json`; the canary suite green; and `canary.py check`
accepting the plan — check 4 resolving `Shipping.Worker` as an entry assembly
and `shipping` as a chart directory, check 8 matching the dispatch menu to the
workload set, and check 9 finding the `AddConsumer` calls PR-5 registered.

- [ ] **Step 5: Commit**

```bash
git add deploy/helm/platform deploy/helm/README.md deploy/canary .github/workflows/deploy.yml
git commit -m "feat(deploy): Shipping joins the umbrella, the canary map and the deploy choice"
```

---

### Task 5: §13.6's two rules and the runbook they share

**Files:**
- Modify: `deploy/observability/alerts/platform-alerts.yaml` — `DeliveryLag`
  and `QueueBacklogGrowing`
- Modify: `deploy/observability/check.py` — one `SHARED_RUNBOOKS` entry
- Create: `docs/runbooks/queue-backlog.md`
- Modify: `docs/runbooks/README.md` — the index row
- Modify: `docs/backend-architecture/13-observability.md` — §13.6's two rows
  and §13.9's row

- [ ] **Step 1: Write the two rules, and see the gate fail**

In `platform-messaging`, after `OutboxAbandonedRows`:

```yaml
      # §13.7's event target as a rule: publish to consumer start, p95 under
      # two seconds. It is the async path's latency number, and it is measured
      # by the consumer that records it — so it stops where the handler starts
      # and can say nothing about what that handler then waited on. The runbook
      # says so rather than this comment's reader inferring it.
      # `for` is kept, unlike the age gauges above: a quantile over a rate is
      # true instantaneously and a single slow burst is not a symptom.
      - alert: DeliveryLag
        expr: |
          histogram_quantile(
            0.95,
            sum by (service_name, le) (rate(messaging_delivery_lag_seconds_bucket[10m]))
          ) > 2
        for: 10m
        labels:
          severity: ticket
          owner: service
        annotations:
          summary: "Event delivery p95 above 2s on {{ $labels.service_name }}"
          description: >-
            Messages are reaching their consumers late, against §13.7's
            two-second target. Nothing has failed: a ticket rather than a page,
            because the rules that fire when a business process has actually
            stopped are the error-queue and outbox-stall ones beside it.
          runbook_url: docs/runbooks/queue-backlog.md
```

In `platform-infrastructure`, after `SkippedQueueDepth`:

```yaml
      # The backlog on a working queue, in OutboxGrowth's shape: over a
      # threshold AND rising. One half alone is a pager people mute — a deep
      # queue draining after a restart is healthy, and a rising shallow one is
      # a Tuesday.
      # The selector excludes the two dead-letter queues, which the rules above
      # own and triage from the opposite end: those are messages nothing will
      # retry, this is messages nobody has reached yet.
      # `sum by (queue)` rather than `max`: the exporter reads the broker, so
      # there is one series per queue and no replica to deduplicate.
      - alert: QueueBacklogGrowing
        expr: |
          sum by (queue) (rabbitmq_queue_messages{queue!~".+_(error|skipped)"}) > 1000
          and
          deriv(sum by (queue) (rabbitmq_queue_messages{queue!~".+_(error|skipped)"})[10m:]) > 0
        for: 10m
        labels:
          severity: ticket
          owner: service
        annotations:
          summary: "Queue {{ $labels.queue }} above 1000 messages and rising"
          description: >-
            A consumer is not keeping up, or there are too few of it. For a
            host with no HTTP traffic this is the capacity signal there is:
            §15.3 gives a worker a fixed replica count precisely because CPU
            would not move while its queue does.
          runbook_url: docs/runbooks/queue-backlog.md
```

The group's opening comment is 11 lines, names an issue and recounts its own
history, so an added line inside it fails the comment gate. It is cut and
rewritten rather than appended to:

```yaml
      # The rules whose signal comes from an exporter rather than from this
      # platform's own meters. They are live because those exporters are part
      # of the target deployment (§15.3), and check.py holds their names in its
      # declared external list: a name it cannot find in C# is either external
      # and declared, or a typo. The members are ErrorQueueDepth,
      # SkippedQueueDepth, QueueBacklogGrowing and MigrationJobFailed, named
      # rather than counted so the next arrival is obvious.
```

Then:

```bash
py -3.12 deploy/observability/check.py
```

Expected, and this is the red step:

```
DeliveryLag: runbook_url names queue-backlog.md, which is not in docs/runbooks
QueueBacklogGrowing: runbook_url names queue-backlog.md, which is not in docs/runbooks
```

- [ ] **Step 2: The runbook, and the declared sharing**

`deploy/observability/check.py`'s `SHARED_RUNBOOKS` gains a second entry:

```python
    "queue-backlog.md":
        "DeliveryLag and QueueBacklogGrowing are one condition read from two "
        "ends — the consumer's own measure of how late a message was, and the "
        "broker's count of how many are still waiting. The procedure is the "
        "same and branches on which fired; two documents would share their "
        "first three steps and drift on the fourth.",
```

`docs/runbooks/queue-backlog.md`, in `error-rate.md`'s form — a header table,
what it means for a user, the branch, the queries with real names, the
lookalike, how to close it, and what it does not cover:

```markdown
# Runbook — queue backlog and delivery lag

| | |
|---|---|
| Alerts | `QueueBacklogGrowing` and `DeliveryLag`, in `deploy/observability/alerts/platform-alerts.yaml` — one condition seen from the broker's side and from the consumer's |
| Condition | A working queue above 1000 messages and rising over 10 minutes; or event delivery p95 above 2 s over 10 minutes |
| Signal | `rabbitmq_queue_messages` from the broker's exporter; `messaging.delivery.lag`, recorded by `IntegrationEventConsumer<T>` ([§13.2](../backend-architecture/13-observability.md)) |
| Owner | The service team of the consumer ([§13.8](../backend-architecture/13-observability.md)) |

## What it means

Messages are arriving faster than something consumes them. **Nothing has
failed**: every message is still on the broker and will be delivered, so this
is work running late rather than work lost. That is why both alerts are tickets
and neither is a page — a business process that has actually stopped raises
[`error-queue.md`](error-queue.md) or [`outbox-broker.md`](outbox-broker.md)
instead, and if one of those is firing too, work it first.

What users see is staleness: a projection behind its source, an order whose
saga has not moved, a shipment whose despatch has not reached the buyer's
timeline yet.

## First, decide which end fired

The two alerts are grouped on different labels and that is the branch.

- `QueueBacklogGrowing` carries `queue`, which names one receive endpoint.
  Somebody's consumer is behind, and the queue says whose.
- `DeliveryLag` carries `service_name`, which names the host doing the
  consuming. It is measured from the message's own `OccurredAt` to the moment
  `Consume` starts, so it includes the publisher's outbox wait and the
  broker's, not only the consumer's.

Both at once is the ordinary shape of one backlog. `DeliveryLag` alone, with
every queue shallow, points at the **publisher's** outbox rather than at a
consumer — check [`outbox-broker.md`](outbox-broker.md) before touching the
consumer's replicas.

```promql
sum by (queue) (rabbitmq_queue_messages{queue!~".+_(error|skipped)"})

histogram_quantile(
  0.95,
  sum by (service_name, le) (rate(messaging_delivery_lag_seconds_bucket[10m]))
)
```

## Then decide whether it is arrival or service

A backlog is a rate problem and has exactly two sides.

- **Arrival went up.** A campaign, a replay, a backfill, another service
  catching up after its own outage. Compare the queue's inbound rate with the
  same hour yesterday before concluding anything about the consumer.
- **Service went down.** A consumer retrying inside its own endpoint, a
  database that has slowed, a third party the handler waits on, or simply too
  few replicas.

```bash
kubectl -n <ns> get deploy <workload> -o jsonpath='{.spec.replicas}{"\n"}'
kubectl -n <ns> logs deploy/<workload> --since=15m | grep -i retry | head
```

**The lookalike is a consumer that is not running at all.** A scaled-to-zero
Deployment, a crash loop, or a binding that was never declared all produce a
queue that grows and never drains — and the last of them produces
[`skipped-queue.md`](skipped-queue.md) rather than this. Check the endpoint has
consumers before adding replicas to a workload that has none.

## Mitigation before diagnosis

In order of preference: scale the consuming workload out; pause the producer if
it is a backfill somebody started; and only then consider whether the handler
itself is the problem. Scaling out is safe for every consumer in this platform
— §9.5's inbox filter makes a repeated delivery a no-op and §9.4's dispatcher
claims rows under a lease, so two replicas of a consumer do not double anything.

**A worker's replica count is a chart value and not an autoscaler's.**
[§15.3](../backend-architecture/15-cicd-deployment.md) gives Shipping
`autoscaling.enabled: false` with three replicas, because CPU does not move
while a queue does; this alert is how that number is found to be too small, and
the fix is `replicaCount` in `deploy/helm/shipping/values.yaml` rather than a
`kubectl scale` that the next deploy undoes.

## What these two do not cover

**Delivery lag stops when a consumer starts.** It is recorded at the top of
`Consume`, before the handler runs, so it never sees what the handler then
waited on — a carrier, an address owner, a database. A service whose work is
done in a `BackgroundService` rather than in a consumer is outside both alerts
entirely: Shipping's fulfilment and tracking workers are that shape, and their
own signal is `shipping.shipments.waiting`, the gauge of rows past their first
backoff, by state.

So a Shipping backlog can be invisible here while every shipment sits `Pending`
behind an unreachable carrier. Read that gauge, and
`shipping.carrier.unavailable` beside it, before concluding from a quiet queue
that the service is healthy.
[§13.7](../backend-architecture/13-observability.md) records the broker-fed
read-model gap on the same terms, and this is its sibling on the worker side.

## Closing it

Both clear on their own once the rate recovers: the depth drops below a
thousand or stops rising, and p95 falls under two seconds. Before closing,
check the backlog **drained** rather than the producer stopping — a queue
nobody publishes to has an excellent depth.

```promql
sum by (queue) (rate(rabbitmq_queue_messages{queue!~".+_(error|skipped)"}[10m]))
```

If that has collapsed too, the incident is upstream and is not over.
```

- [ ] **Step 3: The two chapter tables and the index**

`docs/runbooks/README.md`'s index gains a row after `skipped-queue.md`:

```markdown
| [`queue-backlog.md`](queue-backlog.md) | `QueueBacklogGrowing`, `DeliveryLag` | yes |
```

§13.6's first alert table gains two rows after *Skipped queue depth*:

```markdown
| Queue backlog | a working queue above 1000 messages **and rising** over 10 min | A consumer is not keeping up, or there are too few of it. Both halves are required, as *Outbox growth* below requires both: a deep queue that is draining needs nobody. The dead-letter queues are excluded because the two rows above own them and are triaged from the opposite end. **For a host with no HTTP traffic this is the capacity signal there is** — §15.3 gives a worker a fixed replica count precisely because CPU does not move while its queue does, and this is how that number is found to be too small | `queue-backlog.md` |
| Delivery lag | `messaging.delivery.lag` p95 > 2 s over 10 min | §13.7's event target, as a rule. It measures publish to consumer start, so it covers the publisher's outbox wait and the broker's as well as the consumer's — and **it stops where the handler starts**, so it says nothing about what that handler then waited on. A service whose work is done in a `BackgroundService` rather than in a consumer is outside it entirely, which is why the runbook it shares names the gauge that is not | `queue-backlog.md` |
```

§13.9's table gains, after `skipped-queue.md`:

```markdown
| `docs/runbooks/queue-backlog.md` | A queue growing and events arriving late: telling arrival from service, which end fired, and what neither alert can see — the worker whose wait is outside a consumer |
```

The paragraph under §13.9 that names the one declared sharer becomes: "§13.8's
error-rate pair is one declared sharer and §13.6's backlog pair is the other,
each with its reason beside it in that file, so a third is argued for there
rather than added."

- [ ] **Step 4: Run the gate**

```bash
py -3.12 deploy/observability/check.py
```

Expected: exit 0. Checks 1 and 2 pair both new rules with the new runbook and
the runbook back to them; the `SHARED_RUNBOOKS` entry satisfies the
more-than-one-alert check and the reverse check that the entry is still needed;
check 4 finds `messaging_delivery_lag_seconds_bucket` — `messaging.delivery.lag`
is a `Histogram<double>` with `unit: "s"` in
`Common.Infrastructure/Messaging/MessagingMetrics.cs`, so the exporter's
`_bucket` series is declared — and `rabbitmq_queue_messages`, which
`EXTERNAL_METRICS` already lists as the RabbitMQ exporter's gauge; and check 9
matches both chapter tables to `docs/runbooks`.

Then prove the gate is reading what it claims, because a pairing check that
passed vacuously is this repository's most-repeated failure:

```bash
py -3.12 - <<'PY'
import pathlib
p = pathlib.Path("docs/runbooks/queue-backlog.md")
p.rename(p.with_suffix(".md.bak"))
PY
py -3.12 deploy/observability/check.py; echo "exit $?"
py -3.12 - <<'PY'
import pathlib
pathlib.Path("docs/runbooks/queue-backlog.md.bak").rename("docs/runbooks/queue-backlog.md")
PY
```

Expected: exit 1 naming both alerts, then the file back and exit 0. A rename
rather than a `git` command, because a worktree session's shell refuses a loop
naming git.

- [ ] **Step 5: Commit**

```bash
git add deploy/observability docs/runbooks docs/backend-architecture/13-observability.md
git commit -m "feat(deploy): §13.6 gains a queue-backlog rule and a delivery-lag rule with one runbook"
```

The body argues why both are tickets, why the backlog rule needs both halves,
why the two share a procedure, and what the runbook says is still owed.

---

### Task 6: §15.3, §15.4 and `docs/secrets.md`

**Files:**
- Modify: `docs/backend-architecture/15-cicd-deployment.md`
- Modify: `docs/secrets.md`

- [ ] **Step 1: §15.3's two sentences**

The Redis sentence. Before:

> The gateway, the BFF and Payments declare `redis.enabled: false` — written
> down rather than omitted, because a capability is a claim a chart makes
> rather than one to infer from a missing key.

After:

> The gateway, the BFF, Payments and Shipping declare `redis.enabled: false` —
> written down rather than omitted, because a capability is a claim a chart
> makes rather than one to infer from a missing key.

The sentence's argument is unchanged, and the mechanism that holds the list to
the charts is unchanged with it: `smoke.sh` reads each service's source for a
call to `AddRedisConnections` in both directions.

The worker sentence. Before:

> Against Ordering, a worker differs by `service.enabled` alone.

After:

> Against Ordering, a worker differs by `service.enabled` alone **among these
> two keys**, which is what this paragraph is about; Shipping's chart differs
> in more than them, and the next paragraph says how.

and a new paragraph after it:

> **A worker's replica count is a decision and not a copy.** CPU utilisation is
> the wrong signal for a host that waits on a queue and on a third party — it
> idles through a carrier outage and through a backlog alike — so Shipping's
> chart sets `autoscaling.enabled: false` and `replicaCount: 3`: three for
> availability across a node drain, and §13.6's queue-backlog rule for finding
> out that three is too few. `deploy/helm/smoke.sh` partitions the charts into
> the autoscaled and the fixed-replica, and holds each chart's values to its
> side, because a branch driven by the file it is judging asserts nothing.
> §15.5's rollout already supports it: the first rung scales the Deployment and
> raises an HPA floor only where there is one.

If §15.3 prints a values block per chart it prints none for Shipping beyond the
two keys it already prints: the chapter prints the shapes, and the rest of
Shipping's is Ordering's plus the capability blocks `_helpers.tpl`'s comments
argue.

- [ ] **Step 2: §15.4's rows, which the chart makes concrete**

Four rows and one column gain the Helm spelling they could not carry before a
chart existed. The carrier's two, written by PR-2:

```markdown
| `Carrier__BaseUrl` | Config | Helm `carrier.baseUrl` → ConfigMap | ✓ — **Shipping only**; the carrier's address, and the host refuses to start without it |
| `Carrier__ApiKey` | Secret | Helm `carrier.apiKeySecretRef` → External Secrets | ✓ — **Shipping only**; the carrier's credential, and the host refuses to start without it |
```

The jurisdiction windows, written by PR-6:

```markdown
| `Jurisdiction__AddressRetention` | Config | Helm `jurisdiction.addressRetention` → ConfigMap | ✓ — **Shipping only**; ADR-053's statutory window for a delivery address, and the host refuses to start without it |
| `Jurisdiction__TrackingRetention` | Config | Helm `jurisdiction.trackingRetention` → ConfigMap | ✓ — **Shipping only**; ADR-053's statutory window for a shipment's tracking events |
```

and `Identity__Client__ClientSecret`'s *Where* column, which says
"`web-bff-identity` secret; one per host", names the second:
"`web-bff-identity` and `shipping-identity`; one per host, never shared —
two hosts on one grant is one host able to act as the other (§11.5)".

`AddressSource__BaseUrl`'s row already reads `Helm addressSource.baseUrl`: PR-5
wrote the obligation the way §15.4's own rule asks, and this PR is what makes
it true rather than what states it.

- [ ] **Step 3: `docs/secrets.md`'s two chart rows**

The chart is `docs/secrets.md`'s third place, and PR-2 and PR-4 each reached
four of the five and named this one as owed. Two edits discharge it.

The *Adding a required key* table's Helm row is wrong about the umbrella and
this PR is what shows it — three required values on a new subchart, none of
which the umbrella holds. Before:

```markdown
| 3. Helm values | `deploy/helm/<chart>/values.yaml` and the umbrella (§15.3) |
```

After:

```markdown
| 3. Helm values | `deploy/helm/<chart>/values.yaml`, inside the capability block that makes the key conditional where the chart has one; the umbrella holds no values of its own, so its caller passes the same value under the subchart's name (§15.3) |
```

*A client secret*'s procedure is the BFF's throughout and has been the wrong
number of hosts since ADR-052. Step 2 and step 3 become per host:

> 2. Update the vault entry — `web-bff-identity` for the BFF,
>    `shipping-identity` for Shipping's worker. Each chart names its own under
>    `identity.clientSecretRef`, and they are never one Secret.
> 3. Wait for External Secrets to reconcile, then restart that host's pods —
>    configuration is read at startup, so a reconciled Secret does not reach a
>    running process.

Step 4 is PR-4's and is already per host; nothing else in the file moves.

- [ ] **Step 4: Audit; commit**

Run `/check-links` and `/validate-blueprint`.

```bash
git add docs/backend-architecture/15-cicd-deployment.md docs/secrets.md
git commit -m "docs: §15.3 names Shipping among the charts with no Redis, and §15.4 names the chart values that mount its keys"
```

---

### Task 7: Verification and the PR

- [ ] **Every gate this PR touches, run in full**

```bash
bash deploy/helm/smoke.sh
py -3.12 -m unittest discover -s deploy/canary
py -3.12 deploy/canary/canary.py check
py -3.12 deploy/observability/check.py
py -3.12 -m unittest discover -s .github/comment-gate
py -3.12 .github/comment-gate/comment_gate.py
```

Expected: all green. The comment gate is run here rather than left to CI
because this PR adds comments to four `.yaml` files, one `.sh` file and a
workflow's `run:` block, and it judges an added line as its whole block.

- [ ] **The dashboards key on the host, not on a name**

```bash
grep -c "service.name\|service_name" deploy/observability/dashboards/*.json
grep -n "ordering-api\|catalog-api\|shipping-worker" deploy/observability/dashboards/*.json
```

Expected: the first prints a count above zero for each board, the second
nothing. Two commands, because one search piped through `head` could show the
label matches and truncate the literal it exists to find. No per-service board
is owed: both boards filter on a `$service` variable, and `outbox.json` already
carries the delivery-lag panel this PR's first rule reads.

- [ ] **The workflows run on the PR.** `helm.yml` and `deploy.yml` both cover
  `deploy/helm/**` and `deploy/canary/**`; `observability.yml` covers
  `deploy/observability/**`, `docs/runbooks/**` and §13's chapter. All three
  green.

- [ ] **PR body:** `| Class | D |`, the touch set from Global Constraints as
  paths only with the reasons underneath, and the four things a reviewer
  should read first — the two decided values (`autoscaling.enabled: false` with
  three replicas, and the four required capabilities), ADR-052's two Helm rows
  moving, the surrogate worker render being replaced by the real chart, and the
  two alerts sharing one runbook with the reason declared. Then `/ship`.

## Self-review

**Spec coverage.**

- Section 3's PR-7 row — `deploy/helm/shipping` with `service.enabled: false`
  and `redis.enabled: false` (Task 2), the library chart's `carrier` and
  client-credentials capabilities (Task 1), the umbrella (Task 4),
  `smoke.sh`'s lists (Task 3), `deploy.yml`'s option and the canary map
  (Task 4), §13.6's two rules with the one runbook they share (Task 5).
- Section 10 — the chart is Ordering's less the Service, with
  `ingress.enabled: false` written down as §15.3 asks and `redis.enabled:
  false` (Task 2); the carrier's two keys on the `paymentProvider` pattern,
  required at render (Task 1); the client credentials through the capability
  the BFF's chart uses, with ADR-052's `fail` and `smoke.sh`'s four assertions
  turned green by naming a set rather than one chart (Task 1); autoscaling off,
  three replicas, and §13.6's backlog rule as the signal (Tasks 2, 5); the
  canary row declaring `consume` and carrying an `httpExemption` as Inventory's
  does (Task 4); no port published and no Compose health check — the probes
  over the health endpoint on the container port are the chart's, asserted by
  `smoke.sh`'s probe section and the worker-shape section (Tasks 2, 3). The
  readiness-set assertion is **PR-5's** and is argued in Global Constraints.
- Section 11 — §13.6's first queue-backlog rule and first delivery-lag rule,
  the first in `OutboxGrowth`'s shape, over a threshold and rising; one shared
  runbook declared in `SHARED_RUNBOOKS` with its reason; and what stays owed
  said in the runbook — delivery lag stops when a consumer starts, and
  `shipping.shipments.waiting` is the signal for a worker's wait (Task 5).
- Section 13 — §13.6 gains the two rules (Task 5), §15.3 names Shipping among
  the charts with no Redis (Task 6), and the chart's place is `docs/secrets.md`'s
  fifth for the carrier key and the client secret (Task 6).

**Type and name consistency.** The chart directory is `shipping` and the
workload `shipping-worker`; the canary map's `serviceName` is
`Shipping.Worker`, the entry assembly §13.2 takes `service.name` from, and its
key `shipping-worker`, which `deploy.yml` passes to `helm upgrade`. The images
are `shipping-worker` and `shipping-migrator`, PR-1's. The connection name is
`Shipping`, so the chart injects `ConnectionStrings__Shipping` and
`ConnectionStrings__ShippingMigrator`. The Secrets are `shipping-database`,
`shipping-migrator-secret`, `shipping-rabbitmq` for the `shipping-svc` account,
`shipping-identity` and `shipping-carrier`. The keys rendered are
`Carrier__BaseUrl`, `Carrier__ApiKey`, `AddressSource__BaseUrl`,
`Jurisdiction__AddressRetention`, `Jurisdiction__TrackingRetention` and the
three `Identity__Client__*` — every one of them §15.4's spelling and the
host's. The values keys `carrier`, `addressSource`, `jurisdiction` and
`identity.clientCredentials` are produced by Task 1 and consumed by Tasks 2, 3
and 4 under those spellings; `AUTOSCALED_CHARTS`, `FIXED_REPLICA_CHARTS`,
`CREDENTIALED_CHARTS`, `in_configmap` and the widened `refuses_chart` are
produced by Tasks 1 and 3 and consumed in Task 3; `queue-backlog.md`,
`DeliveryLag` and `QueueBacklogGrowing` are produced by Task 5 and named by
`SHARED_RUNBOOKS`, §13.6, §13.9 and the runbook index in the same task.

**Deliberately left to a later PR.**

- **Notifications' chart**, which is the same worker shape and the reason
  `FIXED_REPLICA_CHARTS` is a list rather than a special case for one name.
- **A second carrier adapter's chart values.** The port is the seam; a second
  `carrier` block naming a second adapter is the PR that proves it.
- **The BFF's own `capabilities.yaml`.** Shipping gets one because its host
  registers four things unconditionally; giving the BFF the equivalent is that
  chart's change, and `refuses_bff` covers its committed path today.
- **§11.7's erasure consumer** and anything it adds to this chart. Spec section
  7 names the path and that extension brings it.
- **A canary anyone has watched split traffic**, and specifically a worker's:
  ADR-022 already records the replica-ratio claim as owed, and a workload with
  no HTTP traffic judged on `consume` does not change what is unwatched.
- **`shipping.shipments.waiting` as an alert.** PR-6 publishes the gauge and
  this PR's runbook names it as what to read; a rule over it is a threshold
  nobody has measured yet, and §13.6's own callout is why one is not invented
  here.
