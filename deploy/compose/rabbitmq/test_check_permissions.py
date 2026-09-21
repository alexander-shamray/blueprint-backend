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
import tempfile
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


# The whole chain check 6 looks for. A planted fragment that stops at the
# constructor is refused for want of the context whichever way the scan goes,
# so a case built on one would pass without exercising the mask at all.
CHAIN = ("new ImageFromDockerfileBuilder()"
         ".WithDockerfileDirectory(BrokerContextPath())")


def fixture_text(name: str) -> str:
    return (gate.TESTS / name / "ServiceFixture.cs").read_text(encoding="utf-8")


def run_over_fixtures(files: dict[str, str], dockerfile: str = "") -> list[str]:
    """Run check 6 with the fixture set replaced, and return its failures.

    Keyed by the directory under `tests/`, so a case reads as the tree it
    describes. Everything else the check opens is the real file, and the
    Dockerfile is the one that ships unless a case passes its own: a COPY
    genuinely moved fails here too.
    """
    original_read = gate.read
    original_failures = gate.failures
    original_fixtures = gate.broker_fixtures
    paths = {
        gate.TESTS / name / "ServiceFixture.cs": text
        for name, text in files.items()
    }
    fixtures = sorted(paths)
    if dockerfile:
        paths[gate.DOCKERFILE] = dockerfile

    def fake_read(path: Path) -> str:
        if Path(path) in paths:
            return paths[Path(path)]
        return original_read(path)

    gate.read = fake_read
    gate.broker_fixtures = lambda: fixtures
    gate.failures = []
    try:
        gate.check_fixture_matches_dockerfile()
        return list(gate.failures)
    finally:
        gate.read = original_read
        gate.broker_fixtures = original_fixtures
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


