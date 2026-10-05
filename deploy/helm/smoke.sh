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

# The NetworkPolicy peers a deployment states for what runs outside the
# namespace (ADR-065), on the same terms: one CIDR per destination, each
# distinct, so a case below can tell which destination a rendered rule names.
NETPOL_PEERS="
database=10.91.0.0/16
redis=10.92.0.0/16
broker=10.93.0.0/16
identity=10.94.0.0/16
carrier=10.95.0.0/16
mail=10.96.0.0/16
paymentProvider=10.97.0.0/16
"
NETPOL_OVERLAY=''
for pair in $NETPOL_PEERS; do
    NETPOL_OVERLAY="$NETPOL_OVERLAY --set networkPolicy.${pair%%=*}.to[0].ipBlock.cidr=${pair#*=}"
done
INGRESS_CONTROLLER_CIDR=10.98.0.0/16
NETPOL_OVERLAY="$NETPOL_OVERLAY --set networkPolicy.ingressController.from[0].ipBlock.cidr=$INGRESS_CONTROLLER_CIDR"

GATEWAY_OVERLAY="--set ingress.trustedNetworks=$CIDR $NETPOL_OVERLAY"
PLATFORM_OVERLAY="--set gateway.ingress.trustedNetworks=$CIDR"

# Every case below is a deployable's descriptor's (deploy/canary/README.md),
# read through canary.py. PYTHON is word-split, so PYTHON="py -3.12" works,
# and the carriage returns a Windows interpreter writes are dropped.
PYTHON="${PYTHON:-python3}"
CASES="$OUT/cases.txt"
$PYTHON "$ROOT/deploy/canary/canary.py" smoke-cases | tr -d '\r' >"$CASES"

field() {
    # field <chart> <field> -> each value that chart's descriptor gives it
    awk -v c="$1" -v f="$2" '$1 == c && $2 == f { print $3 }' "$CASES"
}

charts_where() {
    # charts_where <field> <value> -> the charts whose descriptor says so
    awk -v f="$1" -v v="$2" '$2 == f && $3 == v { print $1 }' "$CASES" | tr '\n' ' ' | sed 's/ *$//'
}

owns() { case " $(field "$1" capability | tr '\n' ' ') " in *" $2 "*) return 0 ;; esac; return 1; }

SERVICE_CHARTS="$(awk '$2 == "release" { print $1 }' "$CASES" | tr '\n' ' ' | sed 's/ *$//')"
MIGRATOR_CHARTS="$(charts_where migrator yes)"
DATABASELESS_CHARTS="$(charts_where migrator no)"

# The required per-chart values a render cannot supply for every chart (§15.4):
# on any other chart each is a setting with the capability off, which the
# library's coherence guard refuses. So none can ride GATEWAY_OVERLAY, which
# every chart receives.
overlay_for() { field "$1" overlay | sed 's/^/--set-string /' | tr '\n' ' '; }

# The umbrella takes each chart's tag and overlay under that chart's name.
PLATFORM_SETS=""
for chart in $SERVICE_CHARTS; do
    PLATFORM_SETS="$PLATFORM_SETS --set-string $chart.image.tag=$TAG"
    for pair in $(field "$chart" overlay); do
        PLATFORM_SETS="$PLATFORM_SETS --set-string $chart.$pair"
    done
    for setting in $NETPOL_OVERLAY; do
        [ "$setting" = --set ] || PLATFORM_SETS="$PLATFORM_SETS --set $chart.$setting"
    done
done

# Every path outside deploy/helm that this script reads, declared once beside
# the reads: the workflow's path filter must cover each of them, or a change to
# one is a green pull request that skips the gate watching it, and the
# agreement is asserted below. Each descriptor's source is one of them.
SOURCE_INPUTS="
src/Gateway/Gateway.Api
src/BFF/Web.Bff
src/BuildingBlocks/Common.Web/HealthCheckExtensions.cs
.gitattributes
deploy/canary
$(awk '$2 == "source" { print $3 }' "$CASES")
"
SOURCE_INPUTS="$(printf '%s\n' $SOURCE_INPUTS | sort -u)"

# The descriptors declare which charts exist and what each is held to; the
# chart directories are reconciled against them before anything is rendered,
# so a chart without a descriptor fails rather than being skipped.
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

# Whether a ConfigMap's data in a render holds a line matching an ERE. The
# pattern travels through the environment, because awk -v rewrites escapes.
in_configmap() {
    # in_configmap <file> <ERE>
    want="$2" awk '/^kind: ConfigMap$/ { in_cm = 1 }
        /^---$/ { in_cm = 0 }
        in_cm && $0 ~ ENVIRON["want"] { found = 1 }
        END { exit found ? 0 : 1 }' "$1"
}
outside_configmap() { ! in_configmap "$@"; }

section() { printf '\n%s\n' "$1"; }

refuses_foreign() {
    # refuses_foreign <chart> <label> <needle> <helm args...> — `refuses`'s
    # shape over a NAMED chart, because these assertions are about a
    # capability reaching a chart that does not own it and so cannot all be
    # made against the gateway. Defined here with the other helpers rather
    # than beside `refuses`, which sits below its first caller.
    local chart="$1" label="$2" needle="$3"
    shift 3
    if "$HELM" template "$chart" "$CHARTS_DIR/$chart" $NETPOL_OVERLAY --set-string "image.tag=$TAG" \
        $GATEWAY_OVERLAY $(overlay_for "$chart") "$@" >"$OUT/foreign-$chart.txt" 2>&1; then
        fail "$label — it rendered instead"
    else
        check "$label" grep -q "$needle" "$OUT/foreign-$chart.txt"
    fi
}

# A capability is a fact about the code, not an environment setting. Each of
# these renders cleanly and produces a pod that will not start, and each has to
# be aimed at a chart that has the capability — `refuses` below renders the
# gateway, which owns no database and no migrator.
refuses_chart() {
    # refuses_chart <chart> <label> <needle> <helm args...>
    local chart="$1" label="$2" needle="$3"
    shift 3
    if "$HELM" template "$chart" "$CHARTS_DIR/$chart" $NETPOL_OVERLAY --set-string "image.tag=$TAG" \
        $(overlay_for "$chart") "$@" >"$OUT/cap.txt" 2>&1; then
        fail "$label — it rendered instead"
    else
        check "$label" grep -q "$needle" "$OUT/cap.txt"
    fi
}

# A member removed outright, which a blank value does not reach: Helm applies
# every --set-string after every --set, so the overlay is passed without the
# key rather than overridden, and the removal is the case's own --set.
refuses_removed() {
    # refuses_removed <chart> <key> <needle>
    local chart="$1" key="$2" needle="$3"
    if "$HELM" template "$chart" "$CHARTS_DIR/$chart" $NETPOL_OVERLAY --set-string "image.tag=$TAG" \
        $(field "$chart" overlay | awk -v k="$key=" 'index($0, k) != 1' | sed 's/^/--set-string /' | tr '\n' ' ') \
        --set "$key=null" >"$OUT/removed.txt" 2>&1; then
        fail "$chart: $key removed outright renders — it must not"
    else
        check "$chart: $key removed outright fails the render" grep -q "$needle" "$OUT/removed.txt"
    fi
}

# --------------------------------------------------------------------------
section 'The gate covers every chart on disk'
# --------------------------------------------------------------------------
# First, because every section below iterates SERVICE_CHARTS: a chart missing
# from that list is not a weaker run, it is an unrun one that reports success.
found="$(discovered_charts | tr '\n' ' ' | sed 's/ *$//')"
listed="$(printf '%s\n' $SERVICE_CHARTS | sort | tr '\n' ' ' | sed 's/ *$//')"
if [ "$found" = "$listed" ]; then
    pass "the descriptors name every deployable chart on disk ($found)"
else
    fail "the descriptors' charts ($listed) do not match the chart directories ($found)"
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

# A worker chart sets a replica count instead of an autoscaler (§15.3), so the
# assertions below branch — and the branch is driven by a declared list rather
# than by each chart's own values. Read from the values alone, a file flipped
# by itself would change what is asserted rather than fail it, which is this
# repository's most-repeated failure pointed at its newest surface.
AUTOSCALED_CHARTS="$(charts_where autoscaled yes)"
FIXED_REPLICA_CHARTS="$(charts_where autoscaled no)"

