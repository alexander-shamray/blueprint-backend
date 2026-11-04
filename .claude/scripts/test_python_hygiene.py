"""Two Windows traps in the harness's Python, held for every file under `.claude/` and `.github/`.

`text=True` with no `encoding=` decodes a child's output with the locale codec, cp1252 on the owner's machine,
not UTF-8: a `gh api` reply holding an em dash raised in the reader thread, `stdout` came back `None`, and a poller
read that as no results. And `["bash", ...]` handed to a subprocess is looked up by CreateProcess, which searches
System32 before `PATH` and finds the WSL launcher rather than Git's bash. `docs/harness/pitfalls.md` owns neither:
a test can see both shapes, so the test is the rule.

The walk covers the root this file runs in, so it judges a target's tree after a sync as well as the harness's
own, and skips what that root's `.harness/config.json` lists under `local`: a target's own files are not the
payload's to fix.
"""

import ast
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TREES = (".claude", ".github")
SUBPROCESS_CALLS = {"run", "Popen", "check_output", "check_call", "call"}
SKIPPED_PARTS = {"__pycache__", "worktrees"}

# `local` is matched as the drift gate matches it, which every target is shipped: `**` crosses `/`, `*` does not,
# and case folds.
_spec = importlib.util.spec_from_file_location("drift_gate", ROOT / ".github" / "harness-drift" / "drift_gate.py")
_drift_gate = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_drift_gate)


def _local_globs(root: Path) -> tuple[str, ...]:
    config = root / ".harness" / "config.json"
    if not config.is_file():
        return ()
    return tuple(json.loads(config.read_text(encoding="utf-8")).get("local", []))


def _is_local(rel: str, globs: tuple[str, ...]) -> bool:
    return _drift_gate.is_local(globs, rel)


def python_files(root: Path) -> list[Path]:
    """Every `*.py` under the root's `.claude/` and `.github/` that is not `local`, never a linked worktree's."""
    globs = _local_globs(root)
    found = []
    for tree in TREES:
        for path in sorted((root / tree).rglob("*.py")):
            rel = path.relative_to(root).as_posix()
            if SKIPPED_PARTS.intersection(path.relative_to(root).parts[:-1]) or _is_local(rel, globs):
                continue
            found.append(path)
    return found


def _is_subprocess_call(node: ast.Call) -> bool:
    func = node.func
    name = func.attr if isinstance(func, ast.Attribute) else func.id if isinstance(func, ast.Name) else None
    return name in SUBPROCESS_CALLS


def _keyword(node: ast.Call, name: str) -> ast.expr | None:
    return next((k.value for k in node.keywords if k.arg == name), None)


def text_without_encoding(source: str) -> list[int]:
    """Lines of a subprocess call that decodes text (`text=True` or `universal_newlines=True`) with no `encoding=`."""
    lines = []
    for node in ast.walk(ast.parse(source)):
        if not isinstance(node, ast.Call) or not _is_subprocess_call(node):
            continue
        decodes = any(isinstance(v, ast.Constant) and v.value is True
                      for v in (_keyword(node, "text"), _keyword(node, "universal_newlines")))
        if decodes and _keyword(node, "encoding") is None:
            lines.append(node.lineno)
    return lines


def bare_bash_argv(source: str) -> list[int]:
    """Lines of a subprocess call whose argv is a list or tuple literal opening with the word `bash`."""
    lines = []
    for node in ast.walk(ast.parse(source)):
        if not isinstance(node, ast.Call) or not _is_subprocess_call(node):
            continue
        argv = node.args[0] if node.args else _keyword(node, "args")
        if (isinstance(argv, (ast.List, ast.Tuple)) and argv.elts and isinstance(argv.elts[0], ast.Constant)
                and argv.elts[0].value in ("bash", "bash.exe")):
            lines.append(node.lineno)
    return lines


def offences(root: Path, check) -> list[str]:
    return [f"{path.relative_to(root).as_posix()}:{line}"
            for path in python_files(root)
            for line in check(path.read_text(encoding="utf-8"))]


class PythonHygiene(unittest.TestCase):
    def test_no_text_true_without_encoding(self) -> None:
        self.assertEqual(offences(ROOT, text_without_encoding), [],
                         "pass encoding=\"utf-8\" beside text=True: the locale codec is cp1252 on Windows")

    def test_no_bare_bash_argv(self) -> None:
        self.assertEqual(offences(ROOT, bare_bash_argv), [],
                         "run bash by the path shutil.which(\"bash\") returns: CreateProcess finds WSL's first")

    def test_the_walk_reaches_the_harness(self) -> None:
        # Without this, a walk that found nothing would pass both checks above.
        names = {path.name for path in python_files(ROOT)}
        self.assertIn(Path(__file__).name, names)

    def test_hygiene_catches_a_planted_file(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            planted = root / ".claude" / "scripts" / "planted.py"
            planted.parent.mkdir(parents=True)
            planted.write_text(
                "import subprocess\n"
                "subprocess.run(['git', 'log'], capture_output=True,\n"
                "               text=True)\n"
                "subprocess.check_output(('bash', '-c', 'true'))\n"
                "subprocess.run(['git', 'log'], text=True, encoding='utf-8')\n"
                "subprocess.run([BASH, '-c', 'true'])\n"
                "guard.reads_stdin_as_script(['bash', '-c', 'x'])\n",
                encoding="utf-8")
            self.assertEqual(offences(root, text_without_encoding), [".claude/scripts/planted.py:2"])
            self.assertEqual(offences(root, bare_bash_argv), [".claude/scripts/planted.py:4"])

    def test_local_path_skipped(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            planted = root / ".github" / "mine" / "planted.py"
            planted.parent.mkdir(parents=True)
            planted.write_text("import subprocess\nsubprocess.run(['bash'], text=True)\n", encoding="utf-8")
            self.assertEqual(len(offences(root, text_without_encoding)), 1)
            (root / ".harness").mkdir()
            (root / ".harness" / "config.json").write_text(json.dumps({"local": [".github/mine/**"]}),
                                                           encoding="utf-8")
            self.assertEqual(offences(root, text_without_encoding), [])
            self.assertEqual(offences(root, bare_bash_argv), [])

    def test_local_matches_as_the_drift_gate_does(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for rel in (".github/mine/deeper/planted.py", ".github/Other/planted.py"):
                planted = root / rel
                planted.parent.mkdir(parents=True)
                planted.write_text("import subprocess\nsubprocess.run(['git'], text=True)\n", encoding="utf-8")
            (root / ".harness").mkdir()
            (root / ".harness" / "config.json").write_text(
                json.dumps({"local": [".github/mine/*", ".github/other/*"]}), encoding="utf-8")
            # `*` stays inside one segment, so the nested file is still judged; case folds, so `Other` is local.
            self.assertEqual(offences(root, text_without_encoding), [".github/mine/deeper/planted.py:2"])

    def test_a_linked_worktree_is_not_walked(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            planted = root / ".claude" / "worktrees" / "x" / ".claude" / "planted.py"
            planted.parent.mkdir(parents=True)
            planted.write_text("import subprocess\nsubprocess.run(['bash'], text=True)\n", encoding="utf-8")
            self.assertEqual(offences(root, text_without_encoding), [])


if __name__ == "__main__":
    unittest.main()
