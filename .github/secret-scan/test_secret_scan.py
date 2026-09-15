#!/usr/bin/env python3
"""Negative cases for the secret scan.

Run against this repository the gate passes, and that proves nothing about what
it would catch — a pattern that matched nothing at all would pass identically.
So every rule here is exercised twice: once with the shape it exists for, and
once with the near miss it must stay quiet about. The second half is where the
work is. A scanner people turn off is a scanner that fires on ordinary code, and
the ordinary code is what the near-miss cases are.

`Coverage` below is the subject test. It asserts that every rule the scanner
declares has both cases, so a rule added without them fails here rather than
shipping as a pattern nobody has established is looking at anything.
"""

from __future__ import annotations

import contextlib
import io
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import secret_scan

RULES = {rule.id: rule for rule in secret_scan.RULES}


def found(rule_id: str, text: str) -> list[str]:
    """Every credential one rule finds in one snippet."""
    rule = RULES[rule_id]
    return [finding.secret for finding in secret_scan.scan_text("probe.txt", text, [rule])]


# Every synthetic entry in this file names `a.txt`, so that is what these
# fixtures declare: without a trailing slash it names one exact path rather
# than a tree, and a fixture covering everything is what the directive exists
# to make unwritable.
COVERS = "a.txt"


def allow_file(directory: Path, *lines: str, covers: str | None = COVERS,
               name: str = "one-tree.txt") -> Path:
    path = directory / name
    declaration = "" if covers is None else f"# covers: {covers}\n"
    path.write_text(
        "# header\n" + declaration + "".join(f"{line}\n" for line in lines),
        encoding="utf-8")
    return path


def parse(*lines: str) -> tuple[list[secret_scan.Suppression], list[str]]:
    with tempfile.TemporaryDirectory() as directory:
        return secret_scan.read_allowed(allow_file(Path(directory), *lines))


def parse_with(*lines: str, covers: str | None = COVERS
               ) -> tuple[list[secret_scan.Suppression], list[str]]:
    """`parse`, with the tree the file declares under the caller's control."""
    with tempfile.TemporaryDirectory() as directory:
        return secret_scan.read_allowed(
            allow_file(Path(directory), *lines, covers=covers))


def run(root: Path, *allowed: str) -> tuple[int, str, str]:
    """The whole gate over one tree, with both streams captured.

    The allow-list is written outside `root`, because the file counts as a file
    and a walk that included it would make every count in these tests one out.
    """
    with tempfile.TemporaryDirectory() as directory:
        path = allow_file(Path(directory), *allowed)
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = secret_scan.main(["--root", str(root), "--allowed", str(path)])
    return code, out.getvalue(), err.getvalue()


# Fixtures assembled to the published length of each provider's key. They are
# invented values of the right shape, which is the only thing under test, and
# the reason this file has entries under allowed/: a positive control has to be
# the literal a real one would be.
AWS_ID = "AKIAIOSFODNN7EXAMPLE"
AWS_SECRET = "wJalrXUtnFEMIK7MDENGbPxRfiCYEXAMPLEKEY12"
GITHUB = "ghp_1234567890abcdefghijklmnopqrstuvwxyzAB"
GOOGLE = "AIzaSyB-1234567890abcdefghijklmnopqrstu"
JWT = ("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0."
       "dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk")