scaled="$(printf '%s\n' $AUTOSCALED_CHARTS $FIXED_REPLICA_CHARTS | sort | tr '\n' ' ' | sed 's/ *$//')"
if [ "$scaled" = "$listed" ]; then
    pass 'every chart is classified as autoscaled or fixed-replica'
else
    fail "AUTOSCALED_CHARTS + FIXED_REPLICA_CHARTS ($scaled) do not partition SERVICE_CHARTS ($listed)"
fi

# The charts whose host calls out under a grant of its own (ADR-052), named by
# their descriptors rather than counted: a count is satisfied by the wrong
# charts, and which host holds a grant is the whole claim. Read from the values
# files rather than a render, because a chart outside the set setting it fails
# its render and the run would abort before this reported.
CREDENTIALED_CHARTS="$(charts_where capability clientCredentials)"
credentialed="$(grep -l 'clientCredentials: true' "$CHARTS_DIR"/*/values.yaml |
    sed -E 's|.*/([^/]+)/values\.yaml|\1|' | sort | tr '\n' ' ' | sed 's/ *$//')"
want_credentialed="$(printf '%s\n' $CREDENTIALED_CHARTS | sort | tr '\n' ' ' | sed 's/ *$//')"
if [ "$credentialed" = "$want_credentialed" ]; then
    pass "exactly the charts whose host calls out under a grant of its own declare client credentials ($credentialed)"
else
    fail "charts declaring client credentials ($credentialed) are not ($want_credentialed)"
fi

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
# The credential handler's attachment, in the same form and for the same
# reason, its type named bare or through its namespace: a host's Program.cs
# names the handler in a comment too.
ATTACHES_CREDENTIALS='^[[:space:]]*[A-Za-z_][A-Za-z0-9_.]*\.AddHttpMessageHandler<([A-Za-z_][A-Za-z0-9_]*\.)*ClientCredentialsHandler>\('

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
disowns() { ! owns "$1" "$2"; }

src_of() { echo "$ROOT/$(field "$1" source)"; }

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

# A descriptor declares client credentials exactly when its host attaches the
# handler that presents them, read from src/ in both directions (ADR-052): a
# chart whose host stopped presenting a grant would still mount its Secret.
for chart in $SERVICE_CHARTS; do
    if grep -rqE "$ATTACHES_CREDENTIALS" "$(src_of "$chart")"; then
        check "$chart attaches ClientCredentialsHandler in src/, so its descriptor declares clientCredentials" \
            owns "$chart" clientCredentials
    else
        check "$chart attaches no ClientCredentialsHandler in src/, so its descriptor declares none" \
            disowns "$chart" clientCredentials
    fi
done

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
check 'ATTACHES_CREDENTIALS refuses a comment that names the handler' \
    sh -c 'printf "// the token client carries no ClientCredentialsHandler:\n" |
        grep -qvE "$0"' "$ATTACHES_CREDENTIALS"
check 'ATTACHES_CREDENTIALS refuses a registration that attaches nothing' \
    sh -c 'printf "builder.Services.AddTransient<ClientCredentialsHandler>();\n" |
        grep -qvE "$0"' "$ATTACHES_CREDENTIALS"
check 'ATTACHES_CREDENTIALS accepts the real attachment' \
    sh -c 'printf "        client.AddHttpMessageHandler<ClientCredentialsHandler>();\n" |
        grep -qE "$0"' "$ATTACHES_CREDENTIALS"
check 'ATTACHES_CREDENTIALS accepts the attachment through a qualified type name' \
    sh -c 'printf "        client.AddHttpMessageHandler<Common.Infrastructure.Identity.ClientCredentialsHandler>();\n" |
        grep -qE "$0"' "$ATTACHES_CREDENTIALS"
check 'ATTACHES_CREDENTIALS refuses a handler whose name only ends in the word' \
    sh -c 'printf "        client.AddHttpMessageHandler<NoClientCredentialsHandler>();\n" |
        grep -qvE "$0"' "$ATTACHES_CREDENTIALS"

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
    check "$chart lints" "$HELM" lint "$CHARTS_DIR/$chart" $NETPOL_OVERLAY --set-string "image.tag=$TAG" \
        $PLATFORM_SETS $GATEWAY_OVERLAY $(overlay_for "$chart") $PLATFORM_OVERLAY
done

# --------------------------------------------------------------------------
section 'A deploy that cannot name its tag fails (§15.3)'
# --------------------------------------------------------------------------
# values.yaml leaves image.tag empty on purpose. Left to default it, the render
# would emit `image: registry/api:` and the kubelet resolves that to :latest —
# the one tag §15.3 forbids by name. This is the assertion that the empty
# default is a refusal rather than a hole.
for chart in $SERVICE_CHARTS; do
    if "$HELM" template "$chart" "$CHARTS_DIR/$chart" $NETPOL_OVERLAY $GATEWAY_OVERLAY $(overlay_for "$chart") \
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
    "$HELM" template "$chart" "$CHARTS_DIR/$chart" $NETPOL_OVERLAY --set-string "image.tag=$TAG" \
        $GATEWAY_OVERLAY $(overlay_for "$chart") >"$OUT/$chart.yaml"
    pass "$chart renders"
done
"$HELM" template platform "$CHARTS_DIR/platform" $PLATFORM_SETS $PLATFORM_OVERLAY >"$OUT/platform.yaml"
pass 'platform renders'

# --------------------------------------------------------------------------
section 'Every workload is fenced by a default-deny NetworkPolicy (ADR-065)'
# --------------------------------------------------------------------------
# The documents of one kind alone, Helm hooks or the rest, so a CIDR or a port
# elsewhere in the render cannot satisfy a case below.
docs_of() {
    # docs_of <render> <kind> <hook|plain> -> those documents, each closed by ---
    kind="$2" want="$3" awk 'function flush() {
            if (k && (h ? "hook" : "plain") == ENVIRON["want"]) printf "%s---\n", doc
            doc = ""; k = h = 0
        }
        /^---$/ { flush(); next }
        { doc = doc $0 "\n" }
        $0 == "kind: " ENVIRON["kind"] { k = 1 }
        /^    "helm\.sh\/hook":/ { h = 1 }
        END { flush() }' "$1"
}
# The workload's policy, which ADR-065 owns; the migrator's is a hook.
policy_of() { docs_of "$1" NetworkPolicy plain; }
workload_of() {
    awk '/^workload:/ { w = 1; next } /^[a-z]/ { w = 0 } w && /^  name:/ { print $2; exit }' \
        "$CHARTS_DIR/$1/values.yaml"
}
cidr_of() { printf '%s\n' $NETPOL_PEERS | sed -n "s/^$1=//p"; }

for chart in $SERVICE_CHARTS; do
    policy_of "$OUT/$chart.yaml" >"$OUT/$chart.policy.yaml"
    check "$chart renders exactly one workload NetworkPolicy" \
        test "$(count '^kind: NetworkPolicy$' "$OUT/$chart.policy.yaml")" -eq 1
    check "$chart's policy isolates its pods in both directions" \
        grep -qE '^  policyTypes: \[Ingress, Egress\]$' "$OUT/$chart.policy.yaml"
    check "$chart's policy selects its own workload's stable pods" \
        sh -c 'grep -q "^      app.kubernetes.io/name: $1$" "$2" &&
               grep -q "^      app.kubernetes.io/track: stable$" "$2"' _ "$(workload_of "$chart")" "$OUT/$chart.policy.yaml"
    check "$chart's policy lets its pods resolve names" \
        grep -q 'port: 53$' "$OUT/$chart.policy.yaml"
    # Every host validates tokens against the identity provider (§11.2), so
    # every one reaches it, whether or not it holds a grant of its own.
    check "$chart's policy reaches the identity provider" \
        grep -q "cidr: $(cidr_of identity)$" "$OUT/$chart.policy.yaml"
    # Egress follows the values: a destination's peers appear exactly when the
    # chart declares the capability that dials it.
    for cap in database redis broker carrier mail paymentProvider; do
        if declares "$chart" "$cap"; then
            check "$chart declares $cap, so its policy reaches $cap's peers" \
                grep -q "cidr: $(cidr_of "$cap")$" "$OUT/$chart.policy.yaml"
        else
            check "$chart declares no $cap, so its policy has no egress to $cap's peers" \
                test "$(count "cidr: $(cidr_of "$cap")$" "$OUT/$chart.policy.yaml")" -eq 0
        fi
    done
    # A worker has no Service, so nothing may open a connection to it; the
    # kubelet's probes come from the node, which a policy never blocks.
    if grep -qE '^  enabled: false$' <(awk '/^service:/ { s = 1; next } /^[a-z]/ { s = 0 } s' "$CHARTS_DIR/$chart/values.yaml"); then
        check "$chart has no Service, so its policy admits no ingress at all" \
            grep -qE '^  ingress: \[\]$' "$OUT/$chart.policy.yaml"
    fi
