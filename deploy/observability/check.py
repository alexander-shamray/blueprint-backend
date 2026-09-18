#!/usr/bin/env python3
"""Gate for deploy/observability and docs/runbooks.

§13.9 requires every alert to have a runbook and every runbook to have an
alert — "checkable in both directions: an alert with no runbook is a 3 a.m.
page with no procedure, and a runbook with no alert is a procedure nobody will
be told to follow." That is checks 1 and 2 below.

§13.6 adds a third leg: "An alert has three parts: a condition, a signal and a
procedure." Two of the alerts in that chapter were written against signals that
did not exist, and both looked correct — the dashboard is empty either way,
whether the system is healthy or the metric was never published. Checks 4 and 5
are that leg, in both directions:

  * every metric a LOADED rule reads is published by something;
  * every metric an AWAITING-SIGNAL rule reads is published by nothing.

The second is what makes awaiting-signal.yaml self-clearing. The day somebody
ships the missing instrument, this gate goes red and names the rule to move —
an "awaiting signal" list that nothing re-checks would quietly become a list of
alerts nobody ever turned on.

Check 6 is the gate's own subject: a parser that silently extracted nothing
would pass 4 and 5 vacuously, which is this repository's most-repeated failure.

Check 8 covers what 4 and 5 structurally cannot. They are about metric NAMES;
the loaded outbox alerts group `by (service_name)`, so a service that runs the
dispatcher and publishes no gauges is "covered" by four alerts that can never
fire for it. Every such service must publish the gauges or carry a stated
exemption, and both directions fail.

Stdlib only, on the licence gate's terms: no restore, no dependencies, and it
runs before anything is built.

    py -3.12 deploy/observability/check.py
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OBSERVABILITY = ROOT / "deploy" / "observability"
RUNBOOKS = ROOT / "docs" / "runbooks"
WORKFLOW_PATH = ".github/workflows/observability.yml"
WORKFLOW = ROOT / WORKFLOW_PATH

LOADED_RULES = OBSERVABILITY / "alerts" / "platform-alerts.yaml"
AWAITING_RULES = OBSERVABILITY / "alerts" / "awaiting-signal.yaml"
CHAPTER = ROOT / "docs" / "backend-architecture" / "13-observability.md"

# EVERY PATH OUTSIDE deploy/observability THAT THIS SCRIPT READS, declared once.
#
# The workflow's path filter must cover each of them, or a change to one is a
# green pull request that skips the gate watching it. deploy/helm/smoke.sh
# learned this three times over and ended it the same way: declare the list
# beside the reads and assert the other copy matches. Check 7 is that assertion.
SOURCE_INPUTS = [
    "src",
    "docs/runbooks",
    "docs/backend-architecture/13-observability.md",
]

# The one file under docs/runbooks that is not a runbook. Declared rather than
# pattern-matched, so a second non-runbook file has to be argued for.
NOT_A_RUNBOOK = {"README.md"}

# Runbooks a MORE THAN ONE rule may name, each with the reason. §13.9 requires
# every alert to have a runbook and every runbook an alert; it does not require
# one-to-one, and "exactly one" was this gate's own invention.
#
# The allowance is declared rather than dropped, because the usual cause of two
# rules sharing a runbook is a copy-paste that wanted a second procedure — and
# a check that silently permits it is one fewer place that notices.
SHARED_RUNBOOKS = {
    "error-rate.md":
        "ErrorRateGateway and ErrorRateService are one condition split by "
        "§13.8's ownership — gateway 5xx is Platform's, a service's is its "
        "team's, and a static owner label on one rule routes both to whoever "
        "it names. The procedure is the same, and it branches on which fired.",
}

# Metrics that no C# file declares because nothing in this solution declares
# them — each is published by an exporter or an instrumentation package that is
# part of the target deployment. A name that is neither found in C# nor listed
# here is a typo, which is the entire point of keeping this list short.
# Each carries its kind, so the same exact-series rule applies to them as to the
# solution's own instruments — a name plus a suffix that kind never exports is
# still a typo, wherever it is published from.
EXTERNAL_METRICS = {
    "http_server_request_duration_seconds": (
        "Histogram", "ASP.NET Core instrumentation, enabled in §13.2"),
    "rabbitmq_queue_messages": (
        "Gauge", "the RabbitMQ exporter — §14.1's broker; §13.6's error-queue "
                 "and skipped-queue alerts both read it"),
    "kube_job_failed": (
        "Gauge", "kube-state-metrics — the Job's Failed CONDITION, which is "
                 "retries exhausted. `kube_job_status_failed` counts failed "
                 "pods and goes above zero mid-retry under `backoffLimit: 2`"),
    "kube_job_status_active": (
        "Gauge", "kube-state-metrics — the stuck-pending half of §13.6's migration alert"),
    "kube_job_status_start_time": (
        "Gauge", "kube-state-metrics — how long that Job has been active"),
}

# THE CACHE ROW HAS NO PREDICATE HERE, AND THAT IS A MEASUREMENT RATHER THAN A
# SHRUG.
#
# An earlier version of this gate treated `hybrid_cache_hits` / `_misses` as
# published once a host called `AddRedisConnections`, on the reasoning that what
# that alert is owed is a consumer rather than an instrument. Reading the pinned
# package settled it the other way: `Microsoft.Extensions.Caching.Hybrid`
# 10.0.0 references `System.Diagnostics.Tracing` and **not**
# `System.Diagnostics.Metrics` — it publishes through `HybridCacheEventSource`
# with `PollingCounter`, so there is no `Meter` and no instrument for OTel to
# collect. §13.2's `AddMeter("Microsoft.Extensions.Caching.Hybrid")` therefore
# collects nothing today, and would still collect nothing with Redis wired.
#
# So a consumer is necessary and not sufficient, and gating on one would have
# been worse than the gap it replaced: the gate would go red the day a host
# wired Redis, somebody would move the rule into platform-alerts.yaml, and it
# would sit there silent — a loaded alert that cannot fire, which is precisely
# what the two-file split exists to prevent.
#
# What that row is really owed is an instrument: an EventCounters-to-OTel bridge
# in this repository, or a package that publishes a Meter. The first would be a
# `Create*` call and check 5 would see it. **The second is invisible to this
# gate**, and is named in §13.6 and in the README as a residual rather than
# left implicit — a gate cannot watch a dependency's internals.

# Services that host §9.4's dispatcher and publish NO outbox gauges, each with
# the reason. Check 8 requires an entry here for every such service, so the
# absence is a decision somebody argued rather than a dashboard nobody noticed
# was empty.
#
# The four loaded outbox alerts group `by (service_name)` and read gauges only
# Ordering and Inventory publish. A stalled Catalog outbox is therefore the
# silent case §13.6 spends its callout on — and Catalog is §4.5's template, so
# every scaffolded service inherits the gap until this is closed.
OUTBOX_METRICS_EXEMPT = {
    "Catalog":
        "§13.3 places OutboxMetrics in Ordering.Infrastructure, so closing this "
        "means lifting the type into common code and teaching §4.5's scaffold to "
        "emit it — a design decision PR-24 does not own. Named here so it is a "
        "known gap rather than an empty panel.",
}

# PromQL keywords that survive the stripping below and are not metric names.
PROMQL_KEYWORDS = {
    "and", "or", "unless", "by", "without", "on", "ignoring",
    "group_left", "group_right", "offset", "bool", "le", "inf", "nan",
    "start", "end",
}

# Suffixes the OpenTelemetry Prometheus exporter appends. Stripped
# progressively when matching a PromQL name back to a C# instrument, because
# `request.duration` with unit `s` is exported as
# `request_duration_seconds_bucket`.
# UCUM units the OpenTelemetry Prometheus exporter turns into a name suffix.
# An annotation unit — anything in braces, like `{message}` — is dropped.
UNIT_SUFFIX = {"s": "_seconds", "By": "_bytes", "1": "_ratio"}

# The generic argument is OPTIONAL, and that is not tidiness. CreateHistogram
# and CreateCounter are always written `<double>` / `<long>`, but
# CreateObservableGauge infers its type from the callback and is written with no
# type argument at all — so a pattern requiring `<...>` finds the histograms,
# finds the counters, and silently misses every gauge §13.6 added. It did, on
# the first run of this gate, and the symptom was four correct alerts reported
# as having no signal.
#
# The KIND is captured too, because a metric's exported series depend on it: a
# histogram exports `_bucket`/`_count`/`_sum`, a counter `_total`, a gauge the
# bare name. Matching on a stripped prefix instead — which this did until
# Copilot's sixth round — accepts `outbox_pending_count_total` for a gauge that
# exports no `_total` series at all, so check 4 could certify an alert that
# queries a name nothing writes. The whole point of check 4 is that it cannot.
INSTRUMENT = re.compile(
    r"Create(Observable)?(Histogram|Counter|Gauge|UpDownCounter)"
    r"\s*(?:<[^>]+>)?\s*\(\s*\"([^\"]+)\"([^;]*?)\)\s*;",
    re.DOTALL)

UNIT_ARGUMENT = re.compile(r"\bunit:\s*\"([^\"]*)\"")

failures: list[str] = []


def fail(message: str) -> None:
    failures.append(message)


# ----------------------------------------------------------------- parsing --


def read(path: Path) -> str:
    # newline="" is deliberately absent: this reads text and never asserts on
    # line endings, so universal newlines is what is wanted. .gitattributes
    # pins this tree to LF for the tools that DO care.
    return path.read_text(encoding="utf-8")


def strip_comments(text: str) -> str:
    """C# source with every comment blanked out and every string left intact.

    **A regex over raw source counts a commented-out instrument as a live
    publisher**, and that is the one direction check 4 must not fail in: it
    would certify a loaded alert whose instrument does not exist at runtime,
    which is the silent gap this whole file exists to close. So comments go
    before the scan — and only comments, because an instrument's name is a
    string literal and blanking strings would leave nothing to find.

    Whitespace replaces each comment character rather than nothing, so a `;`
    inside a comment cannot join two statements INSTRUMENT then reads as one.

    **`#if false` is NOT covered, and is named here rather than half-handled.**
    Excluding a disabled region means evaluating C# preprocessor conditionals —
    defined symbols, `#elif`, nesting — which is a compiler and not a scanner.
    `src/` contains no `#if` today, so the residual is a shape nobody has yet
    written; a `Create` call inside one would still be counted.
    """
    out: list[str] = []
    i, n = 0, len(text)
    while i < n:
        ch = text[i]

        if text.startswith("//", i):
            while i < n and text[i] != "\n":
                out.append(" ")
                i += 1
            continue

        if text.startswith("/*", i):
            close = text.find("*/", i + 2)
            end = n if close < 0 else close + 2
            out.append("".join(c if c == "\n" else " " for c in text[i:end]))
            i = end
            continue

        # A raw string literal closes on a quote run of its own length, so the
        # opening run is measured rather than assumed to be three. OutboxStats
        # composes its SQL in one, and an unmeasured fence would swallow the
        # rest of that file.
        if text.startswith('"""', i):
            fence = 0
            while i + fence < n and text[i + fence] == '"':
                fence += 1
            close = text.find('"' * fence, i + fence)
            end = n if close < 0 else close + fence
            out.append(text[i:end])
            i = end
            continue

        if text.startswith('@"', i):
            j = i + 2
            while j < n:
                if text[j] != '"':
                    j += 1
                elif text.startswith('""', j):
                    j += 2
                else:
                    j += 1
                    break
            out.append(text[i:j])
            i = j
            continue

        if ch in ('"', "'"):
            j = i + 1
            while j < n:
                if text[j] == "\\":
                    j += 2
                elif text[j] == ch:
                    j += 1
                    break
                else:
                    j += 1
            out.append(text[i:j])
            i = j
            continue

        out.append(ch)
        i += 1

    return "".join(out)


