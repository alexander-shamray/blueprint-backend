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
  `docs/runbooks/README.md`,
  `docs/backend-architecture/13-observability.md`,
  `docs/backend-architecture/15-cicd-deployment.md`, `docs/secrets.md`,
  `.github/workflows/deploy.yml`, `.github/workflows/helm.yml`.
  Reasons, since the row above is paths only: the chart, the library chart and
  the smoke gate are `deploy/helm`; the workload map and its suite are
  `deploy/canary`; the two rules and `SHARED_RUNBOOKS` are
  `deploy/observability`; the runbook is the procedure those two rules share
  and `docs/runbooks/README.md` is its index, which the gate reads in both
  directions, so the row arrives with the file;
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
- Modify: `deploy/helm/smoke.sh` — `refuses_chart` moved up beside the other
  helpers and given the per-chart overlay, `overlay_for`'s `shipping` case, the
  capability section, and the four credential assertions ADR-052 marks
  **asserted**

- [ ] **Step 1: Write the failing smoke assertions**

Three edits, all red until Task 2 creates the chart.

**First, `refuses_chart` moves up beside `refuses_foreign`**, to the block of
helpers at the head of the file. It is defined today immediately above the
`refuses_chart catalog …` calls it was written for, near the end of the file,
and the section this step adds calls it some five hundred and thirty lines
earlier — where the name is not bound yet, and `set -euo pipefail` turns that
into an aborted run rather than a failed assertion. `refuses_foreign`'s own
comment already states the layout this restores: defined with the other
helpers rather than beside the first call. The `refuses_chart catalog …` calls
stay exactly where they are and keep working, because a definition further
above a caller is still a definition above it.

**Second, the helper learns the per-chart overlay** in the same move, because
every negative test below renders a chart whose capabilities are required and
would otherwise fail on the wrong value. The helper's own `--set` arguments
come after the overlay, so a case overriding one of them still wins — Helm
takes the last assignment. The comment above the definition travels with it,
and its one clause about where `refuses` sits is corrected by the move rather
than left pointing the wrong way:

```bash
# A capability is a fact about the code, not an environment setting. Each of
# these renders cleanly and produces a pod that will not start, and each has to
# be aimed at a chart that has the capability — `refuses` below renders the
# gateway, which owns no database and no migrator.
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

`overlay_for` gains its `shipping` case in the same step, beside Payments',
because the helper above reads it and the section below renders the chart with
it — a case added later would leave every assertion here failing on a cleared
carrier address rather than on the value each one names:

```bash
        shipping) printf '%s' "--set-string carrier.baseUrl=https://carrier.example.invalid/ --set-string jurisdiction.addressRetention=30.00:00:00 --set-string jurisdiction.trackingRetention=90.00:00:00" ;;
```

`addressSource.baseUrl` is deliberately absent from it: the address owner's
Service name and port are routing configuration the chart ships a default for,
rather than an environment's choice.

**Third, a new section of its own after the `paymentProvider` one** — which is
itself a section rather than part of `Rendering`, so this is the section that
follows it and not a block inside another:

```bash
# --------------------------------------------------------------------------
section 'The worker chart declares four capabilities, and each is required'
# --------------------------------------------------------------------------
# Shipping's host reads every key below before it will start (§15.4), so each
# state that renders cleanly here is a pod that never starts. Asserted by
# placement and not by presence: §15.4 puts the credential and the three
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
    'but a carrier setting is set' --set carrier.enabled=false
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
    'but a jurisdiction window is set' --set jurisdiction.enabled=false
