# Payments PR-6 — chart, deploy target and canary — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Payments deployable: a `paymentProvider` capability in the
library chart, Payments' chart with no Redis, its place in the umbrella and in
`smoke.sh`'s lists, its row in the deploy workflow's choice and in the
canary's workload map, and §15.3's sentence that names the charts with no
Redis.

**Architecture:** `deploy/helm/payments` is `deploy/helm/ordering` with the
workload renamed, `redis.enabled: false` declared, and a `paymentProvider`
block. The library chart has no generic secret variable — every capability
is a guarded block — so the provider's two keys join `commerce.config` and
`commerce.env` on the `identity.clientCredentials` pattern: required when the
block is enabled, and refused when its settings are present and the block is
off. Everything else is a list gaining one entry, and each list has a gate
that fails when it is incomplete.

**Tech Stack:** Helm 3 templates, bash (`smoke.sh`), Python 3.12
(`canary.py`), GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-09-18-payments-service-design.md`,
sections 11 and 12.

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class D.** Touch set: `deploy/helm/**`, `deploy/canary/**`,
  `.github/workflows/deploy.yml`, `.github/workflows/helm.yml`, and
  `docs/backend-architecture/15-cicd-deployment.md` (§15.3's one sentence,
  inside D's `docs/**`).
- Depends on PR-5 having merged (the gateway dials `payments-api`), and on
  Inventory's deploy PR, so every list below already names Inventory and
  Payments goes after it.
- The simulator is never charted. A cluster's `paymentProvider.baseUrl` is an
  environment value; the chart's default is empty and the capability refuses
  to render without one.
- Every list touched has a check that reads it: `smoke.sh` for the chart
  lists, `canary.py`'s check for the workload map, `deploy.yml`'s own
  `pull_request` run for the choice list.

---

### Task 1: The `paymentProvider` capability in the library chart

**Files:**
- Modify: `deploy/helm/common/templates/_helpers.tpl` — `commerce.config`
  gains `PaymentProvider__BaseUrl`, `commerce.env` gains
  `PaymentProvider__ApiKey` from a `secretKeyRef`, and the coherence block
  gains one guard
- Modify: `deploy/helm/smoke.sh` — an assertion that the rendered Payments
  Deployment carries both keys, and that a render with the block enabled and
  no `baseUrl` fails

- [ ] **Step 1: Write the failing smoke assertions**

In `smoke.sh`, beside the Redis `secretKeyRef` assertions, a section for the
capability. It renders `deploy/helm/payments` (Task 2 creates it; until then
this section fails, which is the red step):

```bash
section 'paymentProvider is a capability, and its address is required'
PAYMENTS_RENDER=$(helm template payments deploy/helm/payments \
    --set-string image.tag="$TAG" \
    --set-string paymentProvider.baseUrl=https://psp.example.invalid/)
grep -q 'PaymentProvider__BaseUrl: "https://psp.example.invalid/"' <<<"$PAYMENTS_RENDER" \
    || fail 'payments: PaymentProvider__BaseUrl missing from the ConfigMap'
grep -q 'name: PaymentProvider__ApiKey' <<<"$PAYMENTS_RENDER" \
    || fail 'payments: PaymentProvider__ApiKey missing from the Deployment'
if helm template payments deploy/helm/payments --set-string image.tag="$TAG" >/dev/null 2>&1; then
    fail 'payments: rendered with no paymentProvider.baseUrl; a deploy that forgot it must fail here, not at start'
fi
if helm template payments deploy/helm/payments --set-string image.tag="$TAG" \
    --set paymentProvider.enabled=false --set paymentProvider.apiKeySecretRef=null \
    --set-string paymentProvider.baseUrl=https://psp.example.invalid/ >/dev/null 2>&1; then
    fail 'payments: rendered an address with the capability off; a setting nothing reads must be refused'
fi
pass 'paymentProvider renders both keys and refuses an empty address or an address while off'
```

`section`, `pass`, `fail` and `$TAG` are the script's own helpers and
variable. Place the section after the Redis `secretKeyRef` assertions, inside
`Rendering`.

- [ ] **Step 2: Write the templates**

In `commerce.config`, after the `identity.clientCredentials` block:

```yaml
{{- if (.Values.paymentProvider).enabled }}
{{- /*
The provider's address (spec §9). Config, not a Secret: an address is not a
credential. Required, and refused at render when empty, because the host's
own refusal is at start — a clean render followed by a pod that will not
start is the shape every guard in this file exists to refuse.
*/}}
PaymentProvider__BaseUrl: {{ include "commerce.require" (list .Values.paymentProvider.baseUrl "paymentProvider.baseUrl is required when paymentProvider.enabled: Payments reads it eagerly and does not start without it (§15.4).") | quote }}
{{- end }}
```

In `commerce.env`, after the `identity.clientCredentials` secret:

```yaml
{{- if (.Values.paymentProvider).enabled }}
- name: PaymentProvider__ApiKey
  valueFrom:
    secretKeyRef:
      name: {{ include "commerce.require" (list .Values.paymentProvider.apiKeySecretRef.name "paymentProvider.apiKeySecretRef.name is required when paymentProvider.enabled. The key is a reference, never a value (§15.3).") | quote }}
      key: {{ include "commerce.require" (list .Values.paymentProvider.apiKeySecretRef.key "paymentProvider.apiKeySecretRef.key is required when paymentProvider.enabled.") | quote }}
{{- end }}
```

In the coherence block, beside the `identity.clientId` guard:

```yaml
{{- if and (or (.Values.paymentProvider).apiKeySecretRef (.Values.paymentProvider).baseUrl) (not (.Values.paymentProvider).enabled) }}
{{- fail "paymentProvider.enabled is false but a paymentProvider setting is set. Payments reads both provider keys eagerly (§15.4), so this renders cleanly and the host does not start. A capability is a fact about the code, not an environment setting." }}
{{- end }}
```

`(.Values.paymentProvider).enabled` rather than `.Values.paymentProvider.enabled`:
the four other charts carry no such block, and the parenthesised form reads
a missing map as empty where the dotted form fails the render.

- [ ] **Step 3: Commit with Task 2** — the smoke section needs the chart.

---

### Task 2: The chart

**Files:**
- Create: `deploy/helm/payments/Chart.yaml`, `values.yaml`, `templates/*.yaml`
- Create: `deploy/helm/payments/templates/capabilities.yaml` — the one guard
  the library chart cannot make, because it does not know which chart's host
  registers the provider unconditionally

- [ ] **Step 1: Copy Ordering's chart and rename the workload**

```bash
cp -r deploy/helm/ordering deploy/helm/payments
rm -rf deploy/helm/payments/charts deploy/helm/payments/Chart.lock
```

`Chart.yaml`:

```yaml
apiVersion: v2
name: payments
description: >-
  Payments (§4.1) — its API and migrator hook, behind the payments-admin
  route (§10.2), and the one service that calls a third party (§3.2).
type: application
version: 0.1.0
dependencies:
  - name: commerce-common
    version: 0.1.0
    repository: file://../common
```

In `values.yaml`, replace every value and comment naming Ordering:
`workload.name: payments-api` — §10.2's cluster dials that literal; `image.api:
payments-api`; `image.migrator: payments-migrator`; `database.connectionName:
Payments`; the database's two secret refs and the broker's `secretRef.name`
renamed on Ordering's pattern (`payments-rabbitmq` for `payments-svc`);
`service.enabled: true`. The `terminationGracePeriodSeconds` comment is
rewritten for Payments' two receive endpoints, the outbox dispatcher and the
provider call, citing `ProviderHop.TotalRequestTimeout` and
`HostOptions.ShutdownTimeout` by name rather than their values, so a change
to either cannot leave the chart's explanation stale.

Replace the `redis:` block with:

```yaml
# No Redis (§2): Payments caches nothing and takes no §8.5 key, because it
# has no HTTP write command. Declared rather than omitted, as the gateway and
# the BFF declare it (§15.3): a capability is a claim a chart makes.
redis:
  enabled: false
```

Add:

```yaml
# §3.2's provider (spec §9). The address is an environment value with no
# default a cluster could use — the simulator is Compose's, never a
# cluster's — so the capability refuses to render without one.
paymentProvider:
  enabled: true
  baseUrl: ""
  apiKeySecretRef:
    name: payments-provider
    key: api-key
```

When done, `grep -n -i ordering deploy/helm/payments/values.yaml` prints
nothing.

`templates/capabilities.yaml` renders nothing and refuses one state:

```yaml
{{- /*
Payments registers its provider unconditionally (AddPaymentProvider), so on
this chart the capability is a fact about the code: switched off, with its
settings cleared, the library chart's coherence guard has nothing to refuse
and the pod does not start. So this chart refuses the switch itself.
*/}}
{{- if not (.Values.paymentProvider).enabled }}
{{- fail "paymentProvider.enabled is false on the payments chart. Payments registers its provider unconditionally and does not start without both keys (§15.4)." }}
{{- end }}
```

And `smoke.sh`'s capability section gains the fully-cleared override:

```bash
if helm template payments deploy/helm/payments --set-string image.tag="$TAG" \
    --set paymentProvider.enabled=false --set paymentProvider.apiKeySecretRef=null \
    --set-string paymentProvider.baseUrl= >/dev/null 2>&1; then
    fail 'payments: rendered with the capability off and cleared; the host registers it unconditionally'