def parse_rules(path: Path) -> list[dict[str, object]]:
    """Extract alert name, expression and runbook from a Prometheus rules file.

    A regex rather than a YAML parser, because the stdlib has none and this
    gate refuses dependencies for the reason the licence gate does. The shape
    it accepts is narrow — `- alert:`, `expr:` and `runbook_url:` — and check 6
    fails loudly if a rule comes back without an expression, so a file this
    cannot read is a red gate rather than a silent pass.
    """
    rules: list[dict[str, object]] = []
    current: dict[str, object] | None = None
    expr_lines: list[str] = []
    in_expr = False

    for raw in read(path).splitlines():
        line = raw.rstrip()
        stripped = line.strip()

        match = re.match(r"-\s*alert:\s*(\S+)", stripped)
        if match:
            if current is not None:
                current["expr"] = " ".join(expr_lines)
                rules.append(current)
            current = {"alert": match.group(1), "expr": "", "runbook": None}
            expr_lines = []
            in_expr = False
            continue

        if current is None or stripped.startswith("#"):
            continue

        match = re.match(r"expr:\s*(.*)$", stripped)
        if match:
            rest = match.group(1).strip()
            if rest in ("|", ">-", ">", "|-"):
                in_expr = True
            else:
                expr_lines.append(rest)
                in_expr = False
            continue

        match = re.match(r"runbook_url:\s*(\S+)", stripped)
        if match:
            current["runbook"] = match.group(1).strip("\"'")
            in_expr = False
            continue

        if in_expr:
            if re.match(r"[a-z_]+:\s", stripped) or stripped.startswith("- "):
                in_expr = False
            elif stripped:
                expr_lines.append(stripped)

    if current is not None:
        current["expr"] = " ".join(expr_lines)
        rules.append(current)

    return rules