pass 'the worker chart renders four capabilities and refuses each half state'
```

The plain-HTTP case is the one negative shape repeated from the provider's
matrix, and only one: `commerce.requireUrl` is a single helper and
`refuses_payments` already exercises every shape it rejects. What is new here
is that the carrier's address goes through it at all, which is what that one
case asserts.

**Two of those needles are the second half of a message and not its first, and
that is the whole of what makes the case about the guard it names.** Two guards
refuse a capability switched off on this chart — the library's coherence guard,
which fires while a setting is still present, and Task 2's own
`capabilities.yaml`, which is what is left once every setting is cleared — and
both messages open with `carrier.enabled is false`. A needle matching that
prefix passes on whichever guard the render reaches first, so the library's
half of `commerce.env` could be deleted with the suite still green. `but a
carrier setting is set` and `but a jurisdiction window is set` belong to the
library's messages alone, and `on the shipping chart` to the chart's own, so
each case now fails when its own guard goes.

- [ ] **Step 2: Run it, expect red**

```bash
bash deploy/helm/smoke.sh
```

Expected: the run aborts **inside** the new section, on its first line.
`SERVICE_CHARTS` still matches the directories on disk — `shipping` is in
neither — so every section before this one passes, and then
`helm template shipping "$CHARTS_DIR/shipping"` fails under `set -e` because
`deploy/helm/shipping` does not exist. That is the red step; the section turns
green at the end of Task 2.

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

**Four more sentences in that file name `Web.Bff` as the whole of the set**,
and each is read by somebody whose render has just failed, so each is amended
in this step rather than left to the next reader to disbelieve. The block
comment's "They belong to the one host that calls a peer synchronously (§9.7,
ADR-017)" becomes "They belong to the two hosts that call a peer synchronously
— the BFF (§9.7, ADR-017) and Shipping's worker (ADR-052)". The
`commerce.require` message on `Identity__Client__ClientId` becomes
"identity.clientId is required when identity.clientCredentials: the hosts that
declare it bind ServiceIdentityOptions unconditionally and ValidateOnStart
refuses to boot without it (§15.4)." And the downward coherence guard beside
the others — "identity.clientCredentials is false but identity.clientId is
set. Web.Bff binds ServiceIdentityOptions unconditionally …" — takes the
same correction, because it now fires on two charts and names one. The
`identity.scope` message and the two `clientSecretRef` messages name no host
and stay exactly as they are.

**The fourth is the comment block immediately above the upward guards** — the
one this step adds for the carrier and the one it rewrites for the client
credentials. It argues why an upward guard names an owning chart at all, and
then names the wrong set. Before:

```
{{- /*
…
Both blocks already say a capability is a fact about the code; until now they
only enforced it downwards. These two enforce it upwards, and they name the
owning chart because that is the fact: `AddPaymentProvider` is in
`Payments.Api/Program.cs` and `ServiceIdentityOptions` is bound by `Web.Bff`
alone (§9.7, ADR-017). A second chart growing either is a design change, and
a design change edits this line.
*/}}
```

After:

```
{{- /*
…
Both blocks already say a capability is a fact about the code; until now they
only enforced it downwards. These three enforce it upwards, and they name the
owning charts because that is the fact: `AddPaymentProvider` is in
`Payments.Api/Program.cs`, `AddCarrierGateway` is in
`Shipping.Worker/Program.cs`, and `ServiceIdentityOptions` is bound by those
two hosts that call a peer (§9.7, ADR-052). A further chart growing any of
them is a design change, and a design change edits this line.
*/}}
```

The lines above it in the same block — the paragraph about Helm accepting
values a chart never declares, and the `secretKeyRef` a `--set` would mount —
are unchanged: that argument is about the mechanism and is still exactly true.
**"These two" becomes "these three" in the same edit**, because the carrier's
guard is the third and the sentence counts them. The comment gate does not
read `.tpl`, so this step is the only thing that reaches any of it.

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

In `smoke.sh`'s first section, the count becomes a set. **The comment above it
is part of the block and goes with it**, because its closing sentence is the
claim this step makes false — a replacement that left it standing would put two
contradictory paragraphs in one run of comment lines, which is what the comment
gate counts as one block. Before:

```bash
# Read from the values files rather than from a render, and asserted here: a
# second chart setting it renders nothing at all, so under `set -e` the run
# would abort in the render section before this reported. ADR-017's budget is
# one synchronous hop, so it is one chart (§11.5).
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
`grep -n -iE "PR-|#[0-9]|\*\*" deploy/helm/shipping/values.yaml`
prints nothing, and the only occurrences of *ordering* are the `addressSource`
block's two, which name ADR-052's address owner on purpose.

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

# One receive endpoint, an outbox dispatcher and three hosted services to
# drain — fulfilment, tracking and retention — with a call in flight bounded by
# CarrierHop.TotalRequestTimeout and AddressHop.TotalRequestTimeout. No number
# is taken from those here: the value must exceed HostOptions.ShutdownTimeout,
# and _deployment.tpl carries the argument.
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
  # True: the worker presents this grant to read a delivery address from its
  # owner (ADR-052), which is what a host declaring it is saying. All three
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

# ADR-052's address owner: the host and the port are routing configuration —
# `ordering-api`'s HTTP/2-only endpoint, the same in every cluster — so this is
# the one address the chart defaults rather than requires; the host reads it
# from `AddressSource:BaseUrl` and refuses to start without it (§15.4). Plain
# http, because TLS terminates at the Ingress (§10.1) and every hop past it
# says so.
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
`Redis` line.

