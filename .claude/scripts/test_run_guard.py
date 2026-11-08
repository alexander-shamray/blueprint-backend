"""`run-guard.sh` and `run-guard.py`: each guard hook runs through one launcher, on every host, and only exit 2 refuses.

Ported from frontend's launcher suite. The wiring cases read `settings.json`; the mark cases run copies of the launcher
under stand-in interpreters that log every start, and the remembered-path cases hand those starts to a real one.
"""

import importlib.util
import json
import os
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path
from unittest import mock

from review_helpers import BASH, SCRIPTS, SETTINGS, setUpModule  # noqa: F401 - unittest calls setUpModule

GUARD = re.compile(r"guard-[A-Za-z0-9-]+\.py")
LAUNCHER_PATH = "${CLAUDE_PROJECT_DIR}/.claude/hooks/run-guard.sh"


def bash_event(command):
    return {"tool_name": "Bash", "tool_input": {"command": command}}


# One event each guard lets through, and one each refuses that can refuse anywhere: `guard-sed-crlf.py` refuses only
# where Git Bash's sed drops the CR, and only for a tracked file.
ALLOWS = (
    ("guard-git-argv.py", bash_event("ls")),
    ("guard-index-argv.py", bash_event("cbx search cart")),
    ("guard-helper-argv.py", bash_event("gh pr create --title 'cart'")),
    ("guard-sed-crlf.py", bash_event("sed -i s/a/b/ notes.txt")),
    ("guard-edit-target.py", {"tool_name": "Write", "tool_input": {"file_path": "notes.txt"}}),
)
REFUSES = (
    ("guard-git-argv.py", bash_event("git push --force origin main")),
    ("guard-index-argv.py", bash_event("cbx search x; rm -rf /")),
    ("guard-helper-argv.py", bash_event('gh pr create --title "$(id)"')),
    ("guard-edit-target.py", {"tool_name": "Write", "tool_input": {}}),
)


class LauncherMarkCases:
    """What the classes below share: a checkout, the shells, the prover."""

    HOOKS = SCRIPTS.parent / "hooks"
    LAUNCHER = HOOKS / "run-guard.sh"
    PROVER = HOOKS / "run-guard.py"
    SH = shutil.which("sh")

    PROBED = ["python3 probe", "python3 runs guard-git-argv.py"]
    PROVED = ["python3 proves guard-git-argv.py"]

    def checkout(self, **options):
        return ALauncherCheckout(self, **options)

    def shells(self):
        """The `sh` the hook wirings name, which is `dash` on Linux, then `bash` where it is another shell."""
        if os.name == "posix":
            self.assertIsNotNone(self.SH, "no `sh` on PATH, which is what the hook wirings run")
        found = [self.SH or BASH]
        if os.path.realpath(found[0]) != os.path.realpath(BASH):
            found.append(BASH)
        return found

    def prover(self):
        # Without bytecode beside the hooks: the launcher runs this file as a script, never as an import.
        spec = importlib.util.spec_from_file_location("run_guard", self.PROVER)
        module = importlib.util.module_from_spec(spec)
        with mock.patch.object(sys, "dont_write_bytecode", True):
            spec.loader.exec_module(module)
        return module

    def code(self):
        return [line for line in self.LAUNCHER.read_text(encoding="utf-8").splitlines()
                if not line.lstrip().startswith("#")]

    def admitted(self):
        """The guard names the launcher's closed set admits, read from its one `case "$1"` arm."""
        code = self.code()
        opens = [index for index, line in enumerate(code) if line.strip() == 'case "$1" in']
        self.assertEqual(1, len(opens), opens)
        arm = code[opens[0] + 1].strip()
        self.assertTrue(arm.endswith(") ;;"), arm)
        return set(arm[:-len(") ;;")].split("|"))


class ALauncherCheckout:
    """A throwaway `.claude/` holding the launcher, and a PATH of stand-ins.

    Its own tree, because the launcher keeps its mark in its own checkout's `.claude/cache/`; and its own PATH,
    because the tools' directory carries a real `python3` on Linux. Run under `sh` by default, as the wirings are.
    """

    def __init__(self, case, guards=False):
        # Resolved: a Windows runner's TEMP is an 8.3 alias (`RUNNER~1`), which `pwd -W` hands the guard expanded,
        # so the `cwd` below would be a spelling of the copy's root that its anchor does not recognise, and refused.
        self.root = Path(os.path.realpath(tempfile.mkdtemp(prefix="launcher-mark-")))
        case.addCleanup(shutil.rmtree, str(self.root), ignore_errors=True)
        self.source = SCRIPTS.parent / "hooks"
        self.hooks = self.root / ".claude" / "hooks"
        self.hooks.mkdir(parents=True)
        for path in self.source.glob("run-guard.*"):
            shutil.copy2(path, self.hooks / path.name)
        if guards:
            for path in self.source.glob("guard-*.py"):
                shutil.copy2(path, self.hooks / path.name)
        self.launcher = self.hooks / "run-guard.sh"
        self.cache = self.root / ".claude" / "cache"
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.log = self.root / "log"
        self.gate = self.root / "gate"
        # `rm` is there although the launcher never calls it, so a launcher that did is caught by its case.
        for tool in ("dirname", "mkdir", "rmdir", "rm", "sleep"):
            self.tool(tool)
        self.env = {**os.environ, "PATH": str(self.bin)}
        self.shell = case.shells()[0]

    @staticmethod
    def quoted(path):
        return "'" + Path(path).as_posix().replace("'", "'\\''") + "'"

    def script(self, name, body):
        target = self.bin / name
        target.write_text("#!/bin/sh\n" + body, encoding="utf-8", newline="\n")
        target.chmod(0o755)

    def tool(self, name):
        """Put one real tool on the PATH, and nothing that sits beside it."""
        self.script(name, f'exec {self.quoted(shutil.which(name))} "$@"\n')

    def stand_in(self, name, probe=0, proves=91, runs=0, real=None, hold=False):
        """A candidate that logs what it was asked for, then leaves with `probe`, `proves` or `runs`.

        `probe` may be keyed by `py`'s selector; `real` hands every call to a real interpreter after logging it, and
        `hold` keeps a probe waiting until the gate file exists.
        """
        def leave(status):
            if real is not None:
                return f'exec {self.quoted(real)} "$@"'
            if isinstance(status, dict):
                arms = " ".join(f'"{key}") exit {value} ;;' for key, value in status.items())
                return f'case "$selector" in {arms} esac; exit 1'
            return f"exit {status}"

        wait = 'while [ ! -e "$gate" ]; do sleep 0.05; done; ' if hold else ""
        self.script(name, (
            f"log={self.quoted(self.log)}\n"
            f"gate={self.quoted(self.gate)}\n"
            "selector=\n"
            'case "$1" in -3.12|-3) selector="$1"; shift ;; esac\n'
            f'said="{name}${{selector:+ $selector}}"\n'
            'case "$1" in\n'
            f'  -c) echo "$said probe" >> "$log"; {wait}{leave(probe)} ;;\n'
            f'  */run-guard.py) echo "$said proves $2" >> "$log"; {leave(proves)} ;;\n'
            f'  *) echo "$said runs ${{1##*/}}" >> "$log"; {leave(runs)} ;;\n'
            "esac\n"))

    def launch(self, guard="guard-git-argv.py", event="{}", shell=None, timeout=120, env=None):
        return subprocess.run(
            [shell or self.shell, str(self.launcher), guard], input=event, capture_output=True,
            encoding="utf-8", errors="replace", env=env or self.env, timeout=timeout)

    def start(self, guard="guard-git-argv.py", event="{}"):
        child = subprocess.Popen(
            [self.shell, str(self.launcher), guard], stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=self.env)
        child.stdin.write(event.encode("utf-8"))
        child.stdin.close()
        # Closed here, so `communicate` has nothing of its own to flush.
        child.stdin = None
        return child

    def ran(self):
        """Every interpreter start so far, in order, as the stand-ins saw it."""
        if not self.log.exists():
            return []
        return self.log.read_text(encoding="utf-8").splitlines()

    def since(self, before):
        return self.ran()[len(before):]

    def mark(self, spelling="python3"):
        """Where the launcher's mark for `spelling` goes."""
        return self.cache / f"run-guard.{spelling.replace(' ', '-')}"

    def marks(self):
        """What the cache holds that is named like a mark, whatever it is."""
        if not self.cache.is_dir():
            return []
        return sorted(p.name for p in self.cache.iterdir() if p.name.startswith("run-guard."))

    def forge(self, spelling="python3", inside=None):
        """Make the mark as a session could: `mkdir`, or a `Write` into it."""
        self.mark(spelling).mkdir(parents=True)
        if inside is not None:
            (self.mark(spelling) / inside).write_bytes(b"planted\n")


