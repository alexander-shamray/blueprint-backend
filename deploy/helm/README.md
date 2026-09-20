# Helm charts

§15.3 is the specification; this file is how to run what it specifies. Where
the two disagree, the chapter wins.

A chart per deployable and an umbrella that installs them together. A service
can be rolled on its own — which is what §15.1's per-service path filters
produce — and an environment can be stood up whole.

```
common/      the library chart: every template, once
catalog/     ┐
ordering/    │
inventory/   │ Chart.yaml + values.yaml + one-line templates that include
payments/    │ the library's. The values ARE the per-service decisions.
web-bff/     ┘ Payments carries one template more: the guard on the one
             capability its host registers unconditionally.
gateway/     the same, plus edge-config.yaml — the two keys no service has
             (§15.3), in a template only this chart carries
platform/    the umbrella — one dependency per service chart and no values
             of its own
smoke.sh     renders every chart and the umbrella and asserts what comes out
```

## Setup is one command per chart

`Chart.yaml`'s `file://../common` dependency resolves from disk, so this needs
no network and no chart repository:

```bash
helm dependency update deploy/helm/catalog     # and each other service chart
helm dependency update deploy/helm/platform    # after the service charts
```

Order matters: a service chart must already hold `commerce-common` in its own
`charts/` before the umbrella packages it, or the umbrella renders a subchart
whose library templates are missing. `smoke.sh` does both, in that order.

> **Editing `common/` and then rendering renders the OLD template.**
> `helm dependency update` copies the library chart into each dependant's
> `charts/` as a tarball, and `helm template` reads the tarball — so a change
> to `common/templates/` is invisible until the dependency is resolved again.
> There is no warning; the render simply succeeds with the previous version.
> `smoke.sh` re-resolves on every run and is therefore never wrong about this,
> which is exactly why an ad-hoc `helm template` can be.

`charts/` and `Chart.lock` are ignored rather than committed, and `.gitignore`
argues why.

## Rendering and installing

Every chart refuses to render without an image tag. That is the point:

```bash
$ helm template catalog deploy/helm/catalog
Error: execution error at (catalog/charts/commerce-common/templates/_helpers.tpl:…):
image.tag is required and values.yaml leaves it empty on purpose: a deploy that
cannot name its image must fail rather than roll something nobody chose (§15.3).
```

So a render always carries one:

```bash
helm template catalog deploy/helm/catalog --set-string image.tag="$SHA"

helm upgrade --install catalog-api deploy/helm/catalog \
    --namespace commerce --timeout 20m --set-string image.tag="$SHA"
```

**`--timeout` is not tuning, and leaving it off puts a §13.6 alert outside the
window it watches.** Helm always blocks on a hook — `--wait` governs the
release's own resources, never this — so the migration Job (§7.4) gets whatever
`--timeout` allows, and the default is **5 minutes**. §13.6's `MigrationJobFailed`
has a second branch for a Job that is *stuck*: active for more than 900 seconds,
plus `for: 1m`, so it needs **16 minutes** before it will page. At the default
those two never overlap. Helm gives up first, and a Job that then finishes at,
say, eight minutes takes `kube_job_status_active` back to zero with nothing
having fired — a failed release and no alert.

Two numbers, and the deploy is the one that moves. Pinning the alert under five
minutes instead would page on a migration that is merely slow, on a deploy that
was going to fail anyway; §13.6's threshold is deliberately "well past any
migration this platform has". So the deploy window is set to outlive it, and
**20m > 16m is the whole constraint** — change one and the other has to follow.

The umbrella takes a tag per subchart, because §15.1 builds and deploys per
service — a change under `src/Services/Catalog` rebuilds Catalog alone, and
every other subchart must keep the tag it is already running:

```bash
helm upgrade --install platform deploy/helm/platform \
    --namespace commerce \
    --timeout 20m \
    --values environments/staging.yaml \
    --set-string catalog.image.tag="$CATALOG_SHA" \
    --set-string ordering.image.tag="$ORDERING_SHA" \
    --set-string inventory.image.tag="$INVENTORY_SHA" \
    --set-string payments.image.tag="$PAYMENTS_SHA" \
    --set-string payments.paymentProvider.baseUrl="$PSP_BASE_URL" \
    --set-string gateway.image.tag="$GATEWAY_SHA" \
    --set-string web-bff.image.tag="$BFF_SHA"
```

**Payments' provider address is in that command for the same reason the values
file is below it**: the chart ships no default a cluster could use — §3.2's
provider is a real third party and Compose's simulator is not one — so the
render is refused until an environment names it. It sits on the command line
here beside the tags; an environment that keeps it in `staging.yaml` under
`payments.paymentProvider.baseUrl` is the same fact in the other place.

**The values file is not optional in that command**, and leaving it out is a
render failure rather than a default: the gateway ships
`ingress.trustedNetworks: []` on purpose, so an environment has to name its own
ingress CIDRs before anything templates. That is the point — see *Defaults that
must stay absent* below — and it is why this example carries the argument
rather than an illustrative CIDR that would be wrong in every cluster but one.

```yaml
# environments/staging.yaml — not in this repository; it belongs wherever the
# environment is described, because its contents are facts about one cluster.
gateway:
  ingress:
    host: api.staging.example.com
    trustedNetworks: [ "10.42.0.0/16" ]   # the ingress controller's pod CIDRs
```

## What is deliberately not here

- **Secrets.** External Secrets Operator owns every `Secret` these charts name
  (§15.4); a chart that templated a connection string would put a password into
  `helm get values` and into every diff of this repository. The charts
  reference; they never create.
- **`Namespace` objects.** `--namespace` and `--create-namespace` are the
  installer's decision, and a chart that created one would fight whatever
  manages the cluster's namespaces.
- ~~**Redis connection strings.**~~ **They joined, and this entry is kept
  rather than deleted because the reason it gave is the rule.** §15.4's
  inventory requires them once a host **calls `AddRedisConnections`** — not
  once it reads a cache, which is what this line said until a review caught it:
  the helper reads both connection strings eagerly, and §8.5's store reads the
  *coordination* instance, which is not a cache. §8.5's PR made Catalog and
  Ordering the first callers of that helper, so both charts now carry
  a `redis:` block and `_helpers.tpl` renders the two `secretKeyRef`s. A
  reference to a Secret that does not exist is still a pod that never starts —
  which is why they joined with the PR whose code reads them rather than
  earlier, exactly as §14.1's Compose blocks say.
- **`readOnlyRootFilesystem`.** The right posture, and a decision no chapter
  has taken. Asserting it untested against the chiselled runtime images would
  trade a review question for a CrashLoop. `runAsNonRoot` IS set, because it
  asserts what the image already does — UID 1654, measured off the base
  image's own config.
- **A canary that anyone has watched split traffic.** Since PR-25 the gate
  renders the canary track of every chart and asserts what comes out — that its
  pods still carry the `app.kubernetes.io/name` the *stable* Service selects
  on, that the two Deployments select on different tracks, that the canary
  renders no Service, Ingress, HPA or PodDisruptionBudget, and that each track
  declares its own `deployment.track`. Every one of those is a claim about a
  manifest. Whether kube-proxy's spread of **connections** approximates the
  replica ratio under keep-alive or HTTP/2 is a claim about a cluster, and
  [ADR-022](../../docs/backend-architecture/adr/ADR-022-the-canary-is-a-second-release-weighted-by-replicas.md)
  records it as owed.