Expected from `smoke.sh`: the whole capability section passes, and in the
credential section so do the named-set assertion over the values files, the
worker's source assertion and the BFF's own count. Five assertions are still
red, and every one of them is Task 3's — the chart-list check, because
`SERVICE_CHARTS` does not name `shipping` yet; `and the other is the worker`,
because `$OUT/shipping.yaml` is written by the render loop over that list; the
platform-wide count of two and the two-different-Secrets assertion, because
`$OUT/platform.yaml` is the umbrella render and the umbrella gains the subchart
in Task 3 too; and `SOURCE_INPUTS declares src/Services/Shipping, which this
script reads`, because the source assertion that passed two sentences up is a
new `$ROOT/` read and the scan over this script's own reads finds it before the
list Task 3 step 2 adds it to.

- [ ] **Step 5: Commit**

```bash
git add deploy/helm/common deploy/helm/shipping deploy/helm/smoke.sh
git commit -m "feat(deploy): Shipping's chart, and the library chart's carrier, address and jurisdiction capabilities"
```

The body argues the four decided values, names ADR-052's two Helm rows as the
`fail` and the four assertions this commit moves, and says why a worker's
chart keeps the three templates whose values are off.

---

### Task 3: Every list that names the chart, and the two partitions

**Files:**
- Modify: `deploy/helm/smoke.sh` — `SERVICE_CHARTS`, `MIGRATOR_CHARTS`,
  `SOURCE_INPUTS`, the two umbrella override lists, the autoscaling section and
  its new partition, the new worker-shape section, and the retitled heading
  over the gateway's seven conditional refusals
- Modify: `deploy/helm/platform/Chart.yaml` — a `shipping` dependency
- Modify: `deploy/canary/canary.json` — the `shipping-worker` workload
- Modify: `.github/workflows/helm.yml` — both `paths:` lists
- Modify: `.github/workflows/deploy.yml` — the dispatch `options`

- [ ] **Step 1: Run it to see the list fail**

```bash
bash deploy/helm/smoke.sh
```

Expected: the first section fails — `SERVICE_CHARTS` does not match the chart
directories on disk, which now include `shipping` — and the three credential
assertions Task 2 step 4 left red are still red, because the two renders they
read are the ones this task's lists produce.

- [ ] **Step 2: Edit every list**

```bash
SERVICE_CHARTS="catalog ordering inventory payments shipping gateway web-bff"
MIGRATOR_CHARTS="catalog ordering inventory payments shipping"
```

`DATABASELESS_CHARTS` is unchanged: Shipping owns a database and a migrator.

`overlay_for` already carries the worker's required values — Task 1 Step 1 put
the case there beside Payments', with the helper that reads it, because the
capability section cannot go green without it. It reads:

```bash
overlay_for() {
    case "$1" in
        payments) printf '%s' "--set-string paymentProvider.baseUrl=https://psp.example.invalid/" ;;
        shipping) printf '%s' "--set-string carrier.baseUrl=https://carrier.example.invalid/ --set-string jurisdiction.addressRetention=30.00:00:00 --set-string jurisdiction.trackingRetention=90.00:00:00" ;;
    esac
}
```

and the lists below are what this task adds.

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
that never sets the key proves nothing this one does not.

**The section holds more than the surrogate, so the heading has to split
rather than move.** Below the five surrogate checks sit seven refusals that are
the gateway's and not the worker's — two written as `if` blocks, because they
predate the helper, and five as `refuses` calls: no `trustedNetworks`, no CORS
origins, a blank trusted network, a blank origin, an origin with a trailing
path, an Ingress with no Service, and an Ingress with no TLS. `refuses()`
itself is defined among them and is called again further down the file, so it
stays exactly where it is. Retitling the one heading would file all seven under
a Shipping title; the worker section is therefore inserted above them with a
heading of its own, and the heading they already sit under is retitled to say
what they are.

Before — the surrogate, and the heading the gateway's refusals inherit:

```bash
# --------------------------------------------------------------------------
section 'Branches no chart takes yet, exercised anyway'
# --------------------------------------------------------------------------
# §15.3 specifies `service.enabled: false` for Shipping and Notifications, and
# neither exists yet, so rendering one chart with the value flipped is what
# keeps the key from being decorative. Rendered under a name nothing dials,
# which is the difference between exercising the worker branch and asserting
# that Ordering, a routed destination, may drop its Service.
"$HELM" template shipping "$CHARTS_DIR/ordering" --set-string "image.tag=$TAG" \
    --set-string "workload.name=shipping" \
    --set service.enabled=false >"$OUT/worker.yaml"
check 'service.enabled=false renders no Service' \
    test "$(count '^kind: Service$' "$OUT/worker.yaml")" -eq 0
check 'and the workload survives' \
    test "$(count '^kind: Deployment$' "$OUT/worker.yaml")" -eq 1
# Named separately, because a description is a claim about what the command
# looks at and the line above counts Deployments alone.
check 'and so does its migration hook' \
    test "$(count '^kind: Job$' "$OUT/worker.yaml")" -eq 1
check 'and the probes still address the container port directly' \
    test "$(count 'path: /health/ready$' "$OUT/worker.yaml")" -eq 1

# Conditionally required is a real category (§15.4): off is a valid topology,
```