done

check 'only the gateway admits the ingress controller' \
    test "$(cat "$OUT"/*.policy.yaml | count "cidr: $INGRESS_CONTROLLER_CIDR$" /dev/stdin)" -eq 1
check 'the gateway admits the ingress controller' \
    grep -q "cidr: $INGRESS_CONTROLLER_CIDR$" "$OUT/gateway.policy.yaml"

# The route file is the gateway's list of upstreams (§10.2), so its policy is
# held to it rather than to a second list: each cluster address must be an
# egress peer, or the route answers 502 wherever the policy is enforced.
upstreams="$(grep -oE '"Address": "http://[a-z-]+:' "$ROOT/src/Gateway/Gateway.Api/appsettings.json" |
    sed -E 's|.*//([a-z-]+):|\1|' | sort -u)"
check 'the gateway route file names its upstreams' test -n "$upstreams"
for upstream in $upstreams; do
    check "the gateway's policy reaches its upstream $upstream" \
        grep -q "app.kubernetes.io/name: $upstream$" "$OUT/gateway.policy.yaml"
done

# An edge inside the namespace is two rules, one at each end, and a policy is
# enforced at both: each egress to a workload must meet that workload's ingress
# from this one on the same port, or the call is refused wherever it is enforced.
edges_of() {
    # edges_of <policy> -> "workload port" per in-namespace egress peer
    awk '/^  egress:/ { e = 1 } e && /app.kubernetes.io\/name: / { w = $2 }
         e && w && /- port: / { print w, $3; w = "" }' "$1"
}
admits() {
    # admits <policy> <workload> <port> -> exit 0 when an ingress rule admits it
    awk -v w="$2" -v p="$3" '/^  ingress:/ { i = 1 } /^  egress:/ { i = 0 }
         i && /- from:/ { want = 0 } i && $0 ~ ("app.kubernetes.io/name: " w "$") { want = 1 }
         i && want && $0 ~ ("- port: " p "$") { found = 1 }
         END { exit found ? 0 : 1 }' "$1"
}
chart_of() { for c in $SERVICE_CHARTS; do [ "$(workload_of "$c")" = "$1" ] && echo "$c"; done; }
edges=0
for chart in $SERVICE_CHARTS; do
    while read -r peer port; do
        [ -n "$peer" ] || continue
        edges=$((edges + 1))
        check "$chart's egress to $peer on $port meets $peer's ingress from $(workload_of "$chart")" \
            admits "$OUT/$(chart_of "$peer").policy.yaml" "$(workload_of "$chart")" "$port"
    done < <(edges_of "$OUT/$chart.policy.yaml")
done
check 'the in-namespace edges were read from the renders' test "$edges" -gt 0

# The canary is fenced exactly as the stable track is, or the rollout judges a
# differently fenced workload (ADR-022): its own object, its own pods, the
# same rules.
for chart in $SERVICE_CHARTS; do
    "$HELM" template "$chart" "$CHARTS_DIR/$chart" $NETPOL_OVERLAY --set-string "image.tag=$TAG" \
        $GATEWAY_OVERLAY $(overlay_for "$chart") --set canary.enabled=true \
        --set autoscaling.enabled=false >"$OUT/$chart.canary.yaml"
    policy_of "$OUT/$chart.canary.yaml" >"$OUT/$chart.canary-policy.yaml"
    check "$chart's canary release renders its own NetworkPolicy" \
        grep -q "^  name: $(workload_of "$chart")-canary$" "$OUT/$chart.canary-policy.yaml"
    check "$chart's canary policy selects the canary track" \
        grep -q '^      app.kubernetes.io/track: canary$' "$OUT/$chart.canary-policy.yaml"
    check "$chart's canary policy carries the stable policy's rules" \
        cmp -s <(sed -n '/^  policyTypes:/,$p' "$OUT/$chart.policy.yaml") \
               <(sed -n '/^  policyTypes:/,$p' "$OUT/$chart.canary-policy.yaml")
done

# The migration Job's pods carry labels of their own and run as a hook before
# any other object exists (§7.4), so the subject is the Job's pod template:
# some policy in the render must select it, and that policy must be a hook
# weighted ahead of the Job, or the migrator runs unfenced (ADR-069).
job_pod_labels() {
    awk '/^kind: Job$/ { j = 1 } j && /^  template:/ { t = 1 } t && /^      labels:/ { l = 1; next }
         l && /^ *$/ { next } l && /^        [^ ]/ { sub(/^ +/, ""); print; next } l { exit }' "$1"
}
selected_by_a_policy() {
    # selected_by_a_policy <render> <labels> -> exit 0 when a policy's matchLabels all hold in <labels>
    awk 'function judge() { if (np && n > 0 && !miss) found = 1; np = ps = n = miss = 0 }
         NR == FNR { have[$0] = 1; next }
         /^---$/ { judge(); next }
         /^kind: NetworkPolicy$/ { np = 1 }
         np && /^  podSelector:/ { ps = 1; next }
         ps && /^      [^ ]/ { l = $0; sub(/^ +/, "", l); n++; if (!(l in have)) miss = 1; next }
         ps && !/^    matchLabels:/ { ps = 0 }
         END { judge(); exit found ? 0 : 1 }' "$2" "$1"
}
weight_of() { sed -n 's/^    "helm.sh\/hook-weight": "\(-*[0-9]*\)"$/\1/p' "$1" | head -n 1; }
for chart in $MIGRATOR_CHARTS; do
    for track in "" .canary; do
        render="$OUT/$chart$track.yaml"
        job_pod_labels "$render" >"$OUT/$chart$track.job-labels.txt"
        docs_of "$render" NetworkPolicy hook >"$OUT/$chart$track.migrate-policy.yaml"
        docs_of "$render" Job hook >"$OUT/$chart$track.job.yaml"
        check "$chart${track:+ (canary)}: the migration Job's pod labels were read" \
            test -s "$OUT/$chart$track.job-labels.txt"
        check "$chart${track:+ (canary)}: a NetworkPolicy selects the migration Job's pods" \
            selected_by_a_policy "$OUT/$chart$track.migrate-policy.yaml" "$OUT/$chart$track.job-labels.txt"
    done
    policy="$OUT/$chart.migrate-policy.yaml"
    check "$chart: the migrator's policy runs on the Job's hook events" \
        grep -q '^    "helm.sh/hook": pre-install,pre-upgrade$' "$policy"
    check "$chart: the migrator's policy is weighted ahead of the Job" \
        test "$(weight_of "$policy")" -lt "$(weight_of "$OUT/$chart.job.yaml")"
    # Helm may delete a succeeded hook while the Job it fences still has a pod
    # running, as a Job past Helm's timeout does.
    check "$chart: the migrator's policy outlives the Job's pods" \
        grep -q '^    "helm.sh/hook-delete-policy": before-hook-creation$' "$policy"
    check "$chart: the migrator's policy admits nothing in" grep -qE '^  ingress: \[\]$' "$policy"
    check "$chart: the migrator's policy reaches its database" \
        grep -q "cidr: $(cidr_of database)$" "$policy"
    check "$chart: and no other address outside the namespace" test "$(count 'cidr:' "$policy")" -eq 1
    check "$chart: and no egress rule but DNS and its database" \
        test "$(count '^    - to:$' "$policy")" -eq 2
done

# A capability that is on with no peer stated is refused: a rule with no peer
# admits every destination on its port, which is the opposite of a fence.
refuses_chart notifications 'notifications: mail with no relay peer stated fails the render' \
    'networkPolicy.mail.to is required' --set networkPolicy.mail.to=null