class Detects(unittest.TestCase):
    """One positive control per rule. Named `test_<rule id with underscores>`."""

    def test_private_key_block(self):
        self.assertEqual(
            found("private-key-block", "-----BEGIN RSA PRIVATE KEY-----"),
            ["-----BEGIN RSA PRIVATE KEY-----"])
        self.assertEqual(len(found("private-key-block", "-----BEGIN PRIVATE KEY-----")), 1)
        self.assertEqual(len(found("private-key-block", "-----BEGIN OPENSSH PRIVATE KEY-----")), 1)

    def test_connection_string_password(self):
        line = 'Server=sql,1433;Database=Catalog;User Id=sa;Password=Tr0ub4dor;Encrypt=False'
        self.assertEqual(found("connection-string-password", line), ["Tr0ub4dor"])
        # The parser this mirrors tolerates spaces around the separator and
        # ignores case (§13.4).
        self.assertEqual(found("connection-string-password", "Pwd = Tr0ub4dor"), ["Tr0ub4dor"])
        self.assertEqual(found("connection-string-password", "PASSWORD=Tr0ub4dor"), ["Tr0ub4dor"])

    def test_aws_access_key_id(self):
        self.assertEqual(found("aws-access-key-id", f"aws_key = '{AWS_ID}'"), [AWS_ID])

    def test_aws_secret_access_key(self):
        line = f"aws_secret_access_key = {AWS_SECRET}"
        self.assertEqual(found("aws-secret-access-key", line), [AWS_SECRET])

    def test_github_token(self):
        self.assertEqual(found("github-token", f"GH_TOKEN={GITHUB}"), [GITHUB])
        self.assertEqual(len(found("github-token", GITHUB.replace("ghp_", "ghs_"))), 1)

    def test_slack_token(self):
        # Assembled from two halves because GitHub's push protection refuses a
        # push carrying this literal, fabricated or not, in any commit of the
        # push. The rule sees the joined value; the cost is that this fixture is
        # not a finding in this file, so it has no allow-list entry.
        token = "xox" + "b-2468013579-abcdefghijklmno"
        self.assertEqual(found("slack-token", f"slack: {token}"), [token])

    def test_stripe_live_key(self):
        key = "sk_live_abcdefghijklmnop0123"
        self.assertEqual(found("stripe-live-key", f"stripe = {key}"), [key])

    def test_google_api_key(self):
        self.assertEqual(found("google-api-key", f"key={GOOGLE}"), [GOOGLE])

    def test_json_web_token(self):
        self.assertEqual(found("json-web-token", f"Authorization: Bearer {JWT}"), [JWT])

    def test_model_provider_api_key(self):
        xai = "xai-abcdefghijklmnopqrstuvwxyz0123456789"
        anthropic = "sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123456789"
        self.assertEqual(found("model-provider-api-key", f"XAI_API_KEY={xai}"), [xai])
        self.assertEqual(found("model-provider-api-key", f"key: '{anthropic}'"), [anthropic])

    def test_credential_assignment(self):
        # Eight characters is the floor, so eight characters is a positive case
        # and seven is the near miss below. Writing only one of those is how a
        # boundary ends up asserted on one side.
        self.assertEqual(found("credential-assignment", 'ClientSecret = "abcdefgh"'), ["abcdefgh"])
        self.assertEqual(found("credential-assignment", '"secret": "hunter2xyz"'), ["hunter2xyz"])
        self.assertEqual(
            found("credential-assignment", "  api_key: 'abcdefghij'"), ["abcdefghij"])

    def test_env_assignment(self):
        self.assertEqual(found("env-assignment", "SQL_PASSWORD=Tr0ub4dor"), ["Tr0ub4dor"])
        self.assertEqual(found("env-assignment", "export API_TOKEN=Tr0ub4dor"), ["Tr0ub4dor"])


