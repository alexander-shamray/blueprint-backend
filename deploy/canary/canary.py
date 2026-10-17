#!/usr/bin/env python3
"""§15.5's rollout as one command line; the modules behind it are README's Layout.

Usage: py -3.12 deploy/canary/canary.py check | plan | analyse
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from analysis import PROMOTE, SIGNALS, _shout, analyse, plan, required_stable
from descriptor_model import (
    DEPLOYABLES, PlanError, SHARED_KEY, _ABSENT, _value_at, empty_until, entries, load_plan,
    migrated_by, migrates, shared_values, values_agree, values_named, workload_images,
)
from release_inputs import (
    image_revision, installed_tag, migration_prefix, validate_digest, validate_tag, verified_digest,
)
from repo_gate import check
from workflow_reads import smoke_cases


# --------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------

def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)

    checker = sub.add_parser("check", help="validate the plan and its descriptors against the repository")
    # Absent on a pull request, where there is no image: the plan is then
    # checked against the checkout, which is the only tree there is.
    checker.add_argument(
        "--source", type=Path,
        help="the tree the deployed image was built from (ADR-050)")
    checker.add_argument(
        "--workload", help="whose image --source is, and the only one read from it")
    checker.add_argument(
        "--installed-source", type=Path,
        help="the tree the release being replaced was built from (ADR-050)")

    revision = sub.add_parser("revision", help="the commit an image tag names")
    revision.add_argument("--value", required=True)

    installed = sub.add_parser(
        "installed-tag", help="the tag the running release was installed with")
    installed.add_argument(
        "--values", required=True, type=Path,
        help="a `helm get values --all -o json` document")

    # The workflow asks this BEFORE the first step, because §15.5's 5% is not
    # expressible at §15.3's replicaCount of 3 and scaling up is the operator's
    # decision rather than a surprise mid-rollout. One number, on stdout, so a
    # shell can read it without a JSON parser.
    required = sub.add_parser("required", help="stable replicas the first step needs")
    required.add_argument("--step", type=int, default=0)

    sub.add_parser("steps", help="how many rungs the ladder has")

    tag = sub.add_parser("validate-tag", help="refuse a tag Helm would read as two assignments")
    tag.add_argument("--value", required=True)
    tag.add_argument("--workload", help="apply that workload's migration Job name budget too")

    digest = sub.add_parser("validate-digest", help="refuse a digest Helm would read as two assignments")
    digest.add_argument("--value", required=True)

    verified = sub.add_parser(
        "verified-digest", help="the digest a `cosign verify -o json` document verified")
    verified.add_argument("--file", required=True, type=Path)

    images = sub.add_parser("images", help="the image names a workload's chart pulls")
    images.add_argument("--workload", required=True)
    images.add_argument("--role", required=True, choices=("api", "migrator"),
                        help="empty output for a migrator the chart has none of")

    # Separate from `plan` on purpose: the chart a workload deploys is a fact
    # about the plan, and `plan` REFUSES when the step's weight is not
    # expressible at the current replica count. Asking it for a chart would
    # make resolving the chart depend on the arithmetic succeeding.
    chart = sub.add_parser("chart", help="the chart directory a workload deploys")
    chart.add_argument("--workload", required=True)

    # ADR-118's rollout order: empty for a workload that migrates its own
    # schema or has none, so a shell reads "nothing to wait on" from no output.
    migrated = sub.add_parser("migrated-by", help="the workload whose release migrates this one's schema")
    migrated.add_argument("--workload", required=True)

    # ADR-122's other half, one name per line and none for a release that
    # migrates no other host's schema, for the rollout's shell loop.
    migrating = sub.add_parser("migrates", help="the workloads whose schema this one's release migrates")
    migrating.add_argument("--workload", required=True)

    # ADR-125's pairs, "peer key" one per line and none for a release that
    # shares no value, for the rollout's shell loop.
    sharing = sub.add_parser("shared-values", help="the peers and keys this release must agree with")
    sharing.add_argument("--workload", required=True)

    # Silent and 0 when the key agrees; each difference on stderr and 1 when not; 2 when it cannot compare.
    agreeing = sub.add_parser("values-agree", help="whether one key agrees between two releases' values")
    agreeing.add_argument("--key", required=True)
    agreeing.add_argument(
        "--values", required=True, type=Path, help="the values this release will be installed with, as JSON")
    agreeing.add_argument("--peer", required=True, type=Path, help="the peer's `helm get values --all -o json`")
    agreeing.add_argument("--peer-name", required=True)

    # ADR-126's keys, "key revision" one per line and none for a release that
    # holds none empty, for the rollout's shell loop.
    holding = sub.add_parser("empty-until", help="the keys this release keeps empty, and the revision each waits on")
    holding.add_argument("--workload", required=True)

    # Silent and 0 when the key is empty; what it names on stderr and 1 when not; 2 when it cannot read it.
    emptying = sub.add_parser("values-empty", help="whether one key is empty in a release's values")
    emptying.add_argument("--key", required=True)
    emptying.add_argument(
        "--values", required=True, type=Path, help="the values this release will be installed with, as JSON")

    # One name per line, for the shell loop in which `realm.yml`'s scheduled
    # job judges each release's realm (ADR-043).
    sub.add_parser("workloads", help="every workload in the plan, one per line")

    sub.add_parser("smoke-cases", help="each deployable's smoke.sh cases, one fact per line")

    planner = sub.add_parser("plan", help="canary replicas for one step")
    planner.add_argument("--workload", required=True)
    planner.add_argument("--stable", type=int, required=True)
    planner.add_argument("--step", type=int, required=True)

    analyser = sub.add_parser("analyse", help="promote or roll back")
    analyser.add_argument("--workload", required=True, help="whose declared signals to judge")
    analyser.add_argument("--readings", required=True, help="path to a readings JSON file")

    args = parser.parse_args(argv[1:])

    try:
        document = load_plan()
    except PlanError as error:
        print(f"canary: {error}", file=sys.stderr)
        return 1

    if args.command == "installed-tag":
        try:
            document = json.loads(args.values.read_text(encoding="utf-8"))
            print(installed_tag(document))
        except (OSError, json.JSONDecodeError) as error:
            print(f"canary: {args.values} is not readable JSON: {error}", file=sys.stderr)
            return 1
        except PlanError as error:
            print(f"canary: {error}", file=sys.stderr)
            return 1
        return 0

    if args.command == "revision":
        try:
            print(image_revision(args.value))
        except PlanError as error:
            print(f"canary: {error}", file=sys.stderr)
            return 1
        return 0

    if args.command == "check":
        failures = check(document, source=args.source, workload=args.workload,
                         installed=args.installed_source)
        if failures:
            print(f"canary: {len(failures)} problem(s) with the rollout plan:\n", file=sys.stderr)
            for failure in failures:
                print(f"  - {failure}", file=sys.stderr)
            return 1
        print(f"canary: the plan is consistent - {len(document['steps'])} steps, "
              f"{len(entries(document['workloads']))} workloads.")
        return 0

    if args.command == "steps":
        print(len(document["steps"]))
        return 0

    if args.command == "validate-tag":
        prefix = migration_prefix(args.workload, document) if args.workload else None
        try:
            validate_tag(args.value, prefix)
        except PlanError as error:
            print(f"canary: {error}", file=sys.stderr)
            return 1
        print(args.value)
        return 0

    if args.command == "validate-digest":
        try:
            validate_digest(args.value)
        except PlanError as error:
            print(f"canary: {error}", file=sys.stderr)
            return 1
        print(args.value)
        return 0

    if args.command == "verified-digest":
        try:
            print(verified_digest(json.loads(args.file.read_text(encoding="utf-8"))))
        except (OSError, json.JSONDecodeError) as error:
            print(f"canary: {args.file} is not readable JSON: {error}", file=sys.stderr)
            return 1
        except PlanError as error:
            print(f"canary: {error}", file=sys.stderr)
            return 1
        return 0

    if args.command == "images":
        try:
            print(workload_images(args.workload, document).get(args.role, ""))
        except PlanError as error:
            print(f"canary: {error}", file=sys.stderr)
            return 1
        return 0

    if args.command == "chart":
        workloads = entries(document["workloads"])
        if args.workload not in workloads:
            print(f"canary: no workload {args.workload!r} in the plan", file=sys.stderr)
            return 1
        print(workloads[args.workload]["chart"])
        return 0

    if args.command == "migrated-by":
        try:
            print(migrated_by(args.workload, document) or "")
        except PlanError as error:
            print(f"canary: {error}", file=sys.stderr)
            return 1
        return 0

    if args.command == "migrates":
        try:
            for name in migrates(args.workload, document):
                print(name)
        except PlanError as error:
            print(f"canary: {error}", file=sys.stderr)
            return 1
        return 0

    if args.command == "shared-values":
        try:
            for peer, key in shared_values(args.workload, document):
                print(f"{peer} {key}")
        except PlanError as error:
            print(f"canary: {error}", file=sys.stderr)
            return 1
        return 0

    if args.command == "values-agree":
        if not SHARED_KEY.fullmatch(args.key):
            print(f"canary: {args.key!r} is not a dotted values path", file=sys.stderr)
            return 2
        try:
            mine = json.loads(args.values.read_text(encoding="utf-8"))
            theirs = json.loads(args.peer.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as error:
            print(f"canary: a values document is not readable JSON: {error}", file=sys.stderr)
            return 2
        # A path neither holds compared nothing, which is not a disagreement to report as one.
        if _value_at(mine, args.key) is _ABSENT and _value_at(theirs, args.key) is _ABSENT:
            print(f"canary: {values_agree(args.key, mine, theirs, args.peer_name)[0]}", file=sys.stderr)
            return 2
        differences = values_agree(args.key, mine, theirs, args.peer_name)
        for difference in differences:
            print(f"canary: {difference} (ADR-125)", file=sys.stderr)
        return 1 if differences else 0

    if args.command == "empty-until":
        try:
            for key, revision in empty_until(args.workload, document):
                print(f"{key} {revision}")
        except PlanError as error:
            print(f"canary: {error}", file=sys.stderr)
            return 1
        return 0

    if args.command == "values-empty":
        if not SHARED_KEY.fullmatch(args.key):
            print(f"canary: {args.key!r} is not a dotted values path", file=sys.stderr)
            return 2
        try:
            named = values_named(args.key, json.loads(args.values.read_text(encoding="utf-8")))
        except (OSError, json.JSONDecodeError) as error:
            print(f"canary: {args.values} is not readable JSON: {error}", file=sys.stderr)
            return 2
        except PlanError as error:
            print(f"canary: {error}", file=sys.stderr)
            return 2
        if named:
            print(f"canary: {args.key} names {', '.join(named)} (ADR-126)", file=sys.stderr)
        return 1 if named else 0

    if args.command == "workloads":
        for name in entries(document["workloads"]):
            print(name)
        return 0

    if args.command == "smoke-cases":
        try:
            cases = smoke_cases(document["workloads"])
        except PlanError as error:
            print(f"canary: {error}", file=sys.stderr)
            return 1
        if not cases:
            print(f"canary: no descriptor under {DEPLOYABLES}, so smoke.sh would render nothing", file=sys.stderr)
            return 1
        print("\n".join(cases))
        return 0

    if args.command == "required":
        try:
            step = document["steps"][args.step]
        except IndexError:
            print(f"canary: no step {args.step} in the plan", file=sys.stderr)
            return 1
        print(required_stable(
            step["weight"],
            document["tolerance"]["weightOvershootPoints"],
        ))
        return 0

    if args.command == "plan":
        if args.workload not in entries(document["workloads"]):
            print(f"canary: no workload {args.workload!r} in the plan", file=sys.stderr)
            return 1
        try:
            step = document["steps"][args.step]
        except IndexError:
            print(f"canary: no step {args.step} in the plan", file=sys.stderr)
            return 1
        try:
            result = plan(
                step["weight"],
                args.stable,
                document["tolerance"]["weightOvershootPoints"],
            )
        except PlanError as error:
            print(f"canary: {error}", file=sys.stderr)
            return 1
        workload = entries(document["workloads"])[args.workload]
        result["dwellMinutes"] = step.get("dwellMinutes", 0)
        result["chart"] = workload["chart"]

        # `KEY=value` rather than JSON, so the caller is `eval`-able from a
        # shell and needs no parser. The workflow that drives this is bash on a
        # runner; handing it JSON would put five inline `python -c` invocations
        # in a YAML string, and every one of them is a place for a quoting bug
        # in the one file nothing here can test.
        for key, value in result.items():
            print(f"CANARY_{_shout(key)}={value}")
        return 0

    workloads = entries(document["workloads"])
    if args.workload not in workloads:
        print(f"canary: no workload {args.workload!r} in the plan", file=sys.stderr)
        return 1
    declared = {
        signal: SIGNALS.get(signal, {})
        for signal in workloads[args.workload].get("signals", [])
    }
    readings = json.loads(Path(args.readings).read_text(encoding="utf-8"))
    verdict = analyse(readings, entries(document["thresholds"]), declared)
    print(json.dumps(verdict, indent=2))
    return 0 if verdict["decision"] == PROMOTE else 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