After — two sections, and the gateway's half starts at the comment the `Before`
block ends on, unchanged:

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
# The pair is not independent: an Ingress backend is this workload's Service,
# so a values copy that turned the route on would install cleanly and answer
# 503 for every request (_ingress.tpl).
refuses_chart shipping 'an Ingress on the worker chart fails the render' \
    'ingress.enabled requires service.enabled' --set ingress.enabled=true

# --------------------------------------------------------------------------
section 'A value the gateway requires only when another is set'
# --------------------------------------------------------------------------
# Conditionally required is a real category (§15.4): off is a valid topology,
```

The seven refusals, `refuses()` and everything after them keep their text and
their order; the edit is the heading above them and the section above that.
`refuses_chart` is already bound here — Task 1 step 1 moved its definition up
to the helper block at the head of the file, and this call is the second reason
it had to move.

Then, in the canary loop, one comment beside the four suppressed kinds, because
three of them are vacuous on this chart and a reader must not read the pass as
coverage:

```bash
    # On a chart whose stable release renders no Service, Ingress or HPA, three
    # of these four pass by construction; the PodDisruptionBudget is the live
    # one there. Left in the loop rather than special-cased: the claim is about
    # what a canary release must not own, and it is true of every chart.
```

- [ ] **Step 5: The umbrella, the canary map and the dispatch menu**

Three lists outside `smoke.sh` name this chart or its workload, and the run
below reads every one of them, so they move with `SERVICE_CHARTS` rather than
after it: the umbrella's dependency names are reconciled against that list in
both directions, the canary map is grepped once per chart in it, and the
dispatch menu is matched to the canary map by `canary.py` check 8. Left to a
later task, each would be a red assertion in the run this task ends on.

`platform/Chart.yaml`, after `payments`:

```yaml
  - name: shipping
    version: 0.1.0
    repository: file://../shipping
```

`platform/values.yaml` needs nothing: it holds `{}` and argues why, and the
per-environment overlay is the caller's — which is why Shipping's three
required values reach the umbrella render on step 2's command line rather than
from a file.

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

`deploy.yml`'s `options` becomes

```yaml
        options: [catalog-api, ordering-api, inventory-api, payments-api, shipping-worker, gateway, web-bff]
```

The `options` list moves with the workload it names, and the comment Task 4
corrects — far below it, in the first-rung step rather than in the
`workflow_dispatch` inputs — stays where it is: check 8 matches menu and map
against each other in both directions, so a workload declared without its
option is a gate this task's commit would leave red, while a comment about
what `maxReplicas` bounds is read by no gate at all.

- [ ] **Step 6: Run every gate that reads a list**

```bash
bash deploy/helm/smoke.sh
py -3.12 -m unittest discover -s deploy/canary
py -3.12 deploy/canary/canary.py check
```

Expected: `smoke.sh` green in every section, for all seven charts and the
umbrella — the new capability section, all four credential assertions, both
autoscaling loops, the worker-shape section, and `shipping appears in
deploy/canary/canary.json`; the canary suite green; and `canary.py check`
accepting the plan — check 4 resolving `Shipping.Worker` as an entry assembly
and `shipping` as a chart directory, check 8 matching the dispatch menu to the
workload set, and check 9 finding the `AddConsumer` calls PR-5 registered.

- [ ] **Step 7: Commit**

```bash
git add deploy/helm/smoke.sh deploy/helm/platform/Chart.yaml deploy/canary/canary.json \
        .github/workflows/helm.yml .github/workflows/deploy.yml
