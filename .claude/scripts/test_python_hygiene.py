"""Four Windows traps in the Python under `.claude/` and `.github/` of the root it runs in, its `local` paths skipped.

`text=True` with no `encoding=` decodes with the locale codec, so a byte cp1252 cannot decode leaves `stdout` `None`;
`["bash", ...]` finds System32's WSL launcher before Git's bash; git's read-only objects make a cleanup told to ignore
its errors leave a repository under temp; `mktemp -d` gives a path Python cannot open. The test is the rule."""

import ast
import importlib.util
import json
import re
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TREES = (".claude", ".github")
SUBPROCESS_CALLS = {"run", "Popen", "check_output", "check_call", "call"}
SKIPPED_PARTS = {"__pycache__", "worktrees"}
SWALLOWING_CLEANUPS = {"rmtree": "ignore_errors", "TemporaryDirectory": "ignore_cleanup_errors"}
BUILDS_A_REPOSITORY = re.compile(r"\bgit\b.*\b(?:init|clone|worktree add)\b")
MKTEMP_DIRECTORY = re.compile(r"\bmktemp -d\b")

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


def _name(node: ast.expr) -> str | None:
    return node.attr if isinstance(node, ast.Attribute) else node.id if isinstance(node, ast.Name) else None


def _is_subprocess_call(node: ast.Call) -> bool:
    return _name(node.func) in SUBPROCESS_CALLS


def _is_true(node: ast.expr | None) -> bool:
    return isinstance(node, ast.Constant) and node.value is True


def _strings(tree: ast.AST) -> list[ast.Constant]:
    return [node for node in ast.walk(tree) if isinstance(node, ast.Constant) and isinstance(node.value, str)]


def _keyword(node: ast.Call, name: str) -> ast.expr | None:
    return next((k.value for k in node.keywords if k.arg == name), None)


def text_without_encoding(source: str) -> list[int]:
    """Lines of a subprocess call that decodes text (`text=True` or `universal_newlines=True`) with no `encoding=`."""
    lines = []
    for node in ast.walk(ast.parse(source)):
        if not isinstance(node, ast.Call) or not _is_subprocess_call(node):
            continue
        decodes = any(_is_true(v) for v in (_keyword(node, "text"), _keyword(node, "universal_newlines")))
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


def swallowed_cleanup(source: str) -> list[int]:
    """Lines of a cleanup told to ignore its errors, called or handed on, in a file that builds a repository."""
    tree = ast.parse(source)
    if not any(node.value in ("init", "clone") or BUILDS_A_REPOSITORY.search(node.value) for node in _strings(tree)):
        return []
    lines = []
    for node in ast.walk(tree):
        if not isinstance(node, ast.Call):
            continue
        named = {_name(node.func), *(_name(arg) for arg in node.args)}
        flags = [_keyword(node, flag) for name, flag in SWALLOWING_CLEANUPS.items() if name in named]
        if flags and any(_is_true(value) for value in (*flags, *node.args)):
            lines.append(node.lineno)
    return lines


def mktemp_fixture(source: str) -> list[int]:
    """Lines of a string that spells `mktemp -d`, a bare statement's, as a docstring is, left out as prose."""
    tree = ast.parse(source)
    prose = {id(node.value) for node in ast.walk(tree) if isinstance(node, ast.Expr)}
    return [node.lineno for node in _strings(tree) if id(node) not in prose and MKTEMP_DIRECTORY.search(node.value)]


def offences(root: Path, check) -> list[str]:
    return [f"{path.relative_to(root).as_posix()}:{line}"
            for path in python_files(root)
            for line in check(path.read_text(encoding="utf-8"))]