class TheHookWiringRunsOnMoreThanOneOperatingSystem(LauncherMarkCases, unittest.TestCase):
    """A guard wired to `py -3.12` cannot start where `py` is absent, and a hook that cannot start does not block.

    `python -m unittest` runs the guards' modules and never the wiring, so these cases are the wiring's own.
    """

    def commands_by_event(self):
        # Every event the file registers, not `PreToolUse` alone: a guard wired under another event past the
        # launcher would otherwise pass every case below.
        settings = json.loads(SETTINGS.read_text(encoding="utf-8"))
        return {event: [h.get("command", "") for entry in entries for h in entry.get("hooks", [])]
                for event, entries in settings.get("hooks", {}).items()}

    def commands(self):
        return [command for found in self.commands_by_event().values() for command in found]

    def guard_commands(self):
        return [command for command in self.commands() if GUARD.search(command)]

    def test_every_registered_event_holds_a_command_these_cases_judge(self):
        # The subject test: what the cases below look at, not what they found.
        by_event = self.commands_by_event()
        self.assertLessEqual({"PreToolUse", "PostToolUse", "SessionStart", "UserPromptSubmit"}, set(by_event))
        for event, found in by_event.items():
            with self.subTest(event=event):
                self.assertTrue(found, f"{event} is registered with no command")

    def test_the_settings_wire_the_guards_the_launcher_admits(self):
        # Both ways: a guard wired past the closed set is refused on every call, and one admitted but unwired is
        # a guard that never runs.
        wired = {name for command in self.guard_commands() for name in GUARD.findall(command)}
        self.assertEqual(self.admitted(), wired)
        self.assertEqual({"guard-git-argv.py", "guard-index-argv.py", "guard-helper-argv.py",
                          "guard-sed-crlf.py", "guard-edit-target.py"}, wired)
        for command in self.commands():
            if "run-guard.sh" in command:
                with self.subTest(command=command):
                    self.assertIn(command, self.guard_commands())

    def test_the_launcher_runs_a_hook_from_a_path_holding_an_apostrophe(self):
        # Under MSYS the launcher handed the interpreter `/c/dev/O'Brien/...`, which MSYS leaves unconverted for an
        # argument holding an apostrophe. On a POSIX host the case passes either way; on Windows it needs `pwd -W`.
        parent = Path(tempfile.mkdtemp(prefix="launcher-apos-"))
        self.addCleanup(shutil.rmtree, str(parent), ignore_errors=True)
        hooks = parent / "O'Brien" / ".claude" / "hooks"
        hooks.mkdir(parents=True)
        for name in ("run-guard.sh", "run-guard.py", "guard-git-argv.py"):
            shutil.copy2(self.LAUNCHER.parent / name, hooks / name)
        event = json.dumps(REFUSES[0][1])
        for shell in sorted({BASH, shutil.which("sh")} - {None}):
            with self.subTest(shell=shell):
                # In the environment, as `${CLAUDE_PROJECT_DIR}` travels: as an argument MSYS drops the apostrophe.
                out = subprocess.run(
                    [shell, "-c", 'sh "$LAUNCHER" guard-git-argv.py'], input=event, capture_output=True,
                    encoding="utf-8", errors="replace", env={**os.environ, "LAUNCHER": str(hooks / "run-guard.sh")})
                self.assertEqual(0, out.returncode, out.stderr)
                self.assertIn("permissionDecision", out.stdout)
                self.assertNotIn("Traceback", out.stderr)

    def test_no_guard_command_names_a_windows_only_interpreter(self):
        found = self.guard_commands()
        self.assertTrue(found, "no guard is registered at all")
        for command in found:
            with self.subTest(command=command):
                self.assertNotIn("py -3.12", command)
                # Nor `python3`, which on Windows is the Store's execution alias: on PATH and not Python.
                self.assertNotRegex(command, r"(^|\s)python3(\s|$)")

    def test_every_guard_goes_through_the_launcher(self):
        for command in self.guard_commands():
            with self.subTest(command=command):
                self.assertIn("run-guard.sh", command)
                self.assertIn("${CLAUDE_PROJECT_DIR}", command)
                words = shlex.split(command)
                self.assertEqual(["sh", LAUNCHER_PATH], words[:2])
                self.assertEqual(1, len(words[2:]), words)

    def test_the_launcher_probes_py_before_python3(self):
        # On Windows `python3` is the Store alias, so a launcher probing it first finds the wrong thing there. The
        # probe lines, not the first mention: the `command -v` block above them names the remembered candidate.
        code = "\n".join(self.code())
        self.assertLess(code.find('py -3.12 -c "$probe"'), code.find('python3 -c "$probe"'))
        self.assertIn("exec py -3.12", code)
        self.assertIn("exec python3", code)

    def test_the_launcher_reaches_a_host_that_only_has_python(self):
        # A host may expose 3.12 as `python` alone; `python3` is probed first, so a host with both keeps it.
        code = "\n".join(self.code())
        self.assertIn('python3 -c "$probe"', code)
        self.assertIn('python -c "$probe"', code)
        self.assertIn("exec python ", code)
        self.assertLess(code.find('python3 -c "$probe"'), code.find('python -c "$probe"'))

    def test_a_launcher_that_is_present_but_broken_is_passed_over(self):
        # `command -v` proves a name exists, not that it runs: a `py` with no 3.12, or the Store alias. Stand-ins
        # on a PATH of their own, in a copy of the launcher, so that neither meets this checkout's mark.
        bin_dir = Path(tempfile.mkdtemp(prefix="launcher-"))
        self.addCleanup(shutil.rmtree, str(bin_dir), ignore_errors=True)
        hooks = bin_dir / ".claude" / "hooks"
        hooks.mkdir(parents=True)
        for file in self.LAUNCHER.parent.glob("run-guard.*"):
            shutil.copy2(file, hooks / file.name)
        launcher = hooks / "run-guard.sh"

        def stand_in(name, probe_ok):
            script = bin_dir / name
            script.write_text(
                "#!/bin/sh\n"
                'if [ "$1" = -3.12 ] || [ "$1" = -3 ]; then shift; fi\n'
                f'if [ "$1" = -c ]; then exit {0 if probe_ok else 1}; fi\n'
                f'echo "ran {name} $(basename "$1")"\n',
                encoding="utf-8", newline="\n")
            script.chmod(0o755)

        tools = {os.path.dirname(shutil.which(t)) for t in ("dirname", "sh")}
        path = os.pathsep.join([str(bin_dir), *sorted(tools)])

        def launch():
            return subprocess.run([BASH, str(launcher), "guard-git-argv.py"], capture_output=True,
                                  encoding="utf-8", errors="replace", env={**os.environ, "PATH": path})

        for broken in ("py", "python3"):
            for name in ("py", "python3", "python"):
                (bin_dir / name).unlink(missing_ok=True)
            stand_in(broken, probe_ok=False)
            if broken == "py":
                stand_in("python3", probe_ok=True)
                expected = "ran python3 guard-git-argv.py"
            else:
                stand_in("python", probe_ok=True)
                expected = "ran python guard-git-argv.py"
            with self.subTest(broken=broken):
                out = launch()
                self.assertEqual(expected, out.stdout.strip(), out.stderr)

        # None that runs is a refusal, and only exit 2 refuses: any other status lets the tool run unguarded.
        for name in ("py", "python3", "python"):
            (bin_dir / name).unlink(missing_ok=True)
            stand_in(name, probe_ok=False)
        with self.subTest(broken="all"):
            out = launch()
            self.assertEqual(2, out.returncode, out.stderr)
            self.assertEqual("", out.stdout.strip())
            self.assertIn("no Python 3.12", out.stderr)

        # A `py` with 3.13 and no 3.12 satisfies the floor: the exact selector fails and the generic one runs.
        for name in ("py", "python3", "python"):
            (bin_dir / name).unlink(missing_ok=True)
        (bin_dir / "py").write_text(
            "#!/bin/sh\n"
            'if [ "$1" = -3.12 ]; then exit 1; fi\n'
            'if [ "$1" = -3 ]; then shift; fi\n'
            'if [ "$1" = -c ]; then exit 0; fi\n'
            'echo "ran py -3 $(basename "$1")"\n',
            encoding="utf-8", newline="\n")
        (bin_dir / "py").chmod(0o755)
        with self.subTest(broken="py -3.12 only"):
            out = launch()
            self.assertEqual("ran py -3 guard-git-argv.py", out.stdout.strip(), out.stderr)

    def test_the_launcher_execs_once_rather_than_falling_back(self):
        # `py -3.12 … || python3 …` re-runs the guard whenever it exits non-zero, so a refusal is judged twice.
        # Asked of the `exec` lines: the argument check and the closed set use `||` for their own refusals.
        code = self.code()
        execs = [line for line in code if "exec " in line]
        for line in execs:
            with self.subTest(line=line):
                self.assertNotIn("||", line)
                self.assertNotIn("&&", line)
        self.assertEqual(4, len(execs), execs)

    def test_each_guard_runs_through_the_wrapper(self):
        # End to end on a copy, with the host's PATH, so the mark it leaves is the copy's: every guard the closed
        # set admits gives its verdict. Refusals are a deny on stdout, which a launcher answering 0 never prints.
        self.assertEqual(self.admitted(), {guard for guard, _ in ALLOWS})
        self.assertLessEqual({guard for guard, _ in REFUSES}, self.admitted())
        for shell in self.shells():
            box = self.checkout(guards=True)
            (box.root / "notes.txt").write_bytes(b"a\r\n")
            for guard, event in ALLOWS + REFUSES:
                denies = (guard, event) in REFUSES
                with self.subTest(shell=shell, guard=guard, denies=denies):
                    out = box.launch(guard, json.dumps({**event, "cwd": str(box.root)}), shell=shell,
                                     env=dict(os.environ))
                    self.assertEqual(0, out.returncode, out.stderr)
                    self.assertNotIn("Traceback", out.stderr)
                    if denies:
                        self.assertEqual("deny", json.loads(out.stdout)["hookSpecificOutput"]["permissionDecision"])
                    else:
                        self.assertEqual("", out.stdout)

    def test_a_missing_interpreter_blocks_a_guard(self):
        # A PATH that reaches no interpreter at all: every guard is refused with 2 rather than run unguarded.
        for shell in self.shells():
            box = self.checkout(guards=True)
            for guard in sorted(self.admitted()):
                with self.subTest(shell=shell, guard=guard):
                    out = box.launch(guard, json.dumps(REFUSES[0][1]), shell=shell)
                    self.assertEqual(2, out.returncode, out.stderr)
                    self.assertEqual("", out.stdout)
                    self.assertIn("no Python 3.12", out.stderr)
                    self.assertEqual([], box.marks())

    def test_an_unknown_hook_exits_2(self):
        # A launcher that took any path would run any script through the hook wiring. The names that exist
        # beside the guards are copied in, so the refusal is the closed set's and not a missing file's.
        for shell in self.shells():
            box = self.checkout(guards=True)
            for name in ("refresh-index.py", "index-query-hint.py"):
                shutil.copy2(box.source / name, box.hooks / name)
            scripts = box.root / ".claude" / "scripts"
            scripts.mkdir()
            (scripts / "x.sh").write_text(f"echo 'x.sh ran' >> {box.quoted(box.log)}\n", encoding="utf-8")
            box.stand_in("python3")
            for bad in ("index-refresh.py", "refresh-index.py", "index-query-hint.py", "../scripts/x.sh",
                        "/etc/passwd", "", "guard-git-argv.py extra"):
                with self.subTest(shell=shell, argument=bad):
                    out = box.launch(bad, shell=shell)
                    self.assertEqual(2, out.returncode, out.stderr)
                    self.assertEqual("", out.stdout)
                    self.assertIn("not a guard this launcher runs", out.stderr)
            for arguments in ((), ("guard-git-argv.py", "extra")):
                with self.subTest(shell=shell, arguments=arguments):
                    out = subprocess.run([shell, str(box.launcher), *arguments], input="{}", capture_output=True,
                                         encoding="utf-8", errors="replace", env=box.env)
                    self.assertEqual(2, out.returncode, out.stderr)
                    self.assertIn("usage: run-guard.sh", out.stderr)
            self.assertEqual([], box.ran())
            self.assertEqual([], box.marks())
            # The control: the same checkout runs a guard it admits, so the silence above is the refusal's.
            self.assertEqual(0, box.launch(shell=shell).returncode)
            self.assertEqual(self.PROBED, box.ran())