def metrics_in(expression: str) -> set[str]:
    """Metric names referenced by a PromQL expression.

    Everything that is not a metric name is removed first — quoted strings,
    label matchers, durations, and the label lists after by/without/on/ignoring
    — and what survives that is an identifier not followed by `(`. Function
    names are excluded for free, because a function is always followed by one.
    """
    text = expression
    text = re.sub(r"\"[^\"]*\"|'[^']*'", " ", text)
    text = re.sub(r"\{[^}]*\}", " ", text)
    text = re.sub(r"\[[^\]]*\]", " ", text)

    # Duration literals OUTSIDE a range selector, which the bracket strip above
    # does not reach — `offset 1w`, `offset 5m`. The number is not an identifier
    # so the tokeniser skips it, and the unit is left behind as a metric called
    # `w`. Harmless while the rule sits in awaiting-signal.yaml, because nothing
    # publishes `w` and check 5 wants exactly that; it becomes a false failure
    # the day `orders_placed_total` lands and the rule moves to the loaded file,
    # where check 4 would reject the whole thing over a stray letter.
    text = re.sub(r"\b\d+(?:ms|[smhdwy])\b", " ", text)
    text = re.sub(r"\b(?:by|without|on|ignoring|group_left|group_right)\s*\([^)]*\)", " ", text)

    found = set()
    for token in re.finditer(r"[A-Za-z_][A-Za-z0-9_]*", text):
        name = token.group(0)
        # The WHOLE remainder, left-stripped — not one character of it. Reading
        # a single character means `sum (…)`, with the `by (…)` group already
        # removed, looks like a metric called `sum`: the next character is a
        # space, and a one-character slice has nothing left after stripping it.
        if text[token.end():].lstrip().startswith("("):
            continue
        if name in PROMQL_KEYWORDS:
            continue
        found.add(name)

    return found