class TheFixtureCheckLooksAtEveryFixture(unittest.TestCase):
    """Check 6's subject: what it is looking at, not what it found.

    A case over the result cannot see a reach that has narrowed to whatever a
    constant happens to name, so the search is asserted against the tree on
    its own, apart from any mapping. The expectation is spelled by a plain
    substring over every `.cs` file under `tests/`, which shares neither the
    pattern nor the scanner with the code it judges: two spellings of one
    convention would agree with each other while both missed a fixture.
    """

    def test_the_search_reaches_every_file_that_starts_a_broker(self):
        expected = {
            path for path in gate.TESTS.rglob("*.cs")
            if "RabbitMqBuilder" in path.read_text(encoding="utf-8")
        }
        self.assertTrue(expected, "no broker fixture on disk: the case, not the gate")
        self.assertEqual(expected, set(gate.broker_fixtures()))

    def test_a_fixture_under_another_name_is_still_found(self):
        # The narrowing a filename constant cannot report: a broker fixture
        # that is neither `*.TestSupport` nor `ServiceFixture.cs`. Planted in
        # a temporary tree, because a case that writes into the checkout it is
        # judging leaves the next check reading its leftovers.
        original_tests = gate.TESTS
        with tempfile.TemporaryDirectory() as tmp:
            planted = Path(tmp) / "Platform.IntegrationTests" / "BrokerHarness.cs"
            planted.parent.mkdir(parents=True)
            planted.write_text(fixture_text("Catalog.TestSupport"), encoding="utf-8")
            gate.TESTS = Path(tmp)
            try:
                self.assertEqual([planted], gate.broker_fixtures())
            finally:
                gate.TESTS = original_tests

    def test_the_real_fixtures_pass(self):
        # The positive control the mutations below are evidence against.
        original_failures = gate.failures
        gate.failures = []
        try:
            gate.check_fixture_matches_dockerfile()
            self.assertEqual([], gate.failures)
        finally:
            gate.failures = original_failures

    def test_a_drift_outside_the_first_fixture_is_refused(self):
        # A mapping moved in a fixture other than the first: the case a
        # check reading one named fixture cannot report.
        drifted = fixture_text("Inventory.TestSupport").replace(
            '"/etc/rabbitmq/conf.d/"', '"/etc/rabbitmq/"')
        failures = run_over_fixtures({
            "Catalog.TestSupport": fixture_text("Catalog.TestSupport"),
            "Inventory.TestSupport": drifted,
        })

        self.assertTrue(
            any("Inventory.TestSupport" in f for f in failures),
            f"a moved mapping outside the named fixture went unreported: {failures}")

    def refuses(self, fixture: str, saying: str, because: str) -> None:
        """One unmapped fixture, and the branch the failure has to come from.

        The fixture's name alone does not say which branch fired — several
        carry it — so every case here names the sentence it expects.
        """
        failures = run_over_fixtures({"Catalog.TestSupport": fixture})
        self.assertTrue(
            any(saying in f for f in failures),
            f"{because}: {failures}")

    def unmapped(self, prefix: str = "") -> str:
        """Catalog's fixture with its mappings gone, under an optional header."""
        return prefix + fixture_text("Catalog.TestSupport").replace(
            "WithResourceMapping", "WithNothing")

    def test_a_fixture_that_neither_maps_nor_builds_is_refused(self):
        # Its broker starts with none of the definitions, which is the silent
        # green this check exists for.
        self.refuses(self.unmapped(), "builds no image from its context",
                     "a fixture configuring nothing was accepted")

    def test_a_fixture_naming_the_builder_in_prose_only_is_refused(self):
        # A comment is not a call, whatever the prose says.
        self.refuses(self.unmapped("// " + CHAIN + "\n"),
                     "builds no image from its context",
                     "a commented-out builder excused a fixture that maps nothing")

    def test_a_fixture_naming_the_builder_in_a_literal_only_is_refused(self):
        # A string is not a call either. The mapping regex reads the paths out
        # of the literals, so only the builder search masks them.
        self.refuses(
            self.unmapped('const string B = "' + CHAIN + '";\n'),
            "builds no image from its context",
            "a literal naming the builder excused a fixture that maps nothing")

    def test_a_fixture_naming_the_builder_type_without_building_is_refused(self):
        # The shape the two cases above cannot tell apart from a call: the type
        # in live code, constructing nothing.
        self.refuses(
            self.unmapped("System.Type t = typeof(ImageFromDockerfileBuilder);\n"),
            "builds no image from its context",
            "naming the type excused a fixture that maps nothing")

    def test_a_fixture_building_some_other_image_is_refused(self):
        # A builder is not the broker's builder. This one runs a stock broker
        # with none of the definitions while building a service's image.
        self.refuses(
            self.unmapped("IFutureDockerImage app = new ImageFromDockerfileBuilder()\n"
                          "    .WithDockerfileDirectory(AppContextPath()).Build();\n"),
            "builds no image from its context",
            "a builder for another image excused a fixture that maps nothing")

    def test_a_char_literal_does_not_open_a_string(self):
        # A lone quote inside a char literal opened one that ran to the next
        # quote in the file, which inverted which half of it counted as code.
        self.refuses(
            self.unmapped('private static string U(string v) => v.Trim(\'"\');\n'
                          'const string B = "' + CHAIN + '";\n'),
            "builds no image from its context",
            "a char literal let a string stand in for a call")

    def test_a_raw_literal_is_masked_whole(self):
        # A raw literal's body was masked only while its own quotes were even.
        self.refuses(
            self.unmapped('const string N = """\n'
                          '    Ordering builds with "ImageFromDockerfileBuilder.\n'
                          '    Not us: ' + CHAIN + ' is its route.\n'
                          '    """;\n'),
            "builds no image from its context",
            "a raw literal leaked its body into the code")

    def test_a_commented_out_mapping_is_not_a_mapping(self):
        # The other half of the same scan: a mapping behind `//` is not one,
        # and reading it as one is a broker configured by nothing.
        commented = fixture_text("Catalog.TestSupport").replace(
            "            .WithResourceMapping", "            // .WithResourceMapping")
        self.refuses(commented, "builds no image from its context",
                     "a commented-out mapping was counted as a real one")

    def test_a_fixture_that_stops_mapping_one_file_is_refused(self):
        # The drift the wrong-directory case cannot show: a file the image
        # carries and the test broker does not.
        dropped = fixture_text("Catalog.TestSupport").replace(
            '"20-commerce.conf"', '"nothing.conf"')
        self.refuses(dropped, "does not map it",
                     "a file the fixture stopped mapping went unreported")

    def test_a_fixture_mapping_a_file_the_image_lacks_is_refused(self):
        # The mirror, from the same mutation: a file the test broker carries
        # and the image does not.
        dropped = fixture_text("Catalog.TestSupport").replace(
            '"20-commerce.conf"', '"nothing.conf"')
        self.refuses(dropped, "the Dockerfile does not COPY it",
                     "a mapping of a file the image lacks went unreported")

    def test_a_copy_that_renames_the_file_is_refused(self):
        # The image would hold `renamed.conf` and the test broker
        # `20-commerce.conf`. RabbitMQ loads the definitions by the path the
        # configuration names, so a renamed copy boots with none of them —
        # the directory alone cannot show it, because the directory agrees.
        renaming = gate.read(gate.DOCKERFILE).replace(
            "COPY 20-commerce.conf /etc/rabbitmq/conf.d/20-commerce.conf",
            "COPY 20-commerce.conf /etc/rabbitmq/conf.d/renamed.conf")
        failures = run_over_fixtures(
            {"Catalog.TestSupport": fixture_text("Catalog.TestSupport")}, renaming)

        self.assertTrue(
            any("renamed.conf" in f for f in failures),
            f"a COPY that renames the file reported a pass: {failures}")

    def test_a_copy_the_pattern_cannot_read_is_refused(self):
        # The Dockerfile side of the same failure the fixtures had: a third
        # COPY nothing parses is a file no fixture is ever asked to map.
        unparsed = gate.read(gate.DOCKERFILE).replace(
            "COPY definitions.json /etc/rabbitmq/definitions.json",
            "COPY definitions.json /etc/rabbitmq/definitions.json\n"
            "COPY --chmod=644 30-extra.conf /etc/rabbitmq/conf.d/30-extra.conf")
        failures = run_over_fixtures(
            {"Catalog.TestSupport": fixture_text("Catalog.TestSupport")}, unparsed)

        self.assertTrue(
            any("the pattern, not the file" in f for f in failures),
            f"a COPY line the pattern cannot read went unreported: {failures}")

    def test_a_search_matching_nothing_is_refused(self):
        failures = run_over_fixtures({})

        self.assertTrue(
            any("the search, not the tree" in f for f in failures),
            f"an empty fixture set reported a pass: {failures}")


if __name__ == "__main__":
    unittest.main()