class NearMisses(unittest.TestCase):
    """One false-positive boundary per rule, both ends where there are two."""

    def test_private_key_block(self):
        # The neighbouring headers are ordinary and harmless. Enumerating the
        # algorithm words rather than writing `\\w+` is what keeps them out.
        self.assertEqual(found("private-key-block", "-----BEGIN PUBLIC KEY-----"), [])
        self.assertEqual(found("private-key-block", "-----BEGIN CERTIFICATE-----"), [])

    def test_connection_string_password(self):
        self.assertEqual(found("connection-string-password", "Password=;Encrypt=False"), [])
        self.assertEqual(
            found("connection-string-password", "Password=${SQL_PASSWORD};Encrypt=False"), [])
        self.assertEqual(found("connection-string-password", "password={Pwd} was used"), [])
        self.assertEqual(found("connection-string-password", "Password=<your-password>"), [])
        # A name that merely ends in the keyword is an assignment, not a
        # connection-string segment, and belongs to the two name rules. The
        # lookbehind draws that line, and nothing else here would notice it
        # disappearing.
        self.assertEqual(found("connection-string-password", "SQL_PASSWORD=Tr0ub4dor"), [])
        # A name reached through a member access is C#, not a connection string,
        # where `;` or the start of the string precedes the keyword. The dot in
        # the lookbehind says so.
        self.assertEqual(found("connection-string-password", "        this.password = pw"), [])

    def test_aws_access_key_id(self):
        # Both ends. One character short does not match, and one character long
        # does not either — the second is the end a `\\b` would have got wrong.
        self.assertEqual(found("aws-access-key-id", "AKIAIOSFODNN7EXAMPL"), [])
        self.assertEqual(found("aws-access-key-id", "AKIAIOSFODNN7EXAMPLEX"), [])
        self.assertEqual(found("aws-access-key-id", "akiaiosfodnn7example"), [])

    def test_aws_secret_access_key(self):
        self.assertEqual(found("aws-secret-access-key", f"aws_secret_access_key={AWS_SECRET[:39]}"),
                         [])
        self.assertEqual(found("aws-secret-access-key", f"unrelated_value = {AWS_SECRET}"), [])

    def test_github_token(self):
        self.assertEqual(found("github-token", "ghp_" + "a" * 35), [])
        self.assertEqual(found("github-token", "ghz_" + "a" * 40), [])

    def test_slack_token(self):
        self.assertEqual(found("slack-token", "xoxz-2468013579-abcdefghijklmno"), [])
        self.assertEqual(found("slack-token", "xoxb-short"), [])

    def test_stripe_live_key(self):
        # sk_test_ is a test-mode key by construction. Firing on it is how a
        # rule gets a reputation for crying wolf.
        self.assertEqual(found("stripe-live-key", "sk_test_abcdefghijklmnop0123"), [])
        self.assertEqual(found("stripe-live-key", "sk_live_abcdefgh"), [])

    def test_google_api_key(self):
        self.assertEqual(found("google-api-key", GOOGLE[:-1]), [])
        self.assertEqual(found("google-api-key", GOOGLE + "x"), [])

    def test_json_web_token(self):
        head, payload, _ = JWT.split(".")
        self.assertEqual(found("json-web-token", f"{head}.{payload}"), [])
        self.assertEqual(found("json-web-token", "eyJhbGci.short.sig"), [])

    def test_model_provider_api_key(self):
        self.assertEqual(found("model-provider-api-key", "xai-tooshort"), [])
        # `sk-ant` is a prefix of ordinary English, and the hyphen after it is
        # the only thing that says so.
        self.assertEqual(
            found("model-provider-api-key", "sk-antique-furniture-catalogue-2026"), [])

    def test_credential_assignment(self):
        self.assertEqual(found("credential-assignment", 'ClientSecret = "abcdefg"'), [])
        self.assertEqual(found("credential-assignment", 'if (token == "abcdefgh")'), [])
        self.assertEqual(found("credential-assignment", 'public string Token => "abcdefgh";'), [])
        self.assertEqual(found("credential-assignment", 'secret: "${VAULT_SECRET}"'), [])
        # Keycloak's realm export carries `webAuthnPolicyPasswordless*`
        # keys. The word inside them is the opposite of a credential.
        self.assertEqual(
            found("credential-assignment", '"webAuthnPolicyPasswordlessRpId": "keycloak"'), [])

    def test_env_assignment(self):
        # A constructor storing its parameter. This is the one false positive
        # the bare-word constraint could not remove, because `secret` is a bare
        # word — what removes it is that the name already contains the value.
        self.assertEqual(found("env-assignment", "        self.secret = secret"), [])
        self.assertEqual(found("env-assignment", "        this.password = password"), [])
        # An expression rather than a literal, in the two shapes that reach the
        # left margin: a Python module constant and a C# statement.
        self.assertEqual(found("env-assignment", "TOKEN_PATTERN = re.compile(r'x')"), [])
        self.assertEqual(found("env-assignment", "        _token = someOtherToken;"), [])
        self.assertEqual(found("env-assignment", "# SQL_PASSWORD=Tr0ub4dor"), [])


