#!/usr/bin/env bash
#
# Renders every chart under deploy/helm and asserts what §15.3, §15.4 and
# §7.4 say comes out — the Helm analogue of the Compose smoke (§15.1), run
# by CI on a path filter and by a person before pushing a chart change. It
# deploys nothing and reaches no cluster: `helm template` renders locally, and
# schema validation against a live API server is a deploy-time gate (§15.1).
#
#   HELM=/path/to/helm bash deploy/helm/smoke.sh
#
# `helm` from PATH when HELM is unset. Requires helm 3.
set -euo pipefail

HELM="${HELM:-helm}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CHARTS_DIR="$ROOT/deploy/helm"
OUT="$(mktemp -d)"
trap 'rm -rf "$OUT"' EXIT

# Any tag will do — supplying one is what lets `required` let the render
# through; what happens without one is asserted below.
TAG="0000000000000000000000000000000000000000"

# The gateway's chart ships `ingress.trustedNetworks: []` on purpose: a
# plausible CIDR is a security decision taken on behalf of a cluster nobody
# has seen (§15.3). Every render here plays the environment overlay, and each
# negative test below supplies it too, so a test asserting "no tag is refused"
# fails on the tag and not on something else.
CIDR='{10.42.0.0/16}'
GATEWAY_OVERLAY="--set ingress.trustedNetworks=$CIDR"
PLATFORM_OVERLAY="--set gateway.ingress.trustedNetworks=$CIDR"

# Payments' required provider address (§15.4). Per chart, not for all: on any
# other chart it is a setting with the capability off, which the library's
# coherence guard refuses. So it cannot ride GATEWAY_OVERLAY, which every
# chart receives.
overlay_for() {
    case "$1" in
        payments) printf '%s' "--set-string paymentProvider.baseUrl=https://psp.example.invalid/" ;;
    esac
}

SERVICE_CHARTS="catalog ordering inventory payments gateway web-bff"
MIGRATOR_CHARTS="catalog ordering inventory payments"
DATABASELESS_CHARTS="gateway web-bff"

# Every path outside deploy/helm that this script reads, declared once beside
# the reads: the workflow's path filter must cover each of them, or a change to
# one is a green pull request that skips the gate watching it, and the
# agreement is asserted below.
SOURCE_INPUTS="
src/Gateway/Gateway.Api
src/BFF/Web.Bff
src/Services/Catalog
src/Services/Ordering
src/Services/Inventory
src/Services/Payments
src/BuildingBlocks/Common.Web/HealthCheckExtensions.cs
.gitattributes
deploy/canary/canary.json
"

# The lists above are classifications — which chart owns a database is a fact
# about the platform — and the membership is not written down: the directory
# is the authority, and the lists are reconciled against it before anything is
# rendered, so a new chart directory cannot be skipped silently.
discovered_charts() {
    for d in "$CHARTS_DIR"/*/; do
        name="$(basename "$d")"
        [ "$name" = common ] && continue      # the library chart renders nothing
        [ "$name" = platform ] && continue    # the umbrella has no templates
        [ -f "$d/Chart.yaml" ] && echo "$name"
    done | sort
}

failures=0

pass() { printf '  ok   %s\n' "$1"; }

fail() {
    printf '  FAIL %s\n' "$1" >&2
    failures=$((failures + 1))
}

check() {
    # check <description> <condition-exit-code-producing-command...>
    local what="$1"
    shift
    if "$@" >/dev/null 2>&1; then
        pass "$what"
    else
        fail "$what"
    fi
}

# grep -c counts LINES, not matches, so every count below is a line count and
# the assertions are written to match one claim per line.
count() { grep -c "$1" "$2" 2>/dev/null || true; }

section() { printf '\n%s\n' "$1"; }

# --------------------------------------------------------------------------
section 'The gate covers every chart on disk'
# --------------------------------------------------------------------------
# First, because every section below iterates SERVICE_CHARTS: a chart missing
# from that list is not a weaker run, it is an unrun one that reports success.
found="$(discovered_charts | tr '\n' ' ' | sed 's/ *$//')"
listed="$(printf '%s\n' $SERVICE_CHARTS | sort | tr '\n' ' ' | sed 's/ *$//')"
if [ "$found" = "$listed" ]; then
    pass "SERVICE_CHARTS is every deployable chart on disk ($found)"
else
    fail "SERVICE_CHARTS ($listed) does not match the chart directories ($found)"
fi

# And the two sub-classifications partition it, so a chart cannot be in the
# suite while belonging to neither — which is how it would reach the migration
# section and be checked by nothing there.
both="$(printf '%s\n' $MIGRATOR_CHARTS $DATABASELESS_CHARTS | sort | tr '\n' ' ' | sed 's/ *$//')"
if [ "$both" = "$listed" ]; then
    pass 'every chart is classified as owning a database or not'
else
    fail "MIGRATOR_CHARTS + DATABASELESS_CHARTS ($both) do not partition SERVICE_CHARTS ($listed)"
fi

# Read from the values files rather than from a render, and asserted here: a
# second chart setting it renders nothing at all, so under `set -e` the run
# would abort in the render section before this reported. ADR-017's budget is
# one synchronous hop, so it is one chart (§11.5).
credentialed="$(grep -l 'clientCredentials: true' "$CHARTS_DIR"/*/values.yaml | wc -l | tr -d ' ')"
check "exactly one chart declares client credentials (found $credentialed)" \
    test "$credentialed" -eq 1

# Each chart must declare what its code requires, read from src/ rather than
# trusted: the render-time guards refuse a half override, and a whole one —
# `broker.enabled` and `broker.secretRef` cleared together — has to be
# committed to reach anyone, and then fails here. An ad-hoc `--set` at deploy
# time is outside a render-time gate's reach, and deploy/helm/README.md says so.
#
# The invocation form of §8.2's helper, not a mention of its name: the line
# must open with an identifier and reach the call through a dot, so every
# comment, string and block-comment continuation is refused by its first
# character. It fails closed on a chain broken under the house style, which is
# the safe direction. Declared here so the self-test below runs against the
# same string the gate uses.
CALLS_REDIS='^[[:space:]]*[A-Za-z_][A-Za-z0-9_.]*\.AddRedisConnections\('

declares() {
    # declares <chart> <block> -> exit 0 when that block sets enabled: true
    awk -v want="$2" '
        $0 ~ ("^" want ":") { inside = 1; next }
        /^[a-z]/ { inside = 0 }
        inside && /^  enabled: true$/ { found = 1 }
        END { exit found ? 0 : 1 }
    ' "$CHARTS_DIR/$1/values.yaml"
}

# The negation, as a function rather than a `!` at the call site: `check` runs
# its argument through "$@", which cannot carry a shell keyword.
lacks() { ! declares "$1" "$2"; }

# The mapping is data, because the gateway and the BFF are not under
# src/Services and no capitalisation rule reaches them — and an edge host left
# out of it is one whose chart is never compared to its source.
src_of() {
    case "$1" in
        gateway) echo "$ROOT/src/Gateway/Gateway.Api" ;;
        web-bff) echo "$ROOT/src/BFF/Web.Bff" ;;
        *)       echo "$ROOT/src/Services/$(echo "${1:0:1}" | tr '[:lower:]' '[:upper:]')${1:1}" ;;
    esac
}

for chart in $SERVICE_CHARTS; do
    src="$(src_of "$chart")"
    if [ -d "$src" ]; then
        if grep -rq 'GetConnectionString("RabbitMq")' "$src"; then
            check "$chart reads RabbitMq in src/, so its chart declares a broker" \
                declares "$chart" broker
        fi
        if grep -rqE 'GetConnectionString\("[A-Za-z]+"\)' "$src"; then
            check "$chart resolves a connection string in src/, so its chart names one" \
                grep -qE '^  connectionName: ' "$CHARTS_DIR/$chart/values.yaml"
        fi
        # §8.1's two connections, read from src/ like the broker above: a
        # service whose Infrastructure calls AddRedisConnections resolves both
        # keys eagerly at startup, so a chart that does not declare redis
        # renders cleanly and produces a pod that will not start. Asserted in
        # both directions, because a deleted registration that merely skips
        # the check leaves a chart mounting a Secret reference no pod needs.
        if grep -rqE "$CALLS_REDIS" "$src"; then
            check "$chart calls AddRedisConnections in src/, so its chart declares redis" \
                declares "$chart" redis
        else
            check "$chart calls no AddRedisConnections in src/, so its chart declares no redis" \
                lacks "$chart" redis
        fi
    else
        fail "no source tree found at $src for chart $chart — the mapping, not the chart, is wrong"
    fi