fi
```

- [ ] **Step 2: Render it, then run the smoke script**

```bash
helm dependency update deploy/helm/payments
helm template payments deploy/helm/payments --set-string image.tag=test \
    --set-string paymentProvider.baseUrl=https://psp.example.invalid/ | grep -E "name: payments|image:|PaymentProvider|Redis"
bash deploy/helm/smoke.sh
```

Expected: `payments-api` as the workload, `payments-api-migrate-test` as the
Job, both provider keys, no `Redis` line. `smoke.sh` still fails, now on the
chart lists (Task 3); the capability section passes.

- [ ] **Step 3: Commit**

```bash
git add deploy/helm/common deploy/helm/payments deploy/helm/smoke.sh
git commit -m "feat(deploy): Payments' chart, and the library chart's paymentProvider capability"
```

---

### Task 3: The lists

**Files:**
- Modify: `deploy/helm/smoke.sh` (`SERVICE_CHARTS`, `MIGRATOR_CHARTS`,
  `SOURCE_INPUTS`, and the two per-subchart `image.tag` override lists)
- Modify: `.github/workflows/helm.yml` (both `paths:` lists gain
  `src/Services/Payments/**`)
- Modify: `deploy/helm/platform/Chart.yaml` (dependency after `inventory`)
- Modify: `deploy/helm/platform/values.yaml` (the commented `helm upgrade`
  example)
- Modify: `deploy/helm/README.md` (the tree fence and the two command blocks)
- Modify: `deploy/canary/canary.json` (`"payments-api": { "serviceName": "Payments.Api", "chart": "payments" }`)
- Modify: `.github/workflows/deploy.yml` (`options` gains `payments-api`)

- [ ] **Step 1: Edit every list**

`smoke.sh`: `payments` joins `SERVICE_CHARTS` and `MIGRATOR_CHARTS` after
`inventory`; `SOURCE_INPUTS` gains `src/Services/Payments` after
`src/Services/Inventory`. In both places the script overrides every
subchart's tag for the umbrella — the `helm lint` loop and the `platform`
render — two more lines beside Inventory's:

```bash
        --set-string "payments.image.tag=$TAG" \
        --set-string "payments.paymentProvider.baseUrl=https://psp.example.invalid/" \
```

The second line is the capability's required address, supplied the way a
tag is; without it the umbrella fails the capability's own guard on the
first run, which is the guard working.

`helm.yml`: `'src/Services/Payments/**'` in both `paths:` lists, after
Inventory's line — `smoke.sh` asserts the filter covers every source input it
reads.

`platform/Chart.yaml`: a `payments` dependency after `inventory`, and the
comment naming the services still to join loses "Payments".

`platform/values.yaml`, `README.md`: each command block that enumerates
subcharts gains Payments' tag and address beside Inventory's, because an
example that omits either fails the chart's own checks as printed.

`canary.json`: the entry after `inventory-api`. `canary.py` derives the Job
prefix; its check asserts `Payments.Api` is an entry assembly and `payments`
a chart directory.

`deploy.yml`: `payments-api` after `inventory-api` in `options`.

- [ ] **Step 2: Run every gate that reads a list**

```bash
bash deploy/helm/smoke.sh
py -3.12 -m unittest discover -s deploy/canary
py -3.12 deploy/canary/canary.py check
```

Expected: `smoke.sh` passes every section for every chart and the umbrella;
the canary suite passes; `canary.py check` accepts the plan.

- [ ] **Step 3: Commit**

```bash
git add deploy/helm deploy/canary .github/workflows/deploy.yml .github/workflows/helm.yml
git commit -m "feat(deploy): Payments joins the umbrella, the smoke lists, the canary map and the deploy choice"
```

---

### Task 4: §15.3's sentence

**Files:**
- Modify: `docs/backend-architecture/15-cicd-deployment.md`

- [ ] **Step 1: Edit**

"The gateway and the BFF declare `redis.enabled: false`" becomes "The
gateway, the BFF and Payments declare `redis.enabled: false`", and the
sentence's argument is unchanged. If §15.3 prints a values block per chart,
it prints none for Payments, and that is not owed: the chapter prints the
shapes, and Payments' is Ordering's plus one capability the library chart's
comment argues.

- [ ] **Step 2: Audit; commit**

Run `/check-links` and `/validate-blueprint`.

```bash
git add docs/backend-architecture/15-cicd-deployment.md
git commit -m "docs: §15.3 names Payments among the charts with no Redis"
```

---

### Task 5: Observability confirmation and the PR

- [ ] `grep -n "service.name\|service_name\|ordering-api\|catalog-api" deploy/observability/dashboards/*.json | head`
  — panels filter on a `service.name` variable; a panel naming services
  literally is an issue against the dashboard, not a widening of this PR.
- [ ] `py -3.12 deploy/observability/check.py` — exit 0, Payments instrumented
  since PR-4.
- [ ] `bash deploy/helm/smoke.sh` and the canary suite green;
  `helm.yml` and `deploy.yml` run on the PR and pass.
- [ ] PR body: `| Class | D |`, touch set from the Global Constraints.
  Then `/ship`.

## Self-review

- Spec coverage: section 11's chart, `redis.enabled: false`, the
  `paymentProvider` capability, the lists, canary map and deploy choice →
  Tasks 1–3; §15.3's sentence → Task 4; section 12's confirmation → Task 5.
- The capability's refusal at render is tested by `smoke.sh` in both
  directions: present renders both keys, absent address fails.
- No placeholders: every edit names the file, the list and the value.