def rebound(source: str) -> list[str]:
    """The names `source` binds twice at module level."""
    seen: set[str] = set()
    twice = []
    for node in ast.parse(source).body:
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
            bound = [node.name]
        elif isinstance(node, ast.Assign):
            bound = [name.id for target in node.targets for name in ast.walk(target)
                     if isinstance(name, ast.Name) and isinstance(name.ctx, ast.Store)]
        elif isinstance(node, ast.AnnAssign) and isinstance(node.target, ast.Name):
            bound = [node.target.id]
        else:
            continue
        twice.extend(name for name in bound if name in seen)
        seen.update(bound)
    return twice


class PythonHygiene(unittest.TestCase):
    def test_no_text_true_without_encoding(self) -> None:
        self.assertEqual(offences(ROOT, text_without_encoding), [],
                         "pass encoding=\"utf-8\" beside text=True: the locale codec is cp1252 on Windows")

    def test_no_bare_bash_argv(self) -> None:
        self.assertEqual(offences(ROOT, bare_bash_argv), [],
                         "run bash by the path shutil.which(\"bash\") returns: CreateProcess finds WSL's first")

    def test_no_cleanup_swallows_its_errors_beside_a_repository(self) -> None:
        self.assertEqual(offences(ROOT, swallowed_cleanup), [],
                         "build on tempfile.TemporaryDirectory: its cleanup clears the bit git sets on an object")

    def test_no_fixture_makes_its_root_with_mktemp(self) -> None:
        self.assertEqual(offences(ROOT, mktemp_fixture), [], "take the root from review_helpers.temp_root")

    def test_no_hook_binds_a_module_level_name_twice(self) -> None:
        # A name bound twice keeps only the second, so a reader editing the
        # first changes nothing.
        self.assertEqual(["A"], rebound("A = 1\nA = 2\n"))
        self.assertEqual(["f"], rebound("f = 1\ndef f(): pass\n"))
        self.assertEqual(["B"], rebound("A, B = 1, 2\nB: int = 3\n"))
        self.assertEqual([], rebound("A = 1\nB = 2\n"))
        self.assertEqual([], rebound("A = {}\nA['k'] = 1\nA.x = 2\n"))
        hooks = sorted((Path(__file__).resolve().parent.parent / "hooks").glob("*.py"))
        self.assertGreater(len(hooks), 3)
        for hook in hooks:
            with self.subTest(hook=hook.name):
                self.assertEqual([], rebound(hook.read_text(encoding="utf-8")))

    def test_the_walk_reaches_the_harness(self) -> None:
        # Without this, a walk that found nothing would pass every check above.
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

    def test_a_swallowed_cleanup_is_refused_only_beside_a_repository(self) -> None:
        cleanups = ("shutil.rmtree(root, ignore_errors=True)\n"
                    "self.addCleanup(shutil.rmtree, root, ignore_errors=True)\n"
                    "shutil.rmtree(root, True)\n"
                    "tempfile.TemporaryDirectory(ignore_cleanup_errors=True)\n"
                    "shutil.rmtree(root)\n"
                    "shutil.rmtree(root, ignore_errors=False)\n"
                    "tempfile.TemporaryDirectory()\n"
                    "unrelated(root, ignore_errors=True)\n")
        self.assertEqual(swallowed_cleanup(cleanups), [])
        for build in ('sh("git init -q")', "sh('git -C \"$R\" clone x')", 'sh("git worktree add x")',
                      'run(["git", "-C", root, "init", "-q"])'):
            with self.subTest(build=build):
                self.assertEqual(swallowed_cleanup(cleanups + build + "\n"), [1, 2, 3, 4])

    def test_a_mktemp_fixture_is_refused(self) -> None:
        # Split, or this file's own literal would be an offence.
        self.assertEqual(mktemp_fixture('BUILD = """\nset -e\nroot=$(mktemp' + ' -d)\n"""\n'), [1])
        self.assertEqual(mktemp_fixture("run_bash('mktemp" + " -d')\n"), [1])
        self.assertEqual(mktemp_fixture("run_bash('mktemp x.XXXXXX')\n# mktemp" + " -d in a comment\n"), [])
        self.assertEqual(mktemp_fixture('"""Not by `mktemp' + ' -d`."""\n'), [])

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