def exported_series(name: str, kind: str, unit: str) -> set[str]:
    """The exact Prometheus series one instrument exports — no more.

    Dots become underscores, a UCUM unit becomes a suffix and an annotation unit
    is dropped, and then the KIND decides the rest. Nothing else is accepted:
    that is what stops a rule querying a name the instrument never writes.
    """
    base = name.replace(".", "_") + UNIT_SUFFIX.get(unit, "")

    if kind == "Histogram":
        return {f"{base}_bucket", f"{base}_count", f"{base}_sum"}
    if kind == "Counter":
        return {f"{base}_total"}

    return {base}                        # Gauge, UpDownCounter


def declared_instruments() -> set[str]:
    """Every series this solution's own instruments export."""
    names: set[str] = set()
    for source in (ROOT / "src").rglob("*.cs"):
        if "/obj/" in source.as_posix() or "/bin/" in source.as_posix():
            continue
        for match in INSTRUMENT.finditer(strip_comments(read(source))):
            observable, kind, name, rest = match.groups()
            unit_match = UNIT_ARGUMENT.search(rest or "")
            unit = unit_match.group(1) if unit_match else ""

            # An observable counter still exports `_total`; `Observable` only
            # says how the value is produced, not what shape it has.
            names |= exported_series(name, kind, unit)

    return names


def external_series() -> set[str]:
    return {
        series
        for name, (kind, _) in EXTERNAL_METRICS.items()
        for series in exported_series(name, kind, "")
    }


def is_published(metric: str, instruments: set[str]) -> bool:
    # An exact membership test on both sides. Nothing is stripped, because a
    # stripped name is a name nobody exports.
    return metric in instruments or metric in external_series()


# ------------------------------------------------------------------ checks --


