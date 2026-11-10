"""sync_pr.py: list, verify, review-diff and local-suites, over a real checkout with a bare origin and a fake gh."""

import contextlib
import hashlib
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import types
import unittest
from pathlib import Path
from unittest import mock

import dispatch_board
import sync_pr

NEW_H = "1234567890abcdef1234567890abcdef12345678"
OLD_H = "a" * 40
HEAD_NAME = f"chore/harness-sync-{NEW_H[:7]}"
RUN_URL = "https://github.com/alexander-shamray/blueprint-harness/actions/runs/123"
BODY = f"A sync.\n\n{sync_pr.FOOTER}, in run {RUN_URL}.\n"
SEED = ("-c", "user.name=Seed", "-c", "user.email=seed@example.invalid", "-c", "commit.gpgsign=false")
# A deny only the target's config adds, so a render that leaves the config out differs from the expected one.
CONFIG = {"repo": "o/r", "task_list": ".todo/r.md", "local": ["docs/local.md"],
          "settings": {"deny": ["Bash(target-only:*)"]}}
SETTINGS_OLD = '{"old": true}\n'
# The renderer a target holds, so the proof runs the code a real sync carries, never a stand-in.
RENDER_TEXT = (Path(__file__).resolve().parents[2] / ".harness" / "render.py").read_text(encoding="utf-8")
BASE_TEXT = json.dumps({"permissions": {"allow": ["Bash(ls:*)"], "deny": ["Bash(rm:*)"]}, "hooks": {}}) + "\n"
HEAD_PATHS = ["docs/a.md", "docs/b.md", ".claude/settings.json", ".harness/render.py", ".harness/settings.base.json"]


def rendered(base_text=BASE_TEXT, config=CONFIG):
    module = types.ModuleType("_expected_render")
    exec(compile(RENDER_TEXT, "render.py", "exec"), module.__dict__)
    cfg = types.SimpleNamespace(settings=config.get("settings", {}))
    return module.dump_settings(module.render_settings(json.loads(base_text), cfg))


SETTINGS_NEW = rendered()
MAIN_FILES = {
    "README.md": "target\n",
    "docs/local.md": "first\nsee docs/c.md for the old rule\n",
    "docs/a.md": "A old\n",
    "docs/c.md": "C\n",
    ".claude/settings.json": SETTINGS_OLD,
}
PAYLOAD = {
    "docs/a.md": "A new\n",
    "docs/b.md": "BODY-OF-B\n",
    ".harness/render.py": RENDER_TEXT,
    ".harness/settings.base.json": BASE_TEXT,
}