done

check 'the BFF binds ServiceIdentityOptions in src/, so its chart declares credentials' \
    grep -rq 'ServiceIdentityOptions' "$ROOT/src/BFF/Web.Bff"

# --------------------------------------------------------------------------
section 'The source-detection patterns select code, not prose'
# --------------------------------------------------------------------------
# A pattern is a claim about what it selects, and the only way to establish it
# is to hand it something it must refuse — every other run of this script
# feeds it a tree where the answer is yes either way.
check 'CALLS_REDIS refuses a comment that names the helper' \
    sh -c 'printf "        // AddRedisConnections and nothing called it\n" |
        grep -qvE "$0"' "$CALLS_REDIS"
check 'CALLS_REDIS refuses a block-comment continuation' \
    sh -c 'printf "         * services.AddRedisConnections(configuration);\n" |
        grep -qvE "$0"' "$CALLS_REDIS"
check 'CALLS_REDIS refuses the call inside a string literal' \
    sh -c 'printf "        \"services.AddRedisConnections(configuration)\";\n" |
        grep -qvE "$0"' "$CALLS_REDIS"
check 'CALLS_REDIS still matches the real registration' \
    sh -c 'printf "        services.AddRedisConnections(configuration);\n" |
        grep -qE "$0"' "$CALLS_REDIS"
check 'CALLS_REDIS still matches it through a qualified receiver' \
    sh -c 'printf "        builder.Services.AddRedisConnections(builder.Configuration);\n" |
        grep -qE "$0"' "$CALLS_REDIS"
check 'CALLS_REDIS refuses a commented-out call' \
    sh -c 'printf "        // services.AddRedisConnections(configuration);\n" |
        grep -qvE "$0"' "$CALLS_REDIS"
check 'CALLS_REDIS accepts the real invocation' \
    sh -c 'printf "        services.AddRedisConnections(configuration);\n" |
        grep -qE "$0"' "$CALLS_REDIS"