class Coverage(unittest.TestCase):
    """The gate's own subject: what the tests are looking at, not what they found.

    A rule shipped without a positive control is a pattern nobody has shown
    matches anything at all.
    """

    def test_every_rule_has_a_positive_and_a_near_miss_case(self):
        self.assertTrue(secret_scan.RULES, "the scanner declares no rules")
        for rule in secret_scan.RULES:
            name = "test_" + rule.id.replace("-", "_")
            self.assertTrue(
                hasattr(Detects, name),
                f"rule `{rule.id}` has no positive control: add Detects.{name}")
            self.assertTrue(
                hasattr(NearMisses, name),
                f"rule `{rule.id}` has no false-positive boundary: add NearMisses.{name}")

    def test_every_fixture_is_the_length_its_rule_requires(self):
        # A fixture one character out turns a positive control into a near miss
        # that happens to pass.
        self.assertEqual(len(AWS_ID), 20)
        self.assertEqual(len(AWS_SECRET), 40)
        self.assertEqual(len(GOOGLE), 39)
        self.assertGreaterEqual(len(GITHUB) - len("ghp_"), 36)
        self.assertEqual(len(JWT.split(".")), 3)

    def test_every_rule_has_a_distinct_id_and_a_sentence(self):
        identifiers = [rule.id for rule in secret_scan.RULES]
        self.assertEqual(len(identifiers), len(set(identifiers)))
        for rule in secret_scan.RULES:
            self.assertTrue(rule.sentence.strip(), f"rule `{rule.id}` has no sentence")


class Values(unittest.TestCase):
    """`literal()` — the structural half, which is the half the code may decide."""

    def test_a_bare_variable_reference_carries_nothing(self):
        for reference in ("${SQL_PASSWORD}", "$PGPASSWORD", "%SQL_PASSWORD%",
                          "{{ .Values.db.password }}", "{Pwd}", "<your-password>",
                          "$(OrderingRuntimePassword)"):
            self.assertEqual(secret_scan.literal(reference), "", reference)

    def test_a_defaulted_reference_is_judged_on_its_default(self):
        # §14.1's seam keeps the value out of a deployment. It does not keep it
        # out of the tree, so the default is what gets judged — and then
        # accepted by name in the allow-list rather than by the pattern.
        self.assertEqual(secret_scan.literal("${SQL_PASSWORD:-Tr0ub4dor}"), "Tr0ub4dor")

    def test_a_nested_default_is_unwrapped(self):
        self.assertEqual(secret_scan.literal("${A:-${B:-Tr0ub4dor}}"), "Tr0ub4dor")

    def test_a_mask_carries_nothing(self):
        for mask in ("", "   ", "****", "xxxxxxxx", "...", "---"):
            self.assertEqual(secret_scan.literal(mask), "", repr(mask))

    def test_punctuation_alone_carries_nothing(self):
        self.assertEqual(secret_scan.literal("`"), "")
        self.assertEqual(secret_scan.literal("}]>"), "")

    def test_a_real_value_survives(self):
        self.assertEqual(secret_scan.literal("  Tr0ub4dor  "), "Tr0ub4dor")


class Redaction(unittest.TestCase):
    def test_a_short_prefix_and_a_length(self):
        self.assertEqual(secret_scan.redact("Tr0ub4dor"), "Tr0... 9 chars")

    def test_the_fingerprint_is_stable_and_short(self):
        self.assertEqual(secret_scan.digest("Tr0ub4dor"), secret_scan.digest("Tr0ub4dor"))
        self.assertNotEqual(secret_scan.digest("Tr0ub4dor"), secret_scan.digest("Tr0ub4doR"))
        self.assertEqual(len(secret_scan.digest("Tr0ub4dor")), 12)

    def test_neither_stream_carries_the_secret_it_found(self):
        """Asserted on both streams.

        A gate that prints what it found has copied the credential into the log
        of every run that failed — where it is retained longer, and read by more
        people, than the branch ever was. Checking only stdout would pass a gate
        that wrote the value to stderr, which is where this one writes.
        """
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "leak.txt").write_text(f"aws_key = {AWS_ID}\n", encoding="utf-8")
            code, out, err = run(root)

        self.assertEqual(code, 1)
        self.assertNotIn(AWS_ID, out)
        self.assertNotIn(AWS_ID, err)
        self.assertIn("aws-access-key-id", err)
        self.assertIn("AKI... 20 chars", err)
        self.assertIn(secret_scan.digest(AWS_ID), err)


