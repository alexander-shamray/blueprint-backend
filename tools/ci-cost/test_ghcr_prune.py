"""The prune's suite: what it keeps is the subject, over a fake registry and a fake REST API.

    cd tools/ci-cost && python -m unittest
"""

import contextlib
import io
import re
import unittest
import zlib
from datetime import datetime, timezone
from pathlib import Path
from unittest import mock

import ghcr_prune

REPO = "owner/repo"
NOW = datetime(2026, 11, 9, tzinfo=timezone.utc)
RECENT = "2026-11-08T00:00:00Z"
OLD = "2026-10-01T00:00:00Z"


def digest(name):
    return "sha256:" + name.encode().hex().ljust(64, "0")


def commit(name):
    return name.encode().hex().ljust(40, "0")


def referrer_tag(name):
    return digest(name).replace(":", "-")


def version(name, tags=(), when=OLD, updated=None):
    return {"id": zlib.crc32(name.encode()), "name": digest(name), "created_at": when, "updated_at": updated or when,
            "metadata": {"container": {"tags": list(tags)}}}


def image_family(name, when=OLD, tags=None):
    """One published image as the registry holds it: an index, its two children, the referrers tag and a bundle."""
    return [version(name, tags if tags is not None else [commit(name)], when),
            version(f"{name}-platform", when=when), version(f"{name}-attestation", when=when),
            version(f"{name}-referrers", [referrer_tag(name)], when), version(f"{name}-bundle", when=when)]


def family_manifests(name):
    return {digest(name): {"manifests": [{"digest": digest(f"{name}-platform")},
                                         {"digest": digest(f"{name}-attestation")}]},
            digest(f"{name}-referrers"): {"manifests": [{"digest": digest(f"{name}-bundle")}]}}


class Registry:
    def __init__(self, *names, refuse=()):
        self.bodies, self.refuse = {}, set(refuse)
        for name in names:
            self.bodies.update(family_manifests(name))

    def __call__(self, package, reference):
        if reference in self.refuse:
            raise LookupError(f"{package}@{reference}: HTTP 500")
        return self.bodies.get(reference, {})


def names_of(versions):
    return {row["name"] for row in versions}


def newer_images():
    """Enough recent images that an old one is not among the newest, which would keep it."""
    return [version(f"newer{n}", [commit(f"newer{n}")], RECENT) for n in range(ghcr_prune.KEEP_NEWEST)]


class Plan(unittest.TestCase):
    def going(self, versions, registry, rolled=frozenset()):
        return names_of(ghcr_prune.plan(registry, "pkg", versions, set(rolled), NOW, workers=1))

    def test_an_old_image_nothing_names_goes_with_everything_only_it_needs(self):
        versions = image_family("gone") + [version("orphan")]
        self.assertEqual(self.going(versions + newer_images(), Registry("gone")), names_of(versions))

    def test_a_recent_image_keeps_its_children_signature_and_attestations_however_old_they_are(self):
        versions = [version("kept", [commit("kept")], RECENT)] + image_family("kept")[1:]
        self.assertEqual(self.going(versions, Registry("kept")), set())

    def test_the_main_tag_keeps_an_old_image_and_its_whole_family(self):
        versions = image_family("current", tags=[commit("current"), "main"]) + newer_images()
        self.assertEqual(self.going(versions, Registry("current")), set())

    def test_a_tag_the_rollout_named_keeps_an_old_image_and_its_whole_family(self):
        versions = image_family("rolled") + newer_images()
        self.assertEqual(self.going(versions, Registry("rolled"), {commit("rolled")}), set())

    def test_a_tag_that_is_no_commit_or_referrer_was_put_there_by_hand_and_stays(self):
        versions = image_family("release", tags=["v1.2.3"]) + newer_images()
        self.assertEqual(self.going(versions, Registry("release")), set())

    def test_a_digest_the_rollout_recorded_stays_after_a_rebuild_moves_its_commit_tag(self):
        """ADR-201: the rebuilt bytes took the commit tag, and the rollout's own tag keeps what a release pins."""
        versions = (image_family("pinned", tags=["rollout-42-1"]) + image_family("rebuilt", tags=[commit("pinned")])
                    + newer_images())
        self.assertEqual(self.going(versions, Registry("pinned", "rebuilt"), {commit("pinned")}), set())

    def test_only_the_newest_images_outlast_the_window(self):
        count = ghcr_prune.KEEP_NEWEST + 2
        versions = [version(f"i{n}", [commit(f"i{n}")], f"2026-09-{n + 1:02d}T00:00:00Z") for n in range(count)]
        self.assertEqual(self.going(versions, Registry()), {digest("i0"), digest("i1")})

    def test_a_rebuild_that_tagged_an_old_version_counts_from_when_it_was_tagged(self):
        versions = [version("rebuilt", [commit("rebuilt")], OLD, updated=RECENT)]
        versions += [version(f"i{n}", [commit(f"i{n}")], "2026-10-02T00:00:00Z") for n in range(ghcr_prune.KEEP_NEWEST)]
        self.assertNotIn(digest("rebuilt"), self.going(versions, Registry()))

    def test_an_untagged_version_inside_the_window_stays_whatever_names_it(self):
        self.assertEqual(self.going([version("fresh", when=RECENT)], Registry()), set())

    def test_a_manifest_left_unread_judges_nothing(self):
        versions = image_family("kept", when=RECENT) + [version("orphan")]
        with self.assertRaisesRegex(LookupError, "HTTP 500"):
            self.going(versions, Registry("kept", refuse=[digest("kept-platform")]))

    def test_an_image_goes_before_its_referrers_and_they_before_their_bundles_oldest_first(self):
        bundle = version("bundle", when="2026-09-01T00:00:00Z")
        referrers = version("referrers", [referrer_tag("newer")], "2026-09-02T00:00:00Z")
        newer, older = (version(name, [commit(name)], when) for name, when in (
            ("newer", "2026-10-02T00:00:00Z"), ("older", "2026-10-01T00:00:00Z")))
        order = sorted([bundle, referrers, newer, older], key=ghcr_prune.deletion_order)
        self.assertEqual([row["name"] for row in order],
                         [digest("older"), digest("newer"), digest("referrers"), digest("bundle")])