def main() -> int:
    for path in (LOADED_RULES, AWAITING_RULES, RUNBOOKS, WORKFLOW, CHAPTER):
        if not path.exists():
            fail(f"missing: {path.relative_to(ROOT).as_posix()}")

    if failures:
        return report()

    loaded = parse_rules(LOADED_RULES)
    awaiting = parse_rules(AWAITING_RULES)
    every_rule = loaded + awaiting
    instruments = declared_instruments()


    runbooks = {
        path.name for path in RUNBOOKS.glob("*.md") if path.name not in NOT_A_RUNBOOK
    }

    # 6. THE GATE'S OWN SUBJECT, asserted before anything that relies on it.
    #    A parser that quietly returned nothing would pass every check below
    #    vacuously.
    if not loaded:
        fail("parsed no rules from platform-alerts.yaml — the parser, not the file")
    if not awaiting:
        fail("parsed no rules from awaiting-signal.yaml — the parser, not the file")
    if not runbooks:
        fail("found no runbooks in docs/runbooks")
    if not instruments:
        fail("found no instruments in src/**/*.cs — the INSTRUMENT pattern matched nothing")

    #    strip_comments is part of that subject, and it needs its own probe
    #    because only one of its two failure directions is caught above. A
    #    stripper that removed everything would empty `instruments` and fail
    #    there; a stripper that removed NOTHING restores the defect it was
    #    written for and leaves every check green. So both directions are
    #    asserted, on a sample rather than on the tree.
    #
    #    The sample is the shape that actually bit. Commenting a declaration out
    #    line-by-line is already invisible to INSTRUMENT — a `// ` at the head of
    #    the continuation lines breaks `\s*\(\s*"` — so the first probe tried
    #    reported the defect as absent. A block comment, and a whole call on one
    #    commented line, are the two that match and were counted.
    probe = strip_comments(
        'var a = "http://x/y";  // CreateCounter<long>("ghost.one");\n'
        '/* CreateCounter<long>("ghost.two"); */\n'
        'CreateCounter<long>("real.one");\n')
    stripped = {match.group(3) for match in INSTRUMENT.finditer(probe)}
    if stripped != {"real.one"}:
        fail(f"strip_comments: expected only the live instrument, found {sorted(stripped)}")
    if "http://x/y" not in probe:
        fail("strip_comments: a `//` inside a string literal was read as a comment")

    for rule in every_rule:
        if not rule["expr"]:
            fail(f"{rule['alert']}: no expression parsed")
        elif not metrics_in(str(rule["expr"])):
            fail(f"{rule['alert']}: expression parsed but no metric names found in it")

    if failures:
        return report()

    # 1. Every alert names a runbook, and that runbook exists.
    claimed: dict[str, list[str]] = {}
    for rule in every_rule:
        runbook = rule["runbook"]
        if not runbook:
            fail(f"{rule['alert']}: no runbook_url")
            continue

        name = str(runbook).rsplit("/", 1)[-1]
        if name not in runbooks:
            fail(f"{rule['alert']}: runbook_url names {name}, which is not in docs/runbooks")
        claimed.setdefault(name, []).append(str(rule["alert"]))

    # 2. Every runbook is named by an alert — the other direction, and the one
    #    a reviewer never checks by hand.
    for name in sorted(runbooks - set(claimed)):
        fail(f"docs/runbooks/{name}: no alert points at it (§13.9)")

    for name, alerts in sorted(claimed.items()):
        if len(alerts) > 1 and name not in SHARED_RUNBOOKS:
            fail(
                f"docs/runbooks/{name}: claimed by more than one alert — "
                f"{', '.join(alerts)}. Add it to SHARED_RUNBOOKS with a reason, or "
                f"give one of them its own procedure")

    # The other direction, so the allowance cannot outlive what it allows.
    for name in sorted(SHARED_RUNBOOKS.keys() - {k for k, v in claimed.items() if len(v) > 1}):
        fail(f"docs/runbooks/{name}: in SHARED_RUNBOOKS but no longer shared. Remove the entry")

    # 3. No alert is in both files.
    both = {str(r["alert"]) for r in loaded} & {str(r["alert"]) for r in awaiting}
    for name in sorted(both):
        fail(f"{name}: defined in both platform-alerts.yaml and awaiting-signal.yaml")

    # 4. Every metric a LOADED rule reads is published by something.
    for rule in loaded:
        for metric in sorted(metrics_in(str(rule["expr"]))):
            if not is_published(metric, instruments):
                fail(
                    f"{rule['alert']}: reads `{metric}`, which no C# instrument declares "
                    f"and EXTERNAL_METRICS does not list. A loaded rule with no signal is "
                    f"silent, and silence reads as health (§13.6)")

    # 5. Every metric an AWAITING rule reads is published by NOTHING. This is
    #    the self-clearing half: it goes red on the day the instrument lands.
    for rule in awaiting:
        for metric in sorted(metrics_in(str(rule["expr"]))):
            if is_published(metric, instruments):
                fail(
                    f"{rule['alert']}: reads `{metric}`, which now HAS a signal. "
                    f"Move this rule from awaiting-signal.yaml into platform-alerts.yaml")

    # 6. Every metric a dashboard panel reads is published. A panel over a
    #    metric nothing publishes draws a flat empty line, which reads as "no
    #    traffic" rather than as "this panel is broken" — the same trap as a
    #    silent alert, one artefact over.
    check_dashboards(instruments)

    # 8. Every service hosting the outbox dispatcher publishes outbox gauges,
    #    or is on a declared exemption with a reason. Checks 4 and 5 are about
    #    metric NAMES and cannot see a service missing from a series.
    check_outbox_metrics_per_service()

    # 9. §13.6's and §13.9's tables restate the runbook set in prose, and until
    #    now nothing read them — so the chapter could disagree with the files
    #    written from it and this gate would stay green, which is exactly what
    #    happened (#155).
    check_chapter_inventories(runbooks)

    # 7. The workflow's triggers cover every path this script reads outside its
    #    own tree — asserted on BOTH triggers, because a merged change that
    #    skips the gate on `main` is the same defect one branch later.
    check_source_inputs_covers_reads()
    check_workflow_covers_inputs()

    return report()