- **A cluster.** `smoke.sh` renders and greps; it never applies. Schema
  validation against a live API server is a deploy-time gate (§15.1). These
  gaps live on that side of the line and none of them can be rendered — listed
  rather than counted, because the count was two when this was written:
  - **Mixing the two install modes.** One release owns a *resource* — Helm
    stamps `meta.helm.sh/release-name` on everything and these charts render
    fixed names, so the umbrella and a per-service release cannot both own
    `catalog-api`; whichever installs second is rejected, in either order. Not
    one release per namespace: production is several per-service releases
    sharing one, which is fine because they own disjoint objects. §15.1's
    per-service pipeline is what production uses; the umbrella stands an
    environment up whole. The conflict is an API-server error at install
    time.
  - **A whole capability override at deploy time.** The templates refuse a
    HALF override — disabling a capability whose settings are still present —
    and clearing both together is two values, which no template can tell from a
    chart that never had them. The gate closes the committed path by checking
    each chart against the code it deploys; an ad-hoc `--set` on the command
    line is outside a render-time gate's reach. A `chart:` capability block is
    the real fix and is deferred to its own change.
  - **Secret rotation does not roll pods.** `secretKeyRef` values are
    snapshotted when a container starts, and External Secrets rotating the
    underlying Secret changes no chart value — so the checksum is unchanged
    and nothing restarts. **A rotation is not complete until the consuming
    workloads have been restarted**, before the old credential is revoked.
    Closing it properly is PR-24's secrets work, and §15.3 names it as owed.

## The gate

```bash
bash deploy/helm/smoke.sh          # HELM=/path/to/helm if it is not on PATH
```

**Seventy-two deliberate defects have been run through it and seventy-one
turned a green run red** — a renamed Service, a CPU limit, a grace period back at the
Kubernetes default, a dropped hook annotation, a connection string moved into a
ConfigMap, a second chart growing client credentials, an `envFrom` naming a
ConfigMap nothing renders, a rollout checksum that missed the gateway's own, a
Service publishing only its first port, a chart renumbering the port its
callers dial, a fifth chart the gate never looked at, an Ingress with no
backend, an Ingress with no TLS, a blank CIDR, a blank origin, a
whitespace-only authority, an origin carrying credentials, a pod remounting the
service-account token, a listener moved out from under its own Service, a
validator that checked one string and shipped another, an uppercase host, a
CIDR that is not one, a CIDR octet read as octal, a migrator pod answering its
own service's traffic, a
routed service switching its own Service off, a capability cleared by an
overlay, a health route renamed in the source that maps it, a tag that is legal
for a registry and illegal for Kubernetes, in six spellings, a tag that overruns
the name it derives, an image reference missing its registry, a canary whose
pods the stable Service would never select.

**The tally is this branch's and it grows**; what does not grow is the count of
defects that got past the gate, which is one. That is the number worth reading,
and it is why the sentence below has its own paragraph.

The one is the reason this paragraph exists. "The gateway renders no
migration Job" passed against a gateway that declared `image.migrator`, because
that chart carries no migration template at all — so the assertion could not
fail, and the values comment crediting the missing key was describing a
mechanism nothing consulted. It is now an assertion about the agreement between
the two halves, and it fails from either side.

**One deliberate defect went uncaught by any render-time assertion, and the
fix was not another render check.** Restoring a plausible
`ingress.trustedNetworks` default renders perfectly well — which is exactly
why the value is dangerous — so the gate now asserts against the *values file*
that the default stays absent. A property the render cannot have is a property
the render cannot check.

**Several found defects in the charts rather than in the harness**, and each
was found by writing the assertion rather than by reading the template:
the rollout checksum hashed only the ConfigMap the library renders, so editing
`cors.origins` changed a mounted ConfigMap and left the pod template
byte-identical — a deploy that reports success and rolls nothing. The
annotation now hashes the whole of `.Values`, which over-triggers on a few keys
the container never sees and is the safe direction.

CI runs the same script, path-filtered to this tree **and to every input it
reads outside it** — the routing literals, the Kestrel listeners, the probe
paths, the capability registrations, and `.gitattributes`, which pins this tree
to LF without which the anchored greps match nothing on a Linux runner.

**The paths themselves are deliberately not listed here.** `SOURCE_INPUTS` at
the top of `smoke.sh` is the one place they live, beside the reads, and the
gate asserts that both of the workflow's triggers cover every entry. Three
separate copies of that list went stale before it was done this way.