class RolledTags(unittest.TestCase):
    def fetch(self, titles, total=None):
        runs = [{"id": n, "display_title": title} for n, title in enumerate(titles)]

        def answer(path):
            if "per_page=1&" in path or path.endswith("per_page=1"):
                return {"total_count": len(runs) if total is None else total, "workflow_runs": []}
            page = int(re.search(r"[?&]page=(\d+)", path)[1])
            return {"workflow_runs": runs if page == 1 else []}

        return answer

    def test_the_title_deploy_yml_writes_names_the_commit(self):
        self.assertEqual(ghcr_prune.rolled_tags(self.fetch([f"Roll catalog-api to {commit('a')}"]), REPO),
                         {commit("a")})

    def test_a_tag_the_rollout_refuses_names_nothing_and_stops_nothing(self):
        titles = ["Roll catalog-api to main", "Roll catalog-api to a b", "Roll catalog-api to "]
        self.assertEqual(ghcr_prune.rolled_tags(self.fetch(titles), REPO), set())

    def test_a_title_it_cannot_read_refuses_rather_than_missing_a_tag(self):
        with self.assertRaisesRegex(LookupError, "names no tag"):
            ghcr_prune.rolled_tags(self.fetch(["Deploy"]), REPO)

    def test_more_rollouts_than_the_endpoint_answers_refuses(self):
        with self.assertRaisesRegex(LookupError, "could be missed"):
            ghcr_prune.rolled_tags(self.fetch([], total=ghcr_prune.SEARCH_CAP + 1), REPO)

    def test_deploy_yml_writes_the_title_this_reads(self):
        # A renamed run-name would have the prune refuse every run once a dispatch carried it.
        workflow = Path(__file__).parents[2] / ".github" / "workflows" / "deploy.yml"
        written = re.search(r"format\('(Roll \{0\} to \{1\})', inputs\.workload, inputs\.tag\)",
                            workflow.read_text(encoding="utf-8"))
        self.assertIsNotNone(written, "deploy.yml's run-name no longer names the workload and the tag")
        self.assertIsNotNone(ghcr_prune.ROLLOUT_TITLE.fullmatch(written[1].format("catalog-api", "abc")))

    def test_deploy_yml_records_a_tag_this_keeps(self):
        # ADR-201: a record shaped like a commit or a referrers tag would protect nothing.
        workflow = Path(__file__).parents[2] / ".github" / "workflows" / "deploy.yml"
        written = re.search(r"RECORD: (\S*)\$\{\{ github\.run_id \}\}(\S*)\$\{\{ github\.run_attempt \}\}\s*$",
                            workflow.read_text(encoding="utf-8"), re.MULTILINE)
        self.assertIsNotNone(written, "deploy.yml no longer records a tag of the run's own on what it verified")
        recorded = version("pinned", [f"{written[1]}42737{written[2]}1"])
        self.assertEqual(ghcr_prune.kept_for_themselves([recorded], set(), NOW), [recorded])