class TheLauncherProvesWhatItRemembers(LauncherMarkCases, unittest.TestCase):
    """The launcher remembers that its first candidate passed its probe, which saves a Python start per call.

    A mark is an empty directory named for the first candidate, so it names no program; a remembered interpreter is
    believed only on a status `run-guard.py` alone leaves, no second candidate is tried, and the mark is never opened.
    """

    def test_no_interpreter_is_reachable_beside_the_stand_ins(self):
        # The premise: a real `python3` the shell could still find would be probed in a stand-in's place. One name
        # at a time, because `dash` stops at the first it cannot find.
        look = "for name in py python3 python; do command -v $name; done; :"
        for shell in self.shells():
            with self.subTest(shell=shell):
                box = self.checkout()
                found = subprocess.run([shell, "-c", look], capture_output=True, encoding="utf-8",
                                       env=box.env, check=True)
                self.assertEqual("", found.stdout)
                # The control: the same question finds a stand-in.
                box.stand_in("python3")
                found = subprocess.run([shell, "-c", look], capture_output=True, encoding="utf-8",
                                       env=box.env, check=True)
                self.assertEqual(1, len(found.stdout.splitlines()), found.stdout)

    def test_the_second_call_starts_one_interpreter(self):
        for shell in self.shells():
            with self.subTest(shell=shell):
                box = self.checkout()
                box.stand_in("python3")

                first = box.launch(shell=shell)
                self.assertEqual(0, first.returncode, first.stderr)
                self.assertEqual(self.PROBED, box.ran())
                self.assertEqual(["run-guard.python3"], box.marks())
                self.assertEqual([], list(box.mark().iterdir()))

                before = box.ran()
                second = box.launch(shell=shell)
                self.assertEqual(0, second.returncode, second.stderr)
                self.assertEqual(self.PROVED, box.since(before))
                self.assertEqual(["run-guard.python3"], box.marks())

    def test_only_the_candidate_probed_first_is_ever_remembered(self):
        # A mark that could name a later candidate would choose, so a host whose first candidate fails its probe
        # is never remembered and pays the probe on every call.
        with self.subTest(first="py -3.12"):
            box = self.checkout()
            box.stand_in("py")
            box.stand_in("python3")
            box.launch()
            self.assertEqual(["run-guard.py-3.12"], box.marks())
            before = box.ran()
            self.assertEqual(0, box.launch().returncode)
            self.assertEqual(["py -3.12 proves guard-git-argv.py"], box.since(before))

        with self.subTest(first="python"):
            box = self.checkout()
            box.stand_in("python")
            box.launch()
            self.assertEqual(["run-guard.python"], box.marks())
            before = box.ran()
            self.assertEqual(0, box.launch().returncode)
            self.assertEqual(["python proves guard-git-argv.py"], box.since(before))

        with self.subTest(chosen="py -3", first="py -3.12"):
            box = self.checkout()
            box.stand_in("py", probe={"-3.12": 1, "-3": 0})
            for _ in range(2):
                before = box.ran()
                self.assertEqual(0, box.launch().returncode)
                self.assertEqual(["py -3.12 probe", "py -3 probe", "py -3 runs guard-git-argv.py"],
                                 box.since(before))
            self.assertEqual([], box.marks())

        with self.subTest(chosen="python", first="python3"):
            box = self.checkout()
            box.stand_in("python3", probe=1)
            box.stand_in("python")
            for _ in range(2):
                before = box.ran()
                self.assertEqual(0, box.launch().returncode)
                self.assertEqual(["python3 probe", "python probe", "python runs guard-git-argv.py"],
                                 box.since(before))
            self.assertEqual([], box.marks())

    def test_a_remembered_interpreter_that_does_not_prove_itself_refuses_the_call(self):
        # What a remembered candidate leaves with once the host moved: 127 vanished, 1 below the floor, 103 an
        # unregistered `py`, 49 the Store alias, 120 a failed flush, 126, 137 and 255 the shell's. And 0 and 2,
        # which a program that ran nothing answers; 3, 90 and 94 sit beside the proven three.
        box = self.checkout()
        box.stand_in("python")
        for status in (0, 1, 2, 3, 49, 90, 94, 103, 120, 126, 127, 137, 255):
            with self.subTest(status=status):
                box.forge()
                box.stand_in("python3", probe=1, proves=status)
                before = box.ran()
                out = box.launch()
                self.assertEqual(2, out.returncode, out.stderr)
                self.assertEqual("", out.stdout)
                self.assertIn("refusing the call", out.stderr)
                self.assertIn("forgotten", out.stderr)
                # One start, the remembered one: `python` is good and not tried, as the first may have read the event.
                self.assertEqual(self.PROVED, box.since(before))
                self.assertEqual([], box.marks())

        # Forgotten, so the next call probes, and moves on to the next candidate as the probe always has.
        before = box.ran()
        out = box.launch()
        self.assertEqual(0, out.returncode, out.stderr)
        self.assertEqual(["python3 probe", "python probe", "python runs guard-git-argv.py"], box.since(before))

    def test_the_three_proven_statuses_are_the_guards_own_and_keep_the_mark(self):
        prover = self.prover()
        box = self.checkout()
        box.forge()
        for proved, status in ((prover.ALLOWED, 0), (prover.REFUSED, 2), (prover.ERRORED, 1)):
            with self.subTest(proved=proved):
                box.stand_in("python3", proves=proved)
                before = box.ran()
                out = box.launch()
                self.assertEqual(status, out.returncode, out.stderr)
                self.assertEqual(self.PROVED, box.since(before))
                self.assertEqual(["run-guard.python3"], box.marks())

    def test_the_proven_statuses_are_clear_of_everything_else_known(self):
        # `run-guard.py` says what the three are chosen clear of; moving one onto another program's status is red.
        prover = self.prover()
        proven = {prover.ALLOWED, prover.REFUSED, prover.ERRORED}
        self.assertEqual(3, len(proven))
        foreign = {0, 1, 2, 49, 120, 126, 127} | set(range(100, 115)) | set(range(128, 256))
        self.assertEqual(set(), proven & foreign)

    def test_a_remembered_interpreter_that_was_repointed_is_judged_as_it_is_now(self):
        # `update-alternatives --set python3` moves what the name means while its path stays put. Nothing here
        # reads either, so a repointed interpreter that still proves itself judges.
        box = self.checkout()
        box.stand_in("python3")
        box.launch()
        box.stand_in("python3", probe=1, proves=92)
        before = box.ran()
        out = box.launch()
        self.assertEqual(2, out.returncode, out.stderr)
        self.assertEqual(self.PROVED, box.since(before))
        self.assertEqual(["run-guard.python3"], box.marks())

    def test_a_forged_mark_naming_the_store_alias_refuses_instead_of_running_unguarded(self):
        # A mark for `python3`, which on Windows is the Store alias: it prints its message and exits 49, which a
        # `PreToolUse` hook reads as leave to run the tool.
        for shell in self.shells():
            with self.subTest(shell=shell):
                box = self.checkout()
                box.script("python3", (
                    f"echo 'python3 alias' >> {box.quoted(box.log)}\n"
                    "echo 'Python was not found; run without arguments to install from the Microsoft Store' >&2\n"
                    "exit 49\n"))
                box.stand_in("python")
                box.forge()

                out = box.launch(shell=shell)
                self.assertEqual(2, out.returncode, out.stderr)
                self.assertEqual("", out.stdout)
                self.assertIn("Python was not found", out.stderr)
                self.assertEqual(["python3 alias"], box.ran())
                self.assertEqual([], box.marks())

                before = box.ran()
                out = box.launch(shell=shell)
                self.assertEqual(0, out.returncode, out.stderr)
                self.assertEqual(["python3 alias", "python probe", "python runs guard-git-argv.py"],
                                 box.since(before))

    def test_a_mark_with_something_inside_cannot_be_forgotten_and_keeps_refusing(self):
        # `rmdir` removes an empty directory and nothing else, so a mark with a file in it stays and keeps
        # refusing, which is the closed direction. Nothing here deletes recursively: the planted file survives.
        box = self.checkout()
        box.stand_in("python3", proves=127)
        box.stand_in("python")
        box.forge(inside="planted")
        for _ in range(2):
            before = box.ran()
            out = box.launch()
            self.assertEqual(2, out.returncode, out.stderr)
            self.assertIn("could not be removed", out.stderr)
            self.assertIn(box.mark().name, out.stderr)
            self.assertEqual(self.PROVED, box.since(before))
        self.assertEqual(["run-guard.python3"], box.marks())
        self.assertEqual(b"planted\n", (box.mark() / "planted").read_bytes())

    def test_a_mark_cannot_choose_a_candidate_or_a_command(self):
        # `python3` is first and fails its probe; `python` is good. A mark for `python` would skip the probe if a
        # mark chose, and one for `canary` would run it. Not a differently-cased `python3`, which folds onto it.
        box = self.checkout()
        box.stand_in("python3", probe=1)
        box.stand_in("python")
        box.script("canary", f"echo 'canary ran' >> {box.quoted(box.log)}\n")
        for spelling in ("python", "py -3.12", "canary", "sh -c canary"):
            with self.subTest(spelling=spelling):
                box.forge(spelling)
                before = box.ran()
                out = box.launch()
                self.assertEqual(0, out.returncode, out.stderr)
                self.assertEqual(["python3 probe", "python probe", "python runs guard-git-argv.py"],
                                 box.since(before))
                self.assertTrue(box.mark(spelling).is_dir())
        self.assertNotIn("run-guard.python3", box.marks())

    def test_what_is_not_a_directory_is_not_a_mark_and_is_never_opened(self):
        # A FIFO at the mark, or a file with no newline for `read`, blocks a launcher that opens it until the hook
        # times out, which does not block the tool. Each is left as found and the call returns well in time.
        box = self.checkout()
        box.stand_in("python3")
        box.cache.mkdir()
        things = {
            "an empty file": lambda p: p.write_bytes(b""),
            "a file with no newline, four mebibytes of it": lambda p: p.write_bytes(b"x" * (4 << 20)),
        }
        if hasattr(os, "mkfifo"):
            things["a FIFO nobody will ever write"] = os.mkfifo
        try:
            os.symlink(box.root / "nowhere", box.root / "probe-link")
        except OSError:
            self.assertEqual("nt", os.name, "a POSIX host could not make a symbolic link in its temp root")
        else:
            things["a link to nothing"] = lambda p: os.symlink(box.root / "nowhere", p)
        for name, make in things.items():
            with self.subTest(thing=name):
                if box.mark().exists() or box.mark().is_symlink():
                    box.mark().unlink()
                make(box.mark())
                was = os.lstat(box.mark())
                before = box.ran()
                started = time.monotonic()
                out = box.launch(timeout=60)
                self.assertLess(time.monotonic() - started, 30)
                self.assertEqual(0, out.returncode, out.stderr)
                self.assertEqual(self.PROBED, box.since(before))
                now = os.lstat(box.mark())
                self.assertEqual((was.st_mode, was.st_size), (now.st_mode, now.st_size))

    def test_a_mark_that_cannot_be_kept_costs_the_probe_and_nothing_else(self):
        with self.subTest(obstacle="a file where the cache directory goes"):
            box = self.checkout()
            box.stand_in("python3")
            box.cache.write_bytes(b"")
            for _ in range(2):
                before = box.ran()
                out = box.launch()
                self.assertEqual(0, out.returncode, out.stderr)
                self.assertEqual(self.PROBED, box.since(before))
            self.assertEqual(b"", box.cache.read_bytes())

        # A mark with no `run-guard.py` to run would refuse every second call: remembered, unproven, forgotten.
        with self.subTest(obstacle="no prover beside the launcher"):
            box = self.checkout()
            box.stand_in("python3")
            (box.hooks / "run-guard.py").unlink()
            for _ in range(2):
                before = box.ran()
                out = box.launch()
                self.assertEqual(0, out.returncode, out.stderr)
                self.assertEqual(self.PROBED, box.since(before))
            self.assertEqual([], box.marks())

    def test_racing_first_calls_leave_one_empty_mark_and_nothing_beside_it(self):
        # Every launcher is held inside its probe until all are there, then let go at once. `mkdir` makes the
        # directory or finds it, so no interleaving leaves a different cache; what this holds is that each call
        # ran its guard once, the cache holds one empty mark, and the next call is remembered.
        racers = 6
        box = self.checkout()
        box.stand_in("python3", hold=True)
        box.cache.mkdir()
        children = [box.start() for _ in range(racers)]
        try:
            for _ in range(1200):
                if len(box.ran()) == racers:
                    break
                time.sleep(0.05)
            self.assertEqual(["python3 probe"] * racers, box.ran(), "not every launcher reached its probe")
            self.assertEqual([], box.marks())
        finally:
            box.gate.write_bytes(b"")
            outs = []
            for child in children:
                try:
                    outs.append(child.communicate(timeout=120))
                except subprocess.TimeoutExpired:
                    child.kill()
                    outs.append(child.communicate())
        for child, (_, stderr) in zip(children, outs):
            self.assertEqual(0, child.returncode, stderr)
        self.assertEqual(sorted(["python3 probe"] * racers + ["python3 runs guard-git-argv.py"] * racers),
                         sorted(box.ran()))
        self.assertEqual(["run-guard.python3"], sorted(p.name for p in box.cache.iterdir()))
        self.assertEqual([], list(box.mark().iterdir()))

        before = box.ran()
        self.assertEqual(0, box.launch().returncode)
        self.assertEqual(self.PROVED, box.since(before))

    def test_a_shell_that_dies_on_the_way_refuses_the_call(self):
        # A `$0` naming a directory that is not there fails the launcher's own `cd`, and `set -e` would leave with
        # 1, which a `PreToolUse` hook reads as leave to run the tool. The trap makes it 2.
        for shell in self.shells():
            with self.subTest(shell=shell):
                box = self.checkout()
                box.stand_in("python3")
                out = subprocess.run(
                    [shell, "-c", f". {box.quoted(box.launcher)}", "/no/such/directory/run-guard.sh",
                     "guard-git-argv.py"],
                    input="{}", capture_output=True, encoding="utf-8", errors="replace", env=box.env)
                self.assertEqual(2, out.returncode, out.stderr)
                self.assertIn("/no/such/directory", out.stderr)
                self.assertEqual([], box.ran())

    def test_a_refusal_nobody_is_reading_is_still_a_refusal(self):
        # A refusal's stderr line into a pipe nobody reads is a SIGPIPE, 141; into a closed stream, an error under
        # `set -e`. Either way the trap makes it 2.
        for shell in self.shells():
            for remembered in (True, False):
                with self.subTest(shell=shell, remembered=remembered):
                    box = self.checkout()
                    box.stand_in("python3", probe=1, proves=127)
                    if remembered:
                        box.forge()
                    # A pipe of the case's own, its reading end closed, so no reader thread meets a closed file.
                    unread, stderr = os.pipe()
                    child = subprocess.Popen(
                        [shell, str(box.launcher), "guard-git-argv.py"], stdin=subprocess.DEVNULL,
                        stdout=subprocess.PIPE, stderr=stderr, env=box.env)
                    os.close(stderr)
                    os.close(unread)
                    stdout, _ = child.communicate(timeout=120)
                    self.assertEqual(2, child.returncode)
                    # Not an empty stdout: macOS's bash 3.2 flushes the refusal there on the way out.
                    self.assertNotIn(b"permissionDecision", stdout)
                    self.assertEqual(self.PROVED if remembered else ["python3 probe"], box.ran())

                    # The refusal forgot the mark, so it is made again for the second half to reach the same one.
                    if remembered:
                        box.forge()
                    before = box.ran()
                    out = subprocess.run(
                        [shell, "-c", '"$0" "$1" "$2" 2>&-', shell, str(box.launcher), "guard-git-argv.py"],
                        input="{}", capture_output=True, encoding="utf-8", errors="replace", env=box.env)
                    self.assertEqual(2, out.returncode, out.stdout)
                    self.assertEqual(self.PROVED if remembered else ["python3 probe"], box.since(before))

    def test_no_exit_in_the_launcher_lets_a_call_through_unproven(self):
        prover = self.prover()
        code = "\n".join(self.code())
        arms = dict(re.findall(r"^\s*(\d+)\) trap - EXIT; exit (\d+) ;;$", code, re.MULTILINE))
        self.assertEqual({str(prover.ALLOWED): "0", str(prover.REFUSED): "2", str(prover.ERRORED): "1"}, arms)
        # Five `trap`s, two set and three lifted on those arms, and set before anything the shell does.
        self.assertEqual(3, code.count("trap - EXIT"))
        self.assertEqual(5, len(re.findall(r"\btrap\b", code)))
        self.assertIn("set -eu", code)
        self.assertLess(code.index("trap 'exit 2' EXIT"), code.index('[ "$#"'))
        self.assertIn("trap 'exit 2' PIPE", code)
        # Every `exit` names a literal, and every literal but the two proven arms is a 2.
        self.assertEqual([], re.findall(r"\bexit\s+(?![0-9])\S", code))
        exits = re.findall(r"\bexit (\d+)", code)
        self.assertEqual({"0", "1", "2"}, set(exits))
        self.assertEqual(1, exits.count("0"))
        self.assertEqual(1, exits.count("1"))

    def test_the_mark_is_never_opened(self):
        # Of the text as well as of the FIFO above: no redirection names the mark, and nothing reads anything.
        code = "\n".join(self.code())
        self.assertNotRegex(code, r'[<>]\s*"?\$mark')
        self.assertNotRegex(code, r"\bread\b")
        self.assertNotRegex(code, r"\bcat\b")
        self.assertRegex(code, r'\[ -d "\$mark" \]')

    def test_the_remembered_interpreter_is_never_execed(self):
        # An `exec` cannot fail closed, which is why the remembered path is a child and a status.
        proving = [line for line in self.code() if "run-guard.py" in line and "[" not in line]
        self.assertEqual(3, len(proving), proving)
        for line in proving:
            with self.subTest(line=line):
                self.assertNotIn("exec", line)
                self.assertNotIn("&&", line)
                self.assertRegex(line, r'"\$1" \|\| status=\$\? ;;$')


