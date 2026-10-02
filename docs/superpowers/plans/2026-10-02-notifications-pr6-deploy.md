# Notifications PR-6 — chart, deploy target and canary — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Notifications deployable. `deploy/helm/notifications` is
Shipping's chart renamed, with the Service, the Ingress, Redis and the
autoscaler declared off; the library chart grows the capabilities this host
reads eagerly — `mail`, `contactSource`, `delivery`, a `jurisdiction` block
that holds ADR-053 rule 1's three kinds rather than Shipping's two windows —
and admits a third chart to the client-credentials set; the deployable gains
its descriptor under `deploy/canary/deployables/`, which is the whole of its
deploy target, the canary row and `smoke.sh`'s case; the umbrella gains the
subchart; and the shared queue-backlog runbook and the outbox dashboard gain
the Notifications half: delivery lag stops when a consumer starts, and the
waiting gauge, by step, is the signal for the send worker's wait.

**Architecture:** `deploy/helm/notifications` is `deploy/helm/shipping` with
the workload renamed and every Shipping capability block replaced by the
four this host registers unconditionally. The library chart keeps its rule —
every capability is a guarded block, required when on and refused when its
settings are present and it is off, in both directions — and changes in two
places that a third credentialed worker makes necessary: the `jurisdiction`
block stops naming Shipping's two windows and renders whatever members a chart
declares, by ADR-053 rule 1's three kinds, while each chart's own
`capabilities.yaml` names the members its host binds; and the
`identity.clientCredentials` upward guard names three charts. The descriptor
replaces what Shipping's plan did by hand in four lists: since then
`deploy.yml`'s menu, `smoke.sh`'s chart lists and the canary map are all read
from `deploy/canary/deployables/` (§15.3), so a deployable is its descriptor,
its chart and the umbrella's dependency on that chart.