# --------------------------------------------------------------------------
section 'The workflow watches everything this script reads'
# --------------------------------------------------------------------------
# Both triggers, because a merged change that skips the gate on `main` is the
# same defect one branch later.
covered() {
    # covered <path> <trigger-block> -> exit 0 when some filter entry matches
    awk -v want="$1" '
        { sub(/^ *- /, ""); gsub(/'"'"'/, "") }
        $0 == want { found = 1 }
        /\/\*\*$/ {
            prefix = substr($0, 1, length($0) - 3)
            if (index(want, prefix) == 1) { found = 1 }
        }
        END { exit found ? 0 : 1 }
    ' "$2"
}

awk '/^  pull_request:/ { p = 1 } p && /^      - / { print } /^  push:/ { p = 0 }' \
    "$ROOT/.github/workflows/helm.yml" >"$OUT/pr-paths.txt"
awk '/^  push:/ { p = 1 } p && /^      - / { print }' \
    "$ROOT/.github/workflows/helm.yml" >"$OUT/push-paths.txt"

# The workflow's own path and this gate's own tree are both on the list:
# without the first, a change to the trigger lists does not run the gate
# validating them; without the second, a chart edit does not run it either.
for input in $SOURCE_INPUTS deploy/helm .github/workflows/helm.yml; do
    check "the pull_request filter covers $input" covered "$input" "$OUT/pr-paths.txt"
    check "the push filter covers $input" covered "$input" "$OUT/push-paths.txt"
done

# And the other direction, which stays green when the list is short rather
# than wrong: the loop above can only ask the workflow about entries
# SOURCE_INPUTS already contains, so every `$ROOT/…` path this script names
# must be covered by a declared entry — matched whole or as a directory
# prefix, never as a substring. Two kinds of match are skipped, and neither
# hides a gap: anything ending in `/` is an interpolation prefix whose
# concrete forms are declared, and `deploy/helm` is this script's own tree,
# which SOURCE_INPUTS by definition excludes.
grep -oE '\$ROOT/[A-Za-z0-9_./-]+' "$0" | sed -E 's|^\$ROOT/||' | sort -u >"$OUT/reads.txt"

if [ ! -s "$OUT/reads.txt" ]; then
    # A scan that found nothing would pass the loop below against any list at
    # all.
    fail 'found no $ROOT-relative reads in smoke.sh — the scan is broken, not the list'
else
    while read -r path; do
        case "$path" in
            */|deploy/helm|deploy/helm/*|.github/workflows/helm.yml) continue ;;
        esac
        matched=no
        for input in $SOURCE_INPUTS; do
            case "$path" in
                "$input"|"$input"/*) matched=yes ;;
            esac
        done
        check "SOURCE_INPUTS declares $path, which this script reads" test "$matched" = yes
    done <"$OUT/reads.txt"
fi

# --------------------------------------------------------------------------
section 'Resolving dependencies'
# --------------------------------------------------------------------------
# file:// dependencies resolve from disk, so this needs no network and no repo
# index. Order matters: a service chart must already hold commerce-common in
# its own charts/ before the umbrella packages it, or the umbrella renders a
# subchart whose library templates are missing.
for chart in $SERVICE_CHARTS; do
    "$HELM" dependency update "$CHARTS_DIR/$chart" --skip-refresh >/dev/null
    pass "$chart resolves commerce-common"
done
"$HELM" dependency update "$CHARTS_DIR/platform" --skip-refresh >/dev/null
pass 'platform resolves its subcharts'

# `helm dependency update` only checks that every NAMED dependency resolves —
# deleting one from the list still updates cleanly, the routed-service section
# further down reports "no chart yet" for what it dropped and passes, and every
# render after this point simply has one fewer subchart. So the umbrella's own
# dependency names are reconciled against SERVICE_CHARTS directly, both ways.
deps_declared="$(awk '/^dependencies:/ { d = 1; next } d && /^  - name: / { print $3 }' \
    "$CHARTS_DIR/platform/Chart.yaml" | sort | tr '\n' ' ' | sed 's/ *$//')"
if [ "$deps_declared" = "$listed" ]; then
    pass "platform/Chart.yaml depends on exactly SERVICE_CHARTS ($deps_declared)"
else
    deps_missing="$(comm -23 <(printf '%s\n' $listed) <(printf '%s\n' $deps_declared))"
    deps_extra="$(comm -13 <(printf '%s\n' $listed) <(printf '%s\n' $deps_declared))"
    fail "platform/Chart.yaml's dependencies ($deps_declared) do not match SERVICE_CHARTS ($listed) — missing: ${deps_missing:-none}, extra: ${deps_extra:-none}"
fi

# --------------------------------------------------------------------------
section 'helm lint'
# --------------------------------------------------------------------------
for chart in $SERVICE_CHARTS platform; do
    check "$chart lints" "$HELM" lint "$CHARTS_DIR/$chart" --set-string "image.tag=$TAG" \
        --set-string "catalog.image.tag=$TAG" \
        --set-string "ordering.image.tag=$TAG" \
        --set-string "inventory.image.tag=$TAG" \
        --set-string "payments.image.tag=$TAG" \
        --set-string "payments.paymentProvider.baseUrl=https://psp.example.invalid/" \
        --set-string "gateway.image.tag=$TAG" \
        --set-string "web-bff.image.tag=$TAG" \
        $GATEWAY_OVERLAY $(overlay_for "$chart") $PLATFORM_OVERLAY
done

# --------------------------------------------------------------------------
section 'A deploy that cannot name its tag fails (§15.3)'
# --------------------------------------------------------------------------
# values.yaml leaves image.tag empty on purpose. Left to default it, the render
# would emit `image: registry/api:` and the kubelet resolves that to :latest —
# the one tag §15.3 forbids by name. This is the assertion that the empty
# default is a refusal rather than a hole.
for chart in $SERVICE_CHARTS; do
    if "$HELM" template "$chart" "$CHARTS_DIR/$chart" $GATEWAY_OVERLAY $(overlay_for "$chart") \
        >"$OUT/untagged-$chart.txt" 2>&1; then
        fail "$chart renders WITHOUT a tag — it must not"
    elif grep -q 'image.tag is required' "$OUT/untagged-$chart.txt"; then
        pass "$chart refuses to render without a tag, and says which value is missing"
    else
        fail "$chart fails without a tag but the message does not name image.tag"
    fi
done

# --------------------------------------------------------------------------
section 'Rendering'
# --------------------------------------------------------------------------
for chart in $SERVICE_CHARTS; do
    "$HELM" template "$chart" "$CHARTS_DIR/$chart" --set-string "image.tag=$TAG" \
        $GATEWAY_OVERLAY $(overlay_for "$chart") >"$OUT/$chart.yaml"
    pass "$chart renders"
done
"$HELM" template platform "$CHARTS_DIR/platform" \
    --set-string "catalog.image.tag=$TAG" \
    --set-string "ordering.image.tag=$TAG" \
    --set-string "inventory.image.tag=$TAG" \
    --set-string "payments.image.tag=$TAG" \
    --set-string "payments.paymentProvider.baseUrl=https://psp.example.invalid/" \
    --set-string "gateway.image.tag=$TAG" \
    --set-string "web-bff.image.tag=$TAG" \
    $PLATFORM_OVERLAY >"$OUT/platform.yaml"
pass 'platform renders'

# --------------------------------------------------------------------------
section 'paymentProvider is a capability, and its address is required'
# --------------------------------------------------------------------------
# Both keys are read eagerly by AddPaymentProvider, so every state below that
# renders cleanly is a pod that will not start. Asserted in both directions:
# supplied, the two keys land in the right Kind; absent or contradicted, the
# render is refused rather than deferred to the cluster.
PAYMENTS_RENDER=$("$HELM" template payments "$CHARTS_DIR/payments" \
    --set-string image.tag="$TAG" \
    --set-string paymentProvider.baseUrl=https://psp.example.invalid/)
grep -q 'PaymentProvider__BaseUrl: "https://psp.example.invalid/"' <<<"$PAYMENTS_RENDER" \
    || fail 'payments: PaymentProvider__BaseUrl missing from the ConfigMap'
# The NAME alone would pass on a literal `value:`, which is the one way this
# key can be wrong: §15.4 puts it in the Secret column, and a credential
# rendered into a ConfigMap is readable by anyone with namespace read access
# and unencrypted at rest. So the reference structure is the subject, and the
# ConfigMap is asserted not to carry it — a gate watching only the name stops
# covering the thing it was added for the moment the value moves.
printf '%s\n' "$PAYMENTS_RENDER" >"$OUT/payments-capability.yaml"
check 'payments: PaymentProvider__ApiKey comes from a secretKeyRef, not a literal' \
    awk '/^ *- name: PaymentProvider__ApiKey$/ { at = NR }
         at && NR == at + 1 && /^ *valueFrom:$/ { vf = 1 }
         vf && NR == at + 2 && /^ *secretKeyRef:$/ { found = 1 }
         END { exit found ? 0 : 1 }' "$OUT/payments-capability.yaml"
check 'payments: no ConfigMap carries PaymentProvider__ApiKey' \
    awk '/^kind: ConfigMap$/ { in_cm = 1 }
         /^---$/ { in_cm = 0 }
         in_cm && /PaymentProvider__ApiKey/ { found = 1 }
         END { exit found ? 1 : 0 }' "$OUT/payments-capability.yaml"
if "$HELM" template payments "$CHARTS_DIR/payments" --set-string image.tag="$TAG" >/dev/null 2>&1; then
    fail 'payments: rendered with no paymentProvider.baseUrl; a deploy that forgot it must fail here, not at start'
fi
if "$HELM" template payments "$CHARTS_DIR/payments" --set-string image.tag="$TAG" \
    --set paymentProvider.enabled=false --set paymentProvider.apiKeySecretRef=null \
    --set-string paymentProvider.baseUrl=https://psp.example.invalid/ >/dev/null 2>&1; then
    fail 'payments: rendered an address with the capability off; a setting nothing reads must be refused'
fi
if "$HELM" template payments "$CHARTS_DIR/payments" --set-string image.tag="$TAG" \
    --set paymentProvider.enabled=false --set paymentProvider.apiKeySecretRef=null \
    --set-string paymentProvider.baseUrl= >/dev/null 2>&1; then
    fail 'payments: rendered with the capability off and cleared; the host registers it unconditionally'
fi
pass 'paymentProvider renders both keys and refuses an empty address or an address while off'

# --------------------------------------------------------------------------
section 'Probes — three per workload (§13.5)'
# --------------------------------------------------------------------------
for chart in $SERVICE_CHARTS; do
    for probe in livenessProbe readinessProbe startupProbe; do
        check "$chart declares $probe" test "$(count "^ *$probe:" "$OUT/$chart.yaml")" -eq 1
    done
done

# The paths come from the source that maps them, not from literals repeated
# here: a copy in the gate would close nothing, since a renamed route leaves
# the chart green and a slow-starting pod 404s and is killed mid-boot.
grep -ohE 'MapHealthChecks\("/health/[a-z]+"' "$ROOT/src/BuildingBlocks/Common.Web/HealthCheckExtensions.cs" |
    sed -E 's|.*"(/health/[a-z]+)"|\1|' | sort -u >"$OUT/mapped-probes.txt"

mapped_count="$(wc -l <"$OUT/mapped-probes.txt" | tr -d ' ')"
if [ "$mapped_count" -eq 0 ]; then
    fail 'no health endpoints found in HealthCheckExtensions.cs — the parse, not the chart, is wrong'
else
    pass "MapCommonHealthEndpoints maps $mapped_count paths, read from source"
fi

for chart in $SERVICE_CHARTS; do
    while read -r path; do
        check "$chart probes $path, the path Common.Web maps" \
            test "$(count "path: $path$" "$OUT/$chart.yaml")" -eq 1
    done <"$OUT/mapped-probes.txt"

    # And no probe pointing at a path nothing maps, which is the direction a
    # renamed route breaks.
    awk '/path: \/health\// { sub(/.*path: /, ""); print }' "$OUT/$chart.yaml" |
        sort -u >"$OUT/probed-$chart.txt"
    stray="$(comm -23 "$OUT/probed-$chart.txt" "$OUT/mapped-probes.txt")"
    if [ -z "$stray" ]; then
        pass "$chart probes nothing Common.Web does not map"
    else
        fail "$chart probes path(s) nothing maps: $(echo "$stray" | tr '\n' ' ')"
    fi
done

# --------------------------------------------------------------------------
section 'Resources — a memory limit and no CPU limit (§15.3)'
# --------------------------------------------------------------------------
# Memory is incompressible, so a leak must be bounded; CPU is compressible, and
# a limit throttles into p99 spikes well before the pod is short of capacity.
# awk rather than grep because the claim is about what is INSIDE a limits
# block: `cpu` appears legitimately under requests, and as a metric name on the
# HPA.
limits_without_cpu() {
    awk '
        /^[ ]*limits:[ ]*$/ { match($0, /[^ ]/); indent = RSTART; inside = 1; next }
        inside {
            match($0, /[^ ]/)
            if (RSTART <= indent) { inside = 0; next }
            if ($0 ~ /cpu:/) { print "cpu limit at line " NR; bad = 1 }
        }
        END { exit bad ? 1 : 0 }
    ' "$1"
}

for chart in $SERVICE_CHARTS platform; do
    check "$chart sets no CPU limit" limits_without_cpu "$OUT/$chart.yaml"
    # Every resources block has a limits block — "at least one" would pass on a
    # render where three containers of four were unbounded, which is the shape
    # this assertion exists to refuse.
    check "$chart bounds memory on every container that declares resources" \
        test "$(count '^ *limits:' "$OUT/$chart.yaml")" \
        -eq "$(count '^ *resources:' "$OUT/$chart.yaml")"
done

# --------------------------------------------------------------------------
section 'Autoscaling owns the replica count'
# --------------------------------------------------------------------------
# With the HPA on, `replicas` must be absent from the Deployment: present, every
# helm upgrade writes the chart's value and the autoscaler writes it back, so a
# config-only deploy (§15.1) silently scales the service down and it climbs out
# again over the following minutes.
for chart in $SERVICE_CHARTS; do
    check "$chart leaves replicas to its HPA" test "$(count '^ *replicas:' "$OUT/$chart.yaml")" -eq 0
    check "$chart renders an HPA" test "$(count '^kind: HorizontalPodAutoscaler$' "$OUT/$chart.yaml")" -eq 1
    check "$chart renders a PodDisruptionBudget" \
        test "$(count '^kind: PodDisruptionBudget$' "$OUT/$chart.yaml")" -eq 1
done

# --------------------------------------------------------------------------
section 'The grace period exceeds the host shutdown timeout'
# --------------------------------------------------------------------------
# HostOptions.ShutdownTimeout defaults to 30 s and nothing in this solution
# overrides it, so 30 is not a margin over 30 — a pod on the Kubernetes default
# is SIGKILLed at the instant the host would have finished draining.
for chart in $SERVICE_CHARTS; do
    grace="$(awk '/terminationGracePeriodSeconds:/ { print $2; exit }' "$OUT/$chart.yaml")"
    check "$chart grace period ($grace s) exceeds the 30 s shutdown timeout" test "${grace:-0}" -gt 30
done

# --------------------------------------------------------------------------
section 'Migration hook (§7.4, ADR-007)'
# --------------------------------------------------------------------------
for chart in $MIGRATOR_CHARTS; do
    check "$chart renders a migration Job" test "$(count '^kind: Job$' "$OUT/$chart.yaml")" -eq 1
    check "$chart runs it pre-install,pre-upgrade" \
        grep -q '"helm.sh/hook": pre-install,pre-upgrade' "$OUT/$chart.yaml"
    check "$chart weights the hook ahead of any other" \
        grep -q '"helm.sh/hook-weight": "-5"' "$OUT/$chart.yaml"
    # BOTH policies. `before-hook-creation` matches on NAME and the name embeds
    # the tag, so on its own every new SHA leaves its completed Job behind for
    # ever — and §13.6's runbook then looks for the failed one in a list of
    # every migration that ever succeeded. `hook-failed` is deliberately absent:
    # the failed Job is the artefact that runbook needs.
    check "$chart deletes the previous hook rather than accumulating them" \
        grep -q '"helm.sh/hook-delete-policy": before-hook-creation,hook-succeeded' "$OUT/$chart.yaml"
    check "$chart keeps a FAILED migration Job for the runbook" \
        test "$(count 'hook-failed' "$OUT/$chart.yaml")" -eq 0
    check "$chart mounts the MIGRATOR connection string, not the runtime one" \
        grep -qE '^ *- name: ConnectionStrings__[A-Za-z]+Migrator$' "$OUT/$chart.yaml"

    # The migrator must not be selectable by the Service it migrates: for the
    # length of every pre-upgrade hook a pod with a database connection and no
    # HTTP listener would otherwise be an endpoint of a live service, and
    # inside its PDB. The Service's selector name is compared with the Job pod
    # template's, which is the pair that decides endpoint membership; the Job
    # object's labels still carry the ordinary identity.
    svc_name="$(awk '/^kind: Service$/ { s = 1 } s && /^    app.kubernetes.io\/name: / { sub(/.*: /, ""); print; exit }' "$OUT/$chart.yaml")"
    job_pod_name="$(awk '/^kind: Job$/ { j = 1 } j && /^  template:/ { t = 1 } t && /^        app.kubernetes.io\/name: / { sub(/.*: /, ""); print; exit }' "$OUT/$chart.yaml")"
    check "$chart's migrator pod is not an endpoint of its own Service ($job_pod_name vs $svc_name)" \
        test "$job_pod_name" != "$svc_name"
    check "$chart's migrator pod says what it is" \
        grep -q 'app.kubernetes.io/component: migrator' "$OUT/$chart.yaml"
done

# The gateway and the BFF own no database (§10.1, §4.2), so the hook has
# nothing to run for them. The output assertion alone is vacuous — those
# charts carry no migration template at all (§15.3) — so the subject is the
# agreement between the two halves: a chart has a migration template exactly
# when its values name a migrator image, and it fires broken from either side.
for chart in $DATABASELESS_CHARTS; do
    check "$chart renders no migration Job" test "$(count '^kind: Job$' "$OUT/$chart.yaml")" -eq 0
    check "$chart mounts no connection string at all" \
        test "$(count 'ConnectionStrings__' "$OUT/$chart.yaml")" -eq 0
done

for chart in $SERVICE_CHARTS; do
    has_template=no
    [ -f "$CHARTS_DIR/$chart/templates/migrate-job.yaml" ] && has_template=yes
    has_image=no
    grep -qE '^ +migrator: ' "$CHARTS_DIR/$chart/values.yaml" && has_image=yes
    check "$chart: migration template ($has_template) and image.migrator ($has_image) agree" \
        test "$has_template" = "$has_image"
done

# Driven from the chart's own redis block, over every service chart, because
# redis is not a fact about databases. The expected count is 0 or 2 and never
# 1: the two connections are provisioned together, and a chart carrying one is
# a pod that resolves the other to null at startup.
for chart in $SERVICE_CHARTS; do
    if declares "$chart" redis; then expected=2; else expected=0; fi
    check "$chart declares redis=$(declares "$chart" redis && echo yes || echo no), so it mounts $expected of §8.1's connections" \
        test "$(count '^ *- name: ConnectionStrings__Redis' "$OUT/$chart.yaml")" -eq "$expected"
    # The two keys must differ: both instances are provisioned together and
    # named alike, so one Secret key copied onto both rows renders cleanly,
    # passes the count above, and points §8.5's idempotency claims at the
    # allkeys-lru instance §8.1 exists to keep them off.
    if [ "$expected" -eq 2 ]; then
        check "$chart: the cache and coordination connections read different Secret keys" \
            test "$(awk '/- name: ConnectionStrings__Redis/ { want = 1; next }
                         want && /key: / { print $2; want = 0 }' "$OUT/$chart.yaml" | sort -u | wc -l)" -eq 2
    fi
done

# --------------------------------------------------------------------------
section 'The ConfigMap/Secret split is §15.4 read down its Kind column'
# --------------------------------------------------------------------------
# The rule is mechanical: if the value contains a credential, it is a Secret. A
# connection string in a ConfigMap is a password readable by anyone with
# namespace read access and unencrypted at rest.
configmap_data_lines() {
    awk '
        /^kind: ConfigMap$/ { in_cm = 1 }
        /^---$/ { in_cm = 0 }
        in_cm && /^ *[A-Za-z_]+__[A-Za-z0-9_]*:/ { print }
    ' "$1"
}

configmap_data_lines "$OUT/platform.yaml" >"$OUT/configmap-keys.txt"
check 'no ConfigMap carries a connection string' \
    test "$(count 'ConnectionStrings__' "$OUT/configmap-keys.txt")" -eq 0
check 'no ConfigMap carries a client secret' \
    test "$(count 'Identity__Client__ClientSecret' "$OUT/configmap-keys.txt")" -eq 0
check 'every ConnectionStrings__ value comes from a secretKeyRef' \
    test "$(count 'ConnectionStrings__' "$OUT/platform.yaml")" \
    -eq "$(awk '/- name: ConnectionStrings__/ { want = 1; next } want && /secretKeyRef/ { n++; want = 0 } END { print n + 0 }' "$OUT/platform.yaml")"

# --------------------------------------------------------------------------
section 'Client credentials: exactly one chart (§11.5, §15.3)'
# --------------------------------------------------------------------------
# A second chart growing an identity.clientId is a design change, not a
# configuration change: it means a host started calling a peer synchronously,
# which is ADR-017's budget being spent.
check 'exactly one workload in the platform holds a client secret' \
    test "$(count 'Identity__Client__ClientSecret' "$OUT/platform.yaml")" -eq 1
check 'and it is the BFF' \
    test "$(count 'Identity__Client__ClientSecret' "$OUT/web-bff.yaml")" -eq 1

# --------------------------------------------------------------------------
section 'The edge keys belong to the gateway alone (§15.4)'
# --------------------------------------------------------------------------
for key in Ingress__Enabled Cors__Enabled Ingress__TrustedNetworks__0; do
    check "$key is rendered once across the platform" \
        test "$(count "$key" "$OUT/platform.yaml")" -eq 1
    check "$key is the gateway's" test "$(count "$key" "$OUT/gateway.yaml")" -eq 1
done

check 'the platform has exactly one Ingress' \
    test "$(count '^kind: Ingress$' "$OUT/platform.yaml")" -eq 1
# The backend, not merely a line saying `gateway` somewhere — the Deployment
# and the ConfigMap both carry that name, so a looser grep would pass on a
# render whose Ingress pointed at nothing at all.
check 'and it routes to the gateway Service' \
    awk '/^kind: Ingress$/ { in_ing = 1 } in_ing && /^ *service:$/ { want = 1; next } want && /name: gateway$/ { found = 1 } END { exit found ? 0 : 1 }' \
    "$OUT/platform.yaml"

# --------------------------------------------------------------------------
section 'Every ConfigMap an envFrom names is rendered by the same release'
# --------------------------------------------------------------------------
# The gateway mounts a second ConfigMap it renders itself, and the name is
# derived in two places — values.yaml's extraConfigMaps suffix and
# edge-config.yaml's metadata. Without this a rename in one is a pod stuck in
# CreateContainerConfigError.
awk '/configMapRef:/ { want = 1; next } want && /name:/ { sub(/^ *name: /, ""); print; want = 0 }' \
    "$OUT/platform.yaml" | sort -u >"$OUT/mounted.txt"
awk '/^kind: ConfigMap$/ { want = 1 } want && /^  name: / { sub(/^  name: /, ""); print; want = 0 }' \
    "$OUT/platform.yaml" | sort -u >"$OUT/rendered.txt"
check 'every mounted ConfigMap exists in the render' \
    test -z "$(comm -23 "$OUT/mounted.txt" "$OUT/rendered.txt")"

# ...and a change to any of them rolls the pods: a checksum over only the
# ConfigMap the library renders leaves `gateway-edge` out, so editing
# `cors.origins` rewrites a mounted ConfigMap and leaves the pod template
# byte-identical — a deploy that reports success and changes nothing.
gateway_checksum() {
    "$HELM" template gateway "$CHARTS_DIR/gateway" --set-string "image.tag=$TAG" \
        $GATEWAY_OVERLAY "$@" |
        awk '/checksum\/values:/ { print $2; exit }'
}
before="$(gateway_checksum)"
after="$(gateway_checksum --set cors.enabled=true --set 'cors.origins={https://shop.example.com}')"
check 'editing an edge-only value rolls the gateway pods' test "$before" != "$after"

# --------------------------------------------------------------------------
section 'Service names are routing configuration (§10.2, §9.7)'
# --------------------------------------------------------------------------
# The gateway's route file and PricingHop.cs dial these hosts as literals, on
# the premise that the host is the Kubernetes Service name; this keeps that
# true. One direction only: every Service this platform renders must be a name
# something in src/ dials, plus the gateway, which the Ingress dials. The other
# direction is not asserted, because §10.2's route file deliberately names
# inventory ahead of the service that will answer it. Host and port both,
# because both are literals and both are dialled.
grep -ohE 'http://[a-z0-9-]+:[0-9]+' \
    "$ROOT/src/Gateway/Gateway.Api/appsettings.json" \
    "$ROOT/src/BFF/Web.Bff/PricingHop.cs" |
    sed -E 's|http://([a-z0-9-]+):([0-9]+)|\1 \2|' | sort -u >"$OUT/pairs.txt"

cut -d' ' -f1 "$OUT/pairs.txt" | sort -u >"$OUT/dialled.txt"
echo gateway >>"$OUT/dialled.txt"
sort -u -o "$OUT/dialled.txt" "$OUT/dialled.txt"

awk '/^kind: Service$/ { want = 1 } want && /^  name: / { sub(/^  name: /, ""); print; want = 0 }' \
    "$OUT/platform.yaml" | sort -u >"$OUT/services.txt"
undialled="$(comm -23 "$OUT/services.txt" "$OUT/dialled.txt")"
if [ -z "$undialled" ]; then
    pass 'every rendered Service is a name src/ dials'
else
    fail "Service(s) nothing in src/ dials: $(echo "$undialled" | tr '\n' ' ')"
fi

# ...and the other direction, which an overlay can break: a chart whose
# workload name is dialled from src/ must render a Service, or a healthy
# release answers every routed request with a failure.
for chart in $SERVICE_CHARTS; do
    name="$(awk '/^workload:/ { w = 1 } w && /^  name: / { sub(/^  name: /, ""); print; exit }' \
        "$CHARTS_DIR/$chart/values.yaml")"
    if grep -qx "$name" "$OUT/pairs.txt" 2>/dev/null || cut -d' ' -f1 "$OUT/pairs.txt" | grep -qx "$name"; then
        check "$chart is dialled as $name, so it must keep its Service" \
            grep -qE '^  enabled: true' <(awk '/^service:/ { s = 1; next } s && /^[a-z]/ { s = 0 } s' "$CHARTS_DIR/$chart/values.yaml")
    else
        pass "$chart ($name) is not a routed destination — its Service is optional"
    fi
done

# The ports are literals in the same two files, so they are asserted the same
# way — and it is the Service port, not the container port: `PricingHop.cs`
# dials `http://catalog-api:8081`, and what answers is `spec.ports[].port`.
# Every pair the name gate parses is a row, because `_service.tpl` takes `port`
# from per-chart values.
service_port() {
    # <file> <service name> <port> -> exit 0 when that Service publishes it
    awk -v want="$2" -v port="$3" '
        /^kind: Service$/ { in_svc = 1; named = 0; next }
        /^---$/ { in_svc = 0; named = 0; next }
        in_svc && $0 ~ ("^  name: " want "$") { named = 1 }
        named && $0 ~ ("^ *- port: " port "$") { found = 1 }
        named && $0 ~ ("^ *port: " port "$") { found = 1 }
        END { exit found ? 0 : 1 }
    ' "$1"
}

while read -r host port; do
    if grep -qx "$host" "$OUT/services.txt"; then
        check "$host answers on Service port $port" \
            service_port "$OUT/platform.yaml" "$host" "$port"
    else
        # Not a silent cap: §10.2's route file deliberately names a
        # destination ahead of the service that will answer it, so a host with
        # no chart is expected — and saying which one keeps that expectation
        # from quietly absorbing a chart somebody forgot to add.
        pass "$host:$port dialled by src/, no chart yet — not asserted"
    fi
done <"$OUT/pairs.txt"

# --------------------------------------------------------------------------
section 'Values that must agree across charts'
# --------------------------------------------------------------------------
# §15.3 gives each chart its own values file, so a platform-wide value is
# written once per service chart. That is the chapter's design and it is also
# a drift risk — converted here into a gated invariant rather than left to
# review.
for key in Identity__Authority OTEL_EXPORTER_OTLP_ENDPOINT; do
    distinct="$(grep -h "^ *$key:" "$OUT/platform.yaml" | sed 's/^ *//' | sort -u | wc -l)"
    check "$key has one value across every chart (found $distinct)" test "$distinct" -eq 1
done

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
# on-but-unconfigured is a silent defect. The gateway's own startup guards
# catch it, and catching it at render says which chart value is missing.
if "$HELM" template gateway "$CHARTS_DIR/gateway" --set-string "image.tag=$TAG" \
    --set 'ingress.trustedNetworks=null' >"$OUT/untrusted.txt" 2>&1; then
    fail 'ingress.enabled with no trustedNetworks renders — it must not'
else
    check 'ingress.enabled with no trustedNetworks fails the render' \
        grep -q 'ingress.trustedNetworks must hold at least one CIDR' "$OUT/untrusted.txt"
fi

if "$HELM" template gateway "$CHARTS_DIR/gateway" --set-string "image.tag=$TAG" \
    $GATEWAY_OVERLAY --set cors.enabled=true >"$OUT/uncorsed.txt" 2>&1; then
    fail 'cors.enabled with no origins renders — it must not'
else
    check 'cors.enabled with no origins fails the render' \
        grep -q 'cors.origins must hold at least one origin' "$OUT/uncorsed.txt"
fi

# Blank counts as missing, and an emptiness check does not see it: a list
# holding `" "` is truthy in a template, so the value renders blank and the
# host throws at startup, after the rollout has begun.
refuses() {
    # refuses <label> <needle> <helm args...>
    local label="$1" needle="$2"
    shift 2
    if "$HELM" template gateway "$CHARTS_DIR/gateway" --set-string "image.tag=$TAG" \
        "$@" >"$OUT/blank.txt" 2>&1; then
        fail "$label — it rendered instead"
    else
        check "$label" grep -q "$needle" "$OUT/blank.txt"
    fi
}

refuses 'a blank trusted network fails the render' 'is blank' \
    --set 'ingress.trustedNetworks={ }'
refuses 'a blank CORS origin fails the render' 'is blank' \
    $GATEWAY_OVERLAY --set cors.enabled=true --set 'cors.origins={ }'
refuses 'a CORS origin with a trailing path fails the render' 'is not a browser origin' \
    $GATEWAY_OVERLAY --set cors.enabled=true --set 'cors.origins={https://shop.example.com/app}'

# The Ingress backend is this workload's Service, so the two keys are not
# independent — and the inconsistent pair is what a copied values file
# produces when a worker's Service is turned off and the edge's Ingress is
# left on. It installs cleanly and answers 503 for every request.
refuses 'an Ingress with no Service fails the render' 'ingress.enabled requires service.enabled' \
    $GATEWAY_OVERLAY --set service.enabled=false

# TLS terminates at the Ingress (§10.1) and every hop past it is plain http on
# that premise — including §9.7's. An overlay clearing it rendered a valid
# plaintext Ingress and falsified the premise silently.
refuses 'an Ingress with no TLS fails the render' 'ingress.tls is required' \
    $GATEWAY_OVERLAY --set 'ingress.tls=null'

# --------------------------------------------------------------------------
section 'Defaults that must stay absent'
# --------------------------------------------------------------------------
# Every guard above is a property of the render, and this one cannot be: a
# chart shipping a plausible `trustedNetworks` renders perfectly well. Wrong
# low, the real ingress is untrusted and §10.3's per-client rate limit
# collapses into one global bucket; wrong high, any pod in the range picks its
# own partition. Neither shows up in a rollout, so the only safe default is
# none.
check 'the gateway ships no default trusted network' \
    grep -qE '^  trustedNetworks: \[\]' "$CHARTS_DIR/gateway/values.yaml"

# --------------------------------------------------------------------------
section 'Blank is not present, whatever `required` thinks'
# --------------------------------------------------------------------------
# Helm's `required` fails on nil and on "" and passes `" "`; the hosts guard
# with IsNullOrWhiteSpace, so a whitespace-only overlay renders cleanly and
# dies in the new pod. Every required scalar goes through `commerce.require`,
# which trims first; these assert the ones that reach a host eagerly.
refuses 'a whitespace-only authority fails the render' 'identity.authority is required' \
    $GATEWAY_OVERLAY --set-string 'identity.authority= '
refuses 'a whitespace-only OTLP endpoint fails the render' 'observability.otlpEndpoint is required' \
    $GATEWAY_OVERLAY --set-string 'observability.otlpEndpoint=   '
refuses 'a whitespace-only workload name fails the render' 'workload.name is required' \
    $GATEWAY_OVERLAY --set-string 'workload.name= '

# --------------------------------------------------------------------------
section 'Non-blank is not an address either'
# --------------------------------------------------------------------------
# The hosts parse both of these before they will start, and each rejects far
# more than the empty string — an absolute HTTPS address, no user information,
# no query, no fragment. Under a presence check every value below renders,
# begins a rollout and dies in the new pod, which is what `commerce.requireUrl`
# moves to render time. Asserted per rejected shape rather than once, because a
# guard is a claim about what it refuses.
for bad in 'keycloak:8080/realms/commerce' 'ftp://id.example.com/realms' \
    'http://id.example.com/realms/commerce' 'https://u:p@id.example.com/realms' \
    'https://id.example.com/realms?x' 'https://id.example.com/realms#f' \
    'https://:443/realms' 'https://id.example.com:bad/realms' \
    'https://[::1/realms'; do
    refuses "an authority of '$bad' fails the render" 'HTTPS address this chart will accept' \
        $GATEWAY_OVERLAY --set-string "identity.authority=$bad"
done

# The port's range is a separate refusal with its own message, because the
# digits satisfy the shape and it is `Uri.TryCreate` that draws the bound.
for bad in 'https://id.example.com:65536/realms' 'https://id.example.com:0/realms'; do
    refuses "an authority on port '${bad##*:}' fails the render" 'outside 1-65535' \
        $GATEWAY_OVERLAY --set-string "identity.authority=$bad"
done

# The same guard on the other key it protects, which needs the payments chart
# rather than the gateway: `refuses` renders the gateway, which has no
# capability to carry an address at all.
refuses_payments() {
    local label="$1" needle="$2"
    shift 2
    if "$HELM" template payments "$CHARTS_DIR/payments" --set-string "image.tag=$TAG" \
        "$@" >"$OUT/payments-bad-url.txt" 2>&1; then
        fail "$label — it rendered instead"
    else
        check "$label" grep -q "$needle" "$OUT/payments-bad-url.txt"
    fi
}

for bad in 'psp.example.invalid' 'ftp://psp.example.invalid/' \
    'http://psp.example.invalid/' 'https://user:key@psp.example.invalid/' \
    'https://psp.example.invalid/?x' 'https://psp.example.invalid/#f' \
    'https://:443/' 'https://psp.example.invalid:bad/' \
    'https://[::1/'; do
    refuses_payments "a provider address of '$bad' fails the render" \
        'HTTPS address this chart will accept' \
        --set-string "paymentProvider.baseUrl=$bad"
done

for bad in 'https://psp.example.invalid:65536/' 'https://psp.example.invalid:0/'; do
    refuses_payments "a provider address on port '${bad%/}' fails the render" \
        'outside 1-65535' \
        --set-string "paymentProvider.baseUrl=$bad"
done

# And the other direction, because a guard that only ever refuses is
# indistinguishable from one that refuses everything: the shapes an operator
# legitimately writes must still render.
for good in 'https://psp.example.invalid/' 'https://psp.example.invalid:8443/v1/' \
    'https://psp.example.invalid'; do
    check "a provider address of '$good' renders" \
        "$HELM" template payments "$CHARTS_DIR/payments" --set-string "image.tag=$TAG" \
        --set-string "paymentProvider.baseUrl=$good"
done

# The origin guard has to reject what Program.cs rejects, or it is theatre:
# each of these renders, begins a rollout, and crashes the new pod otherwise.
refuses 'an origin carrying userinfo fails the render' 'is not a browser origin' \
    $GATEWAY_OVERLAY --set cors.enabled=true --set 'cors.origins={https://user:pass@shop.example.com}'
refuses 'an origin carrying a query fails the render' 'is not a browser origin' \
    $GATEWAY_OVERLAY --set cors.enabled=true --set 'cors.origins={https://shop.example.com?x}'
refuses 'an origin naming the default port fails the render' 'default port' \
    $GATEWAY_OVERLAY --set cors.enabled=true --set 'cors.origins={https://shop.example.com:443}'
refuses 'an origin with a leading-zero port fails the render' 'non-canonically' \
    $GATEWAY_OVERLAY --set cors.enabled=true --set 'cors.origins={https://shop.example.com:08080}'

# A capability is a fact about the code, not an environment setting. Each of
# these renders cleanly and produces a pod that will not start, and each has to
# be aimed at a chart that has the capability — `refuses` above renders the
# gateway, which owns no database and no migrator.
refuses_chart() {
    # refuses_chart <chart> <label> <needle> <helm args...>
    local chart="$1" label="$2" needle="$3"
    shift 3
    if "$HELM" template "$chart" "$CHARTS_DIR/$chart" --set-string "image.tag=$TAG" \
        "$@" >"$OUT/cap.txt" 2>&1; then
        fail "$label — it rendered instead"
    else
        check "$label" grep -q "$needle" "$OUT/cap.txt"
    fi
}

refuses_chart catalog 'disabling a database the chart is configured for fails the render' \
    'database.enabled is false' --set database.enabled=false
refuses_chart catalog 'disabling a broker the chart is configured for fails the render' \
    'broker.enabled is false' --set broker.enabled=false
refuses_chart catalog 'disabling redis the chart is configured for fails the render' \
    'redis.enabled is false' --set redis.enabled=false
refuses_chart catalog 'clearing the coordination Secret key fails the render' \
    'redis.secretRef.coordinationKey is required' --set-string 'redis.secretRef.coordinationKey='
# The two keys being present is not the same claim as their being different:
# a production overlay pointing both connections at the allkeys-lru instance
# renders green, and an evicted idempotency claim leaves no trace of having
# existed.
refuses_chart catalog 'pointing both Redis connections at one Secret key fails the render' \
    'are the same key' --set-string 'redis.secretRef.coordinationKey=cache-connection-string'

refuses_chart catalog 'clearing the migrator image fails the render' \
    'image.migrator is required' --set-string 'image.migrator='

# A tag is three things with three alphabets: an image reference, a Job name
# (DNS-1123 subdomain) and a label value. `Release_1` is legal for a registry
# and refused by the API server after the upgrade has started. A DNS-1123
# subdomain is dot-separated labels, so the check is per segment and so are
# these cases.
for bad in Release_1 release_1 release..1 release.-1 -release release-; do
    if "$HELM" template catalog "$CHARTS_DIR/catalog" --set-string "image.tag=$bad" \
        >"$OUT/badtag.txt" 2>&1; then
        fail "image.tag=$bad renders — Kubernetes would refuse the Job it names"
    else
        check "image.tag=$bad fails the render" \
            grep -q 'not usable as Kubernetes metadata' "$OUT/badtag.txt"
    fi
done

for good in 1.2.3 0000000000000000000000000000000000000000 v1-2-3; do
    check "image.tag=$good still renders" \
        "$HELM" template catalog "$CHARTS_DIR/catalog" --set-string "image.tag=$good"
done

# The name budget, which the per-segment check cannot see: a plain `trunc 63`
# can cut immediately after a dot, and trimming a trailing hyphen never
# touches a trailing dot.
long_tag="$(printf 'a%.0s' $(seq 1 42)).b"
if "$HELM" template catalog "$CHARTS_DIR/catalog" --set-string "image.tag=$long_tag" \
    >"$OUT/longtag.txt" 2>&1; then
    fail 'a tag that overruns the Job-name budget renders — it must not'
else
    check 'a tag that overruns the Job-name budget fails the render' \
        grep -q 'may not exceed 63' "$OUT/longtag.txt"
fi

# And the image reference's other two components, guarded like the tag.
refuses_chart catalog 'clearing image.registry fails the render' \
    'image.registry is required' --set-string 'image.registry='
refuses_chart catalog 'clearing image.api fails the render' \
    'image.api is required' --set-string 'image.api='
refuses 'an origin with a non-numeric port fails the render' 'is not a browser origin' \
    $GATEWAY_OVERLAY --set cors.enabled=true --set 'cors.origins={https://shop.example.com:notaport}'
# Case, which the shape test cannot see: the canonical origin lowercases scheme
# and host and WithOrigins compares ordinally, so `https://SPA.example` is
# refused by the host.
refuses 'a non-lowercase origin fails the render' 'is not lowercase' \
    $GATEWAY_OVERLAY --set cors.enabled=true --set 'cors.origins={https://SPA.example}'

# And the CIDR list, where blank was only the emptiest way to be wrong:
# `not-a-network` renders and throws out of IPNetwork.Parse at startup.
refuses 'a malformed trusted network fails the render' 'is not an IPv4 CIDR' \
    --set 'ingress.trustedNetworks={not-a-network}'
refuses 'a trusted network with a bad octet fails the render' 'octet above 255' \
    --set 'ingress.trustedNetworks={10.0.300.0/8}'
refuses 'a trusted network with a bad prefix fails the render' 'prefix length above 32' \
    --set 'ingress.trustedNetworks={10.0.0.0/64}'
# The security case rather than a tidiness one: IPNetwork.Parse("010.0.0.0/8")
# returns 8.0.0.0/8, because a leading zero is read as octal, so the operator
# writes one network and the gateway trusts another.
refuses 'a trusted network with an octal octet fails the render' 'non-canonically' \
    --set 'ingress.trustedNetworks={010.0.0.0/8}'

# The rendered value has to be the validated one: a guard that checks one
# string and ships another passes every test above and fails the host's exact
# text comparison at startup.
"$HELM" template gateway "$CHARTS_DIR/gateway" --set-string "image.tag=$TAG" \
    $GATEWAY_OVERLAY --set cors.enabled=true \
    --set 'cors.origins={https://shop.example.com }' >"$OUT/spaced.txt" 2>&1 || true
check 'the rendered origin is the validated one, not the raw value' \
    grep -q 'Cors__Origins__0: "https://shop.example.com"' "$OUT/spaced.txt"

# Web.Bff binds ServiceIdentityOptions unconditionally, so clearing clientId
# is not an opt-out: it renders a release whose pod refuses to start.
refuses_bff() {
    local label="$1" needle="$2"
    shift 2
    if "$HELM" template web-bff "$CHARTS_DIR/web-bff" --set-string "image.tag=$TAG" \
        "$@" >"$OUT/bff.txt" 2>&1; then
        fail "$label — it rendered instead"
    else
        check "$label" grep -q "$needle" "$OUT/bff.txt"
    fi
}
refuses_bff 'clearing the BFF client id fails the render' 'identity.clientId is required' \
    --set-string 'identity.clientId='
refuses_bff 'a whitespace-only BFF client id fails the render' 'identity.clientId is required' \
    --set-string 'identity.clientId= '
refuses_bff 'disabling the BFF client credentials fails the render' \
    'clientCredentials is false' --set identity.clientCredentials=false

# --------------------------------------------------------------------------
section 'No pod carries a cluster credential it never uses'
# --------------------------------------------------------------------------
# Nothing here calls the Kubernetes API, and omitting the field mounts the
# namespace's default service-account token anyway — so an application
# compromise also hands over a cluster credential. It matters most on the
# migration Job, which holds the one identity with DDL rights (§7.1).
check 'every pod template disables the service-account token' \
    test "$(count 'automountServiceAccountToken: false' "$OUT/platform.yaml")" \
    -eq "$(( $(count '^kind: Deployment$' "$OUT/platform.yaml") + $(count '^kind: Job$' "$OUT/platform.yaml") ))"

# --------------------------------------------------------------------------
section 'The Service forwards to a port something is listening on'
# --------------------------------------------------------------------------
# The routing gate above compares caller URLs with rendered Service ports and
# never looks at the process behind `targetPort`. Catalog declares its two
# Kestrel endpoints in its own appsettings.json (§9.7: a cleartext port cannot
# serve HTTP/1.1 and h2c at once), so moving the h2c listener there would
# deploy a Service forwarding to a closed port.
grep -ohE 'http://0\.0\.0\.0:[0-9]+' "$ROOT/src/Services/Catalog/Catalog.Api/appsettings.json" |
    sed -E 's|.*:([0-9]+)|\1|' | sort -u >"$OUT/listeners.txt"

if [ ! -s "$OUT/listeners.txt" ]; then
    fail 'no Kestrel endpoints found in Catalog appsettings.json — the parse, not the chart, is wrong'
else
    while read -r port; do
        check "catalog-api listens on $port and the chart declares it" \
            grep -q "containerPort: $port$" "$OUT/catalog.yaml"
    done <"$OUT/listeners.txt"
fi

# And the other direction, so a chart port with nothing behind it is caught too.
awk '/^kind: Deployment$/ { in_dep = 1 } in_dep && /containerPort:/ { print $2 }' \
    "$OUT/catalog.yaml" | sort -u >"$OUT/declared.txt"
missing="$(comm -23 "$OUT/declared.txt" "$OUT/listeners.txt")"
if [ -z "$missing" ]; then
    pass 'and declares no port Catalog does not listen on'
else
    fail "chart declares port(s) Catalog has no listener for: $(echo "$missing" | tr '\n' ' ')"
fi

# --------------------------------------------------------------------------
section 'The canary track (§15.5, ADR-022)'
# --------------------------------------------------------------------------
# The canary render is reached by nothing above, and every service chart is
# rendered rather than a representative one: the mechanism lives in the
# library, so a chart that failed to pick it up would be a service with no
# canary and a rollout that promoted it without ever splitting traffic.
for chart in $SERVICE_CHARTS; do
    "$HELM" template "$chart-canary" "$CHARTS_DIR/$chart" --set-string "image.tag=$TAG" \
        --set canary.enabled=true --set autoscaling.enabled=false \
        $GATEWAY_OVERLAY $(overlay_for "$chart") >"$OUT/$chart-canary.yaml"
    pass "$chart renders a canary"

    name="$(awk '/^workload:/ { w = 1 } w && /^  name: / { sub(/^  name: /, ""); print; exit }' \
        "$CHARTS_DIR/$chart/values.yaml")"

    # The one that makes it a canary: traffic reaches these pods because the
    # stable release's Service selects them on the workload name alone, so the
    # canary's pod label has to be the same string. A `-canary` suffix leaking
    # into it is a canary that runs, reports healthy, serves nothing, and is
    # promoted on an analysis of no traffic.
    check "$chart: canary pods answer to the stable Service's selector" \
        awk -v want="$name" '
            /^kind: Deployment$/ { in_dep = 1 }
            in_dep && /^    matchLabels:$/ { in_sel = 1; next }
            in_sel && /app.kubernetes.io\/name: / {
                sub(/.*: /, ""); if ($0 == want) found = 1; in_sel = 0
            }
            END { exit found ? 0 : 1 }
        ' "$OUT/$chart-canary.yaml"

    # And the two Deployments must not select each other's pods, or each
    # scales the other away. The track label has to be in the Deployment's
    # selector, and the same label is also on the metadata and the pod
    # template, so a grep over the document cannot assert it.
    selects_track() {
        # selects_track <file> <track> -> exit 0 when the Deployment's
        # matchLabels carries that track
        awk -v want="$2" '
            /^kind: Deployment$/ { in_dep = 1 }
            in_dep && /^    matchLabels:$/ { in_sel = 1; next }
            in_sel && /^      [a-z]/ {
                if ($0 ~ ("app.kubernetes.io/track: " want)) found = 1
                next
            }
            in_sel { in_sel = 0 }
            END { exit found ? 0 : 1 }
        ' "$1"
    }

    check "$chart: the canary Deployment SELECTS on track=canary" \
        selects_track "$OUT/$chart-canary.yaml" canary
    check "$chart: the stable Deployment SELECTS on track=stable" \
        selects_track "$OUT/$chart.yaml" stable
    check "$chart: no stable object leaks into the canary render" \
        test "$(count 'app.kubernetes.io/track: stable' "$OUT/$chart-canary.yaml")" -eq 0

    # Helm refuses to render an object another release owns (§15.3), so every
    # name the canary release emits has to differ from the stable one's. These
    # are the four the stable release keeps.
    for kind in Service Ingress HorizontalPodAutoscaler PodDisruptionBudget; do
        check "$chart: the canary renders no $kind" \
            test "$(count "^kind: $kind\$" "$OUT/$chart-canary.yaml")" -eq 0
    done
    check "$chart: the canary Deployment is named $name-canary" \
        grep -q "^  name: $name-canary\$" "$OUT/$chart-canary.yaml"

    # The replica count has to reach the spec: `_deployment.tpl` omits
    # `replicas` whenever `autoscaling.enabled` is true, so a canary installed
    # without `--set autoscaling.enabled=false` is defaulted to one pod on
    # every rung of the ladder. The render above passes the flag as the rollout
    # does; this asserts it is what it is passed for.
    check "$chart: the canary Deployment carries a replica count" \
        grep -q '^  replicas: ' "$OUT/$chart-canary.yaml"

    # The ConfigMap too — the mount has to follow the rename or the pod sits
    # in CreateContainerConfigError, asserted as agreement between the two
    # halves rather than against a literal.
    awk '/configMapRef:/ { want = 1; next } want && /name:/ { sub(/^ *name: /, ""); print; want = 0 }' \
        "$OUT/$chart-canary.yaml" | sort -u >"$OUT/$chart-canary-mounted.txt"
    awk '/^kind: ConfigMap$/ { want = 1 } want && /^  name: / { sub(/^  name: /, ""); print; want = 0 }' \
        "$OUT/$chart-canary.yaml" | sort -u >"$OUT/$chart-canary-rendered.txt"
    check "$chart: every ConfigMap the canary mounts, the canary renders" \
        test -z "$(comm -23 "$OUT/$chart-canary-mounted.txt" "$OUT/$chart-canary-rendered.txt")"
    check "$chart: and none of them is the stable release's" \
        test "$(grep -cvE -- '-canary(-|$)' "$OUT/$chart-canary-rendered.txt")" -eq 0

    # The discriminator the analysis actually reads. Without it both tracks
    # report the same series and every step compares a release against itself
    # — which passes, every time, on a canary that is on fire.
    check "$chart: the canary declares deployment.track=canary" \
        grep -q 'OTEL_RESOURCE_ATTRIBUTES: "deployment.track=canary"' "$OUT/$chart-canary.yaml"
    check "$chart: the stable release declares deployment.track=stable" \
        grep -q 'OTEL_RESOURCE_ATTRIBUTES: "deployment.track=stable"' "$OUT/$chart.yaml"
done

# ADR-022's load-bearing consequence: the canary release runs §7.4's hook,
# because it is the first thing carrying the new image, so a rollback removes
# the pods and leaves the schema migrated. The templates do that only because
# `_migration-job.tpl` has no canary guard, and a later `if not canary` would
# pass every assertion above. Both directions, as for the migration-template
# check further up.
for chart in $MIGRATOR_CHARTS; do
    check "$chart: the canary runs the migration hook (ADR-022)" \
        test "$(count '^kind: Job$' "$OUT/$chart-canary.yaml")" -eq 1
    check "$chart: and it is the same hook the stable release runs" \
        test "$(count '"helm.sh/hook": pre-install,pre-upgrade' "$OUT/$chart-canary.yaml")" -eq 1
done

for chart in $DATABASELESS_CHARTS; do
    check "$chart: the canary renders no migration Job either" \
        test "$(count '^kind: Job$' "$OUT/$chart-canary.yaml")" -eq 0
done

# The rollout plan names a chart per workload, and a plan pointing at a chart
# that cannot render a canary is a deploy that fails after the scale-up.
for chart in $SERVICE_CHARTS; do
    check "$chart appears in deploy/canary/canary.json" \
        grep -q "\"chart\": \"$chart\"" "$ROOT/deploy/canary/canary.json"
done

# --------------------------------------------------------------------------
section 'Result'
# --------------------------------------------------------------------------
if [ "$failures" -ne 0 ]; then
    printf '%s assertion(s) failed\n' "$failures" >&2
    exit 1
fi
printf 'all assertions passed\n'