git commit -m "feat(deploy): the worker joins the smoke gate's lists, the umbrella, the canary map and the deploy menu"
```

The body argues why the lists move together: each is read against another in
both directions, so a commit that carried one of them alone would leave a gate
red naming the one it left behind.

---

### Task 4: The umbrella's README, and what `maxReplicas` no longer bounds

**Files:**
- Modify: `deploy/helm/README.md` — the tree fence, the umbrella command, the
  required-values paragraph and the environment example
- Modify: `deploy/canary/canary.json` — the `$comment`'s claim about
  `maxReplicas`
- Modify: `deploy/canary/test_canary.py` — one docstring's claim about
  `maxReplicas`
- Modify: `.github/workflows/deploy.yml` — one sentence in the first-rung step
- Modify: `docs/backend-architecture/15-cicd-deployment.md` — §15.5's
  sentence about what `maxReplicas` bounds, the fourth place it is claimed

- [ ] **Step 1: The README the umbrella's caller reads**

The dependency itself is Task 3's, with the list the gate reconciles it
against; what is left here is the file that tells a caller what the seventh
subchart now requires of them.

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

- [ ] **Step 2: The `maxReplicas` claim, in the four places that make it**

A worker with no autoscaler is a chart `maxReplicas` does not bound, and four
files say otherwise in the same words. One step, because they are one claim —
and §15.5 is the owner the other three are restating, so it moves with them
rather than a PR later.

`test_canary.py`'s `test_five_percent_is_expressible_at_nineteen` docstring
says 20 is "the service charts' maxReplicas". Before:

```python
    def test_five_percent_is_expressible_at_nineteen(self) -> None:
        """And 19 + 1 is 20, which is the service charts' maxReplicas
        exactly — not the gateway's, which is 30 because every external request
        passes through it. The 19 is what the weight costs, and only on those
        charts is it also all the chart allows."""
```

After:

```python
    def test_five_percent_is_expressible_at_nineteen(self) -> None:
        """And 19 + 1 is 20, which is the maxReplicas of every chart that
        autoscales — not the gateway's, which is 30 because every external
        request passes through it, and not the worker's, which sets a replica
        count instead (§15.3). The 19 is what the weight costs, and only on
        those charts is it also all the chart allows."""
```

**The closing sentence is kept and not dropped.** It is the one that says what
19 means where `maxReplicas` is higher, which is the whole reason the gateway
is named in the sentence before it; widening the subject from "the service
charts" to "every chart that autoscales" makes *those charts* a more exact
referent rather than an unanswered one. The comment gate reads a Python
docstring, and five lines with no emphasis and nothing named but the owner is
inside its limit.

`canary.json`'s own `$comment` makes the same claim about the same number and
is the third copy of it, so it is corrected in the same edit rather than left
as the one a reader of this file meets first. The claim runs across **four**
array elements and starts partway through the first, so the whole of what is
replaced is these four lines:

```json
    "That count is 19, and the charts already permit it: §15.3's",
    "autoscaling.maxReplicas is 20 on the service charts, exactly 19",
    "stable plus one canary. The gateway's is 30, so there the 19 is what the",
    "weight costs rather than all the chart allows.",
```

They become:

```json
    "That count is 19, and the charts that autoscale already permit it:",
    "§15.3's autoscaling.maxReplicas is 20 on them, exactly 19 stable plus one",
    "canary. The gateway's is 30, so there the 19 is what the weight costs",
    "rather than all the chart allows, and the worker's chart sets a replica",
    "count with no autoscaler at all, so no maxReplicas bounds it and the",
    "rollout's first rung is what scales its stable track (§15.3).",
```

The third is `deploy.yml`'s first-rung step, whose comment says `maxReplicas`
"is exactly this 19 plus one canary on the service charts". It becomes "…on
the charts that autoscale, higher on the gateway, and not a bound at all on a
worker, which sets its replica count directly". The block stays inside the
gate's limit, and the dispatch `options` in this workflow's inputs are Task 3's
and are already on disk — a different step some three hundred lines above, not
a neighbouring line.

The fourth is §15.5 itself, in the paragraph about the weights being ceilings.
The claim starts partway through its line, so what is quoted here starts there
too. Before:

> `autoscaling.maxReplicas` is
> 20 on every chart but one, so on those 19 plus one canary is exactly the
> ceiling. **The gateway's is 30** — every external request passes through
> it — so there 19 is simply what 5% needs rather than all the chart allows,
> and its autoscaler can still climb past the canary's stable count during a
> dwell. The 19 is a property of the weight, not of every HPA.

After:

> `autoscaling.maxReplicas` is
> 20 on every chart that autoscales but one, so on those 19 plus one canary is
> exactly the ceiling. **The gateway's is 30** — every external request passes
> through it — so there 19 is simply what 5% needs rather than all the chart
> allows, and its autoscaler can still climb past the canary's stable count
> during a dwell. **A worker's chart does not autoscale at all**, and §15.3
> says why, so no `maxReplicas` bounds it: the first rung scales its Deployment
> and there is no floor to raise with it. The 19 is a property of the weight,
> not of every HPA.

The closing sentence is kept for the reason the docstring's is: it says what 19
means wherever the ceiling is not 19 plus one, which is now true in two
directions rather than one.

- [ ] **Step 3: Run the gates this task's files answer to**

```bash
py -3.12 -m unittest discover -s deploy/canary
py -3.12 deploy/canary/canary.py check
```

Expected: both green, and unchanged from Task 3 step 6 — the suite because
only a docstring moved, and `canary.py check` because `canary.json` still
parses and its workload set still matches the menu Task 3 settled. `smoke.sh`
is not rerun here: nothing this task edits is a file it reads. §15.5's
paragraph is audited by the `/check-links` and `/validate-blueprint` run at the
end of Task 6, which is the next task to edit that chapter, rather than twice.

- [ ] **Step 4: Commit**

```bash
git add deploy/helm/README.md deploy/canary .github/workflows/deploy.yml \
        docs/backend-architecture/15-cicd-deployment.md