def sha(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def manifest(commit, paths):
    return json.dumps({"harness_commit": commit, "files": {p: sha(p) for p in sorted(paths)}}, indent=2) + "\n"


def git(cwd, *args, data=None):
    done = subprocess.run(["git", *args], cwd=cwd, input=data, capture_output=True, check=False)
    if done.returncode != 0:
        raise AssertionError(f"git {' '.join(args)} failed: {done.stderr.decode('utf-8', 'replace')}")
    return done.stdout.decode("utf-8")


class FakeGh:
    """The gh calls sync_pr makes; git calls fall through to the real thing in the checkout."""

    def __init__(self, root):
        self.root = root
        self.prs = []
        self.compare = {"status": "behind", "commits": []}  # main...<commit>
        self.compare_old = {"status": "ahead",  # <the base's commit>...<commit>
                            "commits": [{"sha": "b" * 40, "commit": {"message": "feat: the new rule\n\nbody"}}]}
        self.tree = []
        self.compare_by = {}  # a substring of the compare path -> its answer, before the defaults
        self.truncated = False
        self.fail = None  # a substring of the gh argv that fails
        self.calls = []

    def __call__(self, argv, cwd=None, timeout=60):
        if argv[0] != "gh":
            done = subprocess.run(argv, cwd=self.root, capture_output=True, encoding="utf-8",
                                  errors="replace", check=False)
            return done.returncode, done.stdout, done.stderr
        self.calls.append(list(argv))
        if self.fail and self.fail in " ".join(argv):
            return 1, "", "simulated gh failure"
        if argv[1:3] == ["pr", "list"]:
            return 0, json.dumps(self.prs), ""
        if argv[1] == "api" and "/compare/" in argv[2]:
            special = next((r for k, r in self.compare_by.items() if k in argv[2]), None)
            return 0, json.dumps(special or (self.compare if "/compare/main..." in argv[2] else self.compare_old)), ""
        if argv[1] == "api" and "/git/trees/" in argv[2]:
            return 0, json.dumps({"truncated": self.truncated, "tree": self.tree}), ""
        raise AssertionError(f"unexpected gh call {argv}")


class SyncCase(unittest.TestCase):
    config = CONFIG  # the base's .harness/config.json; a subclass sets its own globs

    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        base = Path(tmp.name)
        self.root, origin = base / "target", base / "origin.git"
        git(base, "init", "-q", "--bare", "-b", "main", str(origin))
        git(base, "init", "-q", "-b", "main", str(self.root))
        git(self.root, "config", "core.autocrlf", "false")
        git(self.root, "remote", "add", "origin", str(origin))
        self.write({**MAIN_FILES, ".harness/config.json": json.dumps(self.config)})
        self.write({".harness/manifest.json": manifest(OLD_H, ["docs/a.md", "docs/c.md", ".claude/settings.json"])})
        git(self.root, "add", "-A")
        git(self.root, *SEED, "commit", "-q", "-m", "seed")
        git(self.root, "push", "-q", "origin", "main")
        self.main = git(self.root, "rev-parse", "HEAD").strip()
        self.head_files = {
            **PAYLOAD, ".claude/settings.json": SETTINGS_NEW, ".harness/manifest.json": manifest(NEW_H, HEAD_PATHS),
        }
        self.deleted = ["docs/c.md"]
        self.gh = FakeGh(self.root)
        for patch in (mock.patch.object(sync_pr, "run", self.gh), mock.patch.object(sync_pr, "ROOT", self.root)):
            patch.start()
            self.addCleanup(patch.stop)

    def write(self, files):
        for rel, text in files.items():
            path = self.root / rel
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(text.encode("utf-8"))

    def blob(self, text):
        return git(self.root, "hash-object", "--stdin", data=text.encode("utf-8")).strip()

    def publish_head(self):
        """The head branch on origin, made from main with head_files written and `deleted` removed."""
        git(self.root, "checkout", "-q", "-b", "work", self.main)
        self.write(self.head_files)
        for rel in self.deleted:
            (self.root / rel).unlink(missing_ok=True)
        git(self.root, "add", "-A")
        git(self.root, *SEED, "commit", "-q", "-m", "sync")
        git(self.root, "push", "-q", "--force", "origin", f"work:{HEAD_NAME}")
        oid = git(self.root, "rev-parse", "HEAD").strip()
        git(self.root, "checkout", "-q", "main")
        git(self.root, "branch", "-q", "-D", "work")
        return oid

    def start(self, payload=None, row=None, tree_mode="100644"):
        """Publish the head and fill the fake: the payload tree at NEW_H and one open pull request."""
        oid = self.publish_head()
        payload = PAYLOAD if payload is None else payload
        self.gh.tree = [{"path": f"payload/{p}", "mode": tree_mode, "type": "blob", "sha": self.blob(t)}
                        for p, t in payload.items()]
        self.gh.prs = [{"number": 7, "headRefName": HEAD_NAME, "headRefOid": oid, "baseRefName": "main",
                        "isCrossRepository": False, "body": BODY, **(row or {})}]
        return oid

    def cli(self, *argv):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            try:
                code = sync_pr.main(list(argv))
            except SystemExit as exit_:
                code = exit_.code
        return code, out.getvalue(), err.getvalue()

    def refused(self, text, *argv):
        code, out, err = self.cli(*(argv or ("verify", "7")))
        self.assertEqual(1, code, out + err)
        self.assertIn(text, out)
        return out


class HeadBlobsTests(SyncCase):
    def test_the_head_tree_is_read_from_its_root_whatever_the_working_directory(self):
        # `ls-tree` lists paths relative to the working directory, and a copy run from payload/ has one that is not
        # the root; --full-tree is what makes the listing the head's.
        oid = self.start()

        def from_subdirectory(argv, cwd=None, timeout=60):
            done = subprocess.run(argv, cwd=self.root / "docs", capture_output=True, encoding="utf-8",
                                  errors="replace", check=False)
            return done.returncode, done.stdout, done.stderr

        with mock.patch.object(sync_pr, "run", from_subdirectory):
            found = sync_pr.head_blobs(oid)
        self.assertIn(".claude/settings.json", found)
        self.assertIn("docs/b.md", found)
        self.assertNotIn("b.md", found)


class VerifyTests(SyncCase):
    def test_an_authentic_sync_verifies(self):
        self.start()
        code, out, err = self.cli("verify", "7")
        self.assertEqual((0, f"verified 7 {NEW_H}\n", ""), (code, out, err))
        compare = [c[2] for c in self.gh.calls if "/compare/" in c[2]]
        self.assertEqual([f"repos/{sync_pr.HARNESS_REPO}/compare/main...{NEW_H}",
                          f"repos/{sync_pr.HARNESS_REPO}/compare/{OLD_H}...{NEW_H}"], compare)

    def test_a_branch_name_alone_is_not_a_sync(self):
        self.start(row={"body": "A sync, but no footer.\n"})
        self.refused("#7 is not a sync pull request")
        self.gh.prs[0]["body"] = f"{BODY}\nNote added after the footer.\n"
        self.refused("#7 is not a sync pull request")

    def test_a_cross_repository_head_is_refused(self):
        self.start(row={"isCrossRepository": True})
        self.refused("#7 is not a sync pull request")

    def test_an_unlisted_number_is_refused(self):
        self.start()
        self.refused("#8 is not a sync pull request", "verify", "8")

    def test_a_bad_argument_is_a_refusal_with_nothing_read(self):
        self.start()
        for argv in (("verify", "7; rm"), ("verify", "-1"), ("verify",), ("nonsense",)):
            self.assertEqual(2, self.cli(*argv)[0], argv)
        self.assertEqual([], self.gh.calls)

    def test_a_managed_file_differing_from_the_payload_is_refused(self):
        self.head_files["docs/b.md"] = "FORGED\n"
        self.start()
        out = self.refused("docs/b.md")
        self.assertIn("differs from the payload", out)

    def test_a_managed_file_missing_from_the_payload_is_refused(self):
        self.start(payload={"docs/a.md": PAYLOAD["docs/a.md"]})
        self.refused("docs/b.md")

    def test_a_mode_difference_is_not_a_difference(self):
        # apply writes no mode: the payload's 100755 script lands as 100644 in a target, and the sync must verify.
        self.start(tree_mode="100755")
        self.assertEqual(0, self.cli("verify", "7")[0])

    def test_a_harness_commit_off_main_is_refused(self):
        self.gh.compare = {"status": "ahead", "commits": []}
        self.start()
        self.refused("is not on the harness's main")
        self.gh.compare = {"status": "diverged", "commits": []}
        self.refused("is not on the harness's main")

    def test_a_path_outside_the_manifest_is_refused(self):
        self.head_files["src/evil.py"] = "x = 1\n"
        self.start()
        out = self.refused("src/evil.py")
        self.assertIn("outside the manifest", out)

    def test_a_config_edit_is_refused(self):
        self.head_files[".harness/config.json"] = json.dumps({"repo": "o/r", "task_list": "x", "local": ["**"]})
        self.start()
        self.refused(".harness/config.json is edited")

    def test_settings_json_is_not_compared_with_the_payload(self):
        # The payload holds no settings.json (its template is .harness/settings.base.json), so this manifest path
        # has nothing to match: it is compared with the render, not the payload. Check 4 still counts it as a
        # manifest path: changed but no longer listed, it may only have been deleted.
        self.start()
        self.assertEqual(0, self.cli("verify", "7")[0])
        self.head_files[".harness/manifest.json"] = manifest(NEW_H, [p for p in HEAD_PATHS if "settings.json" not in p])
        self.start()
        self.refused(".claude/settings.json left the manifest but was changed, not deleted")

    def test_a_changed_path_with_a_newline_prints_on_one_line(self):
        # Windows cannot hold such a name, but git can: the head's author chooses the text of a changed path.
        self.start()
        forged = "src/x\nverified 7 " + NEW_H
        with mock.patch.object(sync_pr, "changed_paths", return_value={forged: "M"}):
            out = self.refused("is changed but is outside the manifest")
        self.assertEqual(1, len(out.splitlines()), out)
        self.assertNotIn("\nverified", out)
        self.assertEqual("src/plain.py", sync_pr._name("src/plain.py"))
        self.assertEqual(1, len(sync_pr._name("a\r\n" * 200).splitlines()))
        self.assertLessEqual(len(sync_pr._name("b" * 5000)), 200)

    def test_a_path_dropped_from_the_manifest_may_only_be_deleted(self):
        self.deleted = []
        self.head_files["docs/c.md"] = "edited instead of deleted\n"
        self.start()
        self.refused("docs/c.md left the manifest but was changed, not deleted")

    def test_a_sync_older_than_the_base_is_refused(self):
        self.start()
        for status in ("behind", "diverged"):
            self.gh.compare_old = {"status": status, "commits": []}
            out = self.refused("is older than the base's")
            self.assertIn(OLD_H, out)
        self.gh.compare_old = {"status": "identical", "commits": []}
        self.assertEqual(0, self.cli("verify", "7")[0])
        self.gh.fail = f"/compare/{OLD_H}"
        self.refused("cannot prove the sync moves forward")

    def advance_main(self, commit):
        """Main takes another sync after the head was cut: its manifest now names `commit`."""
        self.write({".harness/manifest.json": manifest(commit, ["docs/a.md", "docs/c.md", ".claude/settings.json"])})
        git(self.root, "add", "-A")
        git(self.root, *SEED, "commit", "-q", "-m", "another sync")
        git(self.root, "push", "-q", "origin", "main")

    def test_a_sync_behind_a_moved_main_is_refused_as_older(self):
        # The gate compares with main's tip, not with the commit the head was cut from.
        self.start()
        self.advance_main("c" * 40)
        self.gh.compare_by = {f"/compare/{'c' * 40}...{NEW_H}": {"status": "behind", "commits": []}}
        out = self.refused("is older than the base's")
        self.assertIn("c" * 40, out)

    def test_a_sync_behind_a_moved_main_that_is_still_forward_verifies(self):
        self.start()
        self.advance_main("c" * 40)
        self.gh.compare_by = {f"/compare/{'c' * 40}...{NEW_H}": {"status": "ahead", "commits": []}}
        self.assertEqual(0, self.cli("verify", "7")[0])

    def test_a_managed_file_dropped_from_the_manifest_is_refused(self):
        # The row goes with the file: a guard hook deleted and unlisted while the payload still ships it.
        self.start(payload={**PAYLOAD, "docs/c.md": "C\n"})
        self.refused("docs/c.md is a payload file but the manifest does not list it")

    def test_a_payload_file_the_manifest_does_not_list_is_refused_with_the_file_untouched(self):
        self.start(payload={**PAYLOAD, "docs/extra.md": "E\n"})
        self.refused("docs/extra.md is a payload file but the manifest does not list it")

    def test_a_payload_tree_entry_is_data_when_it_is_printed(self):
        self.start()
        self.gh.tree = [e for e in self.gh.tree if e["path"] != "payload/docs/b.md"]
        self.gh.tree += [{"path": "payload/docs/b.md", "mode": "100644", "type": "blob", "sha": "x\nforged line"},
                         {"path": "payload/docs/ev\nil.md", "mode": "100644", "type": "blob", "sha": "a" * 40}]
        out = self.refused("docs/b.md: not in the payload")
        self.assertNotIn("forged", out)
        self.assertNotIn("ev\nil", out)
        self.assertIn("ev\\nil.md", out)

    def test_a_truncated_tree_fails_closed(self):
        self.gh.truncated = True
        self.start()
        self.refused("truncated")

    def test_a_gh_failure_fails_closed(self):
        self.start()
        self.gh.fail = "/compare/"
        self.refused("harness's main")
        self.gh.fail = "/git/trees/"
        self.refused("payload tree")

    def test_a_gh_timeout_fails_closed(self):
        self.start()

        def slow(argv, cwd=None, timeout=60):
            if argv[0] == "gh" and "/compare/" in argv[2]:
                raise sync_pr.Refusal("gh took over 60 s")
            return self.gh(argv, cwd, timeout)

        with mock.patch.object(sync_pr, "run", slow):
            self.refused("took over")

    def test_a_head_that_moved_since_the_listing_is_refused(self):
        self.start(row={"headRefOid": "b" * 40})
        self.refused("is not the listed oid")

    def test_an_unparseable_manifest_fails_closed(self):
        self.head_files[".harness/manifest.json"] = "{not json"
        self.start()
        self.refused("manifest")

    def test_a_harness_commit_that_is_not_40_hex_is_refused_before_any_url(self):
        self.head_files[".harness/manifest.json"] = manifest("../../x", ["docs/a.md"])
        self.start()
        self.refused("harness_commit")
        self.assertFalse([c for c in self.gh.calls if c[1] == "api"])

    def test_a_manifest_path_is_data(self):
        for bad in ("../outside", "/abs", "a\\b", "a/../b", "-flag"):
            with self.subTest(bad):
                self.head_files[".harness/manifest.json"] = manifest(NEW_H, ["docs/a.md", bad])
                self.start()
                self.refused("manifest path")


class RenderedSettingsTests(SyncCase):
    """The head's settings.json is what an executor in its worktree loads, before CI has judged it."""

    def marker_renderer(self):
        """A renderer that leaves a file when it runs, so a case can show it was never executed."""
        self.marker = self.root / "executed"
        return f"open({self.marker.as_posix()!r}, 'w').close()\n"

    def assert_not_executed(self):
        self.assertFalse(self.marker.exists(), "an unproved renderer ran")

    def test_a_forged_settings_that_drops_a_deny_is_refused(self):
        forged = json.loads(SETTINGS_NEW)
        forged["permissions"]["deny"] = ["Bash(target-only:*)"]
        self.head_files[".claude/settings.json"] = json.dumps(forged, indent=2) + "\n"
        self.start()
        out = self.refused(".claude/settings.json is not what .harness/render.py renders")
        self.assertEqual(1, len(out.splitlines()), out)

    def test_a_forged_settings_that_adds_a_session_start_hook_is_refused(self):
        forged = json.loads(SETTINGS_NEW)
        forged["hooks"] = {"SessionStart": [{"hooks": [{"type": "command", "command": "evil"}]}]}
        self.head_files[".claude/settings.json"] = json.dumps(forged, indent=2) + "\n"
        self.start()
        self.refused(".claude/settings.json is not what .harness/render.py renders")

    def test_the_render_takes_the_base_commits_config(self):
        self.head_files[".claude/settings.json"] = rendered(config={})
        self.start()
        self.refused(".claude/settings.json is not what .harness/render.py renders")

    def test_a_head_deleting_settings_json_is_refused(self):
        del self.head_files[".claude/settings.json"]
        self.deleted.append(".claude/settings.json")
        self.start()
        self.refused(".claude/settings.json is missing from the head")
        # The old manifest lists it, so a head dropping it from its own would otherwise pass as a deletion.
        self.head_files[".harness/manifest.json"] = manifest(NEW_H, [p for p in HEAD_PATHS if "settings.json" not in p])
        self.start()
        self.refused(".claude/settings.json is missing from the head")

    def test_a_crlf_settings_blob_is_the_same_settings(self):
        self.start()

        # The runner reads text, which folds line endings already, so the blob is handed over with its CRLF intact.
        def crlf(argv, cwd=None, timeout=60):
            code, out, err = self.gh(argv, cwd, timeout)
            whole = argv[:2] == ["git", "show"] and argv[2].endswith(":.claude/settings.json")
            return code, out.replace("\n", "\r\n") if whole else out, err

        with mock.patch.object(sync_pr, "run", crlf):
            self.assertEqual((0, f"verified 7 {NEW_H}\n", ""), self.cli("verify", "7"))

    def test_a_renderer_that_is_not_the_payloads_is_never_executed(self):
        self.head_files[".harness/render.py"] = self.marker_renderer()
        self.start()
        self.refused(".claude/settings.json not checked: .harness/render.py is not the manifest's and the payload's")
        self.assert_not_executed()

    def test_a_base_that_is_not_the_payloads_is_not_trusted(self):
        forged = json.dumps({"permissions": {"allow": ["Bash(ls:*)"]}, "hooks": {}}) + "\n"
        self.head_files[".harness/settings.base.json"] = forged
        self.head_files[".claude/settings.json"] = rendered(base_text=forged)
        self.start()
        self.refused(".claude/settings.json not checked: .harness/settings.base.json is not the manifest's")

    def test_a_renderer_the_manifest_does_not_list_is_never_executed(self):
        text = self.marker_renderer()
        self.head_files[".harness/render.py"] = text
        self.head_files[".harness/manifest.json"] = manifest(NEW_H, [p for p in HEAD_PATHS if "render" not in p])
        self.start(payload={**PAYLOAD, ".harness/render.py": text})
        self.refused(".claude/settings.json not checked: .harness/render.py is not the manifest's and the payload's")
        self.assert_not_executed()

    def test_a_renderer_at_a_commit_off_the_harness_main_is_never_executed(self):
        # GitHub serves a fork's commit by oid through the parent, so a matching tree there proves nothing.
        text = self.marker_renderer()
        self.head_files[".harness/render.py"] = text
        self.gh.compare = {"status": "ahead", "commits": []}
        self.start(payload={**PAYLOAD, ".harness/render.py": text})
        self.refused(".claude/settings.json not checked: the payload is not proved")
        self.assert_not_executed()

    def test_an_unread_payload_tree_is_a_failure_line_and_runs_nothing(self):
        text = self.marker_renderer()
        self.head_files[".harness/render.py"] = text
        self.start(payload={**PAYLOAD, ".harness/render.py": text})
        self.gh.fail = "/git/trees/"
        self.refused(".claude/settings.json not checked: the payload is not proved")
        self.assert_not_executed()

    def test_a_renderer_that_raises_is_a_failure_line(self):
        text = "raise ValueError('the renderer failed')\n"
        self.head_files[".harness/render.py"] = text
        self.start(payload={**PAYLOAD, ".harness/render.py": text})
        out = self.refused("cannot render .claude/settings.json: ValueError('the renderer failed')")
        self.assertEqual(1, len(out.splitlines()), out)

    def test_a_base_the_renderer_refuses_is_a_failure_line(self):
        text = '{"permissions": {"deny": ["Write(x)"]}}\n'
        self.head_files[".harness/settings.base.json"] = text
        self.start(payload={**PAYLOAD, ".harness/settings.base.json": text})
        self.refused("cannot render .claude/settings.json: RenderError(")

    def test_the_renderer_is_read_from_the_head_never_the_working_tree(self):
        planted = self.marker_renderer()
        self.start()
        self.write({".harness/render.py": planted})
        self.assertEqual(0, self.cli("verify", "7")[0])
        self.assert_not_executed()


class PinnedHeadTests(SyncCase):
    """`--head <oid>` binds a verify or a review to the head a session checked out, not to origin's tip now."""

    def out_path(self):
        return str(self.root / "artifacts/review/sync-7-r1.diff")

    def commands(self, oid):
        return (("verify", "7", "--head", oid), ("review-diff", "7", self.out_path(), "--head", oid))

    def moved_on_origin(self):
        """Push another commit to the sync branch while the listing still names the first."""
        self.head_files["docs/b.md"] = PAYLOAD["docs/b.md"] + "moved\n"
        return self.publish_head()

    def test_the_pinned_head_verifies_and_reviews(self):
        oid = self.start()
        self.assertEqual((0, f"verified 7 {NEW_H}\n", ""), self.cli("verify", "7", "--head", oid))
        code, out, err = self.cli("review-diff", "7", self.out_path(), "--head", oid)
        self.assertEqual(0, code, out + err)
        self.assertIn(oid, Path(self.out_path()).read_text(encoding="utf-8"))

    def test_a_listing_that_names_another_head_is_refused(self):
        self.start()
        for argv in self.commands("c" * 40):
            with self.subTest(argv=argv[0]):
                code, out, err = self.cli(*argv)
                self.assertEqual(1, code, out + err)
                self.assertIn("#7: the listed head", out)
                self.assertIn("is not the pinned oid " + "c" * 40, out)
        self.assertFalse(Path(self.out_path()).exists())

    def test_a_branch_that_moved_after_the_pin_is_refused_though_the_listing_is_stale(self):
        pinned = self.start()
        moved = self.moved_on_origin()
        self.assertNotEqual(pinned, moved)
        # Without the pin the listing still agrees with the first head and the fetched tip is refused as moved.
        self.refused("is not the listed oid")
        for argv in self.commands(moved):
            with self.subTest(argv=argv[0], pin="the new tip"):
                self.assertIn("is not the pinned oid", self.cli(*argv)[1])
        for argv in self.commands(pinned):
            with self.subTest(argv=argv[0], pin="the first head"):
                code, out, err = self.cli(*argv)
                self.assertEqual(1, code, out + err)
                self.assertIn(f"the fetched head {moved} is not the pinned oid {pinned}", out)
        self.assertFalse(Path(self.out_path()).exists())

    def test_a_pin_other_than_the_agreeing_listing_and_fetch_is_refused(self):
        self.start()
        moved = self.moved_on_origin()
        self.gh.prs[0]["headRefOid"] = moved
        for argv in self.commands("d" * 40):
            with self.subTest(argv=argv[0]):
                self.assertEqual(1, self.cli(*argv)[0])
        # The listing and the fetch agree on `moved`, so a pin on it passes the pin; its edited file does not verify.
        out = self.cli("verify", "7", "--head", moved)[1]
        self.assertNotIn("pinned oid", out)
        self.assertIn("docs/b.md", out)

    def test_a_pin_that_is_not_40_lowercase_hex_is_a_refusal_with_nothing_read(self):
        self.start()
        for bad in ("", "abc", "A" * 40, "g" * 40, "a" * 41, "a" * 39, "HEAD", "-f", "a" * 40 + "\n"):
            for argv in self.commands(bad):
                with self.subTest(argv=argv[0], pin=bad):
                    self.assertEqual(2, self.cli(*argv)[0])
        self.assertEqual([], self.gh.calls)
        self.assertFalse(Path(self.out_path()).exists())

    def test_without_the_pin_nothing_changes(self):
        self.start()
        self.assertEqual((0, f"verified 7 {NEW_H}\n", ""), self.cli("verify", "7"))


class LocalPathTests(SyncCase):
    def test_a_changed_local_path_is_refused(self):
        self.head_files["docs/local.md"] = "overwritten by a forged sync\n"
        self.start()
        self.refused("docs/local.md is local to this target")

    def test_a_deleted_local_path_is_refused_whatever_its_case(self):
        self.start()
        with mock.patch.object(sync_pr, "changed_paths", return_value={"Docs/LOCAL.md": "D"}):
            self.refused("Docs/LOCAL.md is local to this target")

    def test_a_manifest_path_that_is_local_is_refused_though_its_blob_matches(self):
        self.head_files["docs/local.md"] = "L\n"
        self.head_files[".harness/manifest.json"] = manifest(NEW_H, ["docs/a.md", "docs/b.md", "docs/local.md"])
        self.start(payload={**PAYLOAD, "docs/local.md": "L\n"})
        self.refused("docs/local.md is excluded or local to this target but the manifest lists it")


class ConfigGlobTests(SyncCase):
    config = {**CONFIG, "local": ["docs/local.md", "docs/d.md"], "exclude": ["docs/c.md"]}

    def test_an_excluded_deletion_verifies(self):
        # The path was managed before and is excluded now: apply deletes it, though the payload still ships it.
        self.start(payload={**PAYLOAD, "docs/c.md": "C\n"})
        self.assertEqual(0, self.cli("verify", "7")[0])

    def test_a_manifest_path_that_is_excluded_is_refused_though_its_blob_matches(self):
        self.deleted = []
        self.head_files[".harness/manifest.json"] = manifest(NEW_H, ["docs/a.md", "docs/b.md", "docs/c.md"])
        self.start(payload={**PAYLOAD, "docs/c.md": "C\n"})
        self.refused("docs/c.md is excluded or local to this target but the manifest lists it")

    def test_a_local_payload_path_need_not_be_listed(self):
        self.start(payload={**PAYLOAD, "docs/d.md": "D\n"})
        self.assertEqual(0, self.cli("verify", "7")[0])

    def test_the_globs_match_case_sensitively(self):
        self.start(payload={**PAYLOAD, "docs/D.md": "D\n"})
        self.refused("docs/D.md is a payload file but the manifest does not list it")


class UnreadableConfigTests(SyncCase):
    config = ["not", "an", "object"]

    def test_a_config_that_cannot_be_read_is_a_failure(self):
        self.start()
        self.refused("cannot read the base's .harness/config.json")


class ListTests(SyncCase):
    def row(self, number, **over):
        return {"number": number, "headRefName": f"chore/harness-sync-{number:07x}", "headRefOid": f"{number:040x}",
                "baseRefName": "main", "isCrossRepository": False, "body": BODY, **over}

    def test_list_takes_the_newest_and_names_the_rest(self):
        self.gh.prs = [
            self.row(5), self.row(9), self.row(7),
            self.row(11, isCrossRepository=True), self.row(12, baseRefName="dev"),
            self.row(13, headRefName="chore/harness-sync-notes"), self.row(14, body="no footer\n"),
            self.row(15, body=f"{sync_pr.FOOTER}.\n"),
        ]
        code, out, err = self.cli("list")
        self.assertEqual(0, code, err)
        data = json.loads(out)
        self.assertEqual({"number": 15, "head": "chore/harness-sync-000000f", "oid": f"{15:040x}"}, data["take"])
        self.assertEqual([5, 7, 9], [r["number"] for r in data["rest"]])

    def test_list_with_nothing_open(self):
        self.gh.prs = []
        self.assertEqual({"take": None, "rest": []}, json.loads(self.cli("list")[1]))

    def test_list_accepts_both_footer_shapes_only(self):
        cases = ((f"x\n\n{sync_pr.FOOTER}.\n", True), (f"x\r\n\r\n{sync_pr.FOOTER}, in run {RUN_URL}.\r\n", True),
                 (f"x\n\n{sync_pr.FOOTER}, in run .\n", False), (f"x\n\n{sync_pr.FOOTER} in run {RUN_URL}.\n", False),
                 (f"x\n\n{sync_pr.FOOTER}\n", False), (f"{sync_pr.FOOTER}.\nmore\n", False))
        for body, ok in cases:
            with self.subTest(body):
                self.gh.prs = [self.row(5, body=body)]
                self.assertEqual(5 if ok else None, (json.loads(self.cli("list")[1])["take"] or {}).get("number"))

    def test_a_gh_failure_is_exit_2_with_one_line(self):
        self.gh.fail = "pr list"
        code, out, err = self.cli("list")
        self.assertEqual(2, code)
        self.assertEqual("", out)
        self.assertEqual(1, len(err.strip().splitlines()))
        self.assertIn("pr list", err)


class ReviewDiffTests(SyncCase):
    def out_path(self):
        return str(self.root / "artifacts/review/sync-7-r1.diff")

    def run_diff(self):
        self.start()
        code, out, err = self.cli("review-diff", "7", self.out_path())
        self.assertEqual(0, code, out + err)
        self.assertEqual(Path(self.out_path()), Path(out.strip()))
        return Path(out.strip()).read_text(encoding="utf-8")

    def test_a_managed_file_the_sync_also_deletes_is_not_a_local_citation(self):
        # docs/d.md goes with docs/c.md and names it: a local file would be the target's to fix, this one is not.
        self.write({"docs/d.md": "see docs/c.md\n",
                    ".harness/manifest.json": manifest(OLD_H, ["docs/a.md", "docs/c.md", "docs/d.md",
                                                               ".claude/settings.json"])})
        git(self.root, "add", "-A")
        git(self.root, *SEED, "commit", "-q", "-m", "a second managed file")
        git(self.root, "push", "-q", "origin", "main")
        self.main = git(self.root, "rev-parse", "HEAD").strip()
        self.deleted = ["docs/c.md", "docs/d.md"]
        self.start()
        code, out, err = self.cli("review-diff", "7", self.out_path())
        self.assertEqual(0, code, out + err)
        text = Path(self.out_path()).read_text(encoding="utf-8")
        self.assertIn("docs/local.md:2: see docs/c.md for the old rule", text)
        self.assertNotIn("docs/d.md:1", text)

    def test_a_citation_main_has_since_removed_is_not_listed(self):
        # A sync never changes a local file, so main's tree is the true one: the fix that removed the citing line
        # is on main and not on the head, and a re-take would otherwise raise the same finding for ever.
        self.start()
        self.write({"docs/local.md": "first\nthe fix removed the citation\n"})
        git(self.root, "add", "-A")
        git(self.root, *SEED, "commit", "-q", "-m", "fix the local line")
        git(self.root, "push", "-q", "origin", "main")
        code, out, err = self.cli("review-diff", "7", self.out_path())
        self.assertEqual(0, code, out + err)
        text = Path(self.out_path()).read_text(encoding="utf-8")
        self.assertIn("- docs/c.md", text)
        self.assertNotIn("docs/local.md:2", text)

    def test_review_diff_names_a_local_file_citing_a_deleted_path(self):
        text = self.run_diff()
        self.assertIn("docs/local.md:2: see docs/c.md for the old rule", text)
        self.assertIn(OLD_H, text)
        self.assertIn(NEW_H, text)
        self.assertIn("feat: the new rule", text)
        self.assertNotIn("\nbody", text)

    def test_review_diff_holds_no_managed_file_content(self):
        text = self.run_diff()
        for rel in ("docs/a.md", "docs/b.md", "docs/c.md"):
            self.assertIn(rel, text)
        for content in ("BODY-OF-B", "A new", "A old"):
            self.assertNotIn(content, text)
        # The one managed file whose content is wanted: the rendered settings.
        self.assertIn('-{"old": true}', text)
        self.assertIn('+  "permissions": {', text)

    def test_review_diff_survives_an_odd_compare_and_counts_what_it_leaves_out(self):
        self.gh.compare_old = {"status": "ahead", "total_commits": 253, "commits": [
            "junk", {"sha": "zz\nforged", "commit": {"message": "fix: x"}},
            {"sha": "c" * 40, "commit": {"message": "fix: y"}}]}
        text = self.run_diff()
        self.assertIn("- ccccccc fix: y", text)
        self.assertNotIn("forged", text)
        self.assertIn("- (no sha) fix: x", text)
        self.assertIn("250 more commits not listed", text)  # 253 in all, 3 returned

    def test_review_diff_greps_without_colour(self):
        self.start()
        greps = []
        real = self.gh

        def spy(argv, cwd=None, timeout=60):
            if argv[:2] == ["git", "grep"]:
                greps.append(argv)
            return real(argv, cwd, timeout)

        with mock.patch.object(sync_pr, "run", spy):
            self.assertEqual(0, self.cli("review-diff", "7", self.out_path())[0])
        self.assertTrue(greps and all("--no-color" in argv for argv in greps))

    def test_review_diff_refuses_a_head_that_does_not_verify(self):
        self.head_files["src/hidden.py"] = "x = 1\n"
        self.start()
        code, out, err = self.cli("review-diff", "7", self.out_path())
        self.assertEqual(1, code)
        self.assertIn("src/hidden.py is changed but is outside the manifest", out)
        self.assertFalse(Path(self.out_path()).exists())

    def test_review_diff_reads_the_head_it_verified(self):
        oid = self.start()
        fetches = []
        real = self.gh

        def spy(argv, cwd=None, timeout=60):
            if argv[:2] == ["git", "fetch"]:
                fetches.append(argv)
            return real(argv, cwd, timeout)

        with mock.patch.object(sync_pr, "run", spy):
            self.assertEqual(0, self.cli("review-diff", "7", self.out_path())[0])
        self.assertEqual(2, len(fetches), "the head and main, once each")
        self.assertIn(oid, Path(self.out_path()).read_text(encoding="utf-8"))

    def test_review_diff_refuses_an_out_path_outside_artifacts_review(self):
        self.start()
        for bad in (str(self.root / "README.md"), str(self.root / "artifacts/review/../x.diff"),
                    str(self.root / "artifacts/review/x.txt")):
            self.assertEqual(2, self.cli("review-diff", "7", bad)[0], bad)
        self.assertEqual("target\n", (self.root / "README.md").read_text(encoding="utf-8"))

    def test_review_diff_writes_nothing_when_a_read_fails(self):
        self.start()
        self.gh.fail = "/compare/"
        self.assertEqual(1, self.cli("review-diff", "7", self.out_path())[0])
        self.assertFalse(Path(self.out_path()).exists())


class LocalSuitesTests(SyncCase):
    def tracked(self, *paths):
        self.write({p: "x\n" for p in paths})
        git(self.root, "add", "-A")

    def test_local_suites_reads_the_config(self):
        self.write({".harness/config.json": json.dumps({"local": ["tools/**", "Docs/test_*.py"]})})
        self.tracked("tools/test_a.py", "tools/sub/test_b.py", "tools/helper.py", "docs/test_c.py", "src/test_d.py")
        code, out, err = self.cli("local-suites")
        self.assertEqual((0, ""), (code, err))
        self.assertEqual(["docs/test_c.py", "tools/sub/test_b.py", "tools/test_a.py"], out.splitlines())

    def test_local_suites_skips_what_needs_a_rendered_root(self):
        # In the harness `local` is payload/**: those suites run in CI's rendered root, not in this checkout.
        self.write({".harness/config.json": json.dumps({"local": ["payload/**", "harness_sync/**", "measure/**"]})})
        self.tracked("payload/.claude/scripts/test_p.py", "harness_sync/tests/test_h.py", "measure/test_m.py")
        self.assertEqual(["harness_sync/tests/test_h.py", "measure/test_m.py"], self.cli("local-suites")[1].splitlines())

    def test_local_suites_also_prints_the_payload_suites_that_walk_local_commands(self):
        # They are managed, so no `local` glob names them, yet they read the target's own commands and settings.
        self.write({".harness/config.json": json.dumps({"local": ["tools/**"]})})
        self.tracked(".claude/scripts/test_command_grants.py", ".claude/scripts/test_grants_name_existing_helpers.py",
                     ".claude/scripts/test_other_payload.py", "tools/test_a.py")
        self.assertEqual([".claude/scripts/test_command_grants.py",
                          ".claude/scripts/test_grants_name_existing_helpers.py", "tools/test_a.py"],
                         self.cli("local-suites")[1].splitlines())

    def test_every_command_walker_is_a_suite_the_payload_ships(self):
        here = Path(__file__).resolve().parent
        for path in sync_pr.COMMAND_WALKERS:
            self.assertTrue((here / path.rsplit("/", 1)[-1]).is_file(), path)

    def test_local_suites_lists_a_walker_once_when_a_glob_matches_it_too(self):
        self.write({".harness/config.json": json.dumps({"local": [".claude/scripts/test_*.py"]})})
        self.tracked(".claude/scripts/test_command_grants.py")
        self.assertEqual([".claude/scripts/test_command_grants.py"], self.cli("local-suites")[1].splitlines())

    def test_local_suites_prints_nothing_when_no_suite_matches(self):
        self.write({".harness/config.json": json.dumps({"local": ["nothing/**"]})})
        self.assertEqual((0, ""), self.cli("local-suites")[:2])

    def test_local_suites_without_a_config_is_a_refusal(self):
        (self.root / ".harness/config.json").unlink()
        self.assertEqual(2, self.cli("local-suites")[0])


class RunLocalSuitesTests(SyncCase):
    """The route that replaced a grant on `unittest discover`: nothing on its command line names what runs."""

    PASSES = "import unittest\nclass T(unittest.TestCase):\n    def test_ok(self):\n        pass\n"
    FAILS = "import unittest\nclass T(unittest.TestCase):\n    def test_no(self):\n        self.fail('boom')\n"
    config = {**CONFIG, "local": ["docs/local.md", "tools/**"]}

    def take(self, files):
        """A sync proved as `/ship` takes one: the suites on the target's main, and the checkout at the head."""
        self.write(files)
        git(self.root, "add", "-A")
        git(self.root, *SEED, "commit", "-q", "-m", "suites")
        git(self.root, "push", "-q", "origin", "main")
        self.main = git(self.root, "rev-parse", "HEAD").strip()
        head = self.start()
        git(self.root, "checkout", "-q", head)
        return head

    def test_every_listed_suite_runs_and_a_failure_is_exit_1_naming_it(self):
        head = self.take({"tools/test_good.py": self.PASSES, "tools/sub/test_bad.py": self.FAILS})
        code, out, err = self.cli("run-local-suites", "7", "--head", head)
        self.assertEqual(1, code, out + err)
        self.assertIn("== tools/test_good.py", out)
        self.assertIn("== tools/sub/test_bad.py", out)
        self.assertIn("boom", out, "the failing suite's own output is what the finding is filed from")
        self.assertIn("failed: tools/sub/test_bad.py", out)
        self.assertNotIn("failed: tools/test_good.py", out)

    def test_all_green_is_exit_0(self):
        head = self.take({"tools/test_good.py": self.PASSES})
        code, out, err = self.cli("run-local-suites", "7", "--head", head)
        self.assertEqual(0, code, out + err)
        self.assertIn("Ran 1 test", out, "a zero exit over a run that discovered nothing would prove nothing")

    def test_a_commit_no_sync_pull_request_holds_runs_nothing(self):
        # The settings allow this script to every session, so a test file committed on a local branch, with that
        # branch's own HEAD as the pin, must not run: such a file never has to appear in a pull request.
        self.take({"tools/test_bad.py": self.FAILS})
        self.write({"tools/test_planted.py": self.FAILS})
        git(self.root, "add", "-A")
        git(self.root, *SEED, "commit", "-q", "-m", "planted")
        own = git(self.root, "rev-parse", "HEAD").strip()
        for prs in ([], self.gh.prs):
            with self.subTest(prs=len(prs)):
                self.gh.prs = prs
                code, out, _ = self.cli("run-local-suites", "7", "--head", own)
                self.assertEqual(1, code)
                self.assertIn("no suite ran", out)
                self.assertNotIn("boom", out)

    def test_a_checkout_that_is_not_the_proved_head_runs_nothing(self):
        head = self.take({"tools/test_bad.py": self.FAILS})
        git(self.root, "checkout", "-q", "main")
        code, out, _ = self.cli("run-local-suites", "7", "--head", head)
        self.assertEqual(1, code)
        self.assertIn(f"HEAD is {self.main}", out)
        self.assertNotIn("boom", out)

    def test_a_tree_that_is_not_clean_runs_nothing(self):
        # A tracked suite edited after the proof is not the code the proof vouches for.
        head = self.take({"tools/test_bad.py": self.FAILS})
        self.write({"tools/test_bad.py": self.FAILS + "# edited\n"})
        code, out, _ = self.cli("run-local-suites", "7", "--head", head)
        self.assertEqual(1, code)
        self.assertIn("not clean", out)
        self.assertNotIn("boom", out)

    def test_it_takes_no_path_so_a_file_outside_the_tree_cannot_be_named(self):
        for argv in (["run-local-suites"], ["run-local-suites", "7"], ["run-local-suites", "7", "--head", "x"],
                     ["run-local-suites", "--head", "0" * 40],
                     ["run-local-suites", "7", "--head", "0" * 40, "-s", "elsewhere"]):
            with self.subTest(argv=argv):
                self.assertEqual(2, self.cli(*argv)[0])


class TheScriptPrintsWhatAnAnsiStreamCannot(unittest.TestCase):
    def test_a_path_outside_the_code_page_prints_rather_than_raises(self):
        # Run as a script, since `cli` captures into a StringIO that never meets this. A raise here would leave the
        # later suites unrun and report a passing target as a failing one.
        name = "tools/test_→.py"
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            scripts = root / ".claude" / "scripts"
            scripts.mkdir(parents=True)
            here = Path(__file__).resolve().parent
            for script in ("sync_pr.py", "dispatch_board.py"):
                shutil.copy(here / script, scripts / script)
            (root / ".harness").mkdir()
            (root / ".harness" / "config.json").write_text(json.dumps({"local": ["tools/**"]}), encoding="utf-8")
            (root / "tools").mkdir()
            (root / name).write_text("", encoding="utf-8")
            git(root, "init", "-q")
            git(root, "add", "tools", ".harness/config.json")
            done = subprocess.run([sys.executable, str(scripts / "sync_pr.py"), "local-suites"], cwd=root,
                                  capture_output=True, env={**os.environ, "PYTHONIOENCODING": "cp1252"}, check=False)
        self.assertEqual(0, done.returncode, done.stderr.decode("utf-8", "replace"))
        self.assertEqual(name, done.stdout.decode("utf-8").strip())


class RunnerTests(unittest.TestCase):
    """The runner itself, not the stand-in SyncCase swaps in: its timeout once waited with no limit on a pipe a
    grandchild held, so a hung `gh` stalled whatever was waiting on it."""

    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.root = Path(tmp.name)

    def script(self, name, body):
        path = self.root / name
        path.write_bytes(("#!/usr/bin/env bash\n" + body).encode("utf-8"))
        return [shutil.which("bash"), str(path)]

    def test_a_hung_child_tree_is_ended_in_time(self):
        argv = self.script("hang.sh", "sleep 300 &\nsleep 300\n")
        outcome = {}

        def call():
            try:
                outcome["value"] = sync_pr.run(argv, cwd=self.root, timeout=2)
            except sync_pr.Refusal as error:
                outcome["error"] = error

        # The drain bound sits far above the timeout, so only an ended tree returns well inside it.
        with mock.patch.object(dispatch_board, "DRAIN_TIMEOUT", 45):
            started = time.monotonic()
            worker = threading.Thread(target=call, daemon=True)
            worker.start()
            worker.join(90)
            took = time.monotonic() - started
        self.assertFalse(worker.is_alive(), "the runner's timeout did not arrive within 90 s")
        self.assertIn("took over 2 s", str(outcome.get("error")))
        self.assertLess(took, 30, "the call waited out the drain, so the grandchild outlived the timeout")

    def test_output_is_utf8_with_line_endings_folded(self):
        argv = self.script("crlf.sh", "printf 'один\\r\\nдва\\n'\n")
        self.assertEqual((0, "один\nдва\n", ""), sync_pr.run(argv, cwd=self.root))


if __name__ == "__main__":
    unittest.main()