refuses_chart ordering 'ordering: a database with no peer stated fails the render' \
    'networkPolicy.database.to is required' --set networkPolicy.database.to=null
refuses_chart ordering 'ordering: a broker peer of every address fails the render' \
    'admits every address' --set 'networkPolicy.broker.to[0].ipBlock.cidr=0.0.0.0/0'
refuses_chart gateway 'gateway: an Ingress with no controller peer stated fails the render' \
    'networkPolicy.ingressController.from is required' --set networkPolicy.ingressController.from=null

# A policy's port is matched at the destination pod, after a Service has
# translated it, so the port an address dials is right only where no Service
# maps it: a peer's stated port wins, and the address's is the fallback.
egress_ports() {
    # egress_ports <policy> <ERE> -> the ports of every egress rule whose peer matches
    want="$2" awk '/^  egress:/ { e = 1 } e && /^    - to:/ { hit = 0 }
        e && $0 ~ ENVIRON["want"] { hit = 1 }
        e && hit && /- port: / { print $3 }' "$1" | tr '\n' ' ' | sed 's/ *$//'
}
peer_pattern() {
    case "$1" in
        telemetry) echo 'kubernetes.io/metadata.name: observability$' ;;
        *) echo "cidr: $(cidr_of "$1")$" ;;
    esac
}
render_policy() {
    # render_policy <chart> <file> <helm args...> -> that render's NetworkPolicy in <file>
    local chart="$1" file="$2"
    shift 2
    "$HELM" template "$chart" "$CHARTS_DIR/$chart" $NETPOL_OVERLAY --set-string "image.tag=$TAG" \
        $(overlay_for "$chart") "$@" >"$OUT/port.yaml"
    policy_of "$OUT/port.yaml" >"$file"
}
while read -r chart peer derived; do
    render_policy "$chart" "$OUT/port-derived.yaml"
    check "$chart reaches $peer on the port its address dials when no port is stated" \
        test "$(egress_ports "$OUT/port-derived.yaml" "$(peer_pattern "$peer")")" = "$derived"
    render_policy "$chart" "$OUT/port-stated.yaml" --set "networkPolicy.$peer.port=18443"
    check "$chart reaches $peer on the pod port networkPolicy.$peer.port states" \
        test "$(egress_ports "$OUT/port-stated.yaml" "$(peer_pattern "$peer")")" = 18443
done <<'EOF'
ordering telemetry 4317
ordering identity 443
notifications identity 443
notifications mail 587
payments paymentProvider 443
shipping carrier 443
EOF
# The admin API is the identity provider's too, so its stated port covers both.
render_policy notifications "$OUT/port-admin.yaml" --set-string 'contactSource.baseUrl=https://id.example.invalid:8443/'
check 'notifications reaches the admin API on its own address port when no port is stated' \
    test "$(egress_ports "$OUT/port-admin.yaml" "$(peer_pattern identity)")" = '443 8443'
render_policy notifications "$OUT/port-admin.yaml" --set-string 'contactSource.baseUrl=https://id.example.invalid:8443/' \
    --set networkPolicy.identity.port=18443
check 'notifications reaches the identity provider and its admin API on the one stated pod port' \
    test "$(egress_ports "$OUT/port-admin.yaml" "$(peer_pattern identity)")" = 18443

