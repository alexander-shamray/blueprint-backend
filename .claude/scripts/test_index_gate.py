"""The skill's direct route to the index asks the refresh hook's judgement:
`cbx` asks `index_openable` through `gate-index.py` before it starts the CLI,
and the CLI is not started when that refuses.
"""

import os
import shutil
import sqlite3
import stat
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
SKILL_SCRIPTS = SCRIPTS.parent / "skills" / "codebase-index" / "scripts"
GATE = SKILL_SCRIPTS / "gate-index.py"
CACHE = Path(".claude") / "cache" / "codebase-index"


def _bash():
    git = Path(r"C:\Program Files\Git\bin\bash.exe")
    if git.is_file():
        return str(git)
    return shutil.which("bash")


def python_312():
    probe = "import sys; raise SystemExit(0 if sys.version_info[:2] == (3, 12) else 1)"
    for candidate in (["py", "-3.12"], ["python3"], ["python"]):
        if shutil.which(candidate[0]) and subprocess.run(
                [*candidate, "-c", probe], capture_output=True).returncode == 0:
            return True
    return False


def git_environment():
    return {name: value for name, value in os.environ.items()
            if not name.upper().startswith("GIT_")}


@unittest.skipUnless(_bash() and shutil.which("git") and python_312(),
                     "the gate needs bash, git and Python 3.12")
class TheCliIsNotStartedOverAnIndexTheHookRefuses(unittest.TestCase):
    def setUp(self):
        scratch = tempfile.TemporaryDirectory()
        self.addCleanup(scratch.cleanup)
        self.scratch = Path(scratch.name)
        self.repo = self.scratch / "repo"
        self.repo.mkdir()
        subprocess.run(["git", "init", "-q"], cwd=self.repo, check=True, capture_output=True,
                       env=git_environment())
        self.cache = self.repo / CACHE
        # A stand-in for the CLI that says it ran; the gate is what is under test.
        self.bin = self.scratch / "bin"
        self.bin.mkdir()
        stub = self.bin / "codebase-index"
        stub.write_text("#!/bin/sh\necho cli-ran \"$@\"\n", encoding="utf-8", newline="\n")
        stub.chmod(stub.stat().st_mode | stat.S_IEXEC)

    def index(self, wal=True):
        self.cache.mkdir(parents=True, exist_ok=True)
        connection = sqlite3.connect(self.cache / "index.sqlite", isolation_level=None)
        try:
            connection.execute(f"PRAGMA journal_mode={'WAL' if wal else 'DELETE'}")
            connection.execute("CREATE TABLE chunks (id INTEGER PRIMARY KEY, content TEXT)")
        finally:
            connection.close()

    def cbx(self, *arguments):
        env = {**git_environment(), "PATH": f"{self.bin}{os.pathsep}{os.environ['PATH']}"}
        return subprocess.run([_bash(), str(SKILL_SCRIPTS / "cbx"), *arguments], cwd=self.repo,
                              env=env, capture_output=True, text=True, encoding="utf-8",
                              timeout=60)

    def gate(self, cwd=None):
        return subprocess.run([sys.executable, "-I", str(GATE)], cwd=cwd or self.repo,
                              env=git_environment(), capture_output=True, text=True, encoding="utf-8",
                              timeout=60)

    def test_a_root_with_no_index_is_admitted(self):
        self.assertEqual(0, self.gate().returncode)

    def test_a_wal_index_is_admitted(self):
        self.index()
        self.assertEqual(0, self.gate().returncode)

    def test_a_rollback_journal_beside_the_index_is_refused(self):
        self.index()
        (self.cache / "index.sqlite-journal").write_bytes(b"branch-written")
        done = self.gate()

        self.assertEqual(3, done.returncode, done.stderr)
        self.assertIn("refusing to open the index", done.stderr)

    def test_a_rollback_mode_header_is_refused(self):
        self.index(wal=False)
        self.assertEqual(3, self.gate().returncode)

    def test_a_subdirectory_is_judged_by_its_checkout(self):
        self.index()
        (self.cache / "index.sqlite-journal").write_bytes(b"x")
        sub = self.repo / "src"
        sub.mkdir()
        self.assertEqual(3, self.gate(cwd=sub).returncode)

    def test_a_directory_outside_a_checkout_is_left_to_the_cli(self):
        self.assertEqual(0, self.gate(cwd=self.scratch).returncode)

    def test_a_linked_cache_directory_is_refused(self):
        elsewhere = self.scratch / "elsewhere"
        elsewhere.mkdir()
        (self.repo / ".claude" / "cache").mkdir(parents=True)
        link = self.cache
        try:
            os.symlink(elsewhere, link, target_is_directory=True)
        except (OSError, NotImplementedError):
            if sys.platform != "win32":
                self.skipTest("this host grants no directory link")
            subprocess.run(["cmd", "/c", "mklink", "/J", str(link), str(elsewhere)],
                           check=True, capture_output=True)
        self.assertEqual(3, self.gate().returncode)

    def test_the_gate_asks_the_hooks_own_judgement(self):
        """One owner for the rule: the gate loads the hook beside it."""
        text = GATE.read_text(encoding="utf-8")
        self.assertIn('"hooks", "refresh-index.py"', text)
        self.assertIn("hook.index_openable(root)", text)
        self.assertTrue((SCRIPTS.parent / "hooks" / "refresh-index.py").is_file())

    def test_cbx_starts_the_cli_on_a_clean_index_and_not_on_a_refused_one(self):
        self.index()
        done = self.cbx("stats")
        self.assertEqual(0, done.returncode, done.stderr)
        self.assertIn("cli-ran stats", done.stdout)
        (self.cache / "index.sqlite-journal").write_bytes(b"branch-written")
        done = self.cbx("stats")
        self.assertEqual(3, done.returncode, done.stderr)
        self.assertNotIn("cli-ran", done.stdout)

    def test_cbx_refuses_before_the_gate_when_the_subcommand_is_not_allowed(self):
        self.cache.mkdir(parents=True)
        (self.cache / "index.sqlite-journal").write_bytes(b"x")
        done = self.cbx("clean")
        self.assertEqual(2, done.returncode, done.stderr)
        self.assertIn("refusing subcommand", done.stderr)

    def test_cbx_without_its_gate_refuses_rather_than_running_ungated(self):
        alone = self.scratch / "alone"
        alone.mkdir()
        shutil.copyfile(SKILL_SCRIPTS / "cbx", alone / "cbx")
        env = {**git_environment(), "PATH": f"{self.bin}{os.pathsep}{os.environ['PATH']}"}
        done = subprocess.run([_bash(), str(alone / "cbx"), "stats"], cwd=self.repo, env=env,
                              capture_output=True, text=True, encoding="utf-8",
                              timeout=60)
        self.assertEqual(4, done.returncode, done.stderr)
        self.assertIn("no gate.sh", done.stderr)
        self.assertNotIn("cli-ran", done.stdout)

    def test_no_python_312_is_a_refusal_and_not_a_pass(self):
        """The CLI on PATH could run, but nobody judged the index it opens."""
        env = {**git_environment(), "PATH": str(self.bin)}
        done = subprocess.run([_bash(), str(SKILL_SCRIPTS / "cbx"), "stats"], cwd=self.repo,
                              env=env, capture_output=True, text=True, encoding="utf-8",
                              timeout=60)
        self.assertEqual(4, done.returncode, done.stderr)
        self.assertIn("no Python 3.12", done.stderr)
        self.assertNotIn("cli-ran", done.stdout)


if __name__ == "__main__":
    unittest.main()