class TheClosingAdvice(unittest.TestCase):
    """Where the message sends a reader has to be somewhere.

    `--allowed` takes the directory or one file out of it, and the suite's own
    `run` uses the second, so both wordings are asserted here.
    """

    def leak(self, root: Path) -> Path:
        root.mkdir()
        (root / "leak.txt").write_text(f"aws_key = {AWS_ID}\n", encoding="utf-8")
        return root

    def test_a_single_file_is_named_as_a_file(self):
        with tempfile.TemporaryDirectory() as directory:
            code, _, err = run(self.leak(Path(directory) / "tree"))
        self.assertEqual(code, 1)
        self.assertIn("an entry in one-tree.txt,", err)
        self.assertNotIn("one-tree.txt/", err)

    def test_a_directory_is_named_as_the_file_covering_the_tree(self):
        with tempfile.TemporaryDirectory() as directory:
            root = self.leak(Path(directory) / "tree")
            allowed = Path(directory) / "allowed"
            allowed.mkdir()
            allow_file(allowed, name="a.txt")
            out, err = io.StringIO(), io.StringIO()
            with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
                code = secret_scan.main(["--root", str(root), "--allowed", str(allowed)])
        self.assertEqual(code, 1)
        self.assertIn("an entry in the allowed/ file covering its tree", err.getvalue())