**Tech Stack:** Helm 3 templates (Helm v3.16.3, `helm.yml`'s pin), bash
(`smoke.sh`), Python 3.12 (`canary.py`, `check.py`), Grafana dashboard JSON,
Markdown.

**Spec:** `docs/superpowers/specs/2026-10-02-notifications-service-design.md`,
sections 3 (PR-6's row), 4 (`DeliveryOptions.GiveUpAge`), 6 (the jurisdiction
options' three kinds), 9 (`MailOptions`, the client credential), 11 (the
chart, its capabilities, the scaling decision, the canary row, the readiness
set), 12 (delivery lag, the waiting gauge, the dashboard panel) and 14 (the two
asserted Helm rows of ADR-052's table, §15.1, §15.3). The `Jurisdiction__*`
keys, `Delivery__GiveUpAge`, `DeliveryOptions`,
`NotificationsJurisdictionOptions`, `notifications.waiting` with its steps
and `notifications.overdue` are the spec's sections 4, 6, 11 and 12, and
*Global Constraints* names each assumption this plan makes of PR-4's and
PR-5's plans, every one of which those plans meet as written.

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class D.** Touch set:

  `deploy/helm/**`, `deploy/canary/**`, `deploy/observability/dashboards/outbox.json`, `docs/runbooks/queue-backlog.md`, `docs/runbooks/latency.md`, `docs/backend-architecture/15-cicd-deployment.md`

  Why each, since the row is paths only: the new chart, the library chart,
  Shipping's `capabilities.yaml`, `web-bff/values.yaml`'s first comment, the
  umbrella, the Helm README and the smoke gate are `deploy/helm`; the
  descriptor, `canary.py`'s overlay alphabet, its suite and `canary.json`'s
  `$comment` are `deploy/canary`; the panel and the service variable are the
  one dashboard; the runbook is the one §13.6's two latency rules share, and
  `latency.md`'s slow-peer branch is where the contact read's symptom is
  pointed at it; and chapter 15 holds §15.1's credential sentence (ADR-052's
  row for it, spec section 14), §15.3's worker and credential paragraphs, and
  §15.4's rows the chart makes concrete.
- **No `src/**` and no `tests/**`, and that is a decision.** Spec section 11
  asks for a test that the relay and Keycloak are not in the readiness set; it
  is **PR-1's**: `tests/Notifications.Worker.Tests/HostSmokeTests.cs`'s
  `Ready_probe_reports_the_sql_and_bus_checks` ends
  `options.Registrations.Select(r => r.Name).ShouldBe(["sql", "masstransit-bus"], ignoreOrder: true);`,
  which PR-2, PR-3 and PR-5 inherit. The readiness set is the host's
  registration, and the pull request that adds the dependency someone would be
  tempted to put in it is the one that must refuse it. Task 7 runs that test,
  and the chart's `probes` comment cites the rule. So this PR stays a clean
  Class D, the class row is one letter, and `classes.yml` needs no change.
- **`deploy.yml` is not edited, and `.github/workflows/helm.yml` is not
  either.** The deploy workflow reads its charts from the descriptors (spec,
  section 3): `deploy.yml`'s `workload` input is a string held to
  `deploy/canary/deployables/` by its guard step (`canary.py chart`), and
  `canary.py` check 8 refuses a menu. `helm.yml` already filters on
  `src/Services/**`. The descriptor is the deploy target.
- **`docs/secrets.md` is not edited.** Spec section 11 gives PR-6 "the
  chart's place" for the client secret and the relay password; that place is
  `deploy/helm/notifications/values.yaml` itself — `docs/secrets.md`'s table
  row 3 already says so generically — and the one sentence in that file that
  names a Secret per host, *A client secret*'s step 2, is PR-3's Step 8, which
  names `notifications-identity`. The relay password has no rotation
  subsection, as the provider's and the carrier's keys have none. Task 7
  verifies both rather than writing either again.
- **Depends on PR-5 having merged**, and therefore on PR-1 to PR-4. These are
  the names this plan consumes, as each plan or the spec spells them:
  - PR-1 (on disk): `src/Services/Notifications/Notifications.Worker` with
    `Program.cs` beside its csproj (check 4's entry assembly
    `Notifications.Worker`); images `notifications-worker` and
    `notifications-migrator` in `ci.yml`'s matrix; the connection keys
    `ConnectionStrings__Notifications` and
    `ConnectionStrings__NotificationsMigrator`; the broker account
    `notifications-svc`; no `AddRedisConnections`; no Kestrel pin in
    `appsettings.json`, so the host listens on 8080; the readiness test above.
  - PR-2 (on disk): `MailOptions` binding `Mail:Host`, `Mail:Port`,
    `Mail:From`, `Mail:Security` (`StartTls` or `None`), `Mail:UserName`,
    `Mail:Password`; `AddMailChannel` refusing `None` and an anonymous relay
    outside Development; `notifications.mail.unavailable` with a `cause`
    attribute of `transient`, `unconfirmed`, `tls`, `credential` or
    `rejected`, unit `{attempt}`; §15.4's six `Mail__*` rows without a Helm
    spelling.
  - PR-3 (on disk): `ContactSource:BaseUrl` (absolute, HTTPS outside
    Development, no user information, query or fragment) and
    `ContactSource:Realm` (one path segment); the client `notifications-worker`
    requesting scope `roles`; `client.AddHttpMessageHandler<Common.Infrastructure.Identity.ClientCredentialsHandler>();`
    in `Notifications.Infrastructure/Contacts/DependencyInjection.cs` — a
    namespace-qualified type argument, which Task 2 widens `smoke.sh`'s pattern
    to read; `notifications.contact.refused`, unit `{refusal}`; §15.4's rows
    naming `contactSource.baseUrl`, `contactSource.realm` and
    `notifications-identity`.
  - PR-4 (on disk): `NotificationsJurisdictionOptions` bound from section
    `Jurisdiction` with `Languages`, `TimeZone`, `LogRetention`,
    `ContactRetention` and `OrderRetention`. **`Languages` binds as a list**,
    an `IReadOnlyList<string>`, so the chart renders it as
    `Jurisdiction__Languages__0…n`, the indexed spelling `docs/secrets.md` and
    §15.4 already use for `Ingress__TrustedNetworks__0…n`. The windows bind as
    `TimeSpan` in the `[d.]hh:mm[:ss]` form `commerce.timeSpanPattern`
    accepts, and the zone is an IANA id the host resolves at start. The seven
    consumers register with `AddConsumer` (check 9's `consume`) as
    `Common.Infrastructure`'s sealed `IntegrationEventConsumer<T>`, closed
    over each event, which is what records `messaging.delivery.lag` (Task 5
    verifies it).
  - PR-5 (on disk): `DeliveryOptions` bound from section `Delivery` with
    `GiveUpAge`, in `FulfilmentOptions`' form; `notifications.waiting`, an
    observable gauge created with the literal name at the
    `CreateObservableGauge` call — `check.py`'s `INSTRUMENT` reads only a
    literal — on `Notifications.Outbound`, with an annotation unit or none, so
    its series is `notifications_waiting`, and **a `step` attribute whose
    values are `order_record`, `contact` and `relay`**; and
    `notifications.overdue`, an observable gauge of `Pending` rows due for a
    pass that no pass has claimed for longer than two ticks, in
    `shipping.shipments.overdue`'s form — **a duration with unit `s`**, as
    that form is, so its series is `notifications_overdue_seconds`, with no
    `pass` attribute because the send worker is the one pass. The chart
    defaults `delivery.giveUpAge` to `1.00:00:00`, the day spec section 4
    names, which PR-5's Compose value and `DeliveryOptions`' bounds admit.
- **Every key this chart renders is one the host refuses to start without**,
  which is why each is required at render or defaulted with a reason, and the
  chart's own `capabilities.yaml` refuses each capability switched off.
- **Helm applies `--set-string` after `--set` and `--set-json`, whatever the
  order on the command line** (`values.Options.MergeValues`). Every
  descriptor overlay arrives as `--set-string`, so a case that clears an
  overlay value clears it with `--set-string key=`, and a case that removes a
  key outright renders without that overlay entry. Shipping's cases already
  follow the first half; Task 2's `refuses_removed` is the second.
- **The comment gate reads `.yaml`, `.yml`, `.sh` and `.py` and its block
  limit is five lines** (`.github/comment-gate/comment_gate.py`'s
  `BLOCK_LIMIT`); a touched block is judged whole; no emphasis, no issue or
  pull-request number, no history. It does not read `.tpl`, `.json` or `.md`,
  and the house budget applies to them all the same: every new `.tpl` comment
  here is five lines or fewer.
- **ADR-052 and ADR-047 are not edited.** ADR-052's closing table gives this
  PR `_helpers.tpl` and `smoke.sh` (both asserted) and §15.1; an ADR is
  superseded, never rewritten. ADR-047 names no workload, so a fourth
  `consume`-judged deployable with an `httpExemption` moves nothing in it.
- **ADR-052's two asserted Helm rows, as the tree stands now.** `smoke.sh`'s
  credential assertions already read the set from the descriptors
  (`CREDENTIALED_CHARTS="$(charts_where capability clientCredentials)"`), so
  they stay green for a third chart whose descriptor declares it; what goes red
  is `_helpers.tpl`'s `has .Chart.Name (list "web-bff" "shipping")`, which
  refuses the render. Task 2 moves that literal, and replaces `smoke.sh`'s two
  named source assertions — the BFF's and Shipping's — with one loop over every
  chart in both directions, so the set is read rather than listed in the one
  place it still was.
- The overlay's alphabet: `canary.py`'s `OVERLAY` admits `@`, `{` and `}` after
  Task 1, for a sender address and a one-language list. Brace expansion runs
  before command substitution in bash, so a brace in `overlay_for`'s output is
  never expanded, and neither character is a field separator for `awk`.
- `py -3.12`, never `python`. Use Git's `bash` for `smoke.sh`, with
  `PYTHON="py -3.12"` on this machine.

---

### Task 1: The canary gate admits Notifications' overlay, and stops using its name as a stand-in

**Files:**
- Modify: `deploy/canary/canary.py` — `OVERLAY`'s value alphabet, and the
  comment above it
- Modify: `deploy/canary/test_canary.py` — two new cases in `DescriptorTests`,
  and the stand-in name in the dispatch-menu cases

**Interfaces:**
- Produces: `canary.OVERLAY` accepting `@`, `{` and `}` in a value; a
  `test_canary.py` with no `notifications` in it.

Two edits that must precede the descriptor, because each would turn the
canary suite red on the commit that adds it. `test_canary.py` uses
`notifications` as the name of a workload **no descriptor describes**, in
`test_a_workflow_only_name_is_refused` and
`test_a_menu_written_one_option_per_line_is_refused`, which assert
`"notifications" in f and "no descriptor describes" in f`; Task 3's
`deploy/canary/deployables/notifications.json` makes that false. And
Notifications' overlay carries a sender address and a one-language list,
which `OVERLAY` refuses today.

- [ ] **Step 1: Write the failing cases**

In `DescriptorTests`, after
`test_a_case_smoke_sh_would_split_into_two_words_is_refused`:

```python
    def test_an_address_and_a_one_item_list_are_one_word_each(self) -> None:
        workloads = json.loads(json.dumps(canary.load_plan()["workloads"]))
        workloads["payments-api"]["smoke"]["overlay"] = [
            "paymentProvider.baseUrl=https://psp.example.invalid/",
            "mail.from=no-reply@commerce.example.invalid",
            "jurisdiction.languages={en}",
        ]

        failures = canary.check({**canary.load_plan(), "workloads": workloads})

        self.assertFalse(any("payments-api.smoke.overlay" in f for f in failures), failures)

    def test_a_list_smoke_sh_would_split_at_its_comma_is_refused(self) -> None:
        workloads = json.loads(json.dumps(canary.load_plan()["workloads"]))
        workloads["payments-api"]["smoke"]["overlay"] = ["jurisdiction.languages={en,kk}"]

        failures = canary.check({**canary.load_plan(), "workloads": workloads})

        self.assertTrue(any("payments-api.smoke.overlay" in f for f in failures), failures)
```

The second is the boundary the first widens to: a comma is Helm's assignment
separator in `--set-string`, so a list of more than one item belongs in a
values file, and the descriptor's overlay holds only what a render cannot do
without — one language is enough for that.

- [ ] **Step 2: Run it, expect red**

```bash
py -3.12 -m unittest discover -s deploy/canary -k test_an_address_and_a_one_item_list
```

Expected: FAIL — `check` reports `workloads.payments-api.smoke.overlay is
[...], which is not a list of words smoke.sh can hand to helm`, because `@`
and `{` are outside `OVERLAY`'s value class. The comma case already passes,
and stays passing after Step 3.

- [ ] **Step 3: Widen the alphabet**

`deploy/canary/canary.py`. Before:

```python
# What smoke.sh splits a case into words by, so no value may hold a space or a
# shell metacharacter. The capability names are smoke.sh's own vocabulary.
SOURCE_PATH = re.compile(r"src(/[A-Za-z0-9_-][A-Za-z0-9._-]*)+")
CAPABILITY = re.compile(r"[A-Za-z]+")
OVERLAY = re.compile(r"[A-Za-z][A-Za-z0-9]*(\.[A-Za-z][A-Za-z0-9]*)+=[A-Za-z0-9._:/-]*")
```

After:

```python
# What smoke.sh splits a case into words by, so no value may hold a space, a
# comma or a character a shell acts on in an expansion's result. Braces are not
# one: bash expands them before it substitutes, so a one-item list stays a word.
SOURCE_PATH = re.compile(r"src(/[A-Za-z0-9_-][A-Za-z0-9._-]*)+")
CAPABILITY = re.compile(r"[A-Za-z]+")
OVERLAY = re.compile(r"[A-Za-z][A-Za-z0-9]*(\.[A-Za-z][A-Za-z0-9]*)+=[A-Za-z0-9._:/@{}-]*")
```

The capability names' clause leaves the comment, because `CAPABILITY`'s
vocabulary is stated in `deploy/canary/README.md`'s `smoke.capabilities` row,
and three lines say what the alphabet is for.

- [ ] **Step 4: Retire the stand-in**

In `deploy/canary/test_canary.py`, every `notifications` becomes
`no-such-release` — eleven lines in `DescriptorReadTests`, from
`test_a_workflow_only_name_is_refused` to
`test_a_menu_in_a_flow_mapping_on_the_key_line_is_refused`, `MENU` included —
so the two refusals' assertions read, for example:

```python
        self.assertIn("no-such-release", read["workflow"])
        self.assertTrue(any("by hand (catalog-api, no-such-release)" in f for f in failures), failures)
        self.assertTrue(any("no-such-release" in f and "no descriptor describes" in f for f in failures), failures)
```

and the flow-mapping case's menu reads
`"      workload: {type: choice, options: [catalog-api, 'no-such-release']}\n"`.
It is a name `RELEASE_NAME` admits and no deployable will take, sorted after
`catalog-api` as the old one was, so every `by hand (…)` message keeps its
order. Then:

```bash
grep -n "notifications" deploy/canary/test_canary.py
```

Expected: nothing.

- [ ] **Step 5: Run; commit**

```bash
py -3.12 -m unittest discover -s deploy/canary
py -3.12 deploy/canary/canary.py check
```

Expected: both green, the two new cases among the suite's.

```bash
git add deploy/canary/canary.py deploy/canary/test_canary.py
git commit -m "feat(canary): the smoke overlay admits an address and a one-item list, and the menu cases stop naming Notifications"
```

The body argues the two together: each is what the descriptor in Task 3 would
otherwise turn red, the first by refusal and the second by making a stand-in
real; and why braces and `@` are safe in a word the shell never re-parses
while a comma is not.

---

### Task 2: The library chart's capabilities, and a third credentialed chart

**Files:**
- Modify: `deploy/helm/common/templates/_helpers.tpl` — `commerce.timeSpan`;
  `commerce.config`'s `jurisdiction` block made ADR-053 rule 1's, `fulfilment`
  over the new helper, and three new blocks, `mail`, `contactSource` and
  `delivery`; `commerce.env`'s four coherence guards, the `mail` upward guard,
  the client-credentials set, and `Mail__Password`
- Modify: `deploy/helm/shipping/templates/capabilities.yaml` — the two
  jurisdiction members its host binds
- Modify: `deploy/helm/smoke.sh` — `ATTACHES_CREDENTIALS` and its self-tests,
  the source loop that replaces the two named credential assertions,
  `disowns`, `refuses_removed`, `worker_shape`, `mail` in
  `CREDENTIAL_CAPABILITIES`, Shipping's section's heading and one needle, the
  credential section's comments, and `SOURCE_INPUTS`

**Interfaces:**
- Consumes: Task 1 (nothing of it directly; the suite must already be green).
- Produces, for Task 3: the values blocks `mail` (`enabled`, `host`, `port`,
  `from`, `security`, `userName`, `passwordSecretRef.name|key`),
  `contactSource` (`enabled`, `baseUrl`, `realm`), `jurisdiction`
  (`enabled`, `languages`, `timeZone`, and any other member as a window) and
  `delivery` (`enabled`, `giveUpAge`); the rendered keys `Mail__Host`,
  `Mail__Port`, `Mail__From`, `Mail__Security`, `Mail__UserName`,
  `Mail__Password` (a `secretKeyRef`), `ContactSource__BaseUrl`,
  `ContactSource__Realm`, `Jurisdiction__Languages__<i>`,
  `Jurisdiction__TimeZone`, `Jurisdiction__<Member>` per window and
  `Delivery__GiveUpAge`; the guard messages Task 3's needles match; and in
  `smoke.sh`, `disowns`, `refuses_removed` and `worker_shape`.

**Why the jurisdiction block changes shape.** It names
`jurisdiction.addressRetention` and `jurisdiction.trackingRetention` and
requires both whenever it is on, so on Notifications' chart it would demand
Shipping's windows and render none of its own. ADR-053 rule 1 gives every
service one jurisdiction options class with three kinds of member — a language
set, a zone, windows — and Notifications' is the first to hold all three
(spec section 6). So the block renders the kinds rather than the names:
`languages` as an indexed list, `timeZone` as an IANA id, and every other
member a window, each required and each shaped. **What a generic block cannot
know is which members a host binds**: a member removed outright (`null` in a
values file) renders no key and fails nothing, where Shipping's named block
refused it. That fact moves to each chart's own `capabilities.yaml`, beside the
switches it already refuses, and a smoke case per chart pins it.

- [ ] **Step 1: Write the failing smoke assertions**

**First, the credential pattern must read a qualified type.** PR-3 attaches
the handler as `AddHttpMessageHandler<Common.Infrastructure.Identity.ClientCredentialsHandler>()`,
which `ATTACHES_CREDENTIALS` cannot see; Step 3 widens the pattern. Here, two
self-tests in `deploy/helm/smoke.sh` after `ATTACHES_CREDENTIALS accepts the
real attachment`:

```bash
check 'ATTACHES_CREDENTIALS accepts the attachment through a qualified type name' \
    sh -c 'printf "        client.AddHttpMessageHandler<Common.Infrastructure.Identity.ClientCredentialsHandler>();\n" |
        grep -qE "$0"' "$ATTACHES_CREDENTIALS"
check 'ATTACHES_CREDENTIALS refuses a handler whose name only ends in the word' \
    sh -c 'printf "        client.AddHttpMessageHandler<NoClientCredentialsHandler>();\n" |
        grep -qvE "$0"' "$ATTACHES_CREDENTIALS"
```

**Second, the source assertions read the set.** Before, after the per-chart
broker/connection/redis loop:

```bash
check 'the BFF binds ServiceIdentityOptions in src/, so its chart declares credentials' \
    grep -rq 'ServiceIdentityOptions' "$ROOT/src/BFF/Web.Bff"
check "the worker attaches ClientCredentialsHandler in src/, so its chart declares credentials" \
    grep -rqE "$ATTACHES_CREDENTIALS" "$ROOT/src/Services/Shipping"
```

After:

```bash
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
```

with `disowns` beside `lacks`, for `lacks`'s reason:

```bash
disowns() { ! owns "$1" "$2"; }
```

The BFF attaches the handler in `Program.cs`
(`pricing.AddHttpMessageHandler<ClientCredentialsHandler>();`), so the
`ServiceIdentityOptions` grep goes with Shipping's line: one rule, every chart.
`SOURCE_INPUTS` loses its `src/Services/Shipping` line, whose literal read
this removes; every descriptor's source is already declared through the `awk`
beneath it, and `src/BFF/Web.Bff` stays for `PricingHop.cs`.

**Third, the comment over the set and the section's heading stop counting.**
Before, above `CREDENTIALED_CHARTS`:

```bash
# ADR-052 made ADR-017's budget two: the BFF's pricing hop and Shipping's
# address read. Named rather than counted — a count of two is satisfied by the
# wrong two charts, and which host holds a grant is the whole claim. Read from
# the values files rather than from a render, because a chart outside the two
# setting it fails its render and the run would abort before this reported.
```

After:

```bash
# The charts whose host calls out under a grant of its own (ADR-052), named by
# their descriptors rather than counted: a count is satisfied by the wrong
# charts, and which host holds a grant is the whole claim. Read from the values
# files rather than a render, because a chart outside the set setting it fails
# its render and the run would abort before this reported.
```

The check's pass message beneath it, which names the same old rule, becomes:

```bash
    pass "exactly the charts whose host calls out under a grant of its own declare client credentials ($credentialed)"
```

Before, the client-credentials section's head:

```bash
section 'Client credentials: the hosts that call a peer (§11.5, §15.3, ADR-052)'
# --------------------------------------------------------------------------
# A third chart growing an identity.clientId is a design change, not a
# configuration change: it means a host started calling a peer synchronously,
# which is ADR-017's budget being spent a third time.
```

After:

```bash
section 'Client credentials: the hosts that call out under a grant of their own (§11.5, §15.3, ADR-052)'
# --------------------------------------------------------------------------
# A further chart growing an identity.clientId is a design change, not a
# configuration change: it means another host began calling out under a grant
# of its own, which ADR-017's budget or ADR-052's reads have to argue for.
```

**Fourth, the relay's credential joins the credential-bearing capabilities.**

```bash
CREDENTIAL_CAPABILITIES="paymentProvider carrier mail clientCredentials"
```

and `enable_args` gains a case after `carrier`'s:

```bash
        mail) printf '%s' "--set mail.enabled=true
            --set-string mail.host=relay.example.invalid --set mail.port=587
            --set-string mail.from=no-reply@commerce.example.invalid
            --set-string mail.security=StartTls --set-string mail.userName=notifications
            --set-string mail.passwordSecretRef.name=notifications-mail
            --set-string mail.passwordSecretRef.key=password" ;;
```

`refused_key` needs nothing: `mail` is not `clientCredentials`, so its key is
`mail.enabled`. The loop below it then expects every chart whose descriptor
does not declare `mail` to refuse it with `mail.enabled is true on the <chart>
chart` — red on all seven until Step 3's upward guard.

**Fifth, Shipping's jurisdiction is pinned by member.** Shipping's section's
heading becomes `"Shipping's chart declares its capabilities, and each is
required"` — there are two worker charts after Task 3, and the old title
names neither. Its coherence case's needle moves with Step 3's message:

```bash
refuses_chart shipping 'a jurisdiction the capability is off for fails the render' \
    'but a jurisdiction setting is set' --set jurisdiction.enabled=false
```

and after `the jurisdiction off and cleared fails the render`:

```bash
refuses_removed shipping jurisdiction.trackingRetention \
    'jurisdiction.trackingRetention is required on the shipping chart'
```

with the helper beside `refuses_chart`:

```bash
# A member removed outright, which a blank value does not reach: Helm applies
# every --set-string after every --set, so the overlay is passed without the
# key rather than overridden, and the removal is the case's own --set.
refuses_removed() {
    # refuses_removed <chart> <key> <needle>
    local chart="$1" key="$2" needle="$3"
    if "$HELM" template "$chart" "$CHARTS_DIR/$chart" --set-string "image.tag=$TAG" \
        $(field "$chart" overlay | awk -v k="$key=" 'index($0, k) != 1' | sed 's/^/--set-string /' | tr '\n' ' ') \
        --set "$key=null" >"$OUT/removed.txt" 2>&1; then
        fail "$chart: $key removed outright renders — it must not"
    else
        check "$chart: $key removed outright fails the render" grep -q "$needle" "$OUT/removed.txt"
    fi
}
```

**Sixth, the worker shape becomes a helper.** The section
`'The worker shape, on the chart that has it'` becomes
`'The worker shape, on the charts that have it'`, and its body:

```bash
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
```

A function called once per chart rather than a `for` over a hand list, which
`canary.py` check 8 refuses in `smoke.sh`; and the claim is §15.3's sentence,
which names the two charts.

- [ ] **Step 2: Run it, expect red**

```bash
PYTHON="py -3.12" bash deploy/helm/smoke.sh
```

Expected: these and no others fail — `ATTACHES_CREDENTIALS accepts the
attachment through a qualified type name`; `mail is refused on <chart>, whose
descriptor does not declare it` for all seven charts, each `— it rendered
instead`; `a jurisdiction the capability is off for fails the render` (the
library still says `a jurisdiction window is set`); and `shipping:
jurisdiction.trackingRetention removed outright fails the render` (the
library's named block refuses it first, with `jurisdiction.trackingRetention
is required when jurisdiction.enabled`, so the needle is not found). Every
source-loop case passes already: the BFF and Shipping attach the handler and
declare the capability, and the other five neither.

- [ ] **Step 3: Write the library templates, and widen the credential pattern**

`deploy/helm/common/templates/_helpers.tpl`. **A TimeSpan helper**, after
`commerce.timeSpanPattern`, so the three settings that bind one share one
refusal:

```yaml
{{- /*
A TimeSpan setting, required and shaped, because `3 days` renders and fails
binding in the new pod. The range is the host's, whose options type owns its
bounds. Arguments: the value, its values path, the message for a blank one,
and the sentence naming what binds it.
*/}}
{{- define "commerce.timeSpan" -}}
{{- $value := include "commerce.require" (list (index . 0) (index . 2)) -}}
{{- if not (regexMatch (include "commerce.timeSpanPattern" .) $value) -}}
{{- fail (printf "%s is %q, which is not a TimeSpan this chart will accept: [d.]hh:mm[:ss], as in 3.00:00:00 for three days. %s" (index . 1) $value (index . 3)) -}}
{{- end -}}
{{- $value -}}
{{- end -}}
```

**`commerce.config`'s jurisdiction block.** Before: the block from
`{{- if (.Values.jurisdiction).enabled }}` to its `{{- end }}`, which builds
`$windows` from `addressRetention` and `trackingRetention` and renders
`Jurisdiction__AddressRetention` and `Jurisdiction__TrackingRetention`.
After:

```yaml
{{- if (.Values.jurisdiction).enabled }}
{{- /*
ADR-053 rule 1's three kinds, none defaulted: a language set, the zone dates
are rendered in, and every other member a statutory window. Which members a
host binds is its chart's to say, in capabilities.yaml, since a member removed
outright renders nothing here.
*/}}
{{- $jurisdiction := .Values.jurisdiction }}
{{- if hasKey $jurisdiction "languages" }}
{{- $languages := $jurisdiction.languages | default list }}
{{- if not (kindIs "slice" $languages) }}
{{- fail "jurisdiction.languages is not a list. ADR-053's language set renders one indexed key per language, as §15.4 spells a list." }}
{{- end }}
{{- if not $languages }}
{{- fail "jurisdiction.languages must hold at least one language: ADR-053 makes the set a value the deployment is given, and the host refuses an empty one at start." }}
{{- end }}
{{- range $i, $language := $languages }}
{{- $tag := include "commerce.require" (list $language (printf "jurisdiction.languages[%d] is blank: each entry is a language the deployment sends in (ADR-053)." $i)) }}
{{- if not (regexMatch "^[a-z]{2,3}(-[A-Za-z0-9]{1,8})*$" $tag) }}
{{- fail (printf "jurisdiction.languages[%d] is %q, which is not a language tag this chart will accept, as in en or kk. Whether the deployment ships it is the host's to refuse at start (ADR-053)." $i $tag) }}
{{- end }}
Jurisdiction__Languages__{{ $i }}: {{ $tag | quote }}
{{- end }}
{{- end }}
{{- if hasKey $jurisdiction "timeZone" }}
{{- $zone := include "commerce.require" (list $jurisdiction.timeZone "jurisdiction.timeZone is required when jurisdiction.enabled: ADR-053 makes the zone dates are rendered in a value the deployment is given.") }}
{{- if not (regexMatch "^[A-Za-z][A-Za-z0-9_+-]*(/[A-Za-z0-9_+-]+)*$" $zone) }}
{{- fail (printf "jurisdiction.timeZone is %q, which is not an IANA zone id this chart will accept, as in Europe/London. Whether the image knows the zone is the host's to refuse at start (ADR-053)." $zone) }}
{{- end }}
Jurisdiction__TimeZone: {{ $zone | quote }}
{{- end }}
{{- range $key, $window := omit $jurisdiction "enabled" "languages" "timeZone" }}
{{- $value := include "commerce.timeSpan" (list $window (printf "jurisdiction.%s" $key) (printf "jurisdiction.%s is required when jurisdiction.enabled: ADR-053 makes every window a value the deployment is given, and the host's jurisdiction options refuse to boot without it." $key) "The host's jurisdiction options bind it at start (ADR-053).") }}
Jurisdiction__{{ upper (substr 0 1 $key) }}{{ substr 1 (len $key) $key }}: {{ $value | quote }}
{{- end }}
{{- end }}
```

`range` over a map visits its keys sorted, so Shipping's render is
`Jurisdiction__AddressRetention` then `Jurisdiction__TrackingRetention`, as it
is now, and its needles `jurisdiction.addressRetention is required` and `not a
TimeSpan this chart will accept` match the new messages. `upper` of the first
letter rather than `title`, which is a word rule and not a casing one.

**The fulfilment block** takes the helper, its message unchanged in the part
`smoke.sh` matches. Before: the block from `{{- if (.Values.fulfilment).enabled }}`
to its `{{- end }}`. After:

```yaml
{{- if (.Values.fulfilment).enabled }}
{{- /* ADR-052's give-up age for a pending shipment; FulfilmentOptions owns its range. */}}
Fulfilment__GiveUpAge: {{ include "commerce.timeSpan" (list .Values.fulfilment.giveUpAge "fulfilment.giveUpAge" "fulfilment.giveUpAge is required when fulfilment.enabled: ADR-052 makes the give-up age a value the deployment is given, and FulfilmentOptions refuses to boot without it." "FulfilmentOptions binds it at start (ADR-052).") | quote }}
{{- end }}
```

**Three new blocks**, after it and before `commerce.config`'s closing
`{{- end -}}`:

```yaml
{{- if (.Values.mail).enabled }}
{{- /*
The relay's five Config keys (§15.4); its password is commerce.env's. A chart
sets no environment, so Production is what runs, where the host refuses plain
or anonymous submission: StartTls and a user name are therefore required.
*/}}
{{- $mail := .Values.mail }}
{{- $host := include "commerce.require" (list $mail.host "mail.host is required when mail.enabled: the relay is a value the deployment is given (ADR-053), and MailOptions refuses to boot without it (§15.4).") }}
{{- if not (regexMatch "^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)*$" $host) }}
{{- fail (printf "mail.host is %q, which is not a host name this chart will accept: letters, digits, hyphens and dots, with no scheme, port or path — the port is mail.port (§15.4)." $host) }}
{{- end }}
{{- $port := include "commerce.require" (list $mail.port "mail.port is required when mail.enabled: MailOptions marks it [Required] (§15.4).") }}
{{- if or (not (regexMatch "^[0-9]+$" $port)) (lt (atoi $port) 1) (gt (atoi $port) 65535) }}
{{- fail (printf "mail.port is %q, which is not a port: a whole number in 1-65535 (§15.4)." $port) }}
{{- end }}
{{- $from := include "commerce.require" (list $mail.from "mail.from is required when mail.enabled: it is the one sender every message carries and the domain each Message-ID is minted under (§15.4).") }}
{{- if not (regexMatch "^[^,;\\r\\n@]*@[^,;\\r\\n@]+$" $from) }}
{{- fail (printf "mail.from is %q, which is not one mailbox this chart will accept: one address with one @, optionally behind a display name, and no second address. MailOptions parses it at start (§15.4)." $from) }}
{{- end }}
{{- $security := include "commerce.require" (list $mail.security "mail.security is required when mail.enabled: MailOptions marks it [Required] (§15.4).") }}
{{- if ne $security "StartTls" }}
{{- fail (printf "mail.security is %q, and this chart accepts StartTls alone: the host refuses None outside Development, and a chart sets no environment, so Production is what runs (§15.4)." $security) }}
{{- end }}
{{- $userName := include "commerce.require" (list $mail.userName "mail.userName is required when mail.enabled: the host refuses anonymous submission outside Development, and the password beside it is mail.passwordSecretRef (§15.4).") }}
Mail__Host: {{ $host | quote }}
Mail__Port: {{ $port | quote }}
Mail__From: {{ $from | quote }}
Mail__Security: {{ $security | quote }}
Mail__UserName: {{ $userName | quote }}
{{- end }}
{{- if (.Values.contactSource).enabled }}
{{- /*
ADR-052's contact read. Keycloak can serve its admin API on a hostname of its
own, so the base is not derived from the authority; HTTPS unconditionally, as
`commerce.requireUrl` argues. The realm is the authority's, because the realm
whose users are read is the one that issues this host's token.
*/}}
ContactSource__BaseUrl: {{ include "commerce.requireUrl" (list .Values.contactSource.baseUrl "contactSource.baseUrl is required when contactSource.enabled: the worker reads it eagerly (ADR-052) and does not start without it (§15.4).") | quote }}
{{- $realm := include "commerce.require" (list .Values.contactSource.realm "contactSource.realm is required when contactSource.enabled: the worker builds its admin path from it at start (§15.4).") }}
{{- if or (not (regexMatch "^[A-Za-z0-9._-]+$" $realm)) (eq $realm ".") (eq $realm "..") }}
{{- fail (printf "contactSource.realm is %q, which is not a realm name this chart will accept: one path segment of letters, digits, dots, hyphens and underscores (§15.4)." $realm) }}
{{- end }}
{{- $issuer := base (trimSuffix "/" (toString .Values.identity.authority)) }}
{{- if ne $realm $issuer }}
{{- fail (printf "contactSource.realm is %q and identity.authority names the realm %q: the realm whose users this host reads is the one that issues its token (ADR-052), so the two must agree." $realm $issuer) }}
{{- end }}
ContactSource__Realm: {{ $realm | quote }}
{{- end }}
{{- if (.Values.delivery).enabled }}
{{- /* ADR-052's give-up age for a waiting notification; DeliveryOptions owns its range. */}}
Delivery__GiveUpAge: {{ include "commerce.timeSpan" (list .Values.delivery.giveUpAge "delivery.giveUpAge" "delivery.giveUpAge is required when delivery.enabled: ADR-052's give-up age is a value the deployment is given, and DeliveryOptions refuses to boot without it." "DeliveryOptions binds it at start (ADR-052).") | quote }}
{{- end }}
```

`Identity__Authority` is rendered by `commerce.requireUrl` at the head of
`commerce.config`, so by the realm check the authority is a valid HTTPS
address and `base` of it is its last path segment. Go 1.18 and later
short-circuit `or`, so `atoi` is reached only for digits.

**`commerce.config`'s client-credentials comment** names the set by its
rule. Before:

```
Two of the three client-credential keys are Config; the secret is a Secret
(§15.4). The BFF and Shipping's worker, the hosts that call a peer under
their own identity, bind ServiceIdentityOptions with ValidateOnStart, so a
missing key refuses to boot. The switch is an explicit boolean, so each
value under it is required.
```

After:

```
Two of the three client-credential keys are Config; the secret is a Secret
(§15.4). The hosts that call out under a grant of their own (ADR-052) bind
ServiceIdentityOptions with ValidateOnStart, so a missing key refuses to
boot. The switch is an explicit boolean, so each value under it is required.
```

and `Identity__Client__Scope`'s message, which says the scope "becomes the
audience every service validates" — false for a worker whose scope is
`roles` — becomes:

```
identity.scope is required when identity.clientCredentials: it is the scope the host's token is requested under (§11.5, ADR-052), and ServiceIdentityOptions marks it [Required].
```

**`commerce.env`'s coherence block.** The jurisdiction guard, before:

```yaml
{{- if and (or (.Values.jurisdiction).addressRetention (.Values.jurisdiction).trackingRetention) (not (.Values.jurisdiction).enabled) }}
{{- fail "jurisdiction.enabled is false but a jurisdiction window is set. ShippingJurisdictionOptions is validated at start (ADR-053), so this renders cleanly and the host does not start." }}
{{- end }}
```

After:

```yaml
{{- $jurisdictionSet := false }}
{{- range $key, $value := omit (.Values.jurisdiction | default dict) "enabled" }}
{{- if $value }}
{{- $jurisdictionSet = true }}
{{- end }}
{{- end }}
{{- if and $jurisdictionSet (not (.Values.jurisdiction).enabled) }}
{{- fail "jurisdiction.enabled is false but a jurisdiction setting is set. The host's jurisdiction options are validated at start (ADR-053), so this renders cleanly and the host does not start." }}
{{- end }}
```

and after the `fulfilment` guard, three more:

```yaml
{{- if and (or (.Values.mail).host (.Values.mail).port (.Values.mail).from (.Values.mail).security (.Values.mail).userName (.Values.mail).passwordSecretRef) (not (.Values.mail).enabled) }}
{{- fail "mail.enabled is false but a mail setting is set. The worker reads the relay's keys eagerly (§15.4), so this renders cleanly and the host does not start. A capability is a fact about the code, not an environment setting." }}
{{- end }}
{{- if and (or (.Values.contactSource).baseUrl (.Values.contactSource).realm) (not (.Values.contactSource).enabled) }}
{{- fail "contactSource.enabled is false but a contactSource setting is set. The worker reads ADR-052's contact source at startup, so this renders cleanly and the host does not start." }}
{{- end }}
{{- if and (.Values.delivery).giveUpAge (not (.Values.delivery).enabled) }}
{{- fail "delivery.enabled is false but delivery.giveUpAge is set. DeliveryOptions is validated at start (ADR-052), so this renders cleanly and the host does not start." }}
{{- end }}
```

**The upward guards.** After `carrier`'s:

```yaml
{{- if and (.Values.mail).enabled (ne .Chart.Name "notifications") }}
{{- fail (printf "mail.enabled is true on the %s chart, and only notifications submits to a relay (§3.2). This would mount the relay's Secret into a pod that never reads it — a credential crossing a service boundary, which no value in an environment file may do." .Chart.Name) }}
{{- end }}
```

and the client-credentials guard — ADR-052's asserted `_helpers.tpl` row.
Before:

```yaml
{{- if and .Values.identity.clientCredentials (not (has .Chart.Name (list "web-bff" "shipping"))) }}
{{- fail (printf "identity.clientCredentials is true on the %s chart, and the two hosts that call a peer under their own identity are the BFF (§9.7, ADR-017) and Shipping's worker (ADR-052). This would mount one of their client secrets into a pod that never presents it — a credential crossing a service boundary, which no value in an environment file may do." .Chart.Name) }}
{{- end }}
```

After:

```yaml
{{- if and .Values.identity.clientCredentials (not (has .Chart.Name (list "web-bff" "shipping" "notifications"))) }}
{{- fail (printf "identity.clientCredentials is true on the %s chart, and the hosts that call out under a grant of their own are the BFF (§9.7, ADR-017) and the workers ADR-052 gives a read. This would mount one of their client secrets into a pod that never presents it — a credential crossing a service boundary, which no value in an environment file may do." .Chart.Name) }}
{{- end }}
```

A literal list, named rather than counted, and held to the descriptors in
both directions by `smoke.sh`'s capability loop, so the two cannot part. The
comment above the upward guards already says "a further chart growing one is a
design change made here" and stands.

**The secret half** gains, after `Carrier__ApiKey`:

```yaml
{{- if (.Values.mail).enabled }}
- name: Mail__Password
  valueFrom:
    secretKeyRef:
      name: {{ include "commerce.require" (list (.Values.mail.passwordSecretRef).name "mail.passwordSecretRef.name is required when mail.enabled. The password is a reference, never a value (§15.3).") | quote }}
      key: {{ include "commerce.require" (list (.Values.mail.passwordSecretRef).key "mail.passwordSecretRef.key is required when mail.enabled.") | quote }}
{{- end }}
```

The parenthesised form, so `--set mail.passwordSecretRef=null` with the
capability on is a named refusal rather than a nil-pointer error out of the
template engine.

**The credential pattern reads a qualified type**, which Step 1's self-test
holds it to. Before (`deploy/helm/smoke.sh`, beside `CALLS_REDIS`):

```bash
# The worker's credential attachment, in the same form and for the same
# reason: Shipping.Worker's Program.cs names the handler in a comment too.
ATTACHES_CREDENTIALS='^[[:space:]]*[A-Za-z_][A-Za-z0-9_.]*\.AddHttpMessageHandler<ClientCredentialsHandler>\('
```

After:

```bash
# The credential handler's attachment, in the same form and for the same
# reason, its type named bare or through its namespace: a host's Program.cs
# names the handler in a comment too.
ATTACHES_CREDENTIALS='^[[:space:]]*[A-Za-z_][A-Za-z0-9_.]*\.AddHttpMessageHandler<([A-Za-z_][A-Za-z0-9_]*\.)*ClientCredentialsHandler>\('
```

- [ ] **Step 4: Shipping's chart names the members its host binds**

`deploy/helm/shipping/templates/capabilities.yaml`, after the `jurisdiction`
switch's refusal:

```yaml
{{- range $member := list "addressRetention" "trackingRetention" }}
{{- if not (hasKey $.Values.jurisdiction $member) }}
{{- fail (printf "jurisdiction.%s is required on the shipping chart: ShippingJurisdictionOptions binds it at start (ADR-053), and a member removed outright renders no key at all." $member) }}
{{- end }}
{{- end }}
```

and the comment block's last sentence, "So this chart refuses the switches
themselves (§15.4).", becomes "So this chart refuses the switches, and the
members its host binds (§15.4)." The switch is checked first, so
`$.Values.jurisdiction` exists when the range reads it.

- [ ] **Step 5: Render the existing charts, then run the smoke script**

```bash
helm dependency update deploy/helm/shipping
helm template shipping deploy/helm/shipping --set-string image.tag=test \
    --set-string carrier.baseUrl=https://carrier.example.invalid/ \
    --set-string jurisdiction.addressRetention=30.00:00:00 \
    --set-string jurisdiction.trackingRetention=90.00:00:00 |
    grep -E "Jurisdiction|Fulfilment|Identity__Client"
PYTHON="py -3.12" bash deploy/helm/smoke.sh
```

Expected from the render: `Jurisdiction__AddressRetention: "30.00:00:00"`,
`Jurisdiction__TrackingRetention: "90.00:00:00"`, `Fulfilment__GiveUpAge:
"3.00:00:00"` and the three `Identity__Client__*` lines — Shipping's
ConfigMap byte-identical to `main`'s, which is the point of the jurisdiction
rewrite. Expected from `smoke.sh`: `all assertions passed`, with every case
Step 2 left red now green — the qualified pattern, the seven `mail` refusals,
the new needle and Shipping's removal case — and seven source-loop lines, two
`attaches` and five `attaches no`.

Then prove the removal case is about the guard it names: delete the `range`
Step 4 added, rerun, see `shipping: jurisdiction.trackingRetention removed
outright renders — it must not`, and restore it.

- [ ] **Step 6: Commit**

```bash
git add deploy/helm/common deploy/helm/shipping/templates/capabilities.yaml deploy/helm/smoke.sh
git commit -m "feat(deploy): the library chart's mail, contactSource and delivery capabilities, and a jurisdiction block by ADR-053's kinds"
```

The body argues the jurisdiction block's change of shape and where the member
list moved; names ADR-052's two Helm rows — the `fail` that now names three
charts, and the source assertion that now reads every chart; and says why
`mail` is a credential-bearing capability with an upward guard while
`contactSource` and `delivery` carry no credential and have none.

---

### Task 3: The chart, its descriptor and the umbrella

**Files:**
- Create: `deploy/helm/notifications/Chart.yaml`, `values.yaml`
- Create: `deploy/helm/notifications/templates/{configmap,deployment,hpa,ingress,migrate-job,pdb,service}.yaml`
- Create: `deploy/helm/notifications/templates/capabilities.yaml`
- Create: `deploy/canary/deployables/notifications.json`
- Modify: `deploy/helm/platform/Chart.yaml` — the `notifications` dependency
- Modify: `deploy/helm/smoke.sh` — `in_configmap`, the Notifications
  capability section, `worker_shape notifications`, and one platform-wide
  ConfigMap assertion

**Interfaces:**
- Consumes: Task 2's values blocks, rendered keys, messages and helpers;
  Task 1's overlay alphabet.
- Produces: the release `notifications`, chart `notifications`, workload
  `notifications`, `serviceName` `Notifications.Worker`; the Secrets
  `notifications-database`, `notifications-migrator-secret`,
  `notifications-rabbitmq`, `notifications-identity` and `notifications-mail`.

The chart, its descriptor and the umbrella's dependency are one commit
because `smoke.sh` reconciles each against the others before it renders
anything: a chart without its descriptor fails the first section, a
descriptor without its chart fails `canary.py` check 4, and either without the
umbrella's line fails `platform/Chart.yaml depends on exactly SERVICE_CHARTS`.

- [ ] **Step 1: Write the failing smoke section**

`in_configmap`, beside `count`:

```bash
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
```

A new section after Shipping's:

```bash
# --------------------------------------------------------------------------
section "Notifications' chart declares its capabilities, and each is required"
# --------------------------------------------------------------------------
# The worker reads every key below before it will start (§15.4), so each state
# that renders cleanly here is a pod that never starts. Asserted by placement:
# §15.4 puts the relay's password in a Secret and every other key in Config.
N="$OUT/notifications-capability.yaml"
"$HELM" template notifications "$CHARTS_DIR/notifications" \
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
    "$HELM" "$CHARTS_DIR/notifications" "$TAG" "$(overlay_for notifications)"
check 'notifications: a sender with a display name renders' \
    "$HELM" template notifications "$CHARTS_DIR/notifications" --set-string "image.tag=$TAG" \
    $(overlay_for notifications) --set-string 'mail.from=Commerce <no-reply@commerce.example.invalid>'

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
```

Three notes on cases that look as though they could pass for the wrong
reason. **`refuses_removed notifications jurisdiction.languages`** renders
with the overlay minus its `jurisdiction.languages={en}` entry and with
`--set jurisdiction.languages=null`, which deletes the chart's `[]`, so the
library's `hasKey` branch renders nothing and only the chart's member list can
refuse it. **The two "off and cleared" cases for the relay and the
jurisdiction** clear every member, including the chart's defaults, so the
library's coherence guard has nothing to see and the needle is the chart's
own `on the notifications chart`, as Shipping's cases are built. **The
three-language case** runs the render inside `sh -c` so its own `--set-string`
list, which carries commas, is one argument rather than a descriptor word.

In the *ConfigMap/Secret split* section, after `no ConfigMap carries a client
secret`:

```bash
check 'no ConfigMap carries the relay password' \
    test "$(count 'Mail__Password' "$OUT/configmap-keys.txt")" -eq 0
```

And under `worker_shape shipping`:

```bash
worker_shape notifications
```

- [ ] **Step 2: Run it, expect red**

```bash
PYTHON="py -3.12" bash deploy/helm/smoke.sh
```

Expected: every section before the new one passes, then the run **aborts**
on the section's render: `helm template notifications
"$CHARTS_DIR/notifications"` fails under `set -e` because the directory does
not exist, and `overlay_for notifications` is empty because no descriptor
names the chart. That is the red step.

- [ ] **Step 3: Copy Shipping's chart and rename the workload**

```bash
cp -r deploy/helm/shipping deploy/helm/notifications
rm -rf deploy/helm/notifications/charts deploy/helm/notifications/Chart.lock
```

`deploy/helm/notifications/Chart.yaml`:

```yaml
apiVersion: v2
name: notifications
description: >-
  Notifications (§4.1) — a worker with a migrator hook, no Service and no
  Ingress (§3.2, §15.3), and the two outbound calls §9 gives it: Keycloak for
  a contact and a relay for the send.
type: application
version: 0.1.0
dependencies:
  - name: commerce-common
    version: 0.1.0
    repository: file://../common
```

**The seven one-line templates are Shipping's unchanged**, `service.yaml`,
`ingress.yaml` and `hpa.yaml` included: §15.3 gives a worker the chart minus
the Service and the Ingress **by value**, and `_service.tpl`'s own comment
argues the key is `false` rather than absent.

- [ ] **Step 4: Write `values.yaml`**

Every value and comment naming Shipping is replaced, and every comment is
then read again rather than only those that name it — each says one reason,
cites its owner, and is five lines or fewer. When done,
`grep -n -iE "PR-|#[0-9]|\*\*|shipping|carrier|address" deploy/helm/notifications/values.yaml`
prints nothing.

```yaml
# Notifications, a worker with no front door: §3.2 gives it no API and no
# command, so §15.3's worker shape is the chart — no Service, no Ingress, and
# probes that reach the container port directly.

workload:
  # Named from the service and not the image: the migration Job is
  # `<name>-migrate-<tag>`, and `notifications-worker` would leave a full
  # commit SHA past the budget _migration-job.tpl enforces (ADR-050).
  name: notifications

# Three, and a decision rather than a copy: CPU is the wrong signal for a host
# that waits on a relay and on Keycloak, and it idles through either outage
# alike. Three is for availability across a node drain (§15.3).
replicaCount: 3

# §15.5's canary track, and off is what every ordinary release means. Turned
# on, this chart renders the second of two Deployments, and the stable release
# keeps the names it owns (ADR-022). Set by the rollout, not by an operator.
canary:
  enabled: false

image:
  # Registry namespace only; each workload appends its own name (§7.4).
  registry: registry.example.com/commerce
  # `-worker`, not `-api`: the image is the host, and this host is a worker.
  api: notifications-worker
  migrator: notifications-migrator
  # Supplied by CI, never "latest", and empty rather than defaulted: a deploy
  # that cannot name its tag must fail, not roll something (§15.3).
  tag: ""
  pullPolicy: IfNotPresent

ports:
  # §13.5's health endpoint and nothing else, declared because the kubelet
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

# Off, which is the other half of replicaCount above, and written down rather
# than omitted on service.enabled's terms (§15.3).
autoscaling:
  enabled: false

podDisruptionBudget:
  enabled: true
  minAvailable: 2

# One receive endpoint and the send worker to drain, with a call in flight
# bounded by MailHop's and ContactHop's totals. No number is taken from those
# here: the value must exceed HostOptions.ShutdownTimeout, and _deployment.tpl
# carries the argument.
terminationGracePeriodSeconds: 45

probes:
  probePort: http
  # Readiness is SQL and the bus alone (§13.5): no Service routes here, so it
  # gates a rollout and nothing else, and a relay or Keycloak in it could only
  # block the deploy that fixes them.
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
  # The authority, to validate incoming JWTs (§11.2), and the issuer this host
  # asks for its own token (ADR-052).
  authority: https://id.example.com/realms/commerce
  # True: the worker presents this grant to read a mailbox from Keycloak
  # (ADR-052). The scope is `roles`, whose mapper writes the claim the grant is
  # checked against; this client holds no commerce-api (§15.4).
  clientCredentials: true
  clientId: notifications-worker
  scope: roles
  clientSecretRef:
    # Its own Secret, never another host's: two hosts holding one grant is one
    # host able to act as the other (§11.5).
    name: notifications-identity
    key: client-secret

database:
  enabled: true
  # GetConnectionString("Notifications") at runtime, "NotificationsMigrator"
  # in the hook.
  connectionName: Notifications
  runtimeSecretRef:
    name: notifications-database
    key: connection-string
  migratorSecretRef:
    name: notifications-migrator-secret
    key: connection-string

# The bus (§9), one Secret per service: a connection string for the
# `notifications-svc` account, whose permissions definitions.json declares and
# check_permissions.py holds to the code (ADR-036). Provisioning it on a
# deployed broker is an obligation stated and not checked (§15.4).
broker:
  enabled: true
  secretRef:
    name: notifications-rabbitmq
    key: connection-string

# No Redis (§8.1): the send worker claims rows under a lease in SQL, nothing
# is cached, and no HTTP command takes a §8.5 key. Declared rather than
# omitted (§15.3).
redis:
  enabled: false

# The relay, a processor with a country (ADR-053), so its host, sender and user
# name are the deployment's to state and have no default. 587 is the
# submission port and StartTls the one security the render admits; the
# password is a Secret reference, never a value (§15.4).
mail:
  enabled: true
  host: ""
  port: 587
  from: ""
  security: StartTls
  userName: ""
  passwordSecretRef:
    name: notifications-mail
    key: password

# ADR-052's contact read. Keycloak can serve its admin API on a hostname of its
# own, so the base is the deployment's to state rather than derived from the
# authority; the realm must be the authority's, and the render refuses it
# otherwise.
contactSource:
  enabled: true
  baseUrl: ""
  realm: commerce

# ADR-053's three kinds — a language set, the zone dates are rendered in, and
# the statutory windows — none defaulted: each is a fact about where a
# deployment runs, refused rather than guessed, at render and again at start.
jurisdiction:
  enabled: true
  languages: []
  timeZone: ""
  logRetention: ""
  contactRetention: ""
  orderRetention: ""

# ADR-052's give-up age: how long a notification stays Pending before it is
# given up. Defaulted, because a notice a day late is worse than none in every
# cluster; the host refuses one past the inbox window at start.
delivery:
  enabled: true
  giveUpAge: "1.00:00:00"

observability:
  otlpEndpoint: http://otel-collector.observability:4317

extraConfigMaps: []
```

The grep above excludes `address` because `contactSource`'s comment names
none and a copied `addressSource` block is the likeliest leftover.
`terminationGracePeriodSeconds` is Shipping's 45 for Shipping's reason: the
grace period is held above `HostOptions.ShutdownTimeout` by `smoke.sh`, and
the pass that must fit inside the drain is the send worker's (spec section
4). `notifications-migrate-` plus a forty-character SHA is 62 characters, so
the Job name fits the 63 that `_migration-job.tpl` and `canary.py
validate-tag --workload` enforce.

- [ ] **Step 5: Write `templates/capabilities.yaml`**

Shipping's file is replaced whole:

```yaml
{{- /*
Notifications registers each of these unconditionally, so on this chart every
one is a fact about the code: switched off with its settings cleared, the
library chart's coherence guard has nothing to refuse and the pod does not
start. So this chart refuses the switches, and the members its host binds.
*/}}
{{- if not (.Values.mail).enabled }}
{{- fail "mail.enabled is false on the notifications chart. The worker registers its relay unconditionally and does not start without its keys (§15.4)." }}
{{- end }}
{{- if not (.Values.contactSource).enabled }}
{{- fail "contactSource.enabled is false on the notifications chart. The worker reads ADR-052's contact source at startup and does not start without it." }}
{{- end }}
{{- if not (.Values.jurisdiction).enabled }}
{{- fail "jurisdiction.enabled is false on the notifications chart. NotificationsJurisdictionOptions is bound and validated at start (ADR-053), so the host does not start without its language set, zone and windows." }}
{{- end }}
{{- range $member := list "languages" "timeZone" "logRetention" "contactRetention" "orderRetention" }}
{{- if not (hasKey $.Values.jurisdiction $member) }}
{{- fail (printf "jurisdiction.%s is required on the notifications chart: NotificationsJurisdictionOptions binds it at start (ADR-053), and a member removed outright renders no key at all." $member) }}
{{- end }}
{{- end }}
{{- if not (.Values.delivery).enabled }}
{{- fail "delivery.enabled is false on the notifications chart. DeliveryOptions is bound and validated at start (ADR-052), so the host does not start without a give-up age." }}
{{- end }}
{{- if not .Values.identity.clientCredentials }}
{{- fail "identity.clientCredentials is false on the notifications chart. The worker binds ServiceIdentityOptions with ValidateOnStart (ADR-052) and does not start without all three values (§15.4)." }}
{{- end }}
```

- [ ] **Step 6: The descriptor**

`deploy/canary/deployables/notifications.json`:

```json
{
  "serviceName": "Notifications.Worker",
  "chart": "notifications",
  "source": "src/Services/Notifications",
  "signals": ["consume"],
  "httpExemption": "Notifications has no HTTP surface at all (§3.2): its chart renders no Service, no route reaches it, and its only listener is §13.5's health endpoint, which the http templates exclude by route. A declared http signal would read a series that is empty by construction, and an empty result rolls every rung back, so the rollout could only ever fail. Its seven events arrive on the broker and are judged by consume. The send worker is where this service's risk sits and it is no consumer, so the contact read and the relay carry no canary signal of their own — notifications.waiting, notifications.mail.unavailable and notifications.contact.refused are what watch them, outside the rollout, and docs/runbooks/queue-backlog.md says what neither sees.",
  "smoke": {
    "migrator": true,
    "autoscaled": false,
    "capabilities": ["mail", "clientCredentials"],
    "overlay": [
      "mail.host=relay.example.invalid",
      "mail.from=no-reply@commerce.example.invalid",
      "mail.userName=notifications",
      "contactSource.baseUrl=https://id.example.invalid/",
      "jurisdiction.languages={en}",
      "jurisdiction.timeZone=Europe/London",
      "jurisdiction.logRetention=2190.00:00:00",
      "jurisdiction.contactRetention=30.00:00:00",
      "jurisdiction.orderRetention=90.00:00:00"
    ]
  }
}
```

The release is `notifications`, the chart's own name, as Shipping's is
`shipping`: the key is what `deploy.yml`'s `workload` input takes and what the
migration Job's budget is computed from. `serviceName` is the entry assembly
§13.2 takes `service.name` from. `consume` because PR-4's consumers register
with `AddConsumer`, which check 9 finds in the service's tree; no
`consumeExemption` and no `sagaExemption`, since each would be refused beside
a declared signal or on a service with nothing to exempt. **ADR-047's analysis
needs no change**: it judges a workload on the signals it declares, names no
workload, and a fourth `consume`-judged deployable with an `httpExemption` is
the case it already decides. **The first rung is not expressible at three
replicas** and the rollout scales the stable track before anything rolls, with
no HPA floor to raise — Shipping's case, which `deploy.yml` already handles.

`capabilities` lists the two **credential-bearing** capabilities, in
`smoke.sh`'s vocabulary; `contactSource`, `jurisdiction` and `delivery` carry
no credential, so the library has no upward guard for them and they are not
listed — on another chart they render keys its host never reads, the same as
Shipping's `addressSource` would. The overlay holds the values with no default:
`mail.port`, `mail.security`, `contactSource.realm` and `delivery.giveUpAge`
are defaulted in `values.yaml`. The windows are made up — ADR-053 rule 2's
fixture is the suite's; these are a render's — and `contactRetention` sits
above `ContactOptions`' 24-hour stale ceiling, which the host refuses to be
under.

- [ ] **Step 7: The umbrella**

`deploy/helm/platform/Chart.yaml`, after `shipping`, in Appendix C.1's order:

```yaml
  - name: notifications
    version: 0.1.0
    repository: file://../notifications
```

`platform/values.yaml` needs nothing: it holds `{}` and argues why, and
`smoke.sh`'s `PLATFORM_SETS` already passes every descriptor's overlay under
its chart's name.

- [ ] **Step 8: Render it, then run every gate that reads it**

```bash
helm dependency update deploy/helm/notifications
helm template notifications deploy/helm/notifications --set-string image.tag=test \
    --set-string mail.host=relay.example.invalid \
    --set-string mail.from=no-reply@commerce.example.invalid \
    --set-string mail.userName=notifications \
    --set-string contactSource.baseUrl=https://id.example.invalid/ \
    --set-string 'jurisdiction.languages={en,kk,ru}' \
    --set-string jurisdiction.timeZone=Europe/London \
    --set-string jurisdiction.logRetention=2190.00:00:00 \
    --set-string jurisdiction.contactRetention=30.00:00:00 \
    --set-string jurisdiction.orderRetention=90.00:00:00 |
    grep -E "^kind:|name: notifications|image:|Mail__|ContactSource|Jurisdiction|Delivery|Identity__Client|Redis"
PYTHON="py -3.12" bash deploy/helm/smoke.sh
py -3.12 -m unittest discover -s deploy/canary
py -3.12 deploy/canary/canary.py check
py -3.12 deploy/canary/canary.py validate-tag --value 0000000000000000000000000000000000000000 --workload notifications
py -3.12 -m unittest discover -s deploy/keycloak
```

Expected from the render: `kind: ConfigMap`, `kind: Deployment`, `kind: Job`
and `kind: PodDisruptionBudget`, and **no** `Service`, `Ingress` or
`HorizontalPodAutoscaler`; `notifications` as the workload and
`notifications-migrate-test` as the Job; the five `Mail__*` Config keys,
`ContactSource__BaseUrl`, `ContactSource__Realm: "commerce"`,
`Jurisdiction__Languages__0` to `__2`, `Jurisdiction__TimeZone`, the three
windows sorted (`ContactRetention`, `LogRetention`, `OrderRetention`) and
`Delivery__GiveUpAge: "1.00:00:00"` in the ConfigMap; `Mail__Password` and
`Identity__Client__ClientSecret` as `secretKeyRef`s; no `Redis` line.

Expected from `smoke.sh`: `all assertions passed` — in particular the first
section naming eight charts, both partitions placing `notifications` among the
migrators and the fixed-replica charts, the credential set naming
`notifications shipping web-bff` from the values files and the descriptors
alike, `notifications attaches ClientCredentialsHandler in src/` through the
qualified pattern, `mail renders on notifications`, `mail is refused on
<chart>` for the other seven, the three credentialed workloads holding three
different Secrets in the umbrella render, `notifications calls no
AddRedisConnections in src/, so its chart declares no redis`, its probes, its
grace period, its migration hook, its canary track, the worker shape, and the
new section whole. `canary.py check` accepts the plan: check 4 resolves
`Notifications.Worker` and the chart, check 8 finds no hand list, check 9
finds the consumers PR-4 registered. `validate-tag` exits 0 at a full SHA.
`deploy/keycloak/test_realm_check.py`'s subject test over every deployable
chart's `identity.authority` reads the new values file, finds the authority
under `identity:`, and `split_authority` accepts it.

Prove the source loop reads Notifications rather than passing by the descriptor
alone: delete `"clientCredentials"` from the descriptor's `capabilities`, rerun
`smoke.sh`, see `notifications attaches ClientCredentialsHandler in src/, so
its descriptor declares clientCredentials` fail beside the values-file
comparison, and restore it.

- [ ] **Step 9: Commit**

```bash
git add deploy/helm/notifications deploy/helm/platform/Chart.yaml \
        deploy/canary/deployables/notifications.json deploy/helm/smoke.sh
git commit -m "feat(deploy): Notifications' chart, its descriptor and the umbrella's dependency"
```

The body argues the four decided values (`service.enabled`,
`ingress.enabled`, `redis.enabled` and `autoscaling.enabled` false, three
replicas), the overlay's contents against `values.yaml`'s defaults, why the
three files are one commit, and that the descriptor is the whole of the
deploy target because `deploy.yml` reads the descriptors.

---

### Task 4: What the umbrella's caller reads, and the sentences a third worker makes false

**Files:**
- Modify: `deploy/helm/README.md` — the tree fence, the umbrella command, the
  required-values paragraph and the environment example
- Modify: `deploy/helm/web-bff/values.yaml` — its first comment block
- Modify: `deploy/helm/smoke.sh` — one comment line
- Modify: `deploy/canary/canary.json` — one `$comment` line
- Modify: `deploy/canary/test_canary.py` — one docstring line

- [ ] **Step 1: The README the umbrella's caller reads**

The tree fence. Before:

```
common/      the library chart: every template, once
catalog/     ┐
ordering/    │
inventory/   │ Chart.yaml + values.yaml + one-line templates that include
payments/    │ the library's. The values ARE the per-service decisions.
shipping/    │ Payments and Shipping each carry one template more: the guard
web-bff/     ┘ on the capabilities their hosts register unconditionally.
gateway/     the same, plus edge-config.yaml — the two keys no service has
             (§15.3), in a template only this chart carries
platform/    the umbrella — one dependency per service chart and no values
             of its own
smoke.sh     renders every chart and the umbrella and asserts what comes out
```

After — the column widens by two for the longest name, and the note stops
listing the charts it describes:

```
common/        the library chart: every template, once
catalog/       ┐
ordering/      │
inventory/     │ Chart.yaml + values.yaml + one-line templates that include
payments/      │ the library's. The values ARE the per-service decisions.
shipping/      │ A chart whose host registers a capability unconditionally
notifications/ │ carries one template more: the guard on it.
web-bff/       ┘
gateway/       the same, plus edge-config.yaml — the two keys no service has
               (§15.3), in a template only this chart carries
platform/      the umbrella — one dependency per service chart and no values
               of its own
smoke.sh       renders every chart and the umbrella and asserts what comes out
```

The umbrella `helm upgrade` example gains, after Shipping's line:

```bash
    --set-string notifications.image.tag="$NOTIFICATIONS_SHA" \
```

because the command as printed fails the umbrella's required-tag check the
moment it has the dependency. The paragraph that opens "**Payments' provider
address is required and is deliberately NOT on that command line.**" gains,
after its Shipping sentence:

> Notifications' relay, sender and relay user, Keycloak's admin base, and its
> language set, zone and windows are the same case: the relay is a processor
> the deployment chooses and the rest are facts about where it runs
> (ADR-053), and the language set is a list, which is the other reason it
> belongs in a values file rather than behind `--set-string`'s comma.

The `environments/staging.yaml` fence gains, after `shipping:`'s block:

```yaml
notifications:
  mail:
    # The relay this deployment submits to, a processor with a country
    # (ADR-053). Its password is the notifications-mail Secret.
    host: smtp.staging.example.com
    from: "Commerce <no-reply@staging.example.com>"
    userName: notifications
  contactSource:
    # Keycloak's admin base; its realm is the authority's, which the chart
    # refuses to differ.
    baseUrl: https://id.example.com/
  jurisdiction:
    # ADR-053: the deployment's to state, and the chart ships none of them.
    languages: [ "en", "kk", "ru" ]
    timeZone: Asia/Almaty
    logRetention: "2190.00:00:00"
    contactRetention: "30.00:00:00"
    orderRetention: "90.00:00:00"
```

`baseUrl` names the authority's host because the fence overrides no
`identity.authority`, and an example whose two Keycloak addresses disagree
would be read as a rule.

- [ ] **Step 2: The BFF chart's opening comment**

`deploy/helm/web-bff/values.yaml`. Before:

```yaml
# The charts whose host calls a peer carry client credentials and no other
# chart does; which charts those are is the design rather than an oversight
# (§15.3, ADR-052).
```

After:

```yaml
# The charts whose host calls out under a grant of its own carry client
# credentials and no other chart does; which charts those are is the design
# rather than an oversight (§15.3, ADR-052).
```

Notifications' worker holds a grant and calls Keycloak, which is no peer, so
the old rule names a set its own chart is outside. The four charts whose
`identity:` comment says "Identity:Client is what a host presents when it
calls a peer" are not edited: that is a sufficient condition and still true,
a restatement met in passing.

- [ ] **Step 3: "The worker's chart", in the three places that say it**

`deploy/canary/canary.json`'s `$comment` says "The worker's chart sets a
replica count with no autoscaler at all", which reads as one chart. Before:

```json
    "allows. The worker's chart sets a replica count with no autoscaler at",
```

After:

```json
    "allows. A worker's chart sets a replica count with no autoscaler at",
```

`deploy/canary/test_canary.py`'s `test_five_percent_is_expressible_at_nineteen`
docstring, before:

```python
        request passes through it, and the worker's sets a replica count
```

after:

```python
        request passes through it, and a worker's sets a replica count
```

The docstring stays five lines, which the comment gate judges whole.

`deploy/helm/smoke.sh`'s comment over `AUTOSCALED_CHARTS` opens "One chart
sets a replica count instead of an autoscaler (§15.3), so the", which
Notifications' `"autoscaled": false` makes two. Before:

```bash
# One chart sets a replica count instead of an autoscaler (§15.3), so the
```

After, the block still five lines:

```bash
# A worker chart sets a replica count instead of an autoscaler (§15.3), so the
```

§15.5 and `deploy.yml`'s first-rung comment already say "a worker" and move
nothing.

- [ ] **Step 4: Run; commit**

```bash
py -3.12 -m unittest discover -s deploy/canary
py -3.12 deploy/canary/canary.py check
PYTHON="py -3.12" bash deploy/helm/smoke.sh
```

Expected: all green and unchanged from Task 3 — `canary.json` still parses, the
suite changed one docstring line, and `smoke.sh` changed one comment line and
reads no comment.

```bash
git add deploy/helm/README.md deploy/helm/web-bff/values.yaml deploy/helm/smoke.sh deploy/canary/canary.json deploy/canary/test_canary.py
git commit -m "docs: the Helm README names Notifications' required values, and three sentences stop counting one worker"
```

---

### Task 5: The shared runbook's Notifications half, and the two gauges' panels

**Files:**
- Modify: `docs/runbooks/queue-backlog.md` — the replica paragraph, and the
  Notifications half of *What this does not cover*
- Modify: `docs/runbooks/latency.md` — the slow-peer branch's contact read
- Modify: `deploy/observability/dashboards/outbox.json` — the `service`
  variable's query, and a row with the waiting and overdue gauges' panels

**Interfaces:**
- Consumes: PR-5's `notifications.waiting` (series `notifications_waiting`,
  attribute `step` ∈ `order_record`, `contact`, `relay`) and
  `notifications.overdue` (series `notifications_overdue_seconds`, unit `s`);
  PR-2's
  `notifications.mail.unavailable` (`notifications_mail_unavailable_total`,
  attribute `cause`); PR-3's `notifications.contact.refused`
  (`notifications_contact_refused_total`); `DeliveryOptions.GiveUpAge` and the
  `Undeliverable: gave_up` outcome (spec sections 4 and 5).

**What needs no edit, verified rather than assumed.** `DeliveryLagHigh`
groups `messaging_delivery_lag_seconds_bucket` `by (service_name, le)` with no
selector, and `QueueBacklogGrowing` reads `rabbitmq_queue_messages{queue!~".+_(error|skipped)"}`,
which `notifications-events` matches; neither names a service or a queue, so
both cover Notifications the day its queue is declared (spec section 12).
`check.py`'s `SHARED_RUNBOOKS` entry for `queue-backlog.md` is the pair's and
stays; no rule is added over the gauge, by the spec's own argument that a
growing waiting set during a relay outage is the breaker working; and check 8's
outbox-gauge rule is PR-1's, which taught it a service with no dispatcher.
The lag rule sees Notifications only because its consumers are
`IntegrationEventConsumer<T>`, closed over each event, which is what calls
`MessagingMetrics.Delivered` — Step 4 checks that rather than trusting it.

**The board's service variable, which spec section 12 now names.** `outbox.json`'s
`service` variable is `label_values(outbox_pending_count, service_name)`, and
every panel on the board filters `service_name=~"$service"`. A pure consumer
publishes no outbox gauge (spec section 1), so `Notifications.Worker` is not
among the variable's values, and under *All* the existing *Event end-to-end
p95* panel — the very delivery-lag number the runbook sends a reader to —
never shows it. The variable is widened to the services that publish either
series, so the board's lag panel and the two new gauges' panels all reach it.

- [ ] **Step 1: The two panels, and see the gate hold them**

`deploy/observability/dashboards/outbox.json`. The `service` variable's
`query`, before:

```json
        "query": "label_values(outbox_pending_count, service_name)",
```

after:

```json
        "query": "label_values({__name__=~\"outbox_pending_count|messaging_delivery_lag_seconds_count\"}, service_name)",
```

and after the last panel (`"id": 10`, *Commands the domain refused*, which
ends at `y` 27 plus `h` 8), a row and two panels side by side:

```json
  {
    "id": 11,
    "type": "row",
    "title": "Work a worker keeps in its own tables — what delivery lag cannot see",
    "gridPos": { "h": 1, "w": 24, "x": 0, "y": 35 }
  },
  {
    "id": 12,
    "type": "timeseries",
    "title": "Notifications waiting, by step",
    "description": "notifications.waiting — Pending rows past their first backoff, by the step they wait on: order_record, contact or relay. Delivery lag stops when a consumer starts, and Notifications' consumers only write a row, so this is the send worker's wait that the panel above cannot see. Every replica reads the same table, so the series are combined with max, not sum. A rise on relay during an outage is the breaker parking the queue of rows, not a fault; docs/runbooks/queue-backlog.md says how to read each step.",
    "datasource": { "type": "prometheus", "uid": "${datasource}" },
    "gridPos": { "h": 8, "w": 12, "x": 0, "y": 36 },
    "fieldConfig": { "defaults": { "unit": "short" }, "overrides": [] },
    "targets": [
      {
        "expr": "max by (service_name, step) (notifications_waiting{service_name=~\"$service\"})",
        "legendFormat": "{{service_name}} — {{step}}"
      }
    ]
  },
  {
    "id": 13,
    "type": "timeseries",
    "title": "Notifications overdue — the send worker's capacity",
    "description": "notifications.overdue — how long Pending rows due for a pass have gone unclaimed past two ticks, in shipping.shipments.overdue's form. The waiting gauge beside it counts only rows a pass has already backed off, so a row no pass has reached is visible here alone. Healthy, it stays near zero; one that climbs with the relay and Keycloak answering is too few replicas, and the fix is replicaCount in deploy/helm/notifications/values.yaml (§15.3).",
    "datasource": { "type": "prometheus", "uid": "${datasource}" },
    "gridPos": { "h": 8, "w": 12, "x": 12, "y": 36 },
    "fieldConfig": { "defaults": { "unit": "s" }, "overrides": [] },
    "targets": [
      {
        "expr": "max by (service_name) (notifications_overdue_seconds{service_name=~\"$service\"})",
        "legendFormat": "{{service_name}}"
      }
    ]
  }
```

The row carries the four keys the board's other rows carry and nothing more.
The existing panels' JSON is formatted one key per line; these three follow
that form when written into the file — the compact objects above are for
reading here, and `json.load` reads either. Then:

```bash
py -3.12 deploy/observability/check.py
```

Expected: exit 0, check 6 finding `notifications_waiting` and
`notifications_overdue_seconds` among the series `declared_instruments()`
derives from PR-5's two `CreateObservableGauge` calls — the second through
`exported_series`' `s` → `_seconds` suffix. Prove the check reads each new
panel: change panel 12's metric to `notifications_waitin`, rerun, see
`outbox.json: a panel reads \`notifications_waitin\`, which no C# instrument
…` and exit 1, restore it, and do the same with panel 13's as
`notifications_overdue`, which a duration gauge does not export.

- [ ] **Step 2: The runbook's Notifications half**

`docs/runbooks/queue-backlog.md`, *Mitigation before diagnosis*. Before:

```markdown
**A worker's replica count is a chart value and not an autoscaler's.**
[§15.3](../backend-architecture/15-cicd-deployment.md) gives Shipping
`autoscaling.enabled: false`, so scaling it out is `replicaCount` in
`deploy/helm/shipping/values.yaml` rather than a `kubectl scale` that the next
deploy undoes.
```

After:

```markdown
**A worker's replica count is a chart value and not an autoscaler's.**
[§15.3](../backend-architecture/15-cicd-deployment.md) gives each worker's
chart `autoscaling.enabled: false`, so scaling one out is `replicaCount` in its
own `deploy/helm/<chart>/values.yaml` rather than a `kubectl scale` that the
next deploy undoes.
```

*What this does not cover*, after the paragraph on the overdue gauge that ends
"…as the paragraph on a worker's replica count above says." and before
"**The lag alert is measured at consumer start, and a failure can raise
it.**":

````markdown
**Notifications is the same shape, and its lag ends even earlier.** Its seven
consumers write a `Pending` row in `NotificationLog` — and, for Ordering's
three events, the order record — and acknowledge. Everything that leaves the
service, the contact read and the send, is the send worker's. So
`messaging.delivery.lag` for `Notifications.Worker` stops when a consumer
starts and never sees the worker's wait on Keycloak or on the relay, and
`notifications-events` stays shallow while every notification waits behind a
relay that is down: its breaker parks the queue of rows rather than the
messages, so nothing piles up on the broker and nothing reaches `_error`.

Its signal is `notifications.waiting`, the gauge of `Pending` rows past their
first backoff, by the `step` they wait on. Read the step before anything else:

- **`order_record`** — the event reached Notifications before its order's
  `OrderPlaced`, which §9.4 does not order, or a decline is waiting for the
  cancellation ADR-049 makes it read. Look upstream first: Ordering's outbox,
  and whether the Ordering events are arriving on `notifications-events` at
  all.
- **`contact`** — Keycloak is unreachable or is refusing this host's grant.
  `notifications.contact.refused` rising says it is refusing, which is a
  credential to fix — [`docs/secrets.md`](../secrets.md)'s client-secret
  procedure — not an outage to wait out. With Keycloak healthy, the step also
  counts a row this version cannot render: the worker's error line "stores
  parameters this version cannot read" names it, and it waits for a replica
  that can read it or for `DeliveryOptions.GiveUpAge`.
- **`relay`** — the relay is down or refusing. `notifications.mail.unavailable`
  by `cause` tells an outage (`transient`, `unconfirmed`) from somebody's
  decision (`tls`, `credential`, `rejected`), which backs off and waits for a
  fix rather than clearing on its own.

```promql
max by (step) (notifications_waiting)

sum by (cause) (rate(notifications_mail_unavailable_total[10m]))

sum(rate(notifications_contact_refused_total[10m]))
```

`max` and not `sum`, because every replica reads the same table and reports
the same rows. **Waiting has an end**: a row `Pending` past
`DeliveryOptions.GiveUpAge` becomes `Undeliverable: gave_up`, so a waiting set
that falls without the dependency recovering is notifications given up rather
than sent, and the rows' `Reason` says which. A rising `order_record` alone
during an Ordering backlog is the one step that clears itself when upstream
does.

**The overdue gauge is the one that says three replicas are too few.**
`notifications.overdue` is how long rows due for a pass have gone unclaimed
past two ticks, in `shipping.shipments.overdue`'s form, and it sees the row no
pass has reached yet, which the waiting gauge cannot: that one counts only
rows a pass has already backed off. Healthy, it stays near zero. One that
climbs while `notifications.mail.unavailable` and
`notifications.contact.refused` are flat is a send worker that cannot keep up
with its population, and the answer is `replicaCount`, as the paragraph on a
worker's replica count above says; one that climbs with them is the relay or
Keycloak holding every pass to its budget, so read those first.

```promql
max(notifications_overdue_seconds)
```
````

The block is fenced with four backticks here because it holds a `promql`
fence of its own; the runbook takes it with ordinary three-backtick fences
around the PromQL alone.

- [ ] **Step 3: The slow-peer branch points at it**

`docs/runbooks/latency.md`, *A slow peer*. Before, its last sentence:

```markdown
merely slow. Shipping's worker calls Ordering for an address (ADR-052), and
a slow answer there shows up as a shipment that has not booked rather than
as latency: nothing is waiting on it, and the row backs off.
```

After:

```markdown
merely slow. Shipping's worker calls Ordering for an address (ADR-052), and
a slow answer there shows up as a shipment that has not booked rather than
as latency: nothing is waiting on it, and the row backs off. Notifications'
worker reads a mailbox from Keycloak on the same terms, and a slow answer
there is a notification waiting at `contact` on `notifications.waiting`,
which [`queue-backlog.md`](queue-backlog.md) reads.
```

PR-3's plan leaves this sentence to "the runbook half PR-6 owes", and it is the
one place an on-call already looking at latency would otherwise not be sent.

- [ ] **Step 4: Run the gate, and check what it cannot**

```bash
py -3.12 deploy/observability/check.py
py -3.12 -c "import json; json.load(open('deploy/observability/dashboards/outbox.json', encoding='utf-8'))"
grep -c "AddConsumer<IntegrationEventConsumer<" src/Services/Notifications/Notifications.Infrastructure/Messaging/DependencyInjection.cs
grep -rn -A3 '"notifications.waiting"\|"notifications.overdue"' src/Services/Notifications
```

Expected: `check.py` exits 0 — checks 1, 2 and 9 unchanged, because no rule,
runbook file or chapter table moved; check 6 green over the two new panels. The
JSON loads. The first `grep` prints `7`, PR-4's seven registrations, the second
PR-5's two gauges, each name a literal at the `Create` call, and the overdue
gauge's `unit: "s"` within the lines after it. **If the first prints `0`,
stop**: Notifications records no delivery lag and the runbook's first paragraph
is false; that is a defect in PR-4 to report, not a sentence to soften here.

- [ ] **Step 5: Commit**

```bash
git add docs/runbooks/queue-backlog.md docs/runbooks/latency.md deploy/observability/dashboards/outbox.json
git commit -m "docs(runbook): queue-backlog gains Notifications' half, and the outbox board its waiting and overdue gauges"
```

The body argues why no rule is added over either gauge, why the panels
combine with `max`, which of the two says the replica count is too small, and
why the board's service variable had to stop deriving from an
outbox gauge a pure consumer does not publish.

---

### Task 6: §15.1, §15.3 and §15.4

**Files:**
- Modify: `docs/backend-architecture/15-cicd-deployment.md`

- [ ] **Step 1: §15.1's credential sentence — ADR-052's row, spec section 14**

§15.1 describes what `smoke.sh` asserts, and names the set by a rule
Notifications' worker is outside: it holds a grant and calls Keycloak, which
is no peer. Before:

> §15.4, and a client secret on each of the charts whose host calls a peer and
> on none of the others (§11.5,
> [ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)).

After:

> §15.4, and a client secret on each of the charts whose host calls out under
> a grant of its own and on none of the others (§11.5,
> [ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)).

The phrase is PR-3's, which put it in §9.7, §14.2 and §15.4's rows; one
spelling of the rule across the chapters is what lets a reader search for it.
Rewrap the paragraph at 80 columns and change nothing else in it.

- [ ] **Step 2: §15.3's worker paragraph**

Spec section 14 says §15.3's "sentence giving Notifications a chart with no
Service is already true, and its Redis paragraph lists no charts": "**Shipping
and Notifications get a service chart with no Service and no Ingress.**"
stands, and the Redis paragraph says "Which charts those are is not listed
here", deferring to `smoke.sh`'s source read in both directions, which Task
3's run covers for `notifications`. Neither moves. The paragraph
that does name one worker is the replica count's. Before:

> **A worker's replica count is a decision and not a copy.** CPU utilisation is
> the wrong signal for a host that waits on a queue and on a third party — it
> idles through a carrier outage and through a backlog alike — so Shipping's
> chart sets `autoscaling.enabled: false` and `replicaCount: 3`, three for
> availability across a node drain. **What says three is too few is
> `shipping.shipments.overdue`**, not the queue: §13.6's queue-backlog rule
> watches Shipping's receive endpoint, and its consumers only write a row — a
> `Pending` shipment, or a cancellation — so the replica-bound work is the
> fulfilment and tracking workers'. The gauge is the wait of the longest-due row
> each pass would claim, by pass, and
> [`queue-backlog.md`](../runbooks/queue-backlog.md) says how to read it.

After:

> **A worker's replica count is a decision and not a copy.** CPU utilisation is
> the wrong signal for a host that waits on a queue and on a third party — it
> idles through a carrier's or a relay's outage and through a backlog alike —
> so each worker's chart, Shipping's and Notifications', sets
> `autoscaling.enabled: false` and `replicaCount: 3`, three for availability
> across a node drain. **What says three is too few is
> `shipping.shipments.overdue` for Shipping and `notifications.overdue` for
> Notifications**, not the queue: §13.6's queue-backlog rule watches each
> service's receive endpoint, and each one's consumers only write a row — a
> `Pending` shipment or a cancellation, a `Pending` notification or an order
> record — so the replica-bound work is the workers': Shipping's fulfilment and
> tracking passes, and Notifications' send worker. Each gauge is the wait of
> the rows a pass is due to claim and has not — Shipping's by pass,
> Notifications' for its one — and `notifications.waiting`, by the step a row
> waits on, is the dependency's wait beside it rather than the pass's.
> [`queue-backlog.md`](../runbooks/queue-backlog.md) says how to read them.

The sentences after it — `smoke.sh`'s partition and §15.5's first rung — are
unchanged.

- [ ] **Step 3: §15.3's credential paragraph, its fence and its callout**

Before:

> The charts whose host calls a peer carry client credentials and no other
> chart does, and which charts those are is the design rather than an oversight
> ([ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)):

After:

> The charts whose host calls out under a grant of its own carry client
> credentials and no other chart does, and which charts those are is the design
> rather than an oversight
> ([ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)):

The fence's label line, before:

```yaml
# deploy/helm/web-bff/values.yaml — one of the two charts with an Identity:Client
```

after:

```yaml
# deploy/helm/web-bff/values.yaml — one of the charts with an Identity:Client
```

The rest of the fence is the BFF's block and true of it. The callout under
it, before:

> **A further chart setting `identity.clientCredentials: true` is a design
> change, not a configuration change.** It means another host started calling a
> peer synchronously, which is ADR-017's budget being spent — so the review
> question is not "does the secret exist" but "why is this call not an event".
> ADR-052 is where that question was answered for Shipping's worker, so a
> review of this chart cites that record rather than arguing it again.

After:

> **A further chart setting `identity.clientCredentials: true` is a design
> change, not a configuration change.** It means another host started calling
> out under a grant of its own, which is ADR-017's budget being spent — so the
> review question is not "does the secret exist" but "why is this call not an
> event". ADR-052 is where that question was answered for each worker it gives
> a read, so a review of those charts cites that record rather than arguing it
> again.

- [ ] **Step 4: §15.4's rows, which the chart makes concrete**

PR-2's six `Mail__*` rows name no Helm key, as the provider's two rows do not;
the chart is what gives each one, and §15.4's later rows — the carrier's,
the contact source's — carry the spelling. Before, the six rows' *Source*
cells read `ConfigMap` and `External Secrets`. After:

```markdown
| `Mail__Host` | Config | Helm `mail.host` → ConfigMap | ✓ — **Notifications only**; the relay's host name, and the host refuses to start without it |
| `Mail__Port` | Config | Helm `mail.port` → ConfigMap, defaulted in the chart | ✓ — **Notifications only**; the relay's submission port |
| `Mail__From` | Config | Helm `mail.from` → ConfigMap | ✓ — **Notifications only**; the one sender every message carries, and the domain each `Message-ID` is minted under |
| `Mail__Security` | Config | Helm `mail.security` → ConfigMap, which admits `StartTls` alone | ✓ — **Notifications only**; `StartTls` or `None`, and `None` refuses to start outside Development |
| `Mail__UserName` | Config | Helm `mail.userName` → ConfigMap | ✓ **outside Development** — **Notifications only**; set with the password or not at all |
| `Mail__Password` | Secret | Helm `mail.passwordSecretRef` → External Secrets | ✓ **outside Development** — **Notifications only**; the relay's credential, and absent in Compose, where the sink takes unauthenticated submission |
```

Only the *Source* column moves; each row's last cell is PR-2's word for word.

**PR-4's five jurisdiction rows and PR-5's give-up age row need no edit**:
each already names the Helm key this chart renders — `jurisdiction.languages`
through `jurisdiction.orderRetention`, and `delivery.giveUpAge` defaulted in
the chart — as Shipping's jurisdiction and fulfilment rows do.
`Jurisdiction__Languages__0…n` is §15.4's own spelling of a list, as
`Cors__Origins__0…n` and `Ingress__TrustedNetworks__0…n` are.

The paragraph that opens "**Every options type in the solution had to earn
it.**" is PR-2's, PR-4's and PR-5's to extend with `Mail`, Notifications'
`Jurisdiction` and `Delivery` (spec section 14), and this PR does not touch it.
If any of the three is missing when this PR is rebased, that is a gap in the
PR that bound the type, reported rather than filled here.

- [ ] **Step 5: Audit; commit**

Run `/check-links` and `/validate-blueprint`.

```bash
git add docs/backend-architecture/15-cicd-deployment.md
git commit -m "docs: §15.1 and §15.3 name the charts that call out under a grant, and §15.4's relay rows their Helm keys"
```

The body says which of ADR-052's rows this discharges — §15.1, and with
Task 2 the two asserted Helm rows — and why §15.3's no-Service sentence and
its Redis paragraph needed no edit, as spec section 14 says.

---

### Task 7: Verification and the PR

- [ ] **Every gate this PR touches, run in full**

```bash
PYTHON="py -3.12" bash deploy/helm/smoke.sh
py -3.12 -m unittest discover -s deploy/canary
py -3.12 deploy/canary/canary.py check
py -3.12 -m unittest discover -s deploy/keycloak
py -3.12 deploy/observability/check.py
py -3.12 -m unittest discover -s .github/secret-scan
py -3.12 .github/secret-scan/secret_scan.py
py -3.12 -m unittest discover -s .github/comment-gate
git fetch origin main
py -3.12 .github/comment-gate/comment_gate.py --base origin/main
```

Expected: all green. The secret scan reads the new values file, the
descriptor and `smoke.sh`'s `enable_args`, and finds no credential-shaped
literal: every Secret is a reference (`notifications-mail`,
`notifications-identity`) and every credential-named key is followed by a
reference or nothing. The comment gate judges this branch's added lines in
`values.yaml`, `smoke.sh`, `canary.py` and `test_canary.py` against the
five-line block, and each touched block whole.

- [ ] **The readiness set, which spec section 11 asks a test of**

```bash
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~HostSmokeTests.Ready_probe_reports_the_sql_and_bus_checks"
```

Expected: pass, with Docker running — PR-1's test asserting the registration
names are exactly `sql` and `masstransit-bus`, which PR-2's relay and PR-3's
Keycloak hop did not join. Nothing in this PR can change it; it is run so the
PR body can say the chart's `probes` comment cites a rule that holds.

- [ ] **The places this PR deliberately does not edit, checked**

```bash
grep -n "notifications-identity" docs/secrets.md
grep -n "Mail__Password" docs/secrets.md
grep -n "options:\|type: choice" .github/workflows/deploy.yml
```

Expected: the first prints *A client secret*'s step 2 naming
`notifications-identity` for Notifications' worker (PR-3), the second the
*Rotation* sentence naming the relay's password (PR-2), and the third
nothing — the workload input is a string the descriptors hold.

- [ ] **The workflows run on the PR.** `helm.yml` covers `deploy/helm/**` and
  `deploy/canary/**`; `deploy.yml`'s `check` job covers `deploy/canary/**`,
  `deploy/helm/**` and `deploy/observability/**`; `observability.yml` covers
  `deploy/observability/**` and `docs/runbooks/**`; `realm.yml` reads the
  charts' authorities through `deploy/keycloak`'s suite. All green.

- [ ] **PR body:** `| Class | D |`, the touch set from *Global Constraints* as
  paths only with the reasons beneath, and what a reviewer should read first:
  the jurisdiction block's change of shape and where the member list moved;
  ADR-052's two Helm rows — the `fail` naming three charts and the source loop
  reading every chart; the descriptor as the whole deploy target; the
  dashboard variable that hid a pure consumer; and the assumptions about PR-4
  and PR-5 in *Global Constraints*, each met by those plans as written. Then
  `/ship`.

## Self-review

**Spec coverage.**

- Section 3's PR-6 row — `deploy/helm/notifications` with `service.enabled:
  false` and `redis.enabled: false` (Task 3); the library chart's `mail`,
  `jurisdiction` and `delivery` capabilities and the client-credentials one
  (Task 2); the asserted chart rows ADR-052 names (Task 2); the umbrella
  (Task 3); `smoke.sh`'s lists and the deploy workflow's target, which are the
  descriptor (Task 3, *Global Constraints*); the canary row (Task 3); the shared
  runbook's Notifications half (Task 5).
- Section 11 — the chart is Shipping's renamed, with `service.enabled`,
  `ingress.enabled` and `redis.enabled` false and written down (Task 3); every
  key the host binds at start arrives through a capability the render refuses
  to leave empty — `mail`'s six keys with the password a Secret,
  `contactSource`'s two, `jurisdiction`'s five, `delivery`'s one, and the
  client credentials (Tasks 2, 3); autoscaling off and three replicas, by
  Shipping's argument (Task 3, and §15.3 in Task 6); the canary row declaring
  `consume` with an `httpExemption`, and ADR-047 needing no change (Task 3);
  the readiness set's test is PR-1's and is run (Task 7).
- Section 12 — `DeliveryLagHigh` and `QueueBacklogGrowing` read without
  change, their expressions quoted (Task 5); `check.py` satisfied, with a
  mutation proving each new panel is read (Task 5); the runbook's half — lag
  stops at consumer start, the waiting gauge by step is the signal for the
  worker's wait, and the overdue gauge for too few replicas (Task 5); a panel
  for each gauge, and the board's service variable widened (Task 5); the
  overdue gauge named in §15.3 as `shipping.shipments.overdue` is (Task 6); no
  rule over either gauge.
- Section 14 — ADR-052's `_helpers.tpl` and `smoke.sh` rows (Task 2) and
  §15.1 (Task 6); §15.3's sentence (Task 6, already true, argued); §15.4's
  rows the chart makes concrete (Task 6).

**Beyond the spec, each with its reason.** Task 1 — the canary suite used
`notifications` as an unknown workload, and `OVERLAY` refused a sender and a
list. The jurisdiction block's generalisation and Shipping's member list
(Task 2) — the named block cannot serve a second service. The widened
credential pattern and the source loop (Task 2) — PR-3 attaches the handler
through a qualified name. The `contactSource.realm`-against-authority guard
(Task 2) — §15.4's row, written by PR-3, says the realm read is the realm
that issues the token. `web-bff/values.yaml`'s comment, `canary.json` and a
docstring (Task 4) — each stated a rule or a count a third worker makes
false. `latency.md` (Task 5) — PR-3's plan leaves it to this
runbook half.

**Type and name consistency.** Chart, release and workload `notifications`;
images `notifications-worker` and `notifications-migrator`; `serviceName`
`Notifications.Worker`; connection name `Notifications`; Secrets
`notifications-database`, `notifications-migrator-secret`,
`notifications-rabbitmq` (the `notifications-svc` account),
`notifications-identity` and `notifications-mail`; client
`notifications-worker`, scope `roles`. Values blocks `mail`, `contactSource`,
`jurisdiction`, `delivery` and `identity.clientCredentials` are Task 2's and
are consumed under those spellings by Tasks 3, 4 and 6; the keys rendered are
`Mail__Host`, `Mail__Port`, `Mail__From`, `Mail__Security`, `Mail__UserName`,
`Mail__Password`, `ContactSource__BaseUrl`, `ContactSource__Realm`,
`Jurisdiction__Languages__0…n`, `Jurisdiction__TimeZone`,
`Jurisdiction__LogRetention`, `Jurisdiction__ContactRetention`,
`Jurisdiction__OrderRetention`, `Delivery__GiveUpAge` and the three
`Identity__Client__*`. `commerce.timeSpan`, `disowns`, `refuses_removed`,
`worker_shape`, `in_configmap` and `outside_configmap` are produced in Tasks
2 and 3 and used only after. `notifications_waiting` with `step`,
`notifications_overdue_seconds`, `notifications_mail_unavailable_total` with
`cause` and `notifications_contact_refused_total` are PR-5's, PR-5's, PR-2's
and PR-3's, used in Task 5, each as *Global Constraints* reads it from that
plan.

**Deliberately left.**

- **Panels for Shipping's gauges.** The new row is titled for every worker
  and holds Notifications' two, the panels the spec asks for; Shipping's would
  be panels of their own, argued in their own change.
- **§13.6's queue-backlog row**, whose example of work kept in a host's tables
  names Shipping's passes and says §15.3 names the gauge: still true, and §15.3
  now names Notifications' beside it.
- **A second relay adapter's values and a named relay's country** — ADR-053
  rule 3's naming is owed with the first real relay (spec section 9).
- **§11.7's erasure consumer** and anything it adds to this chart.