git commit -m "docs: the umbrella's README names Shipping, and maxReplicas stops standing for every chart"
```

The body says which four files carried the one claim, which of them owns it,
and why the count is widened rather than qualified: a number that is true of
five charts and false of the sixth is read by whoever has just met the sixth.

---

### Task 5: §13.6's two rules and the runbook they share

**Files:**
- Modify: `deploy/observability/alerts/platform-alerts.yaml` — `DeliveryLag`
  and `QueueBacklogGrowing`
- Modify: `deploy/observability/check.py` — one `SHARED_RUNBOOKS` entry, and
  `EXTERNAL_METRICS`' description of `rabbitmq_queue_messages`
- Modify: `deploy/observability/README.md` — the sentence that counts the
  rules, the runbooks and the declared sharers
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
docs/runbooks/queue-backlog.md: claimed by more than one alert — DeliveryLag, QueueBacklogGrowing. Add it to SHARED_RUNBOOKS with a reason, or give one of them its own procedure
```

Three and not two: check 1 records the claim whether or not the file exists, so
the sharing check behind it fires in the same run as the two absences. Step 2
closes all three at once — the runbook and the `SHARED_RUNBOOKS` entry are one
edit for that reason, not two.

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

`EXTERNAL_METRICS` in the same file describes `rabbitmq_queue_messages` as read
by "§13.6's error-queue and skipped-queue alerts both". This rule is a third
reader, and the entry is the only prose in the file that says who reads that
series, so the clause becomes "§13.6's error-queue, skipped-queue and
queue-backlog alerts read it". Replaced rather than appended to, because a list
that names two and then says "and one more" is the shape that goes stale next.

`docs/runbooks/queue-backlog.md`, in `error-rate.md`'s form — a header table,
what it means for a user, the branch, the queries with real names, the
lookalike, how to close it, and what it does not cover:

The block below is fenced with **four** backticks, because the runbook it holds
opens `promql` and `bash` fences of its own and a three-backtick outer fence
would close at the first of them:

````markdown
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
````

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

`deploy/observability/README.md` states the same pairing, and states it by
counting, so it goes false with the two rules above and the runbook they share.
Before:

> **The pairing is not one-to-one and this check does not require it to be.**
> Fourteen rules name thirteen runbooks: §13.8's ownership split makes error
> rate two rules over one procedure, declared with its reason in
> `SHARED_RUNBOOKS`.

After:

> **The pairing is not one-to-one and this check does not require it to be.**
> §13.8's ownership split and §13.6's backlog pair each put two rules over one
> procedure, and `SHARED_RUNBOOKS` is where each is declared with its reason.

The numerals go rather than move, which is what the two paragraphs above it in
that file already argue for every other count in the gate: a total in front of
a claim records how stale the sentence is. The sentence after it — why
conditions and alerts are counted by nobody there and only paired — is
unchanged, and is the reason the corrected form cites the two sharers rather
than saying how many rules there now are.

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
why the two share a procedure, what the runbook says is still owed, and why the
gate's own README stopped counting rather than counted again.

---

### Task 6: §15.1, §15.3, §15.4 and `docs/secrets.md`

**Files:**
- Modify: `docs/backend-architecture/15-cicd-deployment.md`
- Modify: `deploy/helm/web-bff/values.yaml` — the chart comment that carries
  §15.3's credentials sentence
- Modify: `docs/secrets.md`

- [ ] **Step 1: §15.3's sentences, and the credentials one**

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

and a new paragraph immediately after it, before *Both keys are still written
down rather than left absent*:

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

