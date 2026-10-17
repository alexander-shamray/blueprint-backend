"""The release inputs the rollout refuses: a tag, a digest, the revision a tag names."""

from __future__ import annotations

import json
import unittest

import descriptor_model
import release_inputs


SHA = "sha256:" + "a" * 64


def verification(*digests: str) -> list[dict]:
    """The document `cosign verify -o json` prints: one entry per signature."""
    return [
        {"critical": {"image": {"docker-manifest-digest": digest}, "type": "cosign container image signature"}}
        for digest in digests
    ]


class TagTests(unittest.TestCase):
    """The tag as Helm's parser reads it: `strvals` splits `--set-string
    image.tag="$TAG"` on a comma, so the injection below sets a valid tag AND
    a registry, and render-time validation passes.
    """

    def test_ordinary_tags_are_accepted(self) -> None:
        for tag in ("deadbeef", "1.2.3", "v1-2-3", "a" * 63,
                    "0123456789abcdef0123456789abcdef01234567"):
            with self.subTest(tag=tag):
                release_inputs.validate_tag(tag)

    def test_a_comma_is_a_second_assignment_and_is_refused(self) -> None:
        """The finding, exactly as reported."""
        with self.assertRaises(descriptor_model.PlanError) as raised:
            release_inputs.validate_tag("deadbeef,image.registry=attacker.example")

        self.assertIn("--set-string", str(raised.exception))

    def test_the_other_strvals_metacharacters_are_refused(self) -> None:
        for tag in ("a=b", "a b", "a{b}", "a[0]", "a\\,b", "a\nb"):
            with self.subTest(tag=tag):
                with self.assertRaises(descriptor_model.PlanError):
                    release_inputs.validate_tag(tag)

    def test_it_matches_the_chart_rather_than_inventing_an_alphabet(self) -> None:
        """`commerce.tag` refuses these three by name, and a looser rule here
        would pass something the chart then rejects mid-`helm upgrade` — after
        the release has started, which is the failure that validation exists to
        move earlier."""
        for tag in ("Release_1", "release..1", "release.-1"):
            with self.subTest(tag=tag):
                with self.assertRaises(descriptor_model.PlanError):
                    release_inputs.validate_tag(tag)

    def test_a_migrator_workload_has_a_tighter_budget(self) -> None:
        """63 is the LABEL's budget, not the Job name's: `_migration-job.tpl`
        refuses `<workload>-migrate-<tag>` past 63 only at render time, after
        the stable track is scaled up.
        """
        plan = descriptor_model.load_plan()
        prefix = release_inputs.migration_prefix("catalog-api", plan)
        self.assertEqual(prefix, "catalog-api-migrate-")

        release_inputs.validate_tag("a" * (63 - len(prefix)), prefix)
        with self.assertRaises(descriptor_model.PlanError) as raised:
            release_inputs.validate_tag("a" * (64 - len(prefix)), prefix)

        self.assertIn("job-name", str(raised.exception))

    def test_a_databaseless_workload_has_no_migration_budget(self) -> None:
        """The gateway owns no database (§10.1), so its chart renders no Job
        and its tags are bounded only by the label."""
        plan = descriptor_model.load_plan()

        self.assertIsNone(release_inputs.migration_prefix("gateway", plan))
        release_inputs.validate_tag("a" * 63, release_inputs.migration_prefix("gateway", plan))

    def test_the_bff_spends_its_tag_budget_on_a_migration_job(self) -> None:
        """ADR-051's projection gives the BFF a schema, so its chart renders
        the hook and its tag is bounded by the Job's name."""
        plan = descriptor_model.load_plan()

        self.assertEqual(release_inputs.migration_prefix("web-bff", plan), "web-bff-migrate-")

    def test_length_and_emptiness(self) -> None:
        with self.assertRaises(descriptor_model.PlanError):
            release_inputs.validate_tag("")
        with self.assertRaises(descriptor_model.PlanError) as raised:
            release_inputs.validate_tag("a" * 64)

        self.assertIn("63", str(raised.exception))


class ImageRevisionTests(unittest.TestCase):
    """An image tag names a commit, because §15.2 builds it that way."""

    def test_a_commit_sha_is_the_revision(self) -> None:
        self.assertEqual("a" * 40, release_inputs.image_revision("a" * 40))

    def test_a_version_tag_names_no_revision(self) -> None:
        with self.assertRaises(descriptor_model.PlanError) as raised:
            release_inputs.image_revision("1.4.2")

        self.assertIn("§15.2", str(raised.exception))

    def test_an_abbreviation_is_refused(self) -> None:
        """It resolves against the history like the full name does, and then
        the rollout reports a revision spelled differently from the tag it
        handed to Helm — two strings for the one fact ADR-050 is about."""
        with self.assertRaises(descriptor_model.PlanError):
            release_inputs.image_revision("a" * 12)

    def test_upper_case_is_refused(self) -> None:
        """git prints lower case, and the comparison is textual."""
        with self.assertRaises(descriptor_model.PlanError):
            release_inputs.image_revision("A" * 40)

    def test_the_tag_alphabet_admits_what_names_no_revision(self) -> None:
        """validate_tag is the Helm-injection guard, and a version tag and an
        abbreviated commit both pass it — which is why ADR-050's rule is a
        second check rather than a tightening of that one. Upper case is the
        one refusal the two share, and for the unrelated reason that a tag is
        a DNS-1123 label."""
        for tag in ("1.4.2", "a" * 12):
            with self.subTest(tag=tag):
                release_inputs.validate_tag(tag)