def check_dashboards(instruments: set[str]) -> None:
    dashboards = sorted((OBSERVABILITY / "dashboards").glob("*.json"))

    if not dashboards:
        fail("found no dashboards in deploy/observability/dashboards")
        return

    for path in dashboards:
        try:
            document = json.loads(read(path))
        except json.JSONDecodeError as error:
            fail(f"{path.name}: not valid JSON — {error}")
            continue

        expressions = [
            target["expr"]
            for panel in document.get("panels", [])
            for target in panel.get("targets", [])
            if "expr" in target
        ]

        # Subject again: a dashboard whose panels this cannot read would pass
        # the loop below by having nothing to check. Row panels carry no
        # targets, so the assertion is per FILE rather than per panel.
        if not expressions:
            fail(f"{path.name}: no panel expressions found — the reader, not the file")
            continue

        for expression in expressions:
            for metric in sorted(metrics_in(expression)):
                if not is_published(metric, instruments):
                    fail(
                        f"{path.name}: a panel reads `{metric}`, which no C# instrument "
                        f"declares and EXTERNAL_METRICS does not list")


def check_outbox_metrics_per_service() -> None:
    """Every service hosting §9.4's dispatcher publishes outbox gauges, or says why not.

    The loaded outbox alerts are service-agnostic — they group `by
    (service_name)` — so a service that runs a dispatcher and publishes no
    gauges is covered by four alerts that can never fire for it. That is the
    silent-dashboard case, arriving through a *service* rather than through a
    metric name, which is why checks 4 and 5 cannot see it.
    """
    services = sorted((ROOT / "src" / "Services").glob("*"))

    if not services:
        fail("found no services under src/Services — the reader, not the tree")
        return

    dispatching, instrumented = set(), set()
    for service in services:
        if not service.is_dir():
            continue
        for source in service.rglob("*.cs"):
            if "/obj/" in source.as_posix() or "/bin/" in source.as_posix():
                continue
            body = read(source)
            if "AddHostedService<OutboxDispatcher>" in body:
                dispatching.add(service.name)
            if "AddSingleton<OutboxMetrics>" in body:
                instrumented.add(service.name)

    # Subject: a matcher that found no dispatcher at all would pass this
    # vacuously, and Ordering is known to host one.
    if not dispatching:
        fail("found no service hosting OutboxDispatcher — the matcher, not the tree")
        return

    for name in sorted(dispatching - instrumented - OUTBOX_METRICS_EXEMPT.keys()):
        fail(
            f"{name} hosts OutboxDispatcher and registers no OutboxMetrics, and is not "
            f"in OUTBOX_METRICS_EXEMPT. Four loaded alerts read gauges it never "
            f"publishes, so a stalled lane there is silent (§13.6)")

    # The other direction: an exemption for a service that no longer needs one
    # is a stale excuse, and stale excuses are how a list stops being read.
    for name in sorted(OUTBOX_METRICS_EXEMPT.keys() - (dispatching - instrumented)):
        fail(
            f"{name} is in OUTBOX_METRICS_EXEMPT but no longer needs to be — it "
            f"either publishes outbox gauges now or hosts no dispatcher. Remove the entry")