# --------------------------------------------------------------------------
section 'paymentProvider is a capability, and its address is required'
# --------------------------------------------------------------------------
# Both keys are read eagerly by AddPaymentProvider, so every state below that
# renders cleanly is a pod that will not start. Asserted in both directions:
# supplied, the two keys land in the right Kind; absent or contradicted, the
# render is refused rather than deferred to the cluster.
PAYMENTS_RENDER=$("$HELM" template payments "$CHARTS_DIR/payments" $NETPOL_OVERLAY \
    --set-string image.tag="$TAG" \
    --set-string paymentProvider.baseUrl=https://psp.example.invalid/)
printf '%s\n' "$PAYMENTS_RENDER" >"$OUT/payments-capability.yaml"
# Both keys are asserted by PLACEMENT and not by presence, because §15.4 puts
# them in different Kinds and a global grep proves neither: the address would
# satisfy one that moved it into the pod environment, and the credential would
# satisfy one that rendered it as a literal. A gate watching only the name
# stops covering the thing it was added for the moment the value moves.
check 'payments: PaymentProvider__BaseUrl is in the ConfigMap' \
    awk '/^kind: ConfigMap$/ { in_cm = 1 }
         /^---$/ { in_cm = 0 }
         in_cm && /^ *PaymentProvider__BaseUrl: "https:\/\/psp\.example\.invalid\/"$/ { found = 1 }
         END { exit found ? 0 : 1 }' "$OUT/payments-capability.yaml"
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
if "$HELM" template payments "$CHARTS_DIR/payments" $NETPOL_OVERLAY --set-string image.tag="$TAG" >/dev/null 2>&1; then
    fail 'payments: rendered with no paymentProvider.baseUrl; a deploy that forgot it must fail here, not at start'
fi
if "$HELM" template payments "$CHARTS_DIR/payments" $NETPOL_OVERLAY --set-string image.tag="$TAG" \
    --set paymentProvider.enabled=false --set paymentProvider.apiKeySecretRef=null \
    --set-string paymentProvider.baseUrl=https://psp.example.invalid/ >/dev/null 2>&1; then
    fail 'payments: rendered an address with the capability off; a setting nothing reads must be refused'
fi
if "$HELM" template payments "$CHARTS_DIR/payments" $NETPOL_OVERLAY --set-string image.tag="$TAG" \
    --set paymentProvider.enabled=false --set paymentProvider.apiKeySecretRef=null \
    --set-string paymentProvider.baseUrl= >/dev/null 2>&1; then
    fail 'payments: rendered with the capability off and cleared; the host registers it unconditionally'
fi
pass 'paymentProvider renders both keys and refuses an empty address or an address while off'

# --------------------------------------------------------------------------
section "Shipping's chart declares its capabilities, and each is required"
# --------------------------------------------------------------------------
# Shipping's host reads every key below before it will start (§15.4), so each
# state that renders cleanly here is a pod that never starts. Asserted by
# placement and not by presence: §15.4 puts the credential in a Secret and the
# addresses, the windows and the give-up age in Config, and a global grep
# proves neither.
SHIPPING_RENDER=$("$HELM" template shipping "$CHARTS_DIR/shipping" $NETPOL_OVERLAY \
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
check 'shipping: the give-up age is in the ConfigMap' \
    awk '/^kind: ConfigMap$/ { in_cm = 1 }
         /^---$/ { in_cm = 0 }
         in_cm && /^ *Fulfilment__GiveUpAge: "3\.00:00:00"$/ { found = 1 }
         END { exit found ? 0 : 1 }' "$OUT/shipping-capability.yaml"
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
refuses_chart shipping 'an address source that is not an address fails the render' \
    'not an address this chart will accept' --set-string 'addressSource.baseUrl=ordering-api:8081'
refuses_chart shipping 'an address source carrying credentials fails the render' \
    'not an address this chart will accept' \
    --set-string 'addressSource.baseUrl=http://u:p@ordering-api:8081'
refuses_chart shipping 'an address source with a wildcard host fails the render' \
    'not an address this chart will accept' --set-string 'addressSource.baseUrl=http://*.ordering:8081'
refuses_chart shipping 'an address source on a port past 65535 fails the render' \
    'outside 1-65535' --set-string 'addressSource.baseUrl=http://ordering-api:80810'
refuses_chart shipping 'an address source with the capability off fails the render' \
    'but addressSource.baseUrl is set' --set addressSource.enabled=false
refuses_chart shipping 'the address source off and cleared fails the render' \
    'addressSource.enabled is false on the shipping chart' \
    --set addressSource.enabled=false --set-string 'addressSource.baseUrl='
refuses_chart shipping 'a cleared retention window fails the render' \
    'jurisdiction.addressRetention is required' \
    --set-string 'jurisdiction.addressRetention='
refuses_chart shipping 'a retention window that is not a TimeSpan fails the render' \
    'not a TimeSpan this chart will accept' \
    --set-string 'jurisdiction.trackingRetention=90 days'
refuses_chart shipping 'a retention window counted in hours past 23 fails the render' \
    'not a TimeSpan this chart will accept' \
    --set-string 'jurisdiction.addressRetention=72:00:00'
refuses_chart shipping 'a jurisdiction the capability is off for fails the render' \
    'but a jurisdiction setting is set' --set jurisdiction.enabled=false
refuses_chart shipping 'the jurisdiction off and cleared fails the render' \
    'jurisdiction.enabled is false on the shipping chart' \
    --set jurisdiction.enabled=false --set-string 'jurisdiction.addressRetention=' \
    --set-string 'jurisdiction.trackingRetention='
refuses_removed shipping jurisdiction.trackingRetention \
    'jurisdiction.trackingRetention is required on the shipping chart'
refuses_chart shipping 'client credentials off with a client id fails the render' \
    'identity.clientCredentials is false but identity.clientId is set' \
    --set identity.clientCredentials=false
refuses_chart shipping 'client credentials off and cleared fails the render' \
    'identity.clientCredentials is false on the shipping chart' \
    --set identity.clientCredentials=false --set-string 'identity.clientId='
refuses_chart shipping 'a cleared give-up age fails the render' \
    'fulfilment.giveUpAge is required' --set-string 'fulfilment.giveUpAge='
refuses_chart shipping 'a give-up age that is not a TimeSpan fails the render' \
    'not a TimeSpan this chart will accept' --set-string 'fulfilment.giveUpAge=3 days'
refuses_chart shipping 'a give-up age with the capability off fails the render' \
    'but fulfilment.giveUpAge is set' --set fulfilment.enabled=false
refuses_chart shipping 'the give-up age off and cleared fails the render' \
    'fulfilment.enabled is false on the shipping chart' \
    --set fulfilment.enabled=false --set-string 'fulfilment.giveUpAge='
check 'a retention window in days-and-time form still renders' \
    "$HELM" template shipping "$CHARTS_DIR/shipping" $NETPOL_OVERLAY --set-string "image.tag=$TAG" \
    $(overlay_for shipping) --set-string 'jurisdiction.addressRetention=1.12:30'

# --------------------------------------------------------------------------
section "Notifications' chart declares its capabilities, and each is required"
# --------------------------------------------------------------------------
# The worker reads every key below before it will start (§15.4), so each state
# that renders cleanly here is a pod that never starts. Asserted by placement:
# §15.4 puts the relay's password in a Secret and every other key in Config.
N="$OUT/notifications-capability.yaml"
"$HELM" template notifications "$CHARTS_DIR/notifications" $NETPOL_OVERLAY \
    --set-string image.tag="$TAG" $(overlay_for notifications) >"$N"

check 'notifications: the relay host is in the ConfigMap' in_configmap "$N" '^ *Mail__Host: "relay[.]example[.]invalid"$'
check 'notifications: the submission port is in the ConfigMap' in_configmap "$N" '^ *Mail__Port: "587"$'
check 'notifications: the sender is in the ConfigMap' \
    in_configmap "$N" '^ *Mail__From: "no-reply@commerce[.]example[.]invalid"$'
check 'notifications: StartTls is in the ConfigMap' in_configmap "$N" '^ *Mail__Security: "StartTls"$'
check 'notifications: the relay user is in the ConfigMap' in_configmap "$N" '^ *Mail__UserName: "notifications"$'
check 'notifications: Mail__Password comes from a secretKeyRef, not a literal' \
    awk '/^ *- name: Mail__Password$/ { at = NR }
         at && NR == at + 1 && /^ *valueFrom:$/ { vf = 1 }
         vf && NR == at + 2 && /^ *secretKeyRef:$/ { found = 1 }
         END { exit found ? 0 : 1 }' "$N"
check 'notifications: no ConfigMap carries Mail__Password' outside_configmap "$N" 'Mail__Password'
check 'notifications: the contact source is in the ConfigMap' \
    in_configmap "$N" '^ *ContactSource__BaseUrl: "https://id[.]example[.]invalid/"$'
check 'notifications: its realm is in the ConfigMap' in_configmap "$N" '^ *ContactSource__Realm: "commerce"$'
check 'notifications: the language set is in the ConfigMap' in_configmap "$N" '^ *Jurisdiction__Languages__0: "en"$'
check 'notifications: the zone is in the ConfigMap' in_configmap "$N" '^ *Jurisdiction__TimeZone: "Europe/London"$'
check 'notifications: its three windows are in the ConfigMap' \
    awk '/^kind: ConfigMap$/ { in_cm = 1 }
         /^---$/ { in_cm = 0 }
         in_cm && /^ *Jurisdiction__[A-Za-z]+Retention: / { n++ }
         END { exit n == 3 ? 0 : 1 }' "$N"
check 'notifications: the give-up age is in the ConfigMap' in_configmap "$N" '^ *Delivery__GiveUpAge: "1[.]00:00:00"$'
check "notifications: the token is asked for under ADR-052's roles scope" \
    in_configmap "$N" '^ *Identity__Client__Scope: "roles"$'
check 'notifications: three languages render three indexed keys, in order' \
    sh -c '"$0" template notifications "$1" --set-string "image.tag=$2" $3 \
        --set-string "jurisdiction.languages={en,kk,ru}" | grep -q "Jurisdiction__Languages__2: \"ru\""' \
    "$HELM" "$CHARTS_DIR/notifications" "$TAG" "$(overlay_for notifications) $NETPOL_OVERLAY"
check 'notifications: a sender with a display name renders' \
    "$HELM" template notifications "$CHARTS_DIR/notifications" $NETPOL_OVERLAY --set-string "image.tag=$TAG" \
    $(overlay_for notifications) --set-string 'mail.from=Commerce <no-reply@commerce.example.invalid>'
check 'notifications: a quoted display name holding a comma renders' \
    "$HELM" template notifications "$CHARTS_DIR/notifications" $NETPOL_OVERLAY --set-string "image.tag=$TAG" \
    $(overlay_for notifications) --set-string 'mail.from="Commerce\, Inc." <no-reply@commerce.example.invalid>'
check 'notifications: a display name with an escaped quote renders' \
    "$HELM" template notifications "$CHARTS_DIR/notifications" $NETPOL_OVERLAY --set-string "image.tag=$TAG" \
    $(overlay_for notifications) --set-string 'mail.from="Commerce \\"Shop\\"" <no-reply@commerce.example.invalid>'
check 'notifications: a display name quoted after a word renders' \
    "$HELM" template notifications "$CHARTS_DIR/notifications" $NETPOL_OVERLAY --set-string "image.tag=$TAG" \
    $(overlay_for notifications) --set-string 'mail.from=Commerce "Shop\, Ltd" <no-reply@commerce.example.invalid>'

refuses_chart notifications 'a cleared relay host fails the render' \
    'mail.host is required' --set-string 'mail.host='
refuses_chart notifications 'a relay host written as an address fails the render' \
    'not a host name this chart will accept' --set-string 'mail.host=smtp://relay.example.invalid'
refuses_chart notifications 'a relay port past 65535 fails the render' \
    'which is not a port' --set mail.port=65536
refuses_chart notifications 'a relay port that is a word fails the render' \
    'which is not a port' --set-string mail.port=smtp
refuses_chart notifications 'a cleared sender fails the render' \
    'mail.from is required' --set-string 'mail.from='
refuses_chart notifications 'two senders fail the render' \
    'not one mailbox this chart will accept' --set-string 'mail.from=a@x.invalid;b@y.invalid'
refuses_chart notifications 'a sender with an unbalanced quote fails the render' \
    'not one mailbox this chart will accept' --set-string 'mail.from="Commerce <no-reply@commerce.example.invalid>'
refuses_chart notifications 'plain submission fails the render' \
    'this chart accepts StartTls alone' --set-string mail.security=None
refuses_chart notifications 'anonymous submission fails the render' \
    'mail.userName is required' --set-string 'mail.userName='
refuses_chart notifications 'a relay password with no Secret named fails the render' \
    'mail.passwordSecretRef.name is required' --set mail.passwordSecretRef=null
refuses_chart notifications 'a relay setting with the capability off fails the render' \
    'but a mail setting is set' --set mail.enabled=false
refuses_chart notifications 'the relay off and cleared fails the render' \
    'mail.enabled is false on the notifications chart' \
    --set mail.enabled=false --set mail.port=null --set mail.security=null \
    --set mail.passwordSecretRef=null --set-string 'mail.host=' \
    --set-string 'mail.from=' --set-string 'mail.userName='
refuses_chart notifications 'a cleared contact source fails the render' \
    'contactSource.baseUrl is required' --set-string 'contactSource.baseUrl='
refuses_chart notifications 'a plain-HTTP contact source fails the render' \
    'HTTPS address this chart will accept' --set-string 'contactSource.baseUrl=http://id.example.invalid/'
refuses_chart notifications 'a realm that is not one path segment fails the render' \
    'not a realm name this chart will accept' --set-string 'contactSource.realm=commerce/x'
refuses_chart notifications 'a realm the authority does not name fails the render' \
    'so the two must agree' --set-string 'contactSource.realm=master'
refuses_chart notifications 'a contact source with the capability off fails the render' \
    'but a contactSource setting is set' --set contactSource.enabled=false
refuses_chart notifications 'the contact source off and cleared fails the render' \
    'contactSource.enabled is false on the notifications chart' \
    --set contactSource.enabled=false --set contactSource.realm=null \
    --set-string 'contactSource.baseUrl='
refuses_chart notifications 'an empty language set fails the render' \
    'must hold at least one language' --set-string 'jurisdiction.languages='
refuses_chart notifications 'a language that is not a tag fails the render' \
    'not a language tag this chart will accept' --set-string 'jurisdiction.languages={EN_gb}'
refuses_chart notifications 'a cleared zone fails the render' \
    'jurisdiction.timeZone is required' --set-string 'jurisdiction.timeZone='
refuses_chart notifications 'a zone that is not an IANA id fails the render' \
    'not an IANA zone id this chart will accept' --set-string 'jurisdiction.timeZone=Europe/../London'
refuses_chart notifications 'a cleared window fails the render' \
    'jurisdiction.orderRetention is required' --set-string 'jurisdiction.orderRetention='
refuses_chart notifications 'a window counted in hours past 23 fails the render' \
    'not a TimeSpan this chart will accept' --set-string 'jurisdiction.logRetention=72:00:00'
refuses_chart notifications 'a jurisdiction the capability is off for fails the render' \
    'but a jurisdiction setting is set' --set jurisdiction.enabled=false
refuses_chart notifications 'the jurisdiction off and cleared fails the render' \
    'jurisdiction.enabled is false on the notifications chart' \
    --set jurisdiction.enabled=false --set-string 'jurisdiction.languages=' \
    --set-string 'jurisdiction.timeZone=' --set-string 'jurisdiction.logRetention=' \
    --set-string 'jurisdiction.contactRetention=' --set-string 'jurisdiction.orderRetention='
refuses_removed notifications jurisdiction.languages \
    'jurisdiction.languages is required on the notifications chart'
refuses_removed notifications jurisdiction.orderRetention \
    'jurisdiction.orderRetention is required on the notifications chart'
refuses_chart notifications 'a cleared give-up age fails the render' \
    'delivery.giveUpAge is required' --set-string 'delivery.giveUpAge='
refuses_chart notifications 'a give-up age that is not a TimeSpan fails the render' \
    'not a TimeSpan this chart will accept' --set-string 'delivery.giveUpAge=1 day'
refuses_chart notifications 'a give-up age with the capability off fails the render' \
    'but delivery.giveUpAge is set' --set delivery.enabled=false
refuses_chart notifications 'the give-up age off and cleared fails the render' \
    'delivery.enabled is false on the notifications chart' \
    --set delivery.enabled=false --set-string 'delivery.giveUpAge='
refuses_chart notifications 'client credentials off with a client id fails the render' \
    'identity.clientCredentials is false but identity.clientId is set' \
    --set identity.clientCredentials=false
refuses_chart notifications 'client credentials off and cleared fails the render' \
    'identity.clientCredentials is false on the notifications chart' \
    --set identity.clientCredentials=false --set-string 'identity.clientId='

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

# --------------------------------------------------------------------------
section 'Replicas spread across nodes and zones (§15.3)'
# --------------------------------------------------------------------------
# A disruption budget covers voluntary evictions only, so without a spread
# three replicas on one node is a legal schedule that one node's loss empties.
spread_when() {
    # spread_when <render> <topologyKey> -> the whenUnsatisfiable that key carries
    awk -v k="$2" '$0 ~ "topologyKey: " k "$" { f = 1; next }
        f && /whenUnsatisfiable:/ { print $2; exit }' "$1"
}

for chart in $SERVICE_CHARTS; do
    check "$chart spreads across nodes as a rule" \
        test "$(spread_when "$OUT/$chart.yaml" kubernetes.io/hostname)" = DoNotSchedule
    check "$chart spreads across zones as a preference" \
        test "$(spread_when "$OUT/$chart.yaml" topology.kubernetes.io/zone)" = ScheduleAnyway
    check "$chart counts each revision's pods apart" \
        test "$(count 'matchLabelKeys: \[pod-template-hash\]' "$OUT/$chart.yaml")" -eq 2
    # A tainted node counted as an empty domain would hold every other node to one.
    check "$chart counts no node its pods cannot run on" \
        test "$(count 'nodeTaintsPolicy: Honor' "$OUT/$chart.yaml")" -eq 2
done
check 'every Deployment in the umbrella carries a spread' \
    test "$(count '^ *topologySpreadConstraints:$' "$OUT/platform.yaml")" \
    -eq "$(count '^kind: Deployment$' "$OUT/platform.yaml")"

refuses_chart catalog 'a spread policy the API server does not know fails the render' \
    'topologySpread.whenUnsatisfiable.node is "Never"' \
    --set-string 'topologySpread.whenUnsatisfiable.node=Never'
refuses_chart catalog 'a spread with no skew fails the render' 'topologySpread.maxSkew is required' \
    --set 'topologySpread.maxSkew=0'

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
        grep -q '"helm.sh/hook": pre-install,pre-upgrade' "$OUT/$chart.job.yaml"
    check "$chart weights the Job ahead of every hook but its fence" \
        grep -q '"helm.sh/hook-weight": "-5"' "$OUT/$chart.job.yaml"
    # BOTH policies. `before-hook-creation` matches on NAME and the name embeds
    # the tag, so on its own every new SHA leaves its completed Job behind for
    # ever — and §13.6's runbook then looks for the failed one in a list of
    # every migration that ever succeeded. `hook-failed` is deliberately absent:
    # the failed Job is the artefact that runbook needs.
    check "$chart deletes the previous hook rather than accumulating them" \
        grep -q '"helm.sh/hook-delete-policy": before-hook-creation,hook-succeeded' "$OUT/$chart.job.yaml"
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

# The gateway owns no database (§10.1, §4.2), so the hook has nothing to run
# for it. The output assertion alone is vacuous — that chart carries no
# migration template at all (§15.3) — so the subject is the agreement between
# the two halves: a chart has a migration template exactly when its values
# name a migrator image, and it fires broken from either side.
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

# --------------------------------------------------------------------------
section 'No render opens the seed gate (§14.3)'
# --------------------------------------------------------------------------
# Both halves of the gate a seeding migrator reads, over every default render:
# the flag, in the spelling an env entry or a ConfigMap key would carry, and an
# environment name, without which a migrator runs as Production. Each pattern
# is first shown the line it must find, or a misspelt one passes every render.
SEED_KEY='Seed(__|:)Enabled'
ENVIRONMENT_KEY='(DOTNET|ASPNETCORE)_ENVIRONMENT'

absent() { ! grep -qE "$1" "$2"; }

check 'SEED_KEY finds a container env entry' \
    sh -c 'printf "            - name: Seed__Enabled\n" | grep -qE "$1"' _ "$SEED_KEY"
check 'SEED_KEY finds a ConfigMap key' \
    sh -c 'printf "  Seed__Enabled: \"true\"\n" | grep -qE "$1"' _ "$SEED_KEY"
check 'ENVIRONMENT_KEY finds the job host'"'"'s variable' \
    sh -c 'printf "            - name: DOTNET_ENVIRONMENT\n" | grep -qE "$1"' _ "$ENVIRONMENT_KEY"

for render in $SERVICE_CHARTS platform; do
    check "$render: the render the seed checks read is not empty" test -s "$OUT/$render.yaml"
    check "$render: no render carries a seed flag" absent "$SEED_KEY" "$OUT/$render.yaml"
    check "$render: no render sets an environment name" absent "$ENVIRONMENT_KEY" "$OUT/$render.yaml"
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

# A connection string carries its user, so two charts reading one Redis Secret
# are one ACL user for two services, which §8.1 says does not happen. Each
# chart's name is read from its render, and the read itself is checked first.
redis_declared=0
redis_secrets=''
for chart in $SERVICE_CHARTS; do
    declares "$chart" redis || continue
    redis_declared=$((redis_declared + 1))
    redis_secrets="$redis_secrets $(awk '/- name: ConnectionStrings__Redis/ { want = 1; next }
        want && /name: / { gsub(/"/, "", $2); print $2; want = 0 }' "$OUT/$chart.yaml" | sort -u)"
done
check 'a Redis Secret name was read from every chart that declares redis' \
    test "$(printf '%s\n' $redis_secrets | grep -c .)" -eq "$redis_declared"
check 'no two service charts render the same Redis Secret' \
    test "$(printf '%s\n' $redis_secrets | sort | uniq -d | grep -c .)" -eq 0

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
check 'no ConfigMap carries the relay password' \
    test "$(count 'Mail__Password' "$OUT/configmap-keys.txt")" -eq 0
check 'every ConnectionStrings__ value comes from a secretKeyRef' \
    test "$(count 'ConnectionStrings__' "$OUT/platform.yaml")" \
    -eq "$(awk '/- name: ConnectionStrings__/ { want = 1; next } want && /secretKeyRef/ { n++; want = 0 } END { print n + 0 }' "$OUT/platform.yaml")"

# --------------------------------------------------------------------------
section 'Client credentials: the hosts that call out under a grant of their own (§11.5, §15.3, ADR-052)'
# --------------------------------------------------------------------------
# A further chart growing an identity.clientId is a design change, not a
# configuration change: it means another host began calling out under a grant
# of its own, which ADR-017's budget or ADR-052's reads have to argue for.
credentialed_count="$(printf '%s\n' $CREDENTIALED_CHARTS | grep -c . || true)"
check "exactly the credentialed charts hold a client secret ($CREDENTIALED_CHARTS)" \
    test "$(count 'Identity__Client__ClientSecret' "$OUT/platform.yaml")" -eq "$credentialed_count"
for chart in $CREDENTIALED_CHARTS; do
    check "$chart is one of them" \
        test "$(count 'Identity__Client__ClientSecret' "$OUT/$chart.yaml")" -eq 1
done
# And they read DIFFERENT Secrets, which the counts above cannot see: a values
# file copied from the BFF's leaves Shipping's pod mounting the BFF's grant,
# renders cleanly, passes every count above, and gives one host another's
# identity (§11.5).
check 'and each reads a Secret of its own' \
    test "$(awk '/- name: Identity__Client__ClientSecret/ { want = 1; next }
                 want && /name: / { print $2; want = 0 }' "$OUT/platform.yaml" |
        sort -u | wc -l)" -eq "$credentialed_count"

# The assertions above read the default render, and Helm accepts values a
# chart's values.yaml never declares, so an environment file could put one
# service's Secret in another's pod by turning a credential-bearing capability
# on. The library holds which chart owns each and the descriptors declare it;
# the two are held to each other here in both directions.
CREDENTIAL_CAPABILITIES="paymentProvider carrier mail clientCredentials"
enable_args() {
    case "$1" in
        paymentProvider) printf '%s' "--set paymentProvider.enabled=true
            --set-string paymentProvider.baseUrl=https://psp.example.invalid/
            --set-string paymentProvider.apiKeySecretRef.name=payments-provider
            --set-string paymentProvider.apiKeySecretRef.key=api-key" ;;
        carrier) printf '%s' "--set carrier.enabled=true
            --set-string carrier.baseUrl=https://carrier.example.invalid/
            --set-string carrier.apiKeySecretRef.name=shipping-carrier
            --set-string carrier.apiKeySecretRef.key=api-key" ;;
        mail) printf '%s' "--set mail.enabled=true
            --set-string mail.host=relay.example.invalid --set mail.port=587
            --set-string mail.from=no-reply@commerce.example.invalid
            --set-string mail.security=StartTls --set-string mail.userName=notifications
            --set-string mail.passwordSecretRef.name=notifications-mail
            --set-string mail.passwordSecretRef.key=password" ;;
        clientCredentials) printf '%s' "--set identity.clientCredentials=true
            --set-string identity.clientId=x --set-string identity.scope=y
            --set-string identity.clientSecretRef.name=web-bff-identity
            --set-string identity.clientSecretRef.key=secret" ;;
    esac
}
refused_key() { case "$1" in clientCredentials) echo identity.clientCredentials ;; *) echo "$1.enabled" ;; esac; }

