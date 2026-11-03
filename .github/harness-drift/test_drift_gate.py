#!/usr/bin/env python3
"""Negative cases for the drift gate, each beside a positive control."""

from __future__ import annotations

import contextlib
import hashlib
import importlib.util
import io
import json
import os
import shutil
import subprocess
import tempfile
import types
import unittest
from unittest import mock
from pathlib import Path
from types import SimpleNamespace

import drift_gate
from drift_gate import check, main

HERE = Path(__file__).resolve().parent
BASE = {"env": {"A": "1"}, "permissions": {"allow": ["Bash(ls)"], "deny": []}, "hooks": {}}
# The keys apply requires, so a test config fails only on what the test changes.
CFG = {"repo": "o/r", "task_list": "t"}


def sha(data: bytes) -> str:
    return hashlib.sha256(data.replace(b"\r\n", b"\n")).hexdigest()


class Tree:
    """A throwaway target: the real renderer, a small base, and files recorded in a manifest."""

    def __init__(self, tmp: str, local: tuple[str, ...] = (), base: dict = BASE) -> None:
        self.root = Path(tmp)
        self.files: dict[str, str] = {}
        (self.root / ".harness").mkdir()
        shutil.copy(HERE.parents[1] / ".harness/render.py", self.root / ".harness/render.py")
        self.write_json(".harness/config.json", {**CFG, "local": list(local)})
        self.write_json(".harness/settings.base.json", base)
        spec = importlib.util.spec_from_file_location("_r", self.root / ".harness/render.py")
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
        self.render = mod
        rendered = mod.dump_settings(mod.render_settings(base, SimpleNamespace(settings={})))
        self.add(".claude/settings.json", rendered.encode("utf-8"))
        for rel in (".harness/settings.base.json", ".harness/render.py"):
            self.files[rel] = sha((self.root / rel).read_bytes())
        self.add(".claude/skills/a/SKILL.md", b"one\ntwo\n")
        self.save_manifest()

    def write_json(self, rel: str, body: object) -> None:
        self.put(rel, json.dumps(body).encode("utf-8"))

    def put(self, rel: str, data: bytes) -> None:
        path = self.root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)

    def add(self, rel: str, data: bytes) -> None:
        self.put(rel, data)
        self.files[rel] = sha(data)

    def make_payload(self, where: Path) -> Path:
        """The payload a sync of this tree would have copied: every managed file but the rendered settings."""
        for rel in self.files:
            if rel != ".claude/settings.json":
                target = where / rel
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes((self.root / rel).read_bytes())
        return where

    def save_manifest(self) -> None:
        self.write_json(".harness/manifest.json", {"harness_commit": "c" * 40, "files": self.files})


