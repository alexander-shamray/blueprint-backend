"""The skill wrappers refuse destructive subcommands and prefer PATH.

CI discovers this directory, not `.claude/skills/**`, so the wrappers' own
safety boundary is asserted here with stubbed executables.
"""

import importlib.util
import os
import re
import shutil
import stat
import subprocess
import sys
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


def _powershell():
    for name in ("pwsh", "powershell"):
        found = shutil.which(name)
        if found:
            return found
    raise unittest.SkipTest("PowerShell is not on PATH")


def _ps_invoke(script, *args, env=None, cwd=None):
    return subprocess.run(
        [_powershell(), "-NoProfile", "-ExecutionPolicy", "Bypass",
         "-File", str(script), *args],
        capture_output=True, text=True, env=env, cwd=cwd,
    )


def _path_without_launchers(*names):
    """PATH entries that do not contain a named launcher.

    The 127 and Python-fallback cases have to hide `python`/`py`/
    `codebase-index` without dropping `dotnet`, which the Windows `pwsh`
    shim needs on PATH to start at all.
    """
    suffixes = ("", ".exe", ".cmd", ".bat", ".com")
    kept = []
    for part in os.environ.get("PATH", "").split(os.pathsep):
        if not part:
            continue
        root = Path(part)
        hidden = False
        for name in names:
            if any((root / f"{name}{suffix}").exists() for suffix in suffixes):
                hidden = True
                break
        if not hidden:
            kept.append(part)
    return os.pathsep.join(kept)


_PS_HIDDEN = ("python", "python3", "py", "codebase-index")
_PY_MODULE = (
    "if '-m' in args:\n"
    "    i = args.index('-m')\n"
    "    if i + 1 < len(args) and args[i + 1] == 'codebase_index':\n"
    "        print('MODULE', *args[i + 2:])\n"
    "        raise SystemExit(0)\n"
    "raise SystemExit(1)\n"
)
# `py -3.12` pins the version on the launcher flag; the -c probe is only
# `import codebase_index`.
_PY_LAUNCHER_STUB = (
    "import sys\n"
    "args = sys.argv[1:]\n"
    "if '-c' in args[:3]:\n"
    "    raise SystemExit(0)\n"
    + _PY_MODULE
)
# `python` / `python3` select only when the -c payload is the 3.12 check.
# Succeeding every -c would admit a 3.11 interpreter.
_PY_INTERPRETER_STUB = (
    "import sys\n"
    "args = sys.argv[1:]\n"
    "if '-c' in args[:3]:\n"
    "    i = args.index('-c')\n"
    "    payload = args[i + 1] if i + 1 < len(args) else ''\n"
    "    raise SystemExit(\n"
    "        0 if 'version_info[:2] == (3, 12)' in payload else 1)\n"
    + _PY_MODULE
)
_PY_WRONG_VERSION_STUB = (
    "import sys\n"
    "args = sys.argv[1:]\n"
    "if '-c' in args[:3]:\n"
    "    raise SystemExit(1)\n"
    "if '-m' in args:\n"
    "    print('MODULE')\n"
    "    raise SystemExit(0)\n"
    "raise SystemExit(1)\n"
)


def _checkout_module(directory):
    """A CWD `codebase_index.py` that records it was imported.

    `python -c` / `python -m` prepend the working directory, so this is
    what an auto-approved fallback would execute without `-P`.
    """
    path = Path(directory) / "codebase_index.py"
    path.write_text(
        "from pathlib import Path\n"
        "Path(__file__).with_name('TOUCHED').write_text('1')\n"
        "print('CHECKOUT')\n"
        "raise SystemExit(0)\n",
        encoding="utf-8")
    return path