def trigger_paths(text: str, trigger: str) -> list[str] | None:
    """The `paths:` entries of one trigger, and nothing else in the workflow.

    Reading the trigger's whole remaining text instead — which an earlier
    version did — lets the gate certify a filter that skips it: `push` is the
    last trigger, so its "body" ran to the end of the file and included the
    `python deploy/observability/check.py` run step. Deleting
    `deploy/observability/**` from `paths` left that substring behind and the
    check stayed green. **A gate that reads more than the thing it is judging
    can be satisfied by something that is not the thing.**
    """
    lines = text.splitlines()

    for index, line in enumerate(lines):
        if line.rstrip() != f"  {trigger}:":
            continue

        paths: list[str] = []
        in_paths = False
        for following in lines[index + 1:]:
            stripped = following.strip()
            if not stripped or stripped.startswith("#"):
                continue
            indent = len(following) - len(following.lstrip())
            if indent <= 2:                      # the next trigger, or `jobs:`
                break
            if stripped.startswith("paths:"):
                in_paths = True
                continue
            if in_paths and stripped.startswith("- "):
                paths.append(stripped[2:].strip().strip("'\""))
                continue
            if in_paths:                         # a sibling key ends the list
                in_paths = False

        return paths

    return None


def covers(path: str, entry: str) -> bool:
    """Does one `paths:` glob cover the WHOLE of an input this gate reads?

    The direction matters and an earlier version had it backwards: it accepted
    any path *under* the entry, so `src/Services/Ordering/**` was read as
    covering `src`. That approves a filter which skips every source change
    outside one service — precisely the drift check 7 exists to catch, waved
    through by check 7.

    Coverage runs the other way. A glob covers an entry when the glob's literal
    prefix is the entry or an ancestor of it: `src/**` covers `src`, and
    `docs/runbooks/**` covers `docs/runbooks` but not `docs`.
    """
    # ONLY `/**` COVERS A DIRECTORY. GitHub's `*` does not cross a separator, so
    # `src/*` matches the files directly under `src` and none of the C# beneath
    # them — a filter narrowed that way would skip every source change while
    # this check called it covered. `**` is the only recursive form, and an
    # exact literal covers exactly itself.
    if path == "**":                     # the whole repository
        return True

    if path.endswith("/**"):
        prefix = path[: -len("/**")].rstrip("/")
        return bool(prefix) and (entry == prefix or entry.startswith(prefix + "/"))

    if "*" in path:                      # `src/*`, `*.md`, anything non-recursive
        return False

    return entry == path


def check_chapter_inventories(runbooks: set[str]) -> None:
    """§13.6's Runbook columns and §13.9's table, against the runbooks on disk.

    Checks 1 and 2 pair the RULE FILES with `docs/runbooks`, both ways, and
    nothing reads the chapter those files were written from. So the chapter is
    the one inventory here that could go stale silently — and it did: a
    thirteenth condition landed and five prose sites went on saying twelve,
    while this gate stayed green because it has never counted anything.

    Counting is still not what closes it. A total in front of a table only
    says how stale the sentence is, which is why the fix that shipped with
    this check DROPPED the numerals rather than incrementing them: the table
    is the claim a reader can check, and this makes it one a build checks too.

    Two spellings, because the chapter uses two. §13.9 names each runbook with
    its `docs/runbooks/` prefix; §13.6's alert tables name it bare in the
    Runbook column. **A bare `*.md` in this chapter is a runbook reference by
    construction** — that is a convention this check imposes rather than one it
    infers, and a chapter that ever needs to name some other document bare will
    fail here and say so.
    """
    text = read(CHAPTER)

    prefixed = set(re.findall(r"`docs/runbooks/([A-Za-z0-9._-]+\.md)`", text))
    bare = set(re.findall(r"`([A-Za-z0-9._-]+\.md)`", text))

    # Subject first, as everywhere else in this file. A pattern that matched
    # nothing would agree with any runbook set at all, which is this gate's own
    # most-repeated failure pointed at its newest surface.
    if not prefixed:
        fail("13-observability.md: found no `docs/runbooks/…` references — "
             "the pattern, not the chapter (§13.9's table is the subject)")
        return
    if not bare:
        fail("13-observability.md: found no bare `….md` references — "
             "the pattern, not the chapter (§13.6's Runbook column is the subject)")
        return

    # README.md is reachable in the prose because the gate's own paragraph
    # names it; it is not a runbook, on check 1 and 2's terms.
    prefixed -= NOT_A_RUNBOOK

    for name in sorted(prefixed - runbooks):
        fail(f"13-observability.md: §13.9's table names docs/runbooks/{name}, "
             f"which is not in docs/runbooks")
    for name in sorted(runbooks - prefixed):
        fail(f"docs/runbooks/{name}: §13.9's table does not name it. The table is "
             f"the chapter's inventory of procedures, and a runbook missing from "
             f"it is one no reader of §13.9 knows exists")

    for name in sorted(bare - runbooks):
        fail(f"13-observability.md: names `{name}` where a runbook is expected, "
             f"and docs/runbooks has no such file")
    for name in sorted(runbooks - bare):
        fail(f"docs/runbooks/{name}: no alert row in §13.6 names it. Checks 1 and 2 "
             f"pair the RULE FILES with the runbooks; this is the chapter those "
             f"files were written from, and it disagrees")