class ARememberedGuardAnswersAsAProbedOne(LauncherMarkCases, unittest.TestCase):
    """A probed guard is `exec`ed and a remembered one runs through `run-guard.py`; a session gets one answer.

    These cases hand each call to the interpreter running this suite, so the guards themselves judge. The exception,
    held here: a verdict that cannot be written refuses on the remembered path, where the `exec` lets the tool run.
    """

    # Every guard the closed set admits, the guards that load a sibling by path last, since they leave its bytecode.
    EVENTS = (
        ("guard-git-argv.py", bash_event("ls")),
        ("guard-git-argv.py", bash_event("git push --force origin main")),
        ("guard-sed-crlf.py", bash_event("sed -i s/a/b/ notes.txt")),
        ("guard-sed-crlf.py", "not json"),
        ("guard-edit-target.py", {"tool_name": "Write", "tool_input": {}}),
        ("guard-edit-target.py", {"tool_name": "Write", "tool_input": {"file_path": "notes.txt"}}),
        ("guard-index-argv.py", bash_event("cbx search cart")),
        ("guard-index-argv.py", bash_event("cbx search x; rm -rf /")),
        ("guard-helper-argv.py", bash_event('gh pr create --title "$(id)"')),
        ("guard-helper-argv.py", "not json"),
    )
    SIBLING_LOADERS = ("guard-edit-target.py", "guard-index-argv.py", "guard-helper-argv.py")
    LOADED_BY_A_SIBLING = ("guard-git-argv.", "guard-index-argv.")

    def both(self, box, guard="guard-git-argv.py", event="{}"):
        """One probed call and one remembered call, with their log lines."""
        if box.mark().is_dir():
            box.mark().rmdir()
        before = box.ran()
        probed = box.launch(guard, event)
        self.assertEqual(["python3 probe", f"python3 runs {guard}"], box.since(before))
        before = box.ran()
        remembered = box.launch(guard, event)
        self.assertEqual([f"python3 proves {guard}"], box.since(before))
        return probed, remembered

    def test_the_remembered_path_answers_as_the_probed_one_does(self):
        # Every guard the closed set admits, through both paths: the same stdout, stderr and status.
        box = self.checkout(guards=True)
        box.stand_in("python3", real=sys.executable)
        (box.root / "notes.txt").write_bytes(b"a\r\n")
        self.assertEqual(self.admitted(), {guard for guard, _ in self.EVENTS})
        cache = box.hooks / "__pycache__"
        seen = set()
        first_loader = next(index for index, (guard, _) in enumerate(self.EVENTS) if guard in self.SIBLING_LOADERS)
        self.assertGreater(first_loader, 0)
        for index, (guard, event) in enumerate(self.EVENTS):
            if index == first_loader:
                # Compiled from source, as `python guard.py` compiles it: until a guard loads a sibling, no cache.
                self.assertFalse(cache.exists(), sorted(p.name for p in cache.iterdir()) if cache.exists() else "")
            if isinstance(event, dict):
                event = json.dumps({**event, "cwd": str(box.root)})
            with self.subTest(guard=guard, event=event):
                probed, remembered = self.both(box, guard, event)
                self.assertEqual((probed.returncode, probed.stdout, probed.stderr),
                                 (remembered.returncode, remembered.stdout, remembered.stderr))
                seen.add((remembered.returncode, "permissionDecision" in remembered.stdout))
        # The control: the comparison passes on two paths that both answer nothing, so both shapes were seen.
        self.assertEqual({(0, False), (0, True)}, seen)
        cached = sorted(p.name for p in cache.iterdir()) if cache.exists() else []
        for name in cached:
            with self.subTest(cached=name):
                self.assertTrue(name.startswith(self.LOADED_BY_A_SIBLING), cached)

    def test_a_guard_sees_the_same_main_module_on_both_paths(self):
        # What `python guard.py` gives a guard: its own `__main__`, its path as `__file__` and `sys.argv[0]`, and
        # its directory first on `sys.path`. Spelled by the guard itself.
        box = self.checkout()
        box.stand_in("python3", real=sys.executable)
        (box.hooks / "guard-git-argv.py").write_text(
            "import json, os, sys\n"
            "print(json.dumps(["
            "__name__, "
            "os.path.normcase(os.path.abspath(__file__)), "
            "os.path.normcase(os.path.abspath(sys.argv[0])), "
            "len(sys.argv), "
            "sys.modules['__main__'].__dict__ is globals(), "
            "os.path.normcase(os.path.abspath(sys.path[0]))]))\n",
            encoding="utf-8", newline="\n")
        probed, remembered = self.both(box)
        self.assertEqual((0, 0), (probed.returncode, remembered.returncode))
        self.assertEqual(probed.stdout, remembered.stdout)
        guard = box.hooks / "guard-git-argv.py"
        seen = json.loads(remembered.stdout)
        self.assertEqual(["__main__", 1, True], [seen[0], seen[3], seen[4]])
        # Asked to be the file or directory, not to be spelled like it: macOS links its temp root into
        # `/private`, and `pwd -W` expands a Windows runner's short `RUNNER~1`.
        self.assertTrue(os.path.samefile(seen[1], guard), seen[1])
        self.assertTrue(os.path.samefile(seen[2], guard), seen[2])
        self.assertTrue(os.path.samefile(seen[5], box.hooks), seen[5])

    def test_a_guard_that_fails_falls_the_same_way_on_both_paths(self):
        # Which way a guard's own crash falls is the guard's decision, so the two paths answer alike, and a guard
        # that ran and failed still ran: the mark is kept.
        bodies = {
            "raises": ("raise RuntimeError('boom')\n", 1, "RuntimeError: boom"),
            "raises outside Exception": (
                "class Stop(BaseException):\n    pass\nraise Stop('halt')\n", 1, "Stop: halt"),
            "exits with words": ("import sys\nsys.exit('in words')\n", 1, "in words"),
            "exits with nothing": ("import sys\nsys.exit()\n", 0, ""),
            "does not compile": ("def (:\n", 1, "SyntaxError"),
            "exits 3": ("import sys\nsys.exit(3)\n", 3, ""),
            "exits 2": ("import sys\nprint('no', file=sys.stderr)\nsys.exit(2)\n", 2, "no"),
        }
        box = self.checkout()
        box.stand_in("python3", real=sys.executable)
        for name, (body, status, said) in bodies.items():
            with self.subTest(guard=name):
                (box.hooks / "guard-git-argv.py").write_text(body, encoding="utf-8", newline="\n")
                probed, remembered = self.both(box)
                self.assertEqual(status, probed.returncode, probed.stderr)
                self.assertIn(said, probed.stderr)
                # Every status but 0 and 2 is a non-blocking error to the harness, and arrives here as 1.
                self.assertEqual(status if status in (0, 2) else 1, remembered.returncode, remembered.stderr)
                self.assertIn(said, remembered.stderr)
                self.assertEqual("", remembered.stdout)
                self.assertEqual(["run-guard.python3"], box.marks())

    def test_a_guard_that_is_missing_is_refused_on_both_paths(self):
        # `python` answers a script it cannot open with 2; the remembered path has nothing to prove and refuses.
        box = self.checkout()
        box.stand_in("python3", real=sys.executable)
        probed, remembered = self.both(box)
        self.assertEqual((2, 2), (probed.returncode, remembered.returncode))
        self.assertEqual([], box.marks())

    def test_a_verdict_that_cannot_be_written_is_refused_where_the_exec_let_it_through(self):
        # A stdout that will not flush: under the `exec` the interpreter leaves with 120, a non-blocking error, and
        # the tool runs; under `run-guard.py` the same 120 proves nothing, and the launcher refuses.
        box = self.checkout()
        box.stand_in("python3", real=sys.executable)
        (box.hooks / "guard-git-argv.py").write_text(
            "import sys\n"
            "class Full:\n"
            "    def write(self, text):\n        return len(text)\n"
            "    def flush(self):\n        raise OSError(28, 'full')\n"
            "sys.stdout = Full()\n"
            "sys.exit(0)\n", encoding="utf-8", newline="\n")
        probed, remembered = self.both(box)
        self.assertNotIn(probed.returncode, (0, 2), probed.stderr)
        self.assertEqual(2, remembered.returncode, remembered.stderr)
        self.assertEqual([], box.marks())

    def test_a_closed_stream_is_not_the_remembered_paths_own_failure(self):
        # A stream closed before the interpreter started is `None`, and a `.flush()` on it would be the remembered
        # path's own crash. The `exec` path leaves with the guard's 2, or 120 where Windows cannot flush at exit;
        # the remembered half refuses either way.
        box = self.checkout(guards=True)
        box.stand_in("python3", real=sys.executable)
        # The shipped guards fail open on an unreadable event, so the refusal is this copy's own.
        (box.hooks / "guard-git-argv.py").write_text(
            "import sys\nsys.stdin.read()\nsys.exit(2)\n", encoding="utf-8", newline="\n")
        for closed in ("1>&-", "2>&-"):
            with self.subTest(closed=closed):
                if box.mark().is_dir():
                    box.mark().rmdir()
                statuses = []
                for _ in range(2):
                    out = subprocess.run(
                        [box.shell, "-c", f'"$0" "$1" "$2" {closed}', box.shell, str(box.launcher),
                         "guard-git-argv.py"],
                        input="not json", capture_output=True, encoding="utf-8", errors="replace", env=box.env)
                    statuses.append(out.returncode)
                self.assertIn(statuses[0], (2, 120), statuses)
                self.assertEqual(2, statuses[1], statuses)
                self.assertEqual(["python3 probe", "python3 runs guard-git-argv.py",
                                  "python3 proves guard-git-argv.py"], box.ran()[-3:])
                # Where the interpreter coped, the 2 is the guard's own and the mark stays; after a 120 the
                # remembered path forgot it, and the next call probes.
                self.assertEqual(["run-guard.python3"] if statuses[0] == 2 else [], box.marks())

    def run_prover(self, *arguments, version=None, event="{}"):
        """Run `run-guard.py` here, optionally claiming an older version, then `cat` what it left on stdin."""
        box = self.checkout(guards=True)
        prover = str(box.hooks / "run-guard.py")
        # Compiled by hand, as `runpy` imports modules of its own after the version is replaced; in a file, as
        # Git's `bash` rewrites a `-c` program's backslashes on the way to a native interpreter.
        program = box.root / "claim.py"
        program.write_text(
            "import sys\n"
            f"path = {prover!r}\n"
            "code = compile(open(path, 'rb').read(), path, 'exec')\n"
            f"sys.version_info = {version!r} or sys.version_info\n"
            f"sys.argv = {[prover, *arguments]!r}\n"
            "exec(code, {'__name__': '__main__', '__file__': path})\n",
            encoding="utf-8", newline="\n")
        return subprocess.run(
            [BASH, "-c", '"$0" "$1"; status=$?; echo; echo "--stdin--"; cat; exit $status',
             sys.executable, program.as_posix()],
            input=event, capture_output=True, encoding="utf-8", errors="replace")

    @staticmethod
    def split(out):
        """The prover's own stdout, and what it left unread on stdin."""
        own, _, left = out.stdout.rpartition("\n--stdin--\n")
        return own, left

    def test_an_interpreter_below_the_floor_judges_nothing(self):
        # The prover claims 3.11 and is handed an event `guard-git-argv.py` refuses: a guard that ran anyway would
        # show a deny on stdout, and the event is still whole on stdin for what the launcher does next.
        event = json.dumps(bash_event("git push --force origin main"))
        prover = self.prover()
        proven = {prover.ALLOWED, prover.REFUSED, prover.ERRORED}

        control = self.run_prover("guard-git-argv.py", event=event)
        own, left = self.split(control)
        self.assertEqual(prover.ALLOWED, control.returncode, control.stderr)
        self.assertIn("permissionDecision", own)
        self.assertEqual("", left)

        old = self.run_prover("guard-git-argv.py", event=event, version=(3, 11, 9, "final", 0))
        own, left = self.split(old)
        self.assertNotIn(old.returncode, proven | {0, 2})
        self.assertEqual("", own)
        self.assertEqual(event, left)
        self.assertIn("3.12", old.stderr)

        # A floor, not a pin: a prover that asked for 3.12 exactly would refuse every call on a newer host.
        newer = self.run_prover("guard-git-argv.py", event=event, version=(3, 14, 0, "final", 0))
        own, left = self.split(newer)
        self.assertEqual(prover.ALLOWED, newer.returncode, newer.stderr)
        self.assertIn("permissionDecision", own)

    def test_the_prover_runs_a_bare_name_beside_itself_and_nothing_else(self):
        prover = self.prover()
        proven = {prover.ALLOWED, prover.REFUSED, prover.ERRORED}
        for arguments in ((), ("guard-git-argv.py", "extra"), ("",), ("../hooks/guard-git-argv.py",),
                          (str(self.HOOKS / "guard-git-argv.py"),), ("no-such-guard.py",)):
            with self.subTest(arguments=arguments):
                out = self.run_prover(*arguments)
                own, _ = self.split(out)
                self.assertNotIn(out.returncode, proven | {0})
                self.assertEqual("", own)

    def test_the_launcher_and_the_prover_state_one_floor(self):
        floors = set(re.findall(r"sys\.version_info < \((\d+), (\d+)\)", "\n".join(self.code())))
        self.assertEqual({tuple(str(n) for n in self.prover().FLOOR)}, floors)


if __name__ == "__main__":
    unittest.main()