def _write_ps_stub(directory, name, body):
    """A PATH launcher PowerShell's Get-Command will find.

    Windows looks at PATHEXT (`.cmd`); POSIX pwsh looks for an executable
    with no suffix.
    """
    stub_py = Path(directory) / f"{name}-stub.py"
    stub_py.write_text(body, encoding="utf-8")
    quoted = str(stub_py).replace("'", "'\\''")
    if os.name == "nt":
        launcher = Path(directory) / f"{name}.cmd"
        launcher.write_text(
            f"@echo off\r\n\"{sys.executable}\" \"{stub_py}\" %*\r\n",
            encoding="utf-8")
        return launcher
    launcher = Path(directory) / name
    launcher.write_text(
        f"#!/bin/sh\nexec '{sys.executable}' '{quoted}' \"$@\"\n",
        encoding="utf-8")
    launcher.chmod(launcher.stat().st_mode | stat.S_IEXEC)
    return launcher


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

    def test_python_fallbacks_keep_the_checkout_off_sys_path(self):
        bash = self.uncommented(CBX)
        ps1 = self.uncommented(CBX_PS1)
        self.assertIn("export PYTHONSAFEPATH=1", bash)
        self.assertIn('$env:PYTHONSAFEPATH = "1"', ps1)
        self.assertIn("py -3.12 -P -c", bash)
        self.assertIn("py -3.12 -P -m", bash)
        self.assertIn("python3 -P -c", bash)
        self.assertIn("python3 -P -m", bash)
        self.assertIn("python -P -c", bash)
        self.assertIn("python -P -m", bash)
        self.assertIn("-3.12 -P -c", ps1)
        self.assertIn("-3.12 -P -m", ps1)
        self.assertIn("-P -c", ps1)
        self.assertIn("-P -m", ps1)

    def test_destructive_subcommands_are_refused(self):
        for sub in ("clean", "init", "watch", "graph"):
            with self.subTest(sub=sub):
                out = subprocess.run(
                    [_bash(), str(CBX), sub],
                    capture_output=True, text=True)
                self.assertEqual(2, out.returncode, out.stderr)
                self.assertIn("refusing subcommand", out.stderr)

    def test_path_cli_is_preferred(self):
        with tempfile.TemporaryDirectory(prefix="cbx-path-") as fake:
            stub = Path(fake) / "codebase-index"
            stub.write_text("#!/bin/sh\necho PATH_CLI \"$@\"\n", encoding="utf-8")
            stub.chmod(stub.stat().st_mode | stat.S_IEXEC)
            env = {
                **os.environ,
                "PATH": fake + os.pathsep + os.environ.get("PATH", ""),
            }
            out = subprocess.run(
                [_bash(), str(CBX), "search", "X"],
                capture_output=True, text=True, env=env)
            self.assertEqual(0, out.returncode, out.stderr)
            self.assertIn("PATH_CLI search X", out.stdout)

    def test_python312_fallback_when_cli_is_absent(self):
        with tempfile.TemporaryDirectory(prefix="cbx-py-") as fake:
            py = Path(fake) / "python3"
            py.write_text(
                "#!/bin/sh\n"
                "if [ \"$1\" = \"-P\" ]; then shift; fi\n"
                "if [ \"$1\" = \"-c\" ]; then\n"
                "  case \"$2\" in\n"
                "    *'version_info[:2] == (3, 12)'*) exit 0 ;;\n"
                "  esac\n"
                "  exit 1\n"
                "fi\n"
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

    def test_python_fallback_rejects_a_non_312_interpreter(self):
        with tempfile.TemporaryDirectory(prefix="cbx-py311-") as fake:
            py = Path(fake) / "python3"
            py.write_text(
                "#!/bin/sh\n"
                "if [ \"$1\" = \"-P\" ]; then shift; fi\n"
                "if [ \"$1\" = \"-c\" ]; then exit 1; fi\n"
                "if [ \"$1\" = \"-m\" ]; then echo MODULE; exit 0; fi\n"
                "exit 1\n",
                encoding="utf-8")
            py.chmod(py.stat().st_mode | stat.S_IEXEC)
            env = {**os.environ, "PATH": fake}
            out = subprocess.run(
                [_bash(), str(CBX), "search", "X"],
                capture_output=True, text=True, env=env)
            self.assertEqual(127, out.returncode, out.stderr)
            self.assertIn("not on PATH", out.stderr)
            self.assertNotIn("MODULE", out.stdout)

    def test_wrappers_guard_and_skill_share_one_allow_list(self):
        bash = re.search(
            r'(?m)^ALLOWED="([^"]+)"', self.uncommented(CBX))
        self.assertIsNotNone(bash)
        bash_set = frozenset(bash.group(1).split())
        ps = re.search(
            r"\$allowed\s*=\s*@\((.*?)\)",
            self.uncommented(CBX_PS1), re.S)
        self.assertIsNotNone(ps)
        ps_set = frozenset(re.findall(r'"([^"]+)"', ps.group(1)))
        spec = importlib.util.spec_from_file_location(
            "guard_index_argv",
            SCRIPTS.parent / "hooks" / "guard-index-argv.py")
        guard = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(guard)
        skill = (SCRIPTS.parent / "skills" / "codebase-index" / "SKILL.md"
                 ).read_text(encoding="utf-8")
        grants = frozenset(re.findall(
            r"Bash\(bash \.claude/skills/codebase-index/scripts/cbx "
            r"([^:)]+):\*\)",
            skill.split("---")[1]))
        self.assertEqual(bash_set, ps_set)
        self.assertEqual(bash_set, frozenset(guard.ALLOWED))
        self.assertEqual(bash_set, grants)
        self.assertTrue(
            bash_set.isdisjoint({"graph", "clean", "init", "watch"}))

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

    def test_skill_does_not_instruct_a_cbx_graph_invocation(self):
        skill = (SCRIPTS.parent / "skills" / "codebase-index" / "SKILL.md").read_text(
            encoding="utf-8")
        commands = (
            SCRIPTS.parent / "skills" / "codebase-index" / "references"
            / "commands.md"
        ).read_text(encoding="utf-8")
        self.assertNotIn("cbx graph", skill)
        self.assertNotIn("cbx graph", commands)

    def test_powershell_refuses_destructive_subcommands(self):
        for sub in ("clean", "init", "watch", "graph"):
            with self.subTest(sub=sub):
                out = _ps_invoke(CBX_PS1, sub)
                self.assertEqual(2, out.returncode, out.stderr)
                self.assertIn("refusing subcommand", out.stderr)

    def test_powershell_path_cli_is_preferred(self):
        with tempfile.TemporaryDirectory(prefix="cbx-ps-path-") as fake:
            if os.name == "nt":
                stub = Path(fake) / "codebase-index.cmd"
                stub.write_text(
                    "@echo off\r\necho PATH_CLI %*\r\n", encoding="utf-8")
            else:
                stub = Path(fake) / "codebase-index"
                stub.write_text(
                    "#!/bin/sh\necho PATH_CLI \"$@\"\n", encoding="utf-8")
                stub.chmod(stub.stat().st_mode | stat.S_IEXEC)
            env = {
                **os.environ,
                "PATH": fake + os.pathsep + os.environ.get("PATH", ""),
            }
            out = _ps_invoke(CBX_PS1, "search", "X", env=env)
            self.assertEqual(0, out.returncode, out.stderr)
            self.assertIn("PATH_CLI", out.stdout)
            self.assertIn("search", out.stdout)
            self.assertIn("X", out.stdout)

    def test_powershell_py_fallback_when_cli_is_absent(self):
        with tempfile.TemporaryDirectory(prefix="cbx-ps-py-") as fake:
            _write_ps_stub(fake, "py", _PY_LAUNCHER_STUB)
            env = {
                **os.environ,
                "PATH": fake + os.pathsep + _path_without_launchers(*_PS_HIDDEN),
            }
            out = _ps_invoke(CBX_PS1, "search", "X", env=env)
            self.assertEqual(0, out.returncode, out.stderr)
            self.assertIn("MODULE search X", out.stdout)

    def test_powershell_python_fallback_when_py_is_absent(self):
        with tempfile.TemporaryDirectory(prefix="cbx-ps-python-") as fake:
            _write_ps_stub(fake, "python", _PY_INTERPRETER_STUB)
            env = {
                **os.environ,
                "PATH": fake + os.pathsep + _path_without_launchers(*_PS_HIDDEN),
            }
            out = _ps_invoke(CBX_PS1, "search", "X", env=env)
            self.assertEqual(0, out.returncode, out.stderr)
            self.assertIn("MODULE search X", out.stdout)

    def test_powershell_python_fallback_rejects_a_non_312_interpreter(self):
        with tempfile.TemporaryDirectory(prefix="cbx-ps-py311-") as fake:
            _write_ps_stub(fake, "python", _PY_WRONG_VERSION_STUB)
            env = {
                **os.environ,
                "PATH": fake + os.pathsep + _path_without_launchers(*_PS_HIDDEN),
            }
            out = _ps_invoke(CBX_PS1, "search", "X", env=env)
            self.assertEqual(127, out.returncode, out.stderr)
            self.assertIn("not on PATH", out.stderr)
            self.assertNotIn("MODULE", out.stdout)

    def test_python_fallback_does_not_import_a_checkout_module(self):
        with tempfile.TemporaryDirectory(prefix="cbx-cwd-") as fake:
            _checkout_module(fake)
            py_dir = str(Path(sys.executable).resolve().parent)
            env = {
                **os.environ,
                "PATH": py_dir + os.pathsep + _path_without_launchers(
                    "codebase-index", "py", "python3"),
            }
            out = subprocess.run(
                [_bash(), str(CBX), "search", "X"],
                capture_output=True, text=True, env=env, cwd=fake)
            self.assertFalse(
                (Path(fake) / "TOUCHED").exists(), out.stdout + out.stderr)
            self.assertNotIn("CHECKOUT", out.stdout)
            self.assertNotIn("CHECKOUT", out.stderr)

    def test_powershell_fallback_does_not_import_a_checkout_module(self):
        with tempfile.TemporaryDirectory(prefix="cbx-ps-cwd-") as fake:
            _checkout_module(fake)
            py_dir = str(Path(sys.executable).resolve().parent)
            env = {
                **os.environ,
                "PATH": py_dir + os.pathsep + _path_without_launchers(
                    "codebase-index", "py", "python3"),
            }
            out = _ps_invoke(CBX_PS1, "search", "X", env=env, cwd=fake)
            self.assertFalse(
                (Path(fake) / "TOUCHED").exists(), out.stdout + out.stderr)
            self.assertNotIn("CHECKOUT", out.stdout)
            self.assertNotIn("CHECKOUT", out.stderr)

    def test_powershell_exits_127_when_nothing_can_run_it(self):
        with tempfile.TemporaryDirectory(prefix="cbx-ps-empty-") as fake:
            env = {
                **os.environ,
                "PATH": fake + os.pathsep + _path_without_launchers(*_PS_HIDDEN),
            }
            out = _ps_invoke(CBX_PS1, "search", "X", env=env)
            self.assertEqual(127, out.returncode, out.stderr)
            self.assertIn("not on PATH", out.stderr)


if __name__ == "__main__":
    unittest.main()