for chart in $SERVICE_CHARTS; do
    for cap in $(field "$chart" capability); do
        case " $CREDENTIAL_CAPABILITIES " in
            *" $cap "*) ;;
            *) fail "$chart's descriptor declares $cap, which is not a capability this script can turn on" ;;
        esac
    done
    for cap in $CREDENTIAL_CAPABILITIES; do
        if owns "$chart" "$cap"; then
            check "$cap renders on $chart, whose descriptor declares it" \
                "$HELM" template "$chart" "$CHARTS_DIR/$chart" $NETPOL_OVERLAY --set-string "image.tag=$TAG" \
                $GATEWAY_OVERLAY $(overlay_for "$chart") $(enable_args "$cap")
        else
            refuses_foreign "$chart" "$cap is refused on $chart, whose descriptor does not declare it" \
                "$(refused_key "$cap") is true on the $chart chart" $(enable_args "$cap")
        fi
    done
done

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
    "$HELM" template gateway "$CHARTS_DIR/gateway" $NETPOL_OVERLAY --set-string "image.tag=$TAG" \
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
section 'The worker shape, on the charts that have it'
# --------------------------------------------------------------------------
# §15.3 specifies `service.enabled: false` and `ingress.enabled: false` for
# Shipping and Notifications, and each chart is read from its own render.
worker_shape() {
    # worker_shape <chart>
    local chart="$1"
    check "$chart renders no Service" test "$(count '^kind: Service$' "$OUT/$chart.yaml")" -eq 0
    check "$chart renders no Ingress" test "$(count '^kind: Ingress$' "$OUT/$chart.yaml")" -eq 0
    check "$chart keeps its workload" test "$(count '^kind: Deployment$' "$OUT/$chart.yaml")" -eq 1
    check "$chart keeps its migration hook" test "$(count '^kind: Job$' "$OUT/$chart.yaml")" -eq 1
    check "$chart probes the container port directly" \
        test "$(count 'path: /health/ready$' "$OUT/$chart.yaml")" -eq 1
    # An Ingress backend is this workload's Service, so a values copy that turned
    # the route on would install cleanly and answer 503 (_ingress.tpl).
    refuses_chart "$chart" "an Ingress on $chart fails the render" \
        'ingress.enabled requires service.enabled' --set ingress.enabled=true
}
worker_shape shipping
worker_shape notifications