class DigestTests(unittest.TestCase):
    """The digest as Helm's parser would read it, as the tag is above."""

    def test_a_registry_digest_is_accepted(self) -> None:
        release_inputs.validate_digest(SHA)

    def test_what_strvals_would_split_is_refused(self) -> None:
        for digest in (SHA + ",image.registry=attacker.example", SHA + "\n", "sha256:" + "A" * 64,
                       "sha256:" + "a" * 63, "sha512:" + "a" * 64, "", "latest"):
            with self.subTest(digest=digest):
                with self.assertRaises(descriptor_model.PlanError):
                    release_inputs.validate_digest(digest)

    def test_it_is_the_chart_s_rule(self) -> None:
        """`commerce.digest` refuses what this does not, and the rollout would
        fail inside `helm upgrade` instead of in front of it."""
        helpers = (descriptor_model.ROOT / "deploy/helm/common/templates/_helpers.tpl").read_text(encoding="utf-8")

        self.assertIn(r'regexMatch "^sha256:[a-f0-9]{64}$"', helpers)

    def test_the_digest_cosign_verified_is_the_one_read(self) -> None:
        self.assertEqual(SHA, release_inputs.verified_digest(verification(SHA, SHA)))

    def test_real_cosign_output_is_read(self) -> None:
        """What cosign v3.1.3 printed for the first image the pipeline signed.

        A hand-built document shows the reader agrees with its author, and
        this one shows it agrees with cosign.
        """
        path = descriptor_model.CANARY / "fixtures" / "cosign-verify-catalog-api.json"
        document = json.loads(path.read_text(encoding="utf-8"))

        self.assertEqual(
            "sha256:d07220003b94411646a922059a69eaa79f5a7ec395357e11520224d229936bfd",
            release_inputs.verified_digest(document))
        self.assertEqual(3, len(document))

    def test_real_cosign_output_with_one_entry_over_another_digest_is_refused(self) -> None:
        path = descriptor_model.CANARY / "fixtures" / "cosign-verify-catalog-api.json"
        document = json.loads(path.read_text(encoding="utf-8"))
        document[1]["critical"]["image"]["docker-manifest-digest"] = "sha256:" + "b" * 64

        with self.assertRaises(descriptor_model.PlanError):
            release_inputs.verified_digest(document)

    def test_nothing_verified_is_no_digest(self) -> None:
        for document in ([], {}, None, "sha256"):
            with self.subTest(document=document):
                with self.assertRaises(descriptor_model.PlanError):
                    release_inputs.verified_digest(document)

    def test_signatures_over_two_digests_pin_neither(self) -> None:
        with self.assertRaises(descriptor_model.PlanError) as raised:
            release_inputs.verified_digest(verification(SHA, "sha256:" + "b" * 64))

        self.assertIn("2 different digests", str(raised.exception))

    def test_an_entry_without_the_digest_is_refused_rather_than_skipped(self) -> None:
        with self.assertRaises(descriptor_model.PlanError):
            release_inputs.verified_digest(verification(SHA) + [{"critical": {}}])

    def test_a_verified_digest_that_is_not_one_is_refused(self) -> None:
        with self.assertRaises(descriptor_model.PlanError):
            release_inputs.verified_digest(verification("sha256:short"))


class InstalledTagTests(unittest.TestCase):
    """The stable track's tag is read from the release, not assumed."""

    def test_the_tag_is_read(self) -> None:
        self.assertEqual("a" * 40, release_inputs.installed_tag({"image": {"tag": "a" * 40}}))

    def test_a_release_with_no_tag_is_refused(self) -> None:
        """Every chart requires the tag, so a release without one is not a
        default to fall back on — it is a release whose revision cannot be
        read, and the baseline's probe routes come from that revision."""
        for values in ({}, {"image": {}}, {"image": {"tag": "   "}}, {"image": {"tag": 7}}):
            with self.subTest(values=values):
                with self.assertRaises(descriptor_model.PlanError):
                    release_inputs.installed_tag(values)


if __name__ == "__main__":
    unittest.main()
