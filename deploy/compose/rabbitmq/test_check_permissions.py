#!/usr/bin/env python3
"""The broker permission gate's own suite.

**Every case here is a mutation, and that is the point.** A gate observed only
green is one nobody has established is looking at anything — this repository's
most-repeated failure — and this one guards an authorisation boundary
(ADR-036), so the cases that matter are the ones where it must go RED.

The mutations are the ones that were run by hand while building it, plus the
one Copilot found on PR #160 that none of them covered. Pinning them is the
difference between a negative somebody performed once and a negative the build
performs.

**It mutates a parsed copy of the real `definitions.json` rather than a
fixture.** A hand-written double is a second specification, and a gate tested
against one agrees with itself: the file this asserts on is the file that
ships, so a permission genuinely removed from the real broker fails here too.

    py -3.12 -m unittest discover -s deploy/compose/rabbitmq
"""

from __future__ import annotations

import copy
import importlib.util
import json
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent

_spec = importlib.util.spec_from_file_location("check_permissions", HERE / "check_permissions.py")
gate = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(gate)


def run_against(definitions: dict) -> list[str]:
    """Run the gate with `definitions.json` replaced, and return its failures.

    The module keeps `failures` as a module-level list and reads the file
    through `read`, so both are swapped for the call and restored after — the
    gate itself is untouched, which is what keeps these cases about its logic
    rather than about a copy of it.
    """
    original_read = gate.read
    original_failures = gate.failures
    payload = json.dumps(definitions)

    def fake_read(path: Path) -> str:
        if Path(path) == gate.DEFINITIONS:
            return payload
        return original_read(path)

    gate.read = fake_read
    gate.failures = []
    try:
        gate.main()
        return list(gate.failures)
    finally:
        gate.read = original_read
        gate.failures = original_failures


def real() -> dict:
    return json.loads((HERE / "definitions.json").read_text(encoding="utf-8"))


def permission(definitions: dict, user: str) -> dict:
    return next(e for e in definitions["permissions"] if e["user"] == user)


class TheGateIsLookingAtSomething(unittest.TestCase):
    """The subject, before any case that relies on it."""

    def test_the_real_definitions_pass(self):
        # The positive control. Every mutation below is only evidence because
        # this one is green: a gate that failed on everything would "catch"
        # each case while proving nothing.
        self.assertEqual([], run_against(real()))

    def test_the_repository_declares_the_services_these_cases_name(self):
        # An anti-vacuity floor of the kind check 6 applies to the gate's own
        # parsers. If these accounts ever stop existing, every case below
        # mutates a user nobody has and passes for the wrong reason.
        users = {u["name"] for u in real()["users"]}
        self.assertIn("catalog-svc", users)
        self.assertIn("ordering-svc", users)


class ARequiredGrantRemoved(unittest.TestCase):
    def test_a_peer_queue_only_the_code_knows_about(self):
        # `payments-commands` is reached by no test and by no running broker —
        # the saga never gets that far without an Inventory service — so it is
        # derived from Endpoints.cs. This is the case that proves the gate
        # reads the source rather than a topology capture.
        definitions = real()
        entry = permission(definitions, "ordering-svc")
        for verb in ("configure", "write", "read"):
            entry[verb] = entry[verb].replace("|payments-commands", "")

        failures = run_against(definitions)
        self.assertTrue(
            any("payments-commands" in f for f in failures),
            f"the gate accepted a missing grant for a queue Endpoints.cs names: {failures}")

    def test_a_receive_endpoint_the_service_hosts(self):
        definitions = real()
        entry = permission(definitions, "ordering-svc")
        entry["read"] = r"^(inventory-commands|payments-commands|Common\.Contracts|MassTransit:)"

        failures = run_against(definitions)
        self.assertTrue(
            any("ordering-" in f for f in failures),
            f"the gate accepted a service that cannot read its own queues: {failures}")

    def test_the_framework_fault_exchange(self):
        # A service that cannot publish a fault fails INTO the silence §13.6's
        # error-queue alert exists to break.
        definitions = real()
        entry = permission(definitions, "catalog-svc")
        for verb in ("configure", "write"):
            entry[verb] = entry[verb].replace("|MassTransit:", "")

        failures = run_against(definitions)
        self.assertTrue(
            any("MassTransit:ReceiveFault" in f for f in failures),
            f"the gate accepted a service that cannot report a fault: {failures}")