class ImagePackages(unittest.TestCase):
    def test_the_shipped_matrix_names_each_image_under_the_repository(self):
        packages = ghcr_prune.image_packages(REPO)
        self.assertIn("repo/gateway", packages)
        self.assertIn("repo/catalog-migrator", packages)
        self.assertEqual(len(packages), len(set(packages)))
        self.assertFalse([package for package in packages if "$" in package or "{" in package])

    def test_the_count_is_the_matrix_s_own(self):
        text = ghcr_prune.CI_WORKFLOW.read_text(encoding="utf-8")
        strategy = text[text.index("&image-legs"):text.index("*image-legs")]
        self.assertEqual(len(ghcr_prune.image_packages(REPO)), len(re.findall(r"\bdockerfile:", strategy)))

    def test_a_workflow_that_names_no_image_refuses(self):
        with self.assertRaisesRegex(LookupError, "names no image"):
            ghcr_prune.image_packages(REPO, mock.Mock(read_text=lambda encoding: "jobs: {}\n"))


class Main(unittest.TestCase):
    def setUp(self):
        self.versions = image_family("gone") + newer_images()
        self.deleted = []

    def fetch(self, path):
        page = re.search(r"[?&]page=(\d+)", path)
        first = page is None or page[1] == "1"
        if "actions/workflows" in path:
            return {"total_count": 0, "workflow_runs": []}
        if path.startswith("users/") and "/packages" not in path:
            return {"type": "User"}
        if "/versions" in path:
            if "repo%2Fmissing" in path:
                raise LookupError(f"{path}: gh: Package not found. (HTTP 404)")
            return self.versions if first else []
        # What a job's GITHUB_TOKEN answered on 2026-11-09, in the first run that was not a person's.
        raise LookupError(f"{path}: gh: Invalid argument. (HTTP 400)")

    def run_main(self, *argv, registry=None, delete=None, packages=None):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = ghcr_prune.main(["--repo", REPO, *argv], fetch=self.fetch, manifest=registry or Registry("gone"),
                                   now=NOW, delete=delete or self.deleted.append,
                                   packages=packages or ["repo/pkg"])
        return code, out.getvalue()

    def test_without_the_flag_it_reports_and_deletes_nothing(self):
        code, out = self.run_main()
        self.assertEqual((code, self.deleted), (0, []))
        self.assertIn("5 versions in all", out)
        self.assertIn("Nothing deleted", out)

    def test_with_the_flag_it_deletes_by_version_id_under_the_escaped_package(self):
        self.run_main("--delete")
        self.assertEqual(len(self.deleted), 5)
        self.assertTrue(all("/packages/container/repo%2Fpkg/versions/" in path for path in self.deleted))

    def test_a_capped_run_takes_the_image_and_leaves_its_children_for_a_later_one(self):
        self.versions = image_family("gone") + newer_images()
        with mock.patch.object(ghcr_prune, "MAX_DELETES", 1):
            self.run_main("--delete")
        self.assertEqual(self.deleted, [f"users/owner/packages/container/repo%2Fpkg/versions/"
                                        f"{version('gone')['id']}"])

    def test_only_the_packages_it_was_given_are_read_and_the_owners_listing_is_never_asked(self):
        code, _ = self.run_main("--delete")
        self.assertEqual(code, 0)
        self.assertTrue(all("repo%2Fpkg" in path for path in self.deleted))

    def test_a_package_it_cannot_read_fails_the_run_and_the_others_are_still_judged(self):
        code, out = self.run_main("--delete", packages=["repo/missing", "repo/pkg"])
        self.assertEqual((code, len(self.deleted)), (1, 5))
        self.assertIn("repo/missing: nothing judged", out)

    def test_a_run_deletes_no_more_than_the_cap(self):
        self.versions = [version(f"v{n}") for n in range(ghcr_prune.MAX_DELETES + 3)]
        self.run_main("--delete")
        self.assertEqual(len(self.deleted), ghcr_prune.MAX_DELETES)

    def test_a_package_with_an_unread_manifest_is_reported_failed_and_loses_nothing(self):
        self.versions = image_family("kept", when=RECENT) + [version("orphan")]
        code, out = self.run_main("--delete", registry=Registry("kept", refuse=[digest("kept")]))
        self.assertEqual((code, self.deleted), (1, []))
        self.assertIn("nothing judged", out)

    def test_a_refused_delete_fails_the_run_and_keeps_what_the_tag_still_names(self):
        tried = []

        def refuse(path):
            tried.append(path)
            raise LookupError(f"{path}: HTTP 403")

        code, out = self.run_main("--delete", delete=refuse)
        self.assertEqual((code, len(tried)), (1, 1))
        self.assertIn("not deleted", out)
        self.assertEqual(tried[0].rsplit("/", 1)[1], str(version("gone")["id"]))


if __name__ == "__main__":
    unittest.main()