class AsciiOutput(unittest.TestCase):
    """Both streams stay ASCII even when the tree does not.

    The messages are written in ASCII, which says nothing about the output: two
    of the three things a finding line carries come from the tree — the path and
    the redacted prefix of the value. So the subject here is a file whose name
    and whose credential are both outside ASCII.
    """

    def test_a_non_ascii_path_and_value_still_print(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "tree"
            root.mkdir()
            (root / "café.env").write_text(
                "SECRET_TOKEN=überg3heim-passwort\n", encoding="utf-8")
            code, out, err = run(root)

        self.assertEqual(code, 1)
        (out + err).encode("ascii")  # raises if anything slipped through
        self.assertIn("env-assignment", err)


class AllowList(unittest.TestCase):
    def test_reads_a_well_formed_entry(self):
        entries, problems = parse_with(
            "deploy/compose/.env.example | env-assignment | abc123def456 | "
            "Section 14.1's documented local default.",
            covers="deploy/")
        self.assertEqual(problems, [])
        self.assertEqual(len(entries), 1)
        self.assertEqual(entries[0].rule, "env-assignment")
        self.assertEqual(entries[0].fingerprint, "abc123def456")

    def test_rejects_an_entry_with_the_wrong_number_of_fields(self):
        _, problems = parse("deploy/compose/.env.example | env-assignment | abc123def456")
        self.assertEqual(len(problems), 1)
        self.assertIn("expected", problems[0])

    def test_rejects_a_glob_path(self):
        # A glob is how a suppression arrives for a file nobody has written yet.
        for path in ("tests/*.cs", "tests/Common.Web.Tests/?.cs"):
            _, problems = parse(f"{path} | credential-assignment | abc123def456 | A real reason.")
            self.assertEqual(len(problems), 1, path)
            self.assertIn("exact path", problems[0])

    def test_rejects_a_rule_the_scanner_does_not_declare(self):
        # Only checked when the caller supplies the set, which `main` does. A
        # typo would otherwise be reported as a stale entry: the right verdict
        # with the wrong diagnosis attached.
        with tempfile.TemporaryDirectory() as directory:
            path = allow_file(
                Path(directory),
                "a.txt | aws-acces-key-id | abc123def456 | One letter short of a rule.")
            _, problems = secret_scan.read_allowed(path, set(RULES))
        self.assertEqual(len(problems), 1)
        self.assertIn("not a rule this scanner declares", problems[0])

    def test_rejects_a_reason_that_is_not_one(self):
        _, problems = parse("a.txt | credential-assignment | abc123def456 | ok")
        self.assertEqual(len(problems), 1)
        self.assertIn("states WHY", problems[0])

    def test_reports_a_missing_allow_list(self):
        with tempfile.TemporaryDirectory() as directory:
            _, problems = secret_scan.read_allowed(Path(directory) / "absent.txt")
        self.assertEqual(len(problems), 1)
        self.assertIn("missing", problems[0])

    def test_reads_every_file_in_the_directory(self):
        # The shape the gate actually runs in: the allow-list is a directory,
        # and an entry is read from whichever file covers its tree.
        with tempfile.TemporaryDirectory() as directory:
            allowed = Path(directory)
            allow_file(
                allowed, "a.txt | credential-assignment | abc123def456 | The first tree's.",
                name="one.txt")
            allow_file(
                allowed, "b.txt | credential-assignment | 456def123abc | The second tree's.",
                covers="b.txt", name="two.txt")
            entries, problems = secret_scan.read_allowed(allowed)

        self.assertEqual([], problems)
        self.assertEqual({"a.txt", "b.txt"}, {entry.path for entry in entries})
        # And each remembers where it came from, which is what a diagnostic
        # naming a line number needs in order to mean anything.
        self.assertEqual({"one.txt", "two.txt"}, {entry.source for entry in entries})

    def test_reports_a_directory_with_no_allow_list_in_it(self):
        # Reported as missing rather than as a clean empty list, which would
        # drop every accepted finding at once.
        with tempfile.TemporaryDirectory() as directory:
            _, problems = secret_scan.read_allowed(Path(directory))
        self.assertEqual(len(problems), 1)
        self.assertIn("no allow-list file", problems[0])

    def test_rejects_an_entry_outside_the_tree_its_file_covers(self):
        _, problems = parse_with(
            "b.txt | credential-assignment | abc123def456 | The wrong file's tree.")
        self.assertEqual(len(problems), 1)
        self.assertIn("outside `a.txt`", problems[0])

    def test_rejects_every_entry_in_a_file_that_declares_no_tree(self):
        # Fails closed per entry rather than passing them through unscoped: an
        # undeclared file that suppressed anything would be the single shared
        # list back again, one missing line at a time.
        _, problems = parse_with(
            "a.txt | credential-assignment | abc123def456 | A perfectly good reason.",
            covers=None)
        self.assertEqual(len(problems), 1)
        self.assertIn("declares no `# covers:", problems[0])

    def test_rejects_a_file_that_declares_no_tree_even_with_no_entries(self):
        # An empty or comment-only `.txt` has no entry to complain about, and
        # the grammar still says every file declares exactly one tree.
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "ownerless.txt"
            path.write_text("# a header and nothing else\n", encoding="utf-8")
            entries, problems = secret_scan.read_allowed(path)

        self.assertEqual([], entries)
        self.assertEqual(len(problems), 1)
        self.assertIn("declares no `# covers:", problems[0])

    def test_an_ownerless_file_is_one_complaint_and_not_one_per_entry(self):
        # The same refusal, from the other side: three entries in a file that
        # declares nothing is one thing wrong with the file.
        entry = "a.txt | credential-assignment | abc123def456 | A perfectly good reason."
        entries, problems = parse_with(entry, entry, entry, covers=None)
        self.assertEqual([], entries)
        self.assertEqual(len(problems), 1)

    def test_rejects_a_directive_below_the_first_entry(self):
        # The grammar says the directive comes before the first entry, so it is
        # enforced rather than trusted: below one, it reads to anyone scanning
        # the file as though the lines above were covered by something else.
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "late.txt"
            path.write_text(
                "a.txt | credential-assignment | abc123def456 | Above the directive.\n"
                "# covers: a.txt\n",
                encoding="utf-8")
            _, problems = secret_scan.read_allowed(path)

        self.assertEqual(len(problems), 1)
        self.assertIn("is below the entry on line 1", problems[0])

    def test_rejects_a_second_covers_directive(self):
        with tempfile.TemporaryDirectory() as directory:
            path = allow_file(
                Path(directory),
                "a.txt | credential-assignment | abc123def456 | A perfectly good reason.")
            path.write_text(
                path.read_text(encoding="utf-8").replace(
                    "# header\n", "# header\n# covers: a.txt\n"),
                encoding="utf-8")
            _, problems = secret_scan.read_allowed(path)
        self.assertEqual(len(problems), 1)
        self.assertIn("a second `covers:` directive", problems[0])

    def test_rejects_an_entry_the_parent_file_kept_from_a_child_tree(self):
        # An entry under nested prefixes satisfies both files' own prefix
        # check, so the longest prefix owns it and placement stays mechanical.
        with tempfile.TemporaryDirectory() as directory:
            allowed = Path(directory)
            allow_file(
                allowed,
                "deploy/compose/x.yml | credential-assignment | abc123def456 | "
                "Filed in the parent, owned by the child.",
                covers="deploy/", name="deploy.txt")
            allow_file(allowed, covers="deploy/compose/", name="deploy-compose.txt")
            _, problems = secret_scan.read_allowed(allowed)

        self.assertEqual(len(problems), 1)
        self.assertIn("is covered by `deploy/compose/`", problems[0])
        self.assertIn("deploy-compose.txt declares", problems[0])

    def test_accepts_the_same_entry_in_the_file_that_owns_it(self):
        # The other side of the refusal above, and the reason it is a
        # longest-prefix rule rather than a ban on nesting: the child file may
        # hold the entry, and the parent goes on covering everything else.
        with tempfile.TemporaryDirectory() as directory:
            allowed = Path(directory)
            allow_file(
                allowed,
                "deploy/helm/values.yaml | credential-assignment | 456def123abc | "
                "The parent still covers what no child claims.",
                covers="deploy/", name="deploy.txt")
            allow_file(
                allowed,
                "deploy/compose/x.yml | credential-assignment | abc123def456 | "
                "Filed in the file that covers it.",
                covers="deploy/compose/", name="deploy-compose.txt")
            entries, problems = secret_scan.read_allowed(allowed)

        self.assertEqual([], problems)
        self.assertEqual(
            {"deploy/helm/values.yaml", "deploy/compose/x.yml"},
            {entry.path for entry in entries})

    def test_a_prefix_without_a_slash_is_one_path_and_not_a_tree(self):
        # `startswith` has no path boundary, so `# covers: d` would own entries
        # from both `docs/` and `deploy/`. A prefix that does not end in `/` is
        # one path.
        _, problems = parse_with(
            "docs/x.md | credential-assignment | abc123def456 | "
            "Two trees under one letter.",
            covers="d")
        self.assertEqual(len(problems), 1)
        self.assertIn("outside `d`", problems[0])

    def test_a_prefix_without_a_slash_still_covers_its_own_path(self):
        # The other side: a declaration naming one file is legal and narrow,
        # which is what the fixtures in this class rely on.
        entries, problems = parse_with(
            "a.txt | credential-assignment | abc123def456 | The one path it names.",
            covers="a.txt")
        self.assertEqual([], problems)
        self.assertEqual(["a.txt"], [entry.path for entry in entries])

    def test_a_tree_prefix_does_not_leak_into_a_sibling_that_shares_its_letters(self):
        # `deploy` without the slash would own `deployment/` too. With it, the
        # boundary is the separator rather than a character count.
        _, problems = parse_with(
            "deployment/x.yml | credential-assignment | abc123def456 | "
            "A sibling that shares the first six letters.",
            covers="deploy/")
        self.assertEqual(len(problems), 1)
        self.assertIn("outside `deploy/`", problems[0])

    def test_rejects_two_files_covering_one_tree(self):
        # Then an entry has two homes, and a reader looking for it has two
        # places to fail to find it.
        with tempfile.TemporaryDirectory() as directory:
            allowed = Path(directory)
            allow_file(allowed, name="one.txt")
            allow_file(allowed, name="two.txt")
            _, problems = secret_scan.read_allowed(allowed)
        self.assertEqual(len(problems), 1)
        self.assertIn("already covered by", problems[0])

    def test_reports_a_duplicated_entry(self):
        entry = "a.txt | credential-assignment | abc123def456 | The same reason twice over."
        entries, _ = parse(entry, entry)
        problems = secret_scan.audit([], entries)
        self.assertTrue(any("duplicates" in problem for problem in problems))


class Audit(unittest.TestCase):
    def finding(self, path="a.txt", rule_id="aws-access-key-id", secret=AWS_ID):
        return secret_scan.Finding(path, 1, RULES[rule_id], secret)

    def test_an_uncovered_finding_is_reported(self):
        self.assertEqual(len(secret_scan.audit([self.finding()], [])), 1)

    def test_a_covered_finding_is_not(self):
        entries, _ = parse(
            f"a.txt | aws-access-key-id | {secret_scan.digest(AWS_ID)} | "
            f"A positive control for the AWS rule.")
        self.assertEqual(secret_scan.audit([self.finding()], entries), [])

    def test_an_entry_naming_another_rule_covers_nothing(self):
        entries, _ = parse(
            f"a.txt | github-token | {secret_scan.digest(AWS_ID)} | "
            f"The right value under the wrong rule.")
        problems = secret_scan.audit([self.finding()], entries)
        # Two ways round: the finding is unexplained and the entry is stale.
        self.assertEqual(len(problems), 2)

    def test_a_stale_entry_fails_the_gate(self):
        """The half that keeps the other half honest.

        A suppression whose finding has gone is a decision nobody has re-read,
        so when a finding clears, the build says which entry covered it.
        """
        entries, _ = parse(
            "a.txt | aws-access-key-id | 0123456789ab | The value this entry named has moved.")
        problems = secret_scan.audit([], entries)
        self.assertEqual(len(problems), 1)
        self.assertIn("no longer matches", problems[0])


class EmptySubject(unittest.TestCase):
    """A clean report over a subject nobody read is the failure, not the pass.

    Unguarded, a scan of no files and a scan with no rules would print the
    sentence a clean tree prints.
    """

    def test_a_scan_of_no_files_fails(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "empty"
            root.mkdir()
            code, _, err = run(root)
        self.assertEqual(code, 1)
        self.assertIn("scanned no files", err)

    def test_a_scan_with_no_rules_fails(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "a.txt").write_text("nothing here\n", encoding="utf-8")
            with mock.patch.object(secret_scan, "RULES", []):
                code, _, err = run(root)
        self.assertEqual(code, 1)
        self.assertIn("no rules", err)


class Walking(unittest.TestCase):
    def test_a_binary_file_is_not_scanned(self):
        # A text file sits beside it, so the empty-subject check above cannot be
        # what makes this pass — the walk read something, and skipped the blob.
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "image.bin").write_bytes(b"\x00\x01" + AWS_ID.encode("ascii"))
            (root / "kept.txt").write_text("nothing here\n", encoding="utf-8")
            code, out, _ = run(root)
        self.assertEqual(code, 0)
        self.assertIn("1 file(s)", out)

    def test_build_output_is_not_descended_into(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "obj").mkdir()
            (root / "obj" / "leak.txt").write_text(f"aws={AWS_ID}\n", encoding="utf-8")
            (root / "kept.txt").write_text("nothing here\n", encoding="utf-8")
            code, out, _ = run(root)
        self.assertEqual(code, 0)
        self.assertIn("1 file(s)", out)

    def test_every_shape_of_build_output_is_skipped_at_the_path_it_really_takes(self):
        """The subject is the walk's reach, not one directory name.

        §4.1 puts build output under `artifacts/`, and the subdirectories it
        draws are not all called `obj` or `bin`, so each real path is planted
        separately and the file count says the walk declined it.
        """
        for relative in ("artifacts/obj/Catalog.Api/debug",
                         "artifacts/bin/Catalog.Api/debug",
                         "artifacts/publish/Catalog.Api/release",
                         "artifacts/package/release"):
            with self.subTest(relative), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                planted = root / relative
                planted.mkdir(parents=True)
                (planted / "appsettings.json").write_text(f"aws={AWS_ID}\n", encoding="utf-8")
                (root / "kept.txt").write_text("nothing here\n", encoding="utf-8")
                code, out, _ = run(root)
                self.assertEqual(code, 0)
                self.assertIn("1 file(s)", out)

    def test_a_source_directory_called_artifacts_is_still_scanned(self):
        """The subject is the depth the exclusion reaches, not the name it uses.

        `artifacts` means build output at the repository root and nowhere else,
        so a basename match would silently take a source directory of that name
        out of the gate's reach. The planted secret is the assertion.
        """
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            nested = root / "src" / "Catalog.Api" / "artifacts"
            nested.mkdir(parents=True)
            (nested / "appsettings.json").write_text(f"aws={AWS_ID}\n", encoding="utf-8")
            code, _, err = run(root)
        self.assertEqual(code, 1)
        self.assertIn("aws-access-key-id", err)
        self.assertIn("artifacts", err)


class RealRepository(unittest.TestCase):
    def test_the_repository_passes_its_own_gate(self):
        """The suite's positive control over the real tree.

        The summary names a non-zero count of accepted findings, so a scanner
        that had silently stopped matching fails here.
        """
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = secret_scan.main([])
        self.assertEqual(code, 0, out.getvalue())
        self.assertNotIn(" 0 accepted finding(s)", out.getvalue())


if __name__ == "__main__":
    unittest.main()