def check_source_inputs_covers_reads() -> None:
    """SOURCE_INPUTS against the reads it claims to enumerate, not the workflow.

    Check 7 below compares the list to the triggers, which is the half that
    stays green when the list is SHORT — a workflow can only be checked for
    entries the list already contains, so a read nobody declared is invisible
    from both sides. `deploy/canary/canary.py` shipped exactly that defect:
    two paths declared, three opened, and its trigger assertion green
    throughout.

    docs/lessons.md states the fix as owed by every copy of this pattern rather
    than by the copy that was caught, so this is that debt paid here. The subject is
    this file's own source: every `ROOT / "…"` construction outside
    deploy/observability must be declared.
    """
    source = read(Path(__file__))

    reads = set()
    for segments in re.findall(
            r'ROOT\s*/\s*"([a-z]+)"(?:\s*/\s*"([a-z-]+)")?(?:\s*/\s*"([a-z0-9.-]+)")?',
            source):
        # As many segments as there are, because the declarable unit is not
        # always the top level: `docs` is too wide to declare (this gate does
        # not want every chapter) and `deploy` is too wide to be correct
        # (deploy/compose must not trigger it). The coverage test below accepts
        # a declared entry that is a prefix, so a one-segment declaration still
        # covers a deeper read where that is what somebody meant.
        #
        # THE THIRD SEGMENT IS A FILE AND ITS CHARACTER CLASS SAYS SO — digits
        # and a dot, which the first two do not admit. It exists because
        # `docs/backend-architecture` is the same "too wide" this comment
        # already rejects one directory up: check 9 reads ONE chapter, and
        # declaring its parent would trigger this workflow on all twenty
        # blueprint files. A read this regex truncated would resolve to that
        # parent and be declared there, which is the quiet direction — the list
        # would look covered while watching nineteen files too many.
        reads.add("/".join(part for part in segments if part))

    # Subject first. A regex that matched nothing would pass the loop below
    # against any list at all, which is this gate's own most-repeated failure
    # turned on itself.
    if not reads:
        fail("check.py: found no ROOT-relative reads in its own source — "
             "the scan is broken, not the list")
        return

    declared = SOURCE_INPUTS + ["deploy/observability"]
    for entry in sorted(reads):
        if not any(entry == path or entry.startswith(f"{path}/") for path in declared):
            fail(f"check.py opens `{entry}` and SOURCE_INPUTS does not declare it, "
                 f"so observability.yml's triggers do not watch it: {SOURCE_INPUTS}")


def check_workflow_covers_inputs() -> None:
    text = read(WORKFLOW)

    for name in ("push", "pull_request"):
        paths = trigger_paths(text, name)

        if paths is None:
            fail(f"{WORKFLOW.name}: no `{name}` trigger — the gate must run on both")
            continue

        # Subject: a parser that returned an empty list would pass nothing and
        # fail everything, which is safe — but one that returned entries for
        # the wrong trigger would not. Both triggers must actually list paths.
        if not paths:
            fail(f"{WORKFLOW.name}: the `{name}` trigger lists no paths — the parser, or the file")
            continue

        # The workflow itself is on the list because the gate READS it — check 7
        # is an assertion about this file, so a filter that does not rebuild on
        # a change to it can let the filter rot untested.
        required = SOURCE_INPUTS + ["deploy/observability", WORKFLOW_PATH]

        for entry in required:
            if not any(covers(path, entry) for path in paths):
                fail(
                    f"{WORKFLOW.name}: the `{name}` trigger does not cover `{entry}`, "
                    f"which check.py reads. A change to it would skip this gate")


def report() -> int:
    if failures:
        print("observability gate: FAILED", file=sys.stderr)
        for message in failures:
            print(f"  - {message}", file=sys.stderr)
        return 1

    print("observability gate: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