The credentials sentence, which stands above §15.3's printed `web-bff` values
block and is ADR-052's row for this chapter. Before:

> Exactly one chart in the platform carries client credentials, and the
> asymmetry is the design rather than an oversight:

After:

> The charts whose host calls a peer carry client credentials and no other
> chart does, and which charts those are is the design rather than an
> oversight
> ([ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)):

Named rather than counted, for the reason Task 1 step 4's own comment gives: a
count is satisfied by the wrong charts, and which host holds a grant is the
whole claim. The printed block below it stays the BFF's — the chapter prints
one shape and `deploy/helm/shipping/values.yaml` is the second instance.

**Its first line counts the charts, though, and it is inside the paragraph this
step is amending**, so leaving it would put the contradiction two lines below
its own correction. Before:

```yaml
# deploy/helm/web-bff/values.yaml — the only chart with an Identity:Client
```

After:

```yaml
# deploy/helm/web-bff/values.yaml — one of the two charts with an Identity:Client
```

The rest of that block — the authority, the `ValidateOnStart` comment, the
switch's own key and the `clientSecretRef` — is unchanged: every line of it is
true of the BFF, which is the chart the block prints.

The callout that closes that block. Before:

> **A second chart setting `identity.clientCredentials: true` is a design
> change, not a configuration change.** It means a host started calling a peer
> synchronously, which is ADR-017's budget being spent — so the review question
> is not "does the secret exist" but "why is this call not an event".

After:

> **A further chart setting `identity.clientCredentials: true` is a design
> change, not a configuration change.** It means another host started calling a
> peer synchronously, which is ADR-017's budget being spent — so the review
> question is not "does the secret exist" but "why is this call not an event".
> ADR-052 is where that question was answered for Shipping's worker, so a
> review of this chart cites that record rather than arguing it again.

"A second" becomes "A further" because the second chart is now on disk and
rendered by this PR, which is what turns the callout's own arithmetic stale;
the question it asks is unchanged, because it is the question and not the
count that the callout exists for.

- [ ] **Step 2: §15.4's rows, which the chart makes concrete**

Two rows and one column gain the Helm spelling they did not carry. The
carrier's two, written by PR-2:

```markdown
| `Carrier__BaseUrl` | Config | Helm `carrier.baseUrl` → ConfigMap | ✓ — **Shipping only**; the carrier's address, and the host refuses to start without it |
| `Carrier__ApiKey` | Secret | Helm `carrier.apiKeySecretRef` → External Secrets | ✓ — **Shipping only**; the carrier's credential, and the host refuses to start without it |
```

**PR-6's two jurisdiction rows are not rewritten here, and neither is PR-5's
`AddressSource__BaseUrl`.** All three already name their Helm key, under the
rule PR-5's Task 6 step 3 argues. This PR is what makes those rows true rather
than what states them; only the two above, written before that rule was
settled, still need the spelling.

`Identity__Client__ClientSecret`'s *Source* column, which says
"`web-bff-identity` secret; one per host", names the second:
"`web-bff-identity` and `shipping-identity`; one per host, never shared —
two hosts on one grant is one host able to act as the other (§11.5)".

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

*A client secret*'s steps 3 and 4 are already per host — PR-4 rewrote both in
the change that put a second host in that procedure, and neither is touched
again here. The step no pull request has reached is **step 2**, which names no
vault entry at all, because until this chart there was one Secret and naming it
was redundant. With two, it has to say which:

> 2. Update the vault entry — `web-bff-identity` for the BFF,
>    `shipping-identity` for Shipping's worker. Each chart names its own under
>    `identity.clientSecretRef`, and they are never one Secret.

Nothing else in the file moves. Steps 1 and 5 are Keycloak's and name no host,
and the bold sentence under the list is about a step's position rather than
about whose pods it restarts.

- [ ] **Step 4: §15.1's sentence, and the chart comment that repeats it**

§15.1 describes what `smoke.sh` asserts, and Task 1 step 4 changed the
assertion, so the description moves in the same PR or one of them is wrong.
Before:

> The second is the Helm tree (PR-23). A workflow path-filtered to
> `deploy/helm/**` runs `deploy/helm/smoke.sh`, which resolves the charts'
> `file://` dependencies, lints each one, and then renders every one and
> asserts what comes out: three probes per workload, a memory limit and no CPU
> limit, the hook annotations of [§7.4](07-persistence.md), the
> ConfigMap/Secret split of §15.4, and one client secret in the whole platform
> (§11.5).

After:

> The second is the Helm tree (PR-23). A workflow path-filtered to
> `deploy/helm/**` runs `deploy/helm/smoke.sh`, which resolves the charts'
> `file://` dependencies, lints each one, and then renders every one and
> asserts what comes out: three probes per workload, a memory limit and no CPU
> limit, the hook annotations of [§7.4](07-persistence.md), the
> ConfigMap/Secret split of §15.4, and a client secret on each of the charts
> whose host calls a peer and on none of the others (§11.5,
> [ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)).

The sentence after it — rendering only, no cluster reached — is unchanged. The
`(PR-23)` in the first sentence is [Appendix C](appendix-c-delivery-plan.md)'s
row and stays: it is prose in a chapter, not a comment, and the comment gate
does not read Markdown.

The same sentence is a comment at the head of `deploy/helm/web-bff/values.yaml`,
which is inside this PR's `deploy/helm/**`. Before:

```yaml
# Exactly one chart in the platform carries client credentials, and the
# asymmetry is the design rather than an oversight (§15.3).
```

After:

```yaml
# The charts whose host calls a peer carry client credentials and no other
# chart does; which charts those are is the design rather than an oversight
# (§15.3, ADR-052).
```

Three lines, no emphasis and nothing named but the owners, because the comment
gate reads `.yaml`. `deploy/helm/shipping/values.yaml` does not repeat the
rule: its own identity block cites ADR-052 and §15.4 for why the worker holds
a grant, and a second copy of §15.3's sentence there is the copy the next
review finds stale.

- [ ] **Step 5: Audit; commit**

Run `/check-links` and `/validate-blueprint`.

```bash
git add docs/backend-architecture/15-cicd-deployment.md docs/secrets.md \
        deploy/helm/web-bff/values.yaml
git commit -m "docs: §15.3 names Shipping's chart, and §15.1 names the charts that carry client credentials"
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
  client-credentials capabilities (Task 1), `smoke.sh`'s lists, the umbrella,
  `deploy.yml`'s option and the canary map (Task 3), §13.6's two rules with the
  one runbook they share (Task 5).
- Section 10 — the chart is Ordering's less the Service, with
  `ingress.enabled: false` written down as §15.3 asks and `redis.enabled:
  false` (Task 2); the carrier's two keys on the `paymentProvider` pattern,
  required at render (Task 1); the client credentials through the capability
  the BFF's chart uses, with ADR-052's `fail` and `smoke.sh`'s four assertions
  turned green by naming a set rather than one chart (Task 1); autoscaling off,
  three replicas, and §13.6's backlog rule as the signal (Tasks 2, 5); the
  canary row declaring `consume` and carrying an `httpExemption` as Inventory's
  does (Task 3); no port published and no Compose health check — the probes
  over the health endpoint on the container port are the chart's, asserted by
  `smoke.sh`'s probe section and the worker-shape section (Tasks 2, 3). The
  readiness-set assertion is **PR-5's** and is argued in Global Constraints.
- Section 11 — §13.6's first queue-backlog rule and first delivery-lag rule,
  the first in `OutboxGrowth`'s shape, over a threshold and rising; one shared
  runbook declared in `SHARED_RUNBOOKS` with its reason; and what stays owed
  said in the runbook — delivery lag stops when a consumer starts, and
  `shipping.shipments.waiting` is the signal for a worker's wait (Task 5).
- Section 13 — §13.6 gains the two rules (Task 5), §15.3 names Shipping among
  the charts with no Redis (Task 6 step 1), and the chart's place is
  `docs/secrets.md`'s third for the carrier key and the client secret (Task 6
  step 3). Section 13's table gives this PR §15.1 as well, and the row it lists
  as `_helpers.tpl` and `smoke.sh` — so §15.3's credentials sentence and the
  callout under it move in Task 6 step 1, §15.1's description of what
  `smoke.sh` asserts and the `deploy/helm/web-bff/values.yaml` comment that
  repeats it in Task 6 step 4, and the two Helm rows themselves in Tasks 1 and
  3. §15.5's own sentence about what `maxReplicas` bounds moves in Task 4 step
  2, with the three files that restate it, because a worker with no autoscaler
  is what makes it false. The class row stays one letter:
  `deploy/helm/web-bff/values.yaml` is inside `deploy/helm/**`, which the touch
  set already carries.

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
`CREDENTIALED_CHARTS` and the moved, overlay-carrying `refuses_chart` are
produced by Tasks 1 and 3 and consumed in Tasks 1 and 3; `queue-backlog.md`,
`DeliveryLag` and `QueueBacklogGrowing` are produced by Task 5 and named by
`SHARED_RUNBOOKS`, §13.6, §13.9, `deploy/observability/README.md` and the
runbook index in the same task.

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
