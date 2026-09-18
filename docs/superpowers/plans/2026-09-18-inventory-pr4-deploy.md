# Inventory PR-4 — chart, deploy target and canary — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Inventory deployable: its Helm chart, its place in the umbrella
and in `smoke.sh`'s lists, its row in the deploy workflow's choice and in the
canary's workload map.

**Architecture:** `deploy/helm/inventory` is `deploy/helm/ordering` with the
workload renamed: the library chart carries every template, and the values
file is the per-service decision. Everything else is a list gaining one
entry, and each list has a gate that fails when it is incomplete.

**Tech Stack:** Helm 3, bash (`smoke.sh`), Python 3.12 (`canary.py`), GitHub
Actions.

**Spec:** `docs/superpowers/specs/2026-09-18-inventory-service-design.md`,
sections 12 and 13.

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class D.** Touch set: `deploy/helm/**`, `deploy/canary/canary.json`,
  `.github/workflows/deploy.yml`, `.github/workflows/helm.yml`.
- Depends on PR-1 having merged (the images exist in CI's matrix).
- Every list touched has a check that reads it: `smoke.sh` for the chart
  lists, `canary.py`'s check 4 for the workload map, `deploy.yml`'s own
  `pull_request` run for the choice list.

---

### Task 1: The chart

**Files:**
- Create: `deploy/helm/inventory/Chart.yaml`
- Create: `deploy/helm/inventory/values.yaml`
- Create: `deploy/helm/inventory/templates/{configmap,deployment,hpa,ingress,migrate-job,pdb,service}.yaml`
- Create: `deploy/helm/inventory/.helmignore` if Ordering's has one

- [ ] **Step 1: Copy Ordering's chart and rename the workload**

```bash
cp -r deploy/helm/ordering deploy/helm/inventory
rm -rf deploy/helm/inventory/charts deploy/helm/inventory/Chart.lock
```

`Chart.yaml`:

```yaml
apiVersion: v2
name: inventory
description: >-
  Inventory (§4.1) — its API, its migrator hook, and §7.3's one contended
  table behind the inventory-admin route (§10.2).
type: application
version: 0.1.0
dependencies:
  - name: commerce-common
    version: 0.1.0
    repository: file://../common
```

In `values.yaml`, replace: the header comment (Inventory, one port, no
gRPC); `workload.name: inventory-api` — §10.2 dials that literal;
`image.api: inventory-api`; `image.migrator: inventory-migrator`. Keep
`replicaCount: 3`, the HPA at 20, the PDB, `service.enabled: true`. The
`terminationGracePeriodSeconds` comment names Ordering's four endpoints;
rewrite it for Inventory's two receive endpoints and the outbox dispatcher,
or cut it to the library's argument.

The seven one-line templates are unchanged: each is `{{- include
"commerce.<kind>" . }}`.

- [ ] **Step 2: Render it**

```bash
helm dependency update deploy/helm/inventory
helm template inventory deploy/helm/inventory --set-string image.tag=test | grep -E "name: inventory|image:"
```

Expected: `inventory-api` as the workload, `inventory-api-migrate-test` as
the Job, both images under the registry namespace with tag `test`.

- [ ] **Step 3: Commit**

```bash
git add deploy/helm/inventory
git commit -m "feat(deploy): Inventory's chart"
```

---

### Task 2: The lists

**Files:**
- Modify: `deploy/helm/smoke.sh` (`SERVICE_CHARTS`, `MIGRATOR_CHARTS`,
  `SOURCE_INPUTS`, and the two per-subchart `image.tag` override lists)
- Modify: `.github/workflows/helm.yml` (both `paths:` lists gain
  `src/Services/Inventory/**`)
- Modify: `deploy/helm/platform/Chart.yaml` (dependency after `ordering`)
- Modify: `deploy/helm/README.md` (the tree fence and "four dependencies")
- Modify: `deploy/canary/canary.json` (`"inventory-api": { "serviceName": "Inventory.Api", "chart": "inventory" }`)
- Modify: `.github/workflows/deploy.yml` (`options: [catalog-api, ordering-api, inventory-api, gateway, web-bff]`)

- [ ] **Step 1: Run the smoke script to see it fail**

```bash
bash deploy/helm/smoke.sh
```

Expected: the first check fails — `SERVICE_CHARTS` does not match the chart
directories on disk.

- [ ] **Step 2: Edit every list**

`smoke.sh`:

```bash
SERVICE_CHARTS="catalog ordering inventory gateway web-bff"
MIGRATOR_CHARTS="catalog ordering inventory"
```

and, in both places the script overrides every subchart's tag for the
umbrella — the `helm lint` loop and the `platform` render — one more line
beside the four it has:

```bash
        --set-string "inventory.image.tag=$TAG" \
```

Every chart refuses to render without a tag, and the umbrella passes one
per subchart by name, so a subchart added to `Chart.yaml` and not to these
two lists fails the umbrella's own validation on the first run.

`SOURCE_INPUTS` gains `src/Services/Inventory` after `src/Services/Ordering`,
because `smoke.sh` reads each service's source through `src_of` and asserts
that the workflow's path filter covers every input it reads; so
`.github/workflows/helm.yml` gains `'src/Services/Inventory/**'` in both
its `pull_request` and `push` `paths:` lists, after Ordering's line. Left
out, an Inventory source change would skip the Helm gate that inspects it,
and `smoke.sh`'s own check is what refuses that.

`platform/Chart.yaml`: add

```yaml
  - name: inventory
    version: 0.1.0
    repository: file://../inventory
```

after `ordering`, and change the comment's "Inventory, Payments, Shipping
and Notifications join" to "Payments, Shipping and Notifications join".

`README.md`: add `inventory/` to the bracketed group in the tree fence,
change "four dependencies" to "one dependency per service chart" and
"renders all five" to "renders every chart and the umbrella" — and the two
command blocks that enumerate charts: the setup line's comment
`# and ordering, gateway, web-bff` gains `inventory`, with `after the four
above` becoming `after the service charts`, and the umbrella `helm upgrade`
example gains `--set-string inventory.image.tag="$INVENTORY_SHA" \` beside
its four, because that command as printed fails the chart's required-tag
check the moment the umbrella has a fifth dependency.

`canary.json`: add the entry after `ordering-api`. `canary.py` derives the
Job prefix, so nothing else changes; check 4 will assert `Inventory.Api` is
an entry assembly and `inventory` a chart directory.

`deploy.yml`: add `inventory-api` to `options`.

- [ ] **Step 3: Run every gate that reads a list**

```bash
bash deploy/helm/smoke.sh
py -3.12 -m unittest discover -s deploy/canary
py -3.12 deploy/canary/canary.py --help
```

Expected: `smoke.sh` passes every section for five charts and the umbrella;
the canary suite passes, including its check that `canary.json`'s workloads
match the solution's entry assemblies and the chart directories.

- [ ] **Step 4: Commit**

```bash
git add deploy/helm/smoke.sh deploy/helm/platform/Chart.yaml deploy/helm/README.md deploy/canary/canary.json .github/workflows/deploy.yml .github/workflows/helm.yml
git commit -m "feat(deploy): Inventory joins the umbrella, the smoke lists, the canary map and the deploy choice"
```

---

### Task 3: Observability confirmation

**Files:**
- Possibly modify: `deploy/observability/dashboards/golden-signals.json`
- Test: none new; `deploy/observability/check.py` runs as-is

- [ ] **Step 1: Confirm the boards key on the host, not on a name**

```bash
grep -n "service.name\|service_name\|ordering-api\|catalog-api" deploy/observability/dashboards/*.json | head
```

Expected: panels filter on a `service.name` variable or label, and no panel
names `ordering-api` as a literal. If a panel does name services literally,
add `inventory-api` beside them and say so in the PR body.

- [ ] **Step 2: Run the observability check**

```bash
py -3.12 deploy/observability/check.py
```

Expected: exit 0.

- [ ] **Step 3: Commit only if something changed**

```bash
git add deploy/observability
git commit -m "feat(deploy): name inventory-api where a board lists hosts"
```

---

### Task 4: Verification and the PR

- [ ] `bash deploy/helm/smoke.sh` green; `py -3.12 -m unittest discover -s deploy/canary` green.
- [ ] `.github/workflows/helm.yml` and `deploy.yml` both run on the PR (their
  path filters cover `deploy/helm/**` and `deploy/canary/**`); both green.
- [ ] `/validate-blueprint` is not owed. §15.3's sentence that a number
  describing the tree is wrong on the PR that adds Inventory stays: the
  chapter prints no number.
- [ ] PR body: `| Class | D |`, touch set from Global Constraints.

## Self-review

- Spec coverage: section 12's chart, lists, canary map and deploy choice →
  Tasks 1, 2; section 13's dashboard confirmation → Task 3.
- No placeholders: every edit names the file, the line and the value.
- The tag budget (41) is derived by `canary.py` and deliberately not written
  anywhere in this PR.
