"""The skill wrappers refuse destructive subcommands and prefer PATH.

CI discovers this directory, not `.claude/skills/**`, so the wrappers' own
safety boundary is asserted here with stubbed executables.
"""

import importlib.util
import json
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
SKILL = SCRIPTS.parent / "skills" / "codebase-index"

# The bare console script `codebase-index <subcommand>`, which no wrapper pins to the interpreter or holds to the
# allow list. At the start of a line, or after a backtick or whitespace, so the path `skills/codebase-index/` and the
# package name in prose do not match.
UNPINNED_CLI = re.compile(
    r"(?:^|[`\s])codebase-index\s+(search|explain|architecture|symbol|refs|impact|diff-impact|path|describe|verify"
    r"|stats|doctor|update|index|graph|mcp|clean|init|watch)\b", re.M)


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
        capture_output=True, text=True, encoding="utf-8", env=env, cwd=cwd,
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


def _ps_in_the_callers_process(subcommand, env):
    """cbx.ps1 run as a caller runs it, with `&`, in a session that then says
    what the script exited with and what its environment holds. Under `-File`
    the session ends with the script, and no restore could be seen."""
    script = str(CBX_PS1).replace("'", "''")
    command = (f"& '{script}' {subcommand} X; "
               'Write-Output "code=$LASTEXITCODE safe=[$env:PYTHONSAFEPATH] '
               'auto=[$env:CBX_NO_SKILL_AUTO_UPDATE]"')
    return subprocess.run(
        [_powershell(), "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command],
        capture_output=True, text=True, encoding="utf-8", env=env)


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

    def test_the_bash_wrapper_runs_in_strict_mode(self):
        self.assertRegex(self.uncommented(CBX), r"(?m)^set -euo pipefail$")

    def test_both_wrappers_disable_skill_auto_update(self):
        self.assertIn(
            "export CBX_NO_SKILL_AUTO_UPDATE=1", self.uncommented(CBX))
        self.assertIn(
            '$env:CBX_NO_SKILL_AUTO_UPDATE = "1"', self.uncommented(CBX_PS1))

    def test_the_session_disables_it_for_a_cli_run_without_the_wrapper(self):
        """A bare `codebase-index` run rewrites the tracked skill, wrappers
        included, unless its environment says not to."""
        settings = json.loads((SCRIPTS.parent / "settings.json").read_text(encoding="utf-8"))
        self.assertEqual("1", settings["env"]["CBX_NO_SKILL_AUTO_UPDATE"])

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

    def test_powershell_restores_the_variables_in_a_finally_with_one_exit_after_it(self):
        """The text of the structure the behavioural case above runs: an
        exit inside the `try` would skip the restore."""
        body = self.uncommented(CBX_PS1)
        self.assertIn("$savedSafePath = $env:PYTHONSAFEPATH", body)
        self.assertIn("$savedAutoUpdate = $env:CBX_NO_SKILL_AUTO_UPDATE", body)
        self.assertIn('try {\n    $env:CBX_NO_SKILL_AUTO_UPDATE = "1"', body)
        self.assertIn("} finally {\n    $env:PYTHONSAFEPATH = $savedSafePath\n"
                      "    $env:CBX_NO_SKILL_AUTO_UPDATE = $savedAutoUpdate", body)
        self.assertEqual(["exit 2", "exit $code"], re.findall(r"exit \S+", body))

    def test_destructive_subcommands_are_refused(self):
        for sub in ("clean", "init", "watch", "graph"):
            with self.subTest(sub=sub):
                out = subprocess.run(
                    [_bash(), str(CBX), sub],
                    capture_output=True, text=True, encoding="utf-8")
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
                capture_output=True, text=True, encoding="utf-8", env=env, cwd=fake)
            self.assertEqual(0, out.returncode, out.stderr)
            self.assertIn("PATH_CLI search X", out.stdout)

    def test_python312_fallback_when_cli_is_absent(self):
        with tempfile.TemporaryDirectory(prefix="cbx-py-") as fake:
            py = Path(fake) / "python3"
            # A 3.12 that passes the index gate: `test_index_gate.py` holds it.
            py.write_text(
                "#!/bin/sh\n"
                "while [ \"$1\" = \"-P\" ] || [ \"$1\" = \"-I\" ]; do shift; done\n"
                "case \"$1\" in *gate-index.py) exit 0 ;; esac\n"
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
                capture_output=True, text=True, encoding="utf-8", env=env)
            self.assertEqual(0, out.returncode, out.stderr)
            self.assertIn("MODULE search X", out.stdout)

    def test_python_fallback_rejects_a_non_312_interpreter(self):
        """The index gate asks for the same 3.12 first, so a 3.11 is refused
        there, exit 4, before any fallback could take it."""
        with tempfile.TemporaryDirectory(prefix="cbx-py311-") as fake:
            py = Path(fake) / "python3"
            py.write_text(
                "#!/bin/sh\n"
                "while [ \"$1\" = \"-P\" ] || [ \"$1\" = \"-I\" ]; do shift; done\n"
                "if [ \"$1\" = \"-c\" ]; then exit 1; fi\n"
                "if [ \"$1\" = \"-m\" ]; then echo MODULE; exit 0; fi\n"
                "exit 0\n",
                encoding="utf-8")
            py.chmod(py.stat().st_mode | stat.S_IEXEC)
            env = {**os.environ, "PATH": fake}
            out = subprocess.run(
                [_bash(), str(CBX), "search", "X"],
                capture_output=True, text=True, encoding="utf-8", env=env)
            self.assertEqual(4, out.returncode, out.stderr)
            self.assertIn("no Python 3.12", out.stderr)
            self.assertNotIn("MODULE", out.stdout)

    def test_a_fallback_whose_own_probe_fails_is_not_taken(self):
        """The gate's probe asks for 3.12 alone and the fallback's for 3.12
        with the package, so an interpreter passing the first and failing the
        second is judged by the fallback's own check."""
        with tempfile.TemporaryDirectory(prefix="cbx-py-nopkg-") as fake:
            py = Path(fake) / "python3"
            py.write_text(
                "#!/bin/sh\n"
                "while [ \"$1\" = \"-P\" ] || [ \"$1\" = \"-I\" ]; do shift; done\n"
                "case \"$1\" in *gate-index.py) exit 0 ;; esac\n"
                "if [ \"$1\" = \"-c\" ]; then\n"
                "  case \"$2\" in\n"
                "    *codebase_index*) exit 1 ;;\n"
                "    *'version_info[:2] == (3, 12)'*) exit 0 ;;\n"
                "  esac\n"
                "  exit 1\n"
                "fi\n"
                "if [ \"$1\" = \"-m\" ]; then echo MODULE; exit 0; fi\n"
                "exit 1\n",
                encoding="utf-8")
            py.chmod(py.stat().st_mode | stat.S_IEXEC)
            env = {**os.environ, "PATH": fake}
            out = subprocess.run(
                [_bash(), str(CBX), "search", "X"],
                capture_output=True, text=True, encoding="utf-8", env=env)
            self.assertEqual(127, out.returncode, out.stderr)
            self.assertIn("not on PATH", out.stderr)
            self.assertNotIn("MODULE", out.stdout)

    def test_a_fallback_on_another_version_is_not_taken_behind_a_312_gate(self):
        """`py -3.12` lacking the package passes the gate, and a `python3` or
        `python` holding the package is a 3.11: only that fallback's version
        check stops it from being run."""
        for name in ("python3", "python"):
            with self.subTest(name):
                self.fallback_on_another_version(name)

    def fallback_on_another_version(self, name):
        with tempfile.TemporaryDirectory(prefix="cbx-py-mixed-") as fake:
            launcher = Path(fake) / "py"
            launcher.write_text(
                "#!/bin/sh\n"
                "[ \"$1\" = \"-3.12\" ] && shift\n"
                "while [ \"$1\" = \"-P\" ] || [ \"$1\" = \"-I\" ]; do shift; done\n"
                "case \"$1\" in *gate-index.py) exit 0 ;; esac\n"
                "if [ \"$1\" = \"-c\" ]; then\n"
                "  case \"$2\" in *codebase_index*) exit 1 ;; esac\n"
                "  exit 0\n"
                "fi\n"
                "exit 1\n",
                encoding="utf-8")
            older = Path(fake) / name
            older.write_text(
                "#!/bin/sh\n"
                "while [ \"$1\" = \"-P\" ] || [ \"$1\" = \"-I\" ]; do shift; done\n"
                "if [ \"$1\" = \"-c\" ]; then\n"
                "  case \"$2\" in *'version_info[:2] == (3, 12)'*) exit 1 ;; esac\n"
                "  exit 0\n"
                "fi\n"
                "if [ \"$1\" = \"-m\" ]; then echo MODULE; exit 0; fi\n"
                "exit 1\n",
                encoding="utf-8")
            for stub in (launcher, older):
                stub.chmod(stub.stat().st_mode | stat.S_IEXEC)
            env = {**os.environ, "PATH": fake}
            out = subprocess.run(
                [_bash(), str(CBX), "search", "X"],
                capture_output=True, text=True, encoding="utf-8", env=env)
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

    def test_skill_markdown_does_not_teach_the_unpinned_cli(self):
        # An agent loads SKILL.md and the references. The case above reads two files for the one subcommand the
        # wrapper refuses; a copy-pasted `codebase-index verify` in any other file would bypass the wrapper and
        # its allow list while that case stays green. So every markdown file the skill ships is read, for any
        # subcommand.
        files = sorted(SKILL.rglob("*.md"))
        self.assertGreater(len(files), 3, "found almost no skill markdown")
        for path in files:
            with self.subTest(path=path.relative_to(SKILL).as_posix()):
                found = UNPINNED_CLI.search(path.read_text(encoding="utf-8"))
                self.assertIsNone(found, f"{path.name} teaches the unpinned CLI: {found and found.group(0)!r}")

    def test_the_skill_teaches_the_wrapper_instead(self):
        # The positive control for the case above: it passes against a skill that teaches nothing at all.
        skill = (SKILL / "SKILL.md").read_text(encoding="utf-8")
        self.assertIn("bash .claude/skills/codebase-index/scripts/cbx search", skill)
        self.assertIsNone(UNPINNED_CLI.search("`bash .claude/skills/codebase-index/scripts/cbx search \"X\"`"))

    def test_the_scan_sees_each_way_the_unpinned_cli_is_spelled(self):
        # The scan's subject is the scan: a pattern that matched nothing would pass every file above.
        for text in ("codebase-index verify --json", "Run `codebase-index search X`", "| `codebase-index impact X` |",
                     "x codebase-index  graph", "$ codebase-index doctor", "codebase-index mcp",
                     "`codebase-index clean`", "codebase-index init .", "run codebase-index watch"):
            with self.subTest(text=text):
                self.assertIsNotNone(UNPINNED_CLI.search(text))
        for text in ("the `codebase-index` package", "pip install codebase-index", "skills/codebase-index/scripts/cbx",
                     "codebase-index is a package"):
            with self.subTest(text=text):
                self.assertIsNone(UNPINNED_CLI.search(text))

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

    def test_powershell_puts_both_variables_back_whichever_way_it_leaves(self):
        """The script sets them for what it starts, in the process that
        called it, and would leave them set for every later `python` or
        `codebase-index` that session starts: `PYTHONSAFEPATH` takes the
        script's directory off `sys.path` for all of them."""
        def path_cli(fake):
            if os.name == "nt":
                stub = Path(fake) / "codebase-index.cmd"
                stub.write_text("@echo off\r\necho PATH_CLI %*\r\n", encoding="utf-8")
            else:
                stub = Path(fake) / "codebase-index"
                stub.write_text("#!/bin/sh\necho PATH_CLI \"$@\"\n", encoding="utf-8")
                stub.chmod(stub.stat().st_mode | stat.S_IEXEC)
            return fake + os.pathsep + os.environ.get("PATH", ""), 0, "PATH_CLI"

        def module(fake):
            _write_ps_stub(fake, "py", _PY_LAUNCHER_STUB)
            return fake + os.pathsep + _path_without_launchers(*_PS_HIDDEN), 0, "MODULE"

        def nothing(fake):
            return fake + os.pathsep + _path_without_launchers(*_PS_HIDDEN), 127, ""

        ours = {name: value for name, value in os.environ.items()
                if name not in ("PYTHONSAFEPATH", "CBX_NO_SKILL_AUTO_UPDATE")}
        for route in (path_cli, module, nothing):
            for held in ({}, {"PYTHONSAFEPATH": "kept", "CBX_NO_SKILL_AUTO_UPDATE": "mine"}):
                with self.subTest(route=route.__name__, held=held), \
                        tempfile.TemporaryDirectory(prefix="cbx-ps-env-") as fake:
                    path, code, said = route(fake)
                    out = _ps_in_the_callers_process("search", {**ours, **held, "PATH": path})
                    self.assertIn(f"code={code} ", out.stdout, out.stdout + out.stderr)
                    self.assertIn(said, out.stdout, out.stdout + out.stderr)
                    self.assertIn(
                        f"safe=[{held.get('PYTHONSAFEPATH', '')}] auto=[{held.get('CBX_NO_SKILL_AUTO_UPDATE', '')}]",
                        out.stdout)

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
                capture_output=True, text=True, encoding="utf-8", env=env, cwd=fake)
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