class DriftGate(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.tmp = self._tmp.name

    def test_clean_tree_passes(self) -> None:
        self.assertEqual(check(Tree(self.tmp).root), [])

    def test_crlf_copy_passes(self) -> None:
        tree = Tree(self.tmp)
        tree.put(".claude/skills/a/SKILL.md", b"one\r\ntwo\r\n")
        self.assertEqual(check(tree.root), [])

    def test_one_char_change_fails(self) -> None:
        tree = Tree(self.tmp)
        tree.put(".claude/skills/a/SKILL.md", b"one\ntwp\n")
        problems = check(tree.root)
        self.assertEqual(len(problems), 1)
        self.assertIn(".claude/skills/a/SKILL.md", problems[0])
        self.assertIn("differs", problems[0])

    def test_missing_managed_file_fails(self) -> None:
        tree = Tree(self.tmp)
        (tree.root / ".claude/skills/a/SKILL.md").unlink()
        problems = check(tree.root)
        self.assertEqual(len(problems), 1)
        self.assertIn("missing", problems[0])

    def test_unlisted_file_in_managed_dir_fails(self) -> None:
        tree = Tree(self.tmp)
        tree.put(".claude/hooks/extra.sh", b"x\n")
        problems = check(tree.root)
        self.assertEqual(len(problems), 1)
        self.assertIn(".claude/hooks/extra.sh", problems[0])

    def test_unlisted_file_in_every_managed_dir_fails(self) -> None:
        tree = Tree(self.tmp)
        dirs = (".claude/agents", ".claude/commands", ".claude/hooks", ".claude/scripts", ".claude/skills",
                "docs/commands", ".github/harness-drift")
        for d in dirs:
            tree.put(f"{d}/new/stray.txt", b"x\n")
        self.assertEqual(len(check(tree.root)), len(dirs))

    def test_local_file_in_managed_dir_passes(self) -> None:
        tree = Tree(self.tmp, local=(".claude/commands/*.md",))
        tree.put(".claude/commands/mine.md", b"x\n")
        self.assertEqual(check(tree.root), [])

    def test_local_match_ignores_case(self) -> None:
        tree = Tree(self.tmp, local=(".claude/commands/mine.md",))
        tree.put(".claude/commands/Mine.md", b"x\n")
        self.assertEqual(check(tree.root), [])

    def test_bytecode_is_not_an_unlisted_file(self) -> None:
        tree = Tree(self.tmp)
        tree.put(".claude/scripts/__pycache__/x.cpython-312.pyc", b"\0")
        tree.put(".claude/scripts/y.pyc", b"\0")
        self.assertEqual(check(tree.root), [])

    def _git(self, root: Path, *args: str) -> None:
        if shutil.which("git") is None:
            self.skipTest("git is not on PATH")
        subprocess.run(["git", "-C", str(root), *args], check=True, capture_output=True)

    def test_an_ignored_file_under_a_managed_directory_passes_in_a_checkout(self) -> None:
        tree = Tree(self.tmp)
        self._git(tree.root, "init", "-q")
        tree.put(".gitignore", b".claude/skills/a/.skill_version\n")
        tree.put(".claude/skills/a/.skill_version", b"1\n")
        self.assertEqual(check(tree.root), [])

    def test_a_checkout_still_judges_untracked_and_force_added_files(self) -> None:
        tree = Tree(self.tmp)
        self._git(tree.root, "init", "-q")
        tree.put(".gitignore", b"*.lock\n")
        tree.put(".claude/hooks/extra.sh", b"x\n")
        tree.put(".claude/hooks/forced.lock", b"x\n")
        self._git(tree.root, "add", "-f", ".claude/hooks/forced.lock")
        problems = check(tree.root)
        self.assertEqual(len(problems), 2)
        self.assertIn(".claude/hooks/extra.sh", problems[0])
        self.assertIn(".claude/hooks/forced.lock", problems[1])

    def test_a_nested_repository_in_a_checkout_is_judged_by_its_files(self) -> None:
        tree = Tree(self.tmp, local=(".claude/skills/mine/**",))
        self._git(tree.root, "init", "-q")
        tree.put(".claude/skills/mine/SKILL.md", b"x\n")
        self._git(tree.root / ".claude/skills/mine", "init", "-q")
        tree.put(".claude/skills/theirs/SKILL.md", b"x\n")
        self._git(tree.root / ".claude/skills/theirs", "init", "-q")
        problems = check(tree.root)
        self.assertEqual(len(problems), 1, problems)
        self.assertIn(".claude/skills/theirs/SKILL.md is under a managed directory", problems[0])

    def test_an_empty_nested_repository_is_judged_as_itself(self) -> None:
        tree = Tree(self.tmp)
        self._git(tree.root, "init", "-q")
        (tree.root / ".claude/skills/bare").mkdir(parents=True)
        self._git(tree.root / ".claude/skills/bare", "init", "-q")
        problems = check(tree.root)
        self.assertEqual(len(problems), 1, problems)
        self.assertIn(".claude/skills/bare is under a managed directory", problems[0])

    def test_a_submodule_checked_out_empty_is_still_judged(self) -> None:
        tree = Tree(self.tmp)
        self._git(tree.root, "init", "-q")
        (tree.root / ".claude/skills/sub").mkdir(parents=True)
        self._git(tree.root, "update-index", "--add", "--cacheinfo", f"160000,{'a' * 40},.claude/skills/sub")
        problems = check(tree.root)
        self.assertEqual(len(problems), 1)
        self.assertIn(".claude/skills/sub is under a managed directory", problems[0])

    def test_a_tracked_file_deleted_from_a_checkout_is_not_an_unlisted_file(self) -> None:
        tree = Tree(self.tmp)
        self._git(tree.root, "init", "-q")
        tree.put(".claude/hooks/extra.sh", b"x\n")  # tracked, but no manifest line, so only the unlisted check sees it
        self._git(tree.root, "add", "-A")
        (tree.root / ".claude/hooks/extra.sh").unlink()
        self.assertEqual(check(tree.root), [])

    def test_a_git_in_the_tree_or_the_working_directory_is_never_run(self) -> None:
        real = shutil.which("git")
        if real is None:
            self.skipTest("git is not on PATH")
        name = "git.exe" if os.name == "nt" else "git"
        tree = Tree(self.tmp)
        cwd = Path(self.tmp).parent / f"{Path(self.tmp).name}-cwd"
        cwd.mkdir()
        self.addCleanup(shutil.rmtree, cwd)
        for where in (tree.root / "bin", cwd):
            where.mkdir(exist_ok=True)
            (where / name).write_bytes(b"#!/bin/sh\nexit 0\n")
            (where / name).chmod(0o755)
        path = os.pathsep.join([".", str(cwd), str(tree.root / "bin"), str(Path(real).parent)])
        with mock.patch.dict(os.environ, {"PATH": path}), contextlib.chdir(cwd):
            chosen = drift_gate._git_executable(tree.root)
        self.assertIsNotNone(chosen)
        self.assertEqual(Path(chosen).parent.resolve(), Path(real).parent.resolve())

    def test_a_root_below_the_top_of_a_checkout_is_walked(self) -> None:
        (Path(self.tmp) / "sub").mkdir()
        tree = Tree(str(Path(self.tmp) / "sub"))
        self._git(Path(self.tmp), "init", "-q")
        (Path(self.tmp) / ".gitignore").write_bytes(b".skill_version\n")
        tree.put(".claude/skills/a/.skill_version", b"1\n")
        problems = check(tree.root)
        self.assertEqual(len(problems), 1)
        self.assertIn(".skill_version", problems[0])

    def test_file_outside_managed_dirs_is_not_judged(self) -> None:
        tree = Tree(self.tmp)
        tree.put("src/app.py", b"x\n")
        self.assertEqual(check(tree.root), [])

    def test_settings_edited_by_hand_fails(self) -> None:
        tree = Tree(self.tmp)
        tree.add(".claude/settings.json", b'{"env": {}}\n')  # manifest agrees, so only the render check can object
        tree.save_manifest()
        problems = check(tree.root)
        self.assertEqual(len(problems), 1)
        self.assertIn(".claude/settings.json", problems[0])
        self.assertIn("render", problems[0])

    def test_settings_follow_the_config(self) -> None:
        tree = Tree(self.tmp)
        tree.write_json(".harness/config.json", {**CFG, "settings": {"allow": ["Bash(pwd)"]}})
        problems = check(tree.root)
        self.assertEqual(len(problems), 1)
        self.assertIn("render", problems[0])

    def test_render_error_is_a_problem_not_a_traceback(self) -> None:
        tree = Tree(self.tmp)
        tree.write_json(".harness/config.json", {**CFG, "settings": {"env": {"a": "2"}}})
        problems = check(tree.root)
        self.assertTrue(any("already set" in p for p in problems), problems)

    def test_unreadable_inputs_are_problems(self) -> None:
        for rel in (".harness/config.json", ".harness/settings.base.json", ".harness/manifest.json",
                    ".harness/render.py"):
            with self.subTest(rel=rel), tempfile.TemporaryDirectory() as tmp:
                tree = Tree(tmp)
                (tree.root / rel).unlink()
                self.assertTrue(check(tree.root))
                tree.put(rel, b"{ not valid python or json")
                self.assertTrue(check(tree.root))

    def test_non_object_inputs_are_problems(self) -> None:
        for rel in (".harness/config.json", ".harness/settings.base.json", ".harness/manifest.json"):
            for body in (b"[]", b'{"harness_commit": "x", "files": []}', b'{"settings": 3, "local": "a"}'):
                with self.subTest(rel=rel, body=body), tempfile.TemporaryDirectory() as tmp:
                    tree = Tree(tmp)
                    tree.put(rel, body)
                    problems = check(tree.root)  # must not raise
                    if body == b"[]":
                        self.assertTrue(problems, problems)

    def test_bad_manifest_paths_are_problems_and_never_read(self) -> None:
        outside = Path(self.tmp).parent / "outside-secret.txt"
        outside.write_bytes(b"s\n")
        self.addCleanup(outside.unlink)
        for bad in ("../outside-secret.txt", "/etc/passwd", "a\\b", "C:/x", "a//b", "./a"):
            with self.subTest(bad=bad), tempfile.TemporaryDirectory() as tmp:
                tree = Tree(tmp)
                tree.files[bad] = sha(b"s\n")
                tree.save_manifest()
                problems = check(tree.root)
                self.assertEqual(len(problems), 1)
                self.assertIn("canonical", problems[0])

    def test_crlf_settings_pass(self) -> None:
        tree = Tree(self.tmp)
        tree.put(".claude/settings.json", (tree.root / ".claude/settings.json").read_bytes().replace(b"\n", b"\r\n"))
        self.assertEqual(check(tree.root), [])

    def test_without_a_payload_the_manifest_must_list_what_settings_rely_on(self) -> None:
        for rel in (".harness/settings.base.json", ".harness/render.py", ".claude/settings.json"):
            with self.subTest(rel=rel), tempfile.TemporaryDirectory() as tmp:
                tree = Tree(tmp)
                del tree.files[rel]
                tree.save_manifest()
                self.assertTrue(any("does not list" in p for p in check(tree.root)))

    def test_base_and_renderer_cannot_be_local_or_excluded(self) -> None:
        for key in ("local", "exclude"):
            for rel in (".harness/settings.base.json", ".harness/render.py"):
                with self.subTest(key=key, rel=rel), tempfile.TemporaryDirectory() as tmp:
                    tree = Tree(tmp)
                    tree.write_json(".harness/config.json", {**CFG, key: [rel]})
                    self.assertTrue(any("needs managed" in p for p in check(tree.root)))

    def test_the_gate_files_cannot_be_local_or_excluded(self) -> None:
        # Without its gate or the workflow that runs it, a target stays green and is never checked again.
        for key in ("local", "exclude"):
            for glob in (".github/harness-drift/**", ".github/harness-drift/drift_gate.py",
                         ".github/workflows/harness-drift.yml", ".github/workflows/*.yml", ".github/**"):
                with self.subTest(key=key, glob=glob), tempfile.TemporaryDirectory() as tmp:
                    tree = Tree(tmp)
                    tree.write_json(".harness/config.json", {**CFG, key: [glob]})
                    self.assertTrue(any("needs managed" in p for p in check(tree.root)))

    def test_a_file_a_hook_runs_cannot_be_local_or_excluded(self) -> None:
        command = 'py -3.12 "${CLAUDE_PROJECT_DIR}/.claude/hooks/guard.py"'
        hooks = {"PreToolUse": [{"matcher": "Bash", "hooks": [{"type": "command", "command": command}]}]}
        base = {**BASE, "hooks": hooks}
        for key in ("local", "exclude"):
            with self.subTest(key=key), tempfile.TemporaryDirectory() as tmp:
                tree = Tree(tmp, base=base)
                tree.write_json(".harness/config.json", {**CFG, key: [".claude/hooks/*"]})
                problems = check(tree.root)
                self.assertTrue(any(".claude/hooks/guard.py" in p and "which a hook" in p for p in problems), problems)
                tree.write_json(".harness/config.json", {**CFG, key: [".claude/hooks/other.py"]})
                self.assertFalse(any("which a hook" in p for p in check(tree.root)))

    def test_a_config_apply_refuses_is_a_problem(self) -> None:
        cases = {
            "not an object": [],
            "an unknown key": {**CFG, "extra": 1},
            "no repo": {"task_list": "t"},
            "a task list that is not a string": {**CFG, "task_list": 1},
            "a glob list that is not a list": {**CFG, "local": "a"},
            "a backslash glob": {**CFG, "local": ["a\\b"]},
            "a leading-slash glob": {**CFG, "exclude": ["/a"]},
        }
        for name, body in cases.items():
            with self.subTest(name), tempfile.TemporaryDirectory() as tmp:
                tree = Tree(tmp)
                tree.write_json(".harness/config.json", body)
                self.assertTrue(any(p.startswith(".harness/config.json ") for p in check(tree.root)), name)

    def test_a_tampered_renderer_is_not_executed_without_a_payload(self) -> None:
        tree = Tree(self.tmp)
        marker = tree.root / "ran.txt"
        tree.put(".harness/render.py", f"open({str(marker)!r}, 'w').close()\n".encode("utf-8"))
        self.assertTrue(check(tree.root))  # the manifest still holds the old hash
        self.assertFalse(marker.exists())

    def test_a_linked_fixed_path_is_a_problem_and_not_read(self) -> None:
        tree = Tree(self.tmp)
        outside = Path(self.tmp).parent / "outside-config.json"
        outside.write_text("{}", encoding="utf-8")
        self.addCleanup(outside.unlink)
        (tree.root / ".harness/config.json").unlink()
        try:
            (tree.root / ".harness/config.json").symlink_to(outside)
        except OSError:
            self.skipTest("symlinks need privileges here")
        self.assertTrue(any(".harness/config.json passes through a link" in p for p in check(tree.root)))

    def test_a_linked_managed_directory_is_a_problem_and_not_walked(self) -> None:
        tree = Tree(self.tmp)
        outside = Path(self.tmp).parent / "outside-dir"
        outside.mkdir()
        self.addCleanup(shutil.rmtree, outside)
        (outside / "x.sh").write_text("x\n", encoding="utf-8")
        try:
            (tree.root / ".claude/hooks").symlink_to(outside, target_is_directory=True)
        except OSError:
            self.skipTest("symlinks need privileges here")
        problems = check(tree.root)
        self.assertEqual(len(problems), 1)
        self.assertIn("link", problems[0])

    def test_render_module_replaces_the_trees_renderer(self) -> None:
        tree = Tree(self.tmp)
        marker = tree.root / "ran.txt"
        source = f"open({str(marker)!r}, 'w').close()\n".encode("utf-8")
        tree.add(".harness/render.py", source)  # the manifest agrees, so only the hook keeps it from running
        tree.save_manifest()
        self.assertEqual(check(tree.root, render_module=tree.render), [])
        self.assertFalse(marker.exists())

    def test_gate_reads_the_shipped_manifest(self) -> None:
        """The gate looks at the tree CI hands it: this file's own rendered root.

        It runs where it sits in a rendered tree (payload-suites' worktree, then each target), where
        `.harness/manifest.json` exists. It is never run from raw `payload/`, which has no manifest.
        """
        root = Path(__file__).resolve().parents[2]
        self.assertEqual(check(root), [])

    def test_cli_exits_1_with_one_line_per_problem(self) -> None:
        tree = Tree(self.tmp)
        tree.put(".claude/skills/a/SKILL.md", b"changed\n")
        tree.put(".claude/hooks/extra.sh", b"x\n")
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = main(["--root", str(tree.root)])
        self.assertEqual(code, 1)
        self.assertEqual(len(out.getvalue().strip().splitlines()), 2)

    def test_cli_exits_0_when_clean(self) -> None:
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = main(["--root", str(Tree(self.tmp).root)])
        self.assertEqual(code, 0)
        self.assertIn("clean", out.getvalue())


class AgainstThePayload(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.base = Path(self._tmp.name)
        (self.base / "t").mkdir()
        self.tree = Tree(str(self.base / "t"))
        self.payload = self.tree.make_payload(self.base / "p")

    def test_a_synced_tree_passes(self) -> None:
        self.assertEqual(check(self.tree.root, self.payload), [])

    def test_a_file_edited_with_its_manifest_line_fails(self) -> None:
        self.tree.add(".claude/skills/a/SKILL.md", b"evil\n")
        self.tree.save_manifest()
        self.assertEqual(check(self.tree.root), [])  # the tree agrees with itself
        problems = check(self.tree.root, self.payload)
        self.assertEqual(len(problems), 1)
        self.assertIn("not the payload's", problems[0])

    def test_a_deleted_manifest_line_fails(self) -> None:
        del self.tree.files[".claude/skills/a/SKILL.md"]
        self.tree.save_manifest()
        problems = check(self.tree.root, self.payload)
        self.assertTrue(any("does not list it" in p for p in problems), problems)

    def test_an_edited_base_with_its_manifest_line_fails(self) -> None:
        self.tree.add(".harness/settings.base.json", json.dumps({"hooks": {}}).encode("utf-8"))
        self.tree.save_manifest()
        self.assertTrue(any("settings.base.json" in p for p in check(self.tree.root, self.payload)))

    def test_a_manifest_entry_the_payload_lacks_fails(self) -> None:
        self.tree.add(".claude/skills/extra.md", b"x\n")
        self.tree.save_manifest()
        self.assertTrue(any("not a payload file" in p for p in check(self.tree.root, self.payload)))

    def test_a_local_path_that_differs_from_the_payload_fails(self) -> None:
        self.tree.write_json(".harness/config.json", {**CFG, "local": [".claude/skills/a/SKILL.md"]})
        self.tree.put(".claude/skills/a/SKILL.md", b"mine\n")
        del self.tree.files[".claude/skills/a/SKILL.md"]
        self.tree.save_manifest()
        problems = check(self.tree.root, self.payload)
        self.assertEqual(len(problems), 1)
        self.assertIn("is local but the payload manages it", problems[0])

    def test_a_local_path_identical_to_the_payload_or_absent_passes(self) -> None:
        rel = ".claude/skills/a/SKILL.md"
        self.tree.write_json(".harness/config.json", {**CFG, "local": [rel]})
        del self.tree.files[rel]
        self.tree.save_manifest()
        self.assertEqual(check(self.tree.root, self.payload), [])  # identical
        (self.tree.root / rel).unlink()
        self.assertEqual(check(self.tree.root, self.payload), [])  # absent

    def test_an_excluded_payload_file_need_not_be_listed(self) -> None:
        rel = ".claude/skills/a/SKILL.md"
        self.tree.write_json(".harness/config.json", {**CFG, "exclude": [rel]})
        del self.tree.files[rel]
        (self.tree.root / rel).unlink()
        self.tree.save_manifest()
        self.assertEqual(check(self.tree.root, self.payload), [])

    def test_a_tampered_renderer_with_a_matching_line_fails_and_never_runs(self) -> None:
        marker = self.tree.root / "ran.txt"
        source = (self.tree.root / ".harness/render.py").read_bytes()
        source += f"\nopen({str(marker)!r}, 'w').close()\n".encode("utf-8")
        self.tree.add(".harness/render.py", source)
        self.tree.save_manifest()
        problems = check(self.tree.root, self.payload)
        self.assertTrue(any("not trusted" in p for p in problems), problems)
        self.assertFalse(marker.exists())

    def test_a_sync_that_changes_the_renderers_output_passes(self) -> None:
        new = (self.tree.root / ".harness/render.py").read_text(encoding="utf-8").replace("indent=2", "indent=4")
        for where in (self.tree.root, self.payload):
            (where / ".harness/render.py").write_text(new, encoding="utf-8", newline="\n")
        self.tree.files[".harness/render.py"] = sha(new.encode("utf-8"))
        module = types.ModuleType("_new")
        exec(compile(new, "render.py", "exec"), module.__dict__)  # not imported: a stale .pyc would hide the edit
        rendered = module.dump_settings(module.render_settings(BASE, SimpleNamespace(settings={})))
        self.tree.add(".claude/settings.json", rendered.encode("utf-8"))
        self.tree.save_manifest()
        self.assertIn('    "env"', rendered)
        self.assertEqual(check(self.tree.root, self.payload), [])

    def test_an_excluded_payload_path_that_exists_in_the_tree_fails(self) -> None:
        rel = ".claude/skills/a/SKILL.md"
        self.tree.write_json(".harness/config.json", {**CFG, "exclude": [rel]})
        del self.tree.files[rel]
        self.tree.save_manifest()  # the file stays in the tree: excluded means not delivered, never replaced
        problems = check(self.tree.root, self.payload)
        self.assertTrue(any("excluded but exists" in p for p in problems), problems)

    def test_a_local_path_that_is_a_link_is_a_problem(self) -> None:
        rel = ".claude/skills/a/SKILL.md"
        self.tree.write_json(".harness/config.json", {**CFG, "local": [rel]})
        del self.tree.files[rel]
        self.tree.save_manifest()
        outside = self.base / "outside.md"
        outside.write_bytes(b"one\ntwo\n")
        (self.tree.root / rel).unlink()
        try:
            (self.tree.root / rel).symlink_to(outside)
        except OSError:
            self.skipTest("symlinks need privileges here")
        problems = check(self.tree.root, self.payload)
        self.assertTrue(any(f"{rel} is local but unreadable" in p and "link" in p for p in problems), problems)

    def test_an_unreadable_local_copy_is_a_problem_not_a_traceback(self) -> None:
        rel = ".claude/skills/a/SKILL.md"
        self.tree.write_json(".harness/config.json", {**CFG, "local": [rel]})
        del self.tree.files[rel]
        self.tree.save_manifest()
        original = Path.read_bytes

        def deny(path: Path) -> bytes:
            if path.name == "SKILL.md" and path.is_relative_to(self.tree.root):
                raise PermissionError("denied")
            return original(path)

        with mock.patch.object(Path, "read_bytes", deny):
            problems = check(self.tree.root, self.payload)
        self.assertTrue(any("cannot read" in p for p in problems), problems)

    def test_local_protection_of_base_is_case_sensitive_like_apply(self) -> None:
        self.tree.write_json(".harness/config.json", {**CFG, "local": [".HARNESS/render.py"]})
        self.assertFalse(any("needs managed" in p for p in check(self.tree.root, self.payload)))

    def test_payload_that_is_not_a_directory_is_a_problem(self) -> None:
        self.assertTrue(check(self.tree.root, self.base / "nope"))

    def test_cli_takes_the_payload(self) -> None:
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = main(["--root", str(self.tree.root), "--payload", str(self.payload)])
        self.assertEqual((code, out.getvalue().strip()), (0, "harness-drift: clean"))


if __name__ == "__main__":
    unittest.main()