class AForbiddenGrantAdded(unittest.TestCase):
    """#44's property. Each of these is the exploit, re-opened one way."""

    def test_a_peer_s_command_endpoint(self):
        # Widens the real grant rather than replacing it, so catalog-svc's own
        # receive endpoint stays covered and check 2 does not fail first.
        definitions = real()
        entry = permission(definitions, "catalog-svc")
        entry["write"] = entry["write"].replace("|MassTransit:", "|MassTransit:|ordering-")

        failures = run_against(definitions)
        self.assertTrue(
            any("ordering-commands" in f for f in failures),
            f"the gate accepted broker write access to a peer's command queue: {failures}")

    def test_another_context_s_contracts(self):
        # Removes only the owned-contract alternative, so `catalog-` survives
        # and check 2 still finds its own receive endpoint covered.
        definitions = real()
        entry = permission(definitions, "catalog-svc")
        entry["write"] = entry["write"].replace(
            r"Common\.Contracts(\.Catalog\.V1:|:)", r"Common\.Contracts")

        failures = run_against(definitions)
        self.assertTrue(
            any("Common.Contracts.Ordering.V1:" in f for f in failures),
            f"the gate accepted a service that can forge a peer's events: {failures}")

    def test_a_peer_s_private_messaging_vocabulary(self):
        # COPILOT FOUND THIS ON PR #160 AND NOTHING ELSE HERE COVERED IT.
        # `Common.Contracts` is the published half; §9.6's `*Expired` timeouts
        # live in the service's own Messaging namespace, and a peer able to
        # write them can forge a saga timeout. Every other case above passed
        # while this one did not exist.
        definitions = real()
        entry = permission(definitions, "catalog-svc")
        entry["write"] = entry["write"].rstrip(")") + r"|Ordering\.Infrastructure\.Messaging:)"

        failures = run_against(definitions)
        self.assertTrue(
            any("Ordering.Infrastructure.Messaging:" in f for f in failures),
            f"the gate accepted a peer that can forge a saga timeout: {failures}")


class TheAccountsThemselves(unittest.TestCase):
    def test_guest_is_refused(self):
        definitions = real()
        definitions["users"].append({
            "name": "guest",
            "password_hash": "irrelevant",
            "hashing_algorithm": "rabbit_password_hashing_sha256",
            "tags": [],
        })

        failures = run_against(definitions)
        self.assertTrue(
            any("guest" in f for f in failures),
            f"the gate accepted the shared principal #44 is about: {failures}")

    def test_a_service_account_carrying_a_tag_is_refused(self):
        definitions = real()
        for user in definitions["users"]:
            if user["name"] == "catalog-svc":
                user["tags"] = ["administrator"]

        failures = run_against(definitions)
        self.assertTrue(
            any("administrator" in f or "tags" in f for f in failures),
            f"the gate accepted an administrator service account: {failures}")

    def test_a_service_with_source_and_no_account_is_refused(self):
        # The scaffold gap: a rendered service that cannot authenticate at all.
        definitions = real()
        definitions["users"] = [u for u in definitions["users"] if u["name"] != "catalog-svc"]
        definitions["permissions"] = [
            e for e in definitions["permissions"] if e["user"] != "catalog-svc"]

        failures = run_against(definitions)
        self.assertTrue(
            any("Catalog" in f for f in failures),
            f"the gate accepted a service with no broker account: {failures}")

    def test_an_account_for_a_service_that_does_not_exist_is_refused(self):
        definitions = real()
        template = copy.deepcopy(permission(definitions, "catalog-svc"))
        template["user"] = "phantom-svc"
        definitions["permissions"].append(template)
        definitions["users"].append({
            "name": "phantom-svc",
            "password_hash": "irrelevant",
            "hashing_algorithm": "rabbit_password_hashing_sha256",
            "tags": [],
        })

        failures = run_against(definitions)
        self.assertTrue(
            any("phantom-svc" in f for f in failures),
            f"the gate accepted a live credential for no service: {failures}")


class AScaffoldedServiceIsNotRefused(unittest.TestCase):
    def test_an_account_whose_context_has_no_contracts_yet_is_allowed(self):
        # §4.5's scaffold grants a broker account; `Common.Contracts` gains a
        # record only in the PR whose code publishes one. The dogfood found the
        # gate refusing exactly this, and the fix must not regress into
        # refusing it again.
        definitions = real()
        template = copy.deepcopy(permission(definitions, "catalog-svc"))
        template["user"] = "yankee-svc"
        for verb in ("configure", "write", "read"):
            template[verb] = template[verb].replace("Catalog", "Yankee")
        definitions["permissions"].append(template)
        definitions["users"].append({
            "name": "yankee-svc",
            "password_hash": "irrelevant",
            "hashing_algorithm": "rabbit_password_hashing_sha256",
            "tags": [],
        })

        failures = run_against(definitions)
        # It has no service directory either, so the "account with no service"
        # rule is the only thing it may trip — never the contracts rule.
        self.assertFalse(
            any("owned contracts" in f or "vacuously" in f for f in failures),
            f"the gate refused a correctly scaffolded service: {failures}")


if __name__ == "__main__":
    unittest.main()