# --------------------------------------------------------------------------
section 'A value the gateway requires only when another is set'
# --------------------------------------------------------------------------

# Conditionally required is a real category (§15.4): off is a valid topology,
# on-but-unconfigured is a silent defect. The gateway's own startup guards
# catch it, and catching it at render says which chart value is missing.
if "$HELM" template gateway "$CHARTS_DIR/gateway" $NETPOL_OVERLAY --set-string "image.tag=$TAG" \
    --set 'ingress.trustedNetworks=null' >"$OUT/untrusted.txt" 2>&1; then
    fail 'ingress.enabled with no trustedNetworks renders — it must not'
else
    check 'ingress.enabled with no trustedNetworks fails the render' \
        grep -q 'ingress.trustedNetworks must hold at least one CIDR' "$OUT/untrusted.txt"
fi

if "$HELM" template gateway "$CHARTS_DIR/gateway" $NETPOL_OVERLAY --set-string "image.tag=$TAG" \
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
    if "$HELM" template gateway "$CHARTS_DIR/gateway" $NETPOL_OVERLAY --set-string "image.tag=$TAG" \
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
# Aimed at a chart with no Service, where a message naming one would be false.
refuses_chart shipping 'the workload-name message holds on a chart with no Service' \
    'the Service included where there is one' --set-string 'workload.name= '

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
    'https://[::1/realms' 'https://*.example.com/realms' 'https://id%.example.com/realms'; do
    refuses "an authority of '$bad' fails the render" 'HTTPS address this chart will accept' \
        $GATEWAY_OVERLAY --set-string "identity.authority=$bad"
