"""The skill wrappers refuse destructive subcommands and prefer PATH.

CI discovers this directory, not `.claude/skills/**`, so the wrappers' own
safety boundary is asserted here with stubbed executables.
"""

import os
import shutil
import stat
import subprocess
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
CBX = SCRIPTS.parent / "skills" / "codebase-index" / "scripts" / "cbx"
CBX_PS1 = CBX.with_suffix(".ps1")


def _bash():
    git = Path(r"C:\Program Files\Git\bin\bash.exe")
    if git.is_file():
        return str(git)
    found = shutil.which("bash")
    if not found:
        raise unittest.SkipTest("bash is not on PATH")
    return found


class CbxWrapper(unittest.TestCase):
    def uncommented(self, path):
        text = path.read_text(encoding="utf-8")
        return "\n".join(
            line for line in text.splitlines()
            if not line.lstrip().startswith("#"))

    def test_both_wrappers_disable_skill_auto_update(self):
        self.assertIn(
            "export CBX_NO_SKILL_AUTO_UPDATE=1", self.uncommented(CBX))
        self.assertIn(
            '$env:CBX_NO_SKILL_AUTO_UPDATE = "1"', self.uncommented(CBX_PS1))

    def test_destructive_subcommands_are_refused(self):
        for sub in ("clean", "init", "watch", "graph"):
            with self.subTest(sub=sub):
                out = subprocess.run(
                    [_bash(), str(CBX), sub],
                    capture_output=True, text=True)
                self.assertEqual(2, out.returncode, out.stderr)
                self.assertIn("refusing subcommand", out.stderr)

    def test_path_cli_is_preferred(self):
        fake = tempfile.mkdtemp(prefix="cbx-path-")
        stub = Path(fake) / "codebase-index"
        stub.write_text("#!/bin/sh\necho PATH_CLI \"$@\"\n", encoding="utf-8")
        stub.chmod(stub.stat().st_mode | stat.S_IEXEC)
        env = {**os.environ, "PATH": fake + os.pathsep + os.environ.get("PATH", "")}
        out = subprocess.run(
            [_bash(), str(CBX), "search", "X"],
            capture_output=True, text=True, env=env)
        self.assertEqual(0, out.returncode, out.stderr)
        self.assertIn("PATH_CLI search X", out.stdout)

    def test_python312_fallback_when_cli_is_absent(self):
        fake = tempfile.mkdtemp(prefix="cbx-py-")
        py = Path(fake) / "python3"
        py.write_text(
            "#!/bin/sh\n"
            "if [ \"$1\" = \"-c\" ]; then exit 0; fi\n"
            "if [ \"$1\" = \"-m\" ] && [ \"$2\" = \"codebase_index\" ]; then\n"
            "  shift 2\n"
            "  echo MODULE \"$@\"\n"
            "  exit 0\n"
            "fi\n"
            "exit 1\n",
            encoding="utf-8")
        py.chmod(py.stat().st_mode | stat.S_IEXEC)
        env = {**os.environ, "PATH": fake}
        out = subprocess.run(
            [_bash(), str(CBX), "search", "X"],
            capture_output=True, text=True, env=env)
        self.assertEqual(0, out.returncode, out.stderr)
        self.assertIn("MODULE search X", out.stdout)

    def test_powershell_wrapper_names_the_same_allow_list(self):
        text = self.uncommented(CBX_PS1)
        for sub in ("search", "index", "verify"):
            self.assertIn(f'"{sub}"', text)
        self.assertNotIn('"graph"', text)
        self.assertNotIn('"clean"', text)

    def test_skill_frontmatter_grants_cbx_per_subcommand_not_the_cli(self):
        text = (SCRIPTS.parent / "skills" / "codebase-index" / "SKILL.md").read_text(
            encoding="utf-8")
        fm = text.split("---")[1]
        self.assertNotIn("Bash(codebase-index", fm)
        self.assertNotIn("graph:*", fm)
        self.assertNotIn(
            "Bash(bash .claude/skills/codebase-index/scripts/cbx:*)", fm)
        self.assertIn(
            "Bash(bash .claude/skills/codebase-index/scripts/cbx search:*)",
            fm)


if __name__ == "__main__":
    unittest.main()