done

# The port's range is a separate refusal with its own message, because the
# digits satisfy the shape and the range is a test of its own.
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
    if "$HELM" template payments "$CHARTS_DIR/payments" $NETPOL_OVERLAY --set-string "image.tag=$TAG" \
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
    'https://[::1/' 'https://*.psp.example.invalid/'; do
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
        "$HELM" template payments "$CHARTS_DIR/payments" $NETPOL_OVERLAY --set-string "image.tag=$TAG" \
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
# A wildcard subdomain is the commonest CORS mistake, and Uri.TryCreate
# refuses the host; an underscore it accepts, so that one must render.
refuses 'a wildcard origin fails the render' 'is not a browser origin' \
    $GATEWAY_OVERLAY --set cors.enabled=true --set 'cors.origins={https://*.example.com}'
check 'an origin with an underscore in its host renders' \
    "$HELM" template gateway "$CHARTS_DIR/gateway" $NETPOL_OVERLAY --set-string "image.tag=$TAG" \
    $GATEWAY_OVERLAY --set cors.enabled=true --set 'cors.origins={https://shop_1.example.com}'

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
    if "$HELM" template catalog "$CHARTS_DIR/catalog" $NETPOL_OVERLAY --set-string "image.tag=$bad" \
        >"$OUT/badtag.txt" 2>&1; then
        fail "image.tag=$bad renders — Kubernetes would refuse the Job it names"
    else
        check "image.tag=$bad fails the render" \
            grep -q 'which is not a DNS-1123 label' "$OUT/badtag.txt"
    fi
done
# Aimed at a chart with no migrator, where a message naming its Job would be false.
refuses 'the tag-shape message holds on a chart with no migrator' \
    'migration Job.s name needs where there is one' $GATEWAY_OVERLAY --set-string 'image.tag=Release_1'

for good in 1.2.3 0000000000000000000000000000000000000000 v1-2-3; do
    check "image.tag=$good still renders" \
        "$HELM" template catalog "$CHARTS_DIR/catalog" $NETPOL_OVERLAY --set-string "image.tag=$good"
done

# The name budget, which the per-segment check cannot see: a plain `trunc 63`
# can cut immediately after a dot, and trimming a trailing hyphen never
# touches a trailing dot.
long_tag="$(printf 'a%.0s' $(seq 1 42)).b"
if "$HELM" template catalog "$CHARTS_DIR/catalog" $NETPOL_OVERLAY --set-string "image.tag=$long_tag" \
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
# Case, which the shape test cannot see: the gateway refuses at startup an
# origin that is not its canonical form, which lowercases scheme and host.
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
"$HELM" template gateway "$CHARTS_DIR/gateway" $NETPOL_OVERLAY --set-string "image.tag=$TAG" \
    $GATEWAY_OVERLAY --set cors.enabled=true \
    --set 'cors.origins={https://shop.example.com }' >"$OUT/spaced.txt" 2>&1 || true
check 'the rendered origin is the validated one, not the raw value' \
    grep -q 'Cors__Origins__0: "https://shop.example.com"' "$OUT/spaced.txt"

# Web.Bff binds ServiceIdentityOptions unconditionally, so clearing clientId
# is not an opt-out: it renders a release whose pod refuses to start.
refuses_bff() {
    local label="$1" needle="$2"
    shift 2
    if "$HELM" template web-bff "$CHARTS_DIR/web-bff" $NETPOL_OVERLAY --set-string "image.tag=$TAG" \
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
# never looks at the process behind `targetPort`. A service declaring
# Kestrel:Endpoints owns its ports outright (§14.2), and one that does not
# listens on the image's ASPNETCORE_HTTP_PORTS default, 8080 — so every chart
# is compared in both directions against one or the other, and a chart whose
# service stops pinning is held to 8080 rather than skipped. The count that
# pins is asserted below, so a search that stops finding any fails rather than
# passing quietly.
pinned=0
for chart in $SERVICE_CHARTS; do
    settings="$(grep -rl '"Kestrel"' --include=appsettings.json --exclude-dir=bin --exclude-dir=obj \
        "$(src_of "$chart")" || true)"

    if [ -z "$settings" ]; then
        echo 8080 >"$OUT/$chart-listeners.txt"
    elif [ "$(printf '%s\n' "$settings" | wc -l)" -ne 1 ]; then
        fail "$chart pins ports in more than one appsettings.json — the search, not the chart, is wrong"
        continue
    else
        pinned=$((pinned + 1))
        { grep -ohE 'http://0\.0\.0\.0:[0-9]+' "$settings" || true; } |
            sed -E 's|.*:([0-9]+)|\1|' | sort -u >"$OUT/$chart-listeners.txt"

        if [ ! -s "$OUT/$chart-listeners.txt" ]; then
            fail "no Kestrel endpoint parsed out of $settings — the parse, not the chart, is wrong"
            continue
        fi
    fi

    while read -r port; do
        check "$chart listens on $port and its chart declares it" \
            grep -q "containerPort: $port$" "$OUT/$chart.yaml"
    done <"$OUT/$chart-listeners.txt"

    # And the other direction, so a chart port with nothing behind it is caught too.
    awk '/^kind: Deployment$/ { in_dep = 1 } in_dep && /containerPort:/ { print $2 }' \
        "$OUT/$chart.yaml" | sort -u >"$OUT/$chart-declared.txt"
    missing="$(comm -23 "$OUT/$chart-declared.txt" "$OUT/$chart-listeners.txt")"
    if [ -z "$missing" ]; then
        pass "and $chart declares no port it does not listen on"
    else
        fail "$chart's chart declares port(s) it has no listener for: $(echo "$missing" | tr '\n' ' ')"
    fi
done

if [ "$pinned" -eq 0 ]; then
    fail 'no chart pins its own Kestrel endpoints — the search, not the charts, is wrong'
else
    pass "the listener comparison covered $pinned chart(s) that pin their own endpoints"
fi

# --------------------------------------------------------------------------
section 'The canary track (§15.5, ADR-022)'
# --------------------------------------------------------------------------
# The canary render is reached by nothing above, and every service chart is
# rendered rather than a representative one: the mechanism lives in the
# library, so a chart that failed to pick it up would be a service with no
# canary and a rollout that promoted it without ever splitting traffic.
for chart in $SERVICE_CHARTS; do
    "$HELM" template "$chart-canary" "$CHARTS_DIR/$chart" $NETPOL_OVERLAY --set-string "image.tag=$TAG" \
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

    # The spread counts the canary's own pods, so the stable track's placement
    # never holds a rung back.
    check "$chart: the canary's spread counts its own track" \
        awk '/^      topologySpreadConstraints:$/ { s = 1 } /^      containers:$/ { s = 0 }
            s && /app.kubernetes.io\/track: canary$/ { n++ } END { exit n == 2 ? 0 : 1 }' \
        "$OUT/$chart-canary.yaml"

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

    # Helm refuses to touch an object another release owns (§15.3), so every
    # name the canary release emits has to differ from the stable one's.
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
        test "$(docs_of "$OUT/$chart-canary.yaml" Job hook | count '"helm.sh/hook": pre-install,pre-upgrade' /dev/stdin)" -eq 1
done

for chart in $DATABASELESS_CHARTS; do
    check "$chart: the canary renders no migration Job either" \
        test "$(count '^kind: Job$' "$OUT/$chart-canary.yaml")" -eq 0
done

# --------------------------------------------------------------------------
section 'Result'
# --------------------------------------------------------------------------
if [ "$failures" -ne 0 ]; then
    printf '%s assertion(s) failed\n' "$failures" >&2
    exit 1
fi
printf 'all assertions passed\n'
