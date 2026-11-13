#!/usr/bin/env python3
"""The harness drift gate: a target's managed files are what its manifest says (shared-harness spec 5.4).

Stdlib only, so its hash and glob matcher are copies harness_sync's parity test keeps equal to apply's. A pull
request edits the manifest freely, so `--payload` compares it with the payload (owner: the README, Trust model)."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import types
from pathlib import Path

SETTINGS = ".claude/settings.json"
BASE = ".harness/settings.base.json"
RENDER = ".harness/render.py"
MANIFEST = ".harness/manifest.json"
CONFIG = ".harness/config.json"
# The directories whose every file is the harness's; a file there that no manifest line names is drift.
MANAGED_DIRS = (
    ".claude/agents",
    ".claude/commands",
    ".claude/hooks",
    ".claude/scripts",
    ".claude/skills",
    "docs/commands",
    "docs/harness",
    ".github/harness-drift",
)
# What the gate needs delivered to judge a target; excluded or local, the target is never checked again (copy of
# harness_sync.apply, as is the hook-path rule below).
GATE_FILES = (
    BASE,
    RENDER,
    ".github/harness-drift/README.md",
    ".github/harness-drift/drift_gate.py",
    ".github/harness-drift/test_drift_gate.py",
    ".github/workflows/gates.yml",
)
# The config's keys, as harness_sync.config takes them.
_TOP_KEYS = ("repo", "task_list", "settings", "local", "exclude")
_HOOK_TOKEN = re.compile(
    r"(?<![\w$.-])(?P<path>[\w.-]+(?:/[\w.-]+)+)"
    r"|(?<![\w$./-])(?P<bare>[\w-][\w.-]*\.(?:py|sh))(?![\w/.-])"
)


def file_hash(data: bytes) -> str:
    """SHA-256 with CRLF folded, so a Windows checkout hashes like the payload (copy of harness_sync.manifest)."""
    return hashlib.sha256(data.replace(b"\r\n", b"\n")).hexdigest()


def _glob_regex(glob: str) -> re.Pattern[str]:
    out, i = [], 0
    while i < len(glob):
        if glob.startswith("**/", i):
            out.append("(?:.*/)?")
            i += 3
        elif glob.startswith("**", i):
            out.append(".*")
            i += 2
        elif glob[i] == "*":
            out.append("[^/]*")
            i += 1
        elif glob[i] == "?":
            out.append("[^/]")
            i += 1
        else:
            out.append(re.escape(glob[i]))
            i += 1
    return re.compile("".join(out))


def glob_match(glob: str, path: str) -> bool:
    """POSIX relative path match: `**` crosses `/`, `*` and `?` do not (copy of harness_sync.apply)."""
    return _glob_regex(glob).fullmatch(path) is not None


def is_local(globs: list[str] | tuple[str, ...], path: str) -> bool:
    # Case-insensitive on every OS, as apply's deletion protection is: any filesystem may fold case.
    return any(glob_match(g.lower(), path.lower()) for g in globs)


def _matches(globs: list[str], path: str) -> bool:
    return any(glob_match(g, path) for g in globs)


def _command_paths(command: str) -> set[str]:
    """Paths one hook command names. A leading `./` is dropped. A bare `x.py` or `x.sh` is a path only after the
    first script path of its simple command, and is then that script's sibling: `hint-bound.sh <event>
    index-search-hint.py` loads it from `hint-bound.sh`'s own directory. A command is split at `&`, `|`, `;` and a
    newline, but not at a redirection's `&`. A module a hook loads by path itself, or a hook a target adds through
    its config, is not derived."""
    found: set[str] = set()
    for part in re.split(r"[;|\n]|(?<![<>&])&(?!>)", command):
        receiver: str | None = None
        for match in _HOOK_TOKEN.finditer(part):
            if (path := match.group("path")) is not None:
                while path.startswith("./"):
                    path = path[2:]
                found.add(path)
                if receiver is None and path.endswith((".py", ".sh")):
                    receiver = path.rpartition("/")[0]
            elif receiver is not None:
                found.add(f"{receiver}/{match.group('bare')}" if receiver else match.group("bare"))
    return found


def hook_paths(base: object) -> list[str]:
    """The paths the settings base's hook commands name; a target without one runs a hook that fails, unseen."""
    found: set[str] = set()
    hooks = base.get("hooks") if isinstance(base, dict) else None
    for groups in hooks.values() if isinstance(hooks, dict) else ():
        for group in groups if isinstance(groups, list) else ():
            inner = group.get("hooks") if isinstance(group, dict) else None
            for hook in inner if isinstance(inner, list) else ():
                command = hook.get("command") if isinstance(hook, dict) else None
                if isinstance(command, str):
                    found.update(_command_paths(command))
    return sorted(found)


def _is_link(path: Path) -> bool:
    return path.is_symlink() or path.is_junction()


def walk_files(directory: Path) -> list[str]:
    """Entries under `directory` as POSIX paths relative to it, skipping what test runs leave behind.

    A linked directory is listed and not entered, so a link cannot make the walk read outside the root.
    """
    paths = []
    for current, dirs, names in os.walk(directory):
        here = Path(current)
        dirs[:] = [d for d in dirs if d != "__pycache__"]
        entered = []
        for name in dirs:
            if _is_link(here / name):
                paths.append((here / name).relative_to(directory).as_posix())
            else:
                entered.append(name)
        dirs[:] = entered
        for name in names:
            if not name.endswith(".pyc"):
                paths.append((here / name).relative_to(directory).as_posix())
    return sorted(paths)


def _skipped(rel: str) -> bool:
    return "__pycache__" in rel.split("/") or rel.endswith(".pyc")


def _git_executable(root: Path) -> str | None:
    """An absolute git from PATH, never one in the working directory or the judged tree.

    Windows runs a bare `git` from the current directory before PATH, and the gate is run from a target's root, so
    a branch that commits a `git.exe` would otherwise run it."""
    name = "git.exe" if os.name == "nt" else "git"
    # Python 3.12's non-strict resolve() raises RuntimeError, not OSError, on a symlink loop.
    try:
        here, tree = Path.cwd().resolve(), root.resolve()
    except (OSError, RuntimeError):
        return None
    for entry in os.environ.get("PATH", "").split(os.pathsep):
        if not entry or not os.path.isabs(entry):
            continue
        # One unreadable entry (a symlink loop, a permission error) is not a reason to stop looking, and an
        # exception here would reach the caller outside its try and end the gate in a traceback.
        try:
            directory = Path(entry).resolve()
            if directory == here or directory.is_relative_to(tree):
                continue
            candidate = directory / name
            if candidate.is_file() and os.access(candidate, os.X_OK):
                return str(candidate)
        except (OSError, RuntimeError):
            continue
    return None


def _git_listed(root: Path, directory: str) -> list[str] | None:
    """What git would commit under `root/directory`, tracked or untracked but not ignored, relative to it.

    An ignored file never reaches a pull request, so judging it fails a developer checkout CI passes. None when
    `root` is not the top of a git work tree, or no safe git can answer: the caller then walks the file system."""
    git = _git_executable(root)
    if git is None:
        return None
    try:
        top = subprocess.run(
            [git, "-C", str(root), "rev-parse", "--show-toplevel"], capture_output=True, check=True
        ).stdout.decode("utf-8").strip()
        if not top or not os.path.samefile(top, root):
            return None
        out = subprocess.run(
            [git, "-C", str(root), "ls-files", "-z", "--cached", "--others", "--exclude-standard", "--", directory],
            capture_output=True,
            check=True,
        ).stdout.decode("utf-8")
    except (OSError, ValueError, subprocess.CalledProcessError):
        return None
    prefix = f"{directory}/"
    paths = set()
    for rel in out.split("\0"):
        rel = rel.rstrip("/")
        # A tracked file deleted from the work tree is the manifest check's to report, not an unlisted one.
        if not rel.startswith(prefix) or not os.path.lexists(root / rel) or _skipped(rel):
            continue
        sub = rel[len(prefix):]
        inner = (
            [name for name in walk_files(root / rel) if ".git" not in name.split("/")]  # git's own, never committed
            if (root / rel).is_dir() and not _is_link(root / rel)
            else []
        )
        if inner:
            # A nested repository or a submodule is one entry to git; its files are judged as the walk judged them,
            # so a `local` glob such as `skill/**` still covers them.
            paths.update(f"{sub}/{name}" for name in inner)
        else:
            # A file, a link, or a submodule CI checked out empty: still a path the pull request delivers.
            paths.add(sub)
    return sorted(paths)


def _canonical_problem(rel: str) -> str | None:
    """Same rule as apply: a manifest is editable by any target PR, so no entry may reach outside the root."""
    parts = rel.split("/")
    if "\\" in rel or ":" in rel or any(part in ("", ".", "..") for part in parts):
        return f"{MANIFEST} lists {rel!r}, which is not a canonical relative POSIX path"
    if any(part.lower() == ".git" for part in parts):
        return f"{MANIFEST} lists {rel!r}, which has a .git segment"
    return None


def _link_problem(root: Path, rel: str) -> str | None:
    current = root
    for part in rel.split("/"):
        current = current / part
        if _is_link(current):
            return f"{rel} passes through a link at {current.relative_to(root).as_posix()}"
    if not (root / rel).resolve().is_relative_to(root.resolve()):
        return f"{rel} resolves outside the root"
    return None


def _load_json(root: Path, rel: str, problems: list[str]) -> object | None:
    problem = _link_problem(root, rel)
    if problem:
        problems.append(problem)
        return None
    try:
        return json.loads((root / rel).read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:  # UnicodeDecodeError is a ValueError
        problems.append(f"cannot read {rel}: {exc}")
        return None


def _manifest_files(root: Path, problems: list[str]) -> dict[str, str] | None:
    raw = _load_json(root, MANIFEST, problems)
    if raw is None:
        return None
    files = raw.get("files") if isinstance(raw, dict) else None
    if not isinstance(files, dict) or not all(
        isinstance(k, str) and isinstance(v, str) and re.fullmatch(r"[0-9a-f]{64}", v) for k, v in files.items()
    ):
        problems.append(f"{MANIFEST} must be an object whose files map paths to 64-character lowercase hex hashes")
        return None
    return files


def config_problems(raw: object) -> list[str]:
    """What harness_sync.config refuses, as lines (a copy of its rules); `settings` is the renderer's to refuse."""
    if not isinstance(raw, dict):
        return [f"{CONFIG} must be a JSON object"]
    problems = [f"{CONFIG} has unknown key {key!r}" for key in raw if key not in _TOP_KEYS]
    for key in ("repo", "task_list"):
        if not isinstance(raw.get(key), str):
            problems.append(f"{CONFIG} {key!r} must be present and a string")
    for key in ("local", "exclude"):
        value = raw.get(key, [])
        if not isinstance(value, list) or not all(isinstance(g, str) for g in value):
            problems.append(f"{CONFIG} {key!r} must be a list of strings")
            continue
        for glob in value:
            # A backslash or leading slash would silently match nothing.
            if "\\" in glob or glob.startswith("/"):
                problems.append(f"{CONFIG} {key!r} glob {glob!r} must be relative POSIX: no backslash or leading slash")
    return problems


def _globs(root: Path, problems: list[str]) -> tuple[list[str], list[str]]:
    """The config's local and exclude globs; a config that cannot be read yields none and a problem."""
    raw = _load_json(root, CONFIG, problems)
    if raw is None:
        return [], []
    problems.extend(config_problems(raw))
    if not isinstance(raw, dict):
        return [], []
    out = []
    for key in ("local", "exclude"):
        value = raw.get(key, [])
        out.append(value if isinstance(value, list) and all(isinstance(g, str) for g in value) else [])
    return out[0], out[1]


def _check_protected(root: Path, local: list[str], exclude: list[str], problems: list[str]) -> None:
    base = _load_json(root, BASE, [])  # an unreadable base is reported by _check_settings
    needed = [(rel, "the drift gate needs managed") for rel in GATE_FILES]
    needed += [(rel, f"a hook in {BASE} runs") for rel in hook_paths(base)]
    for rel, why in needed:
        if _matches(local, rel):  # case-sensitive, as apply's is
            problems.append(f"{CONFIG} makes {rel} local, which {why}")
        if _matches(exclude, rel):
            problems.append(f"{CONFIG} excludes {rel}, which {why}")


def _check_listed(root: Path, files: dict[str, str], problems: list[str]) -> None:
    for rel, expected in sorted(files.items()):
        problem = _canonical_problem(rel) or _link_problem(root, rel)
        if problem:
            problems.append(problem)
            continue
        path = root / rel
        if not path.is_file():
            problems.append(f"{rel} is in the manifest but missing")
            continue
        try:
            actual = file_hash(path.read_bytes())
        except OSError as exc:
            problems.append(f"cannot read {rel}: {exc}")
            continue
        if actual != expected:
            problems.append(f"{rel} differs from the manifest")


def _check_unlisted(root: Path, files: dict[str, str], local: list[str], problems: list[str]) -> None:
    for directory in MANAGED_DIRS:
        problem = _link_problem(root, directory)
        if problem:
            problems.append(problem)
            continue
        base = root / directory
        if not base.is_dir():
            continue
        listed = _git_listed(root, directory)
        for sub in walk_files(base) if listed is None else listed:
            rel = f"{directory}/{sub}"
            if rel not in files and not is_local(local, rel):
                problems.append(f"{rel} is under a managed directory but neither in the manifest nor local")


def _check_required(files: dict[str, str], problems: list[str]) -> None:
    """Without a payload to compare with, the manifest must at least cover what the settings check relies on."""
    for rel in (BASE, RENDER, SETTINGS):
        if rel not in files:
            problems.append(f"{MANIFEST} does not list {rel}")


def _check_local_copy(root: Path, rel: str, digest: str, problems: list[str]) -> None:
    link = _link_problem(root, rel)
    if link:
        problems.append(f"{rel} is local but unreadable: {link}")
        return
    target = root / rel
    if not target.is_file():
        return
    try:
        same = file_hash(target.read_bytes()) == digest
    except OSError as exc:
        problems.append(f"cannot read {rel}: {exc}")
        return
    if not same:
        problems.append(f"{rel} is local but the payload manages it and the tree differs")


def _check_payload(
    root: Path, payload: Path, files: dict[str, str], local: list[str], exclude: list[str], problems: list[str]
) -> None:
    """Compare the manifest with the payload the sync should have copied; this mirrors apply's own rules."""
    if not payload.is_dir():
        problems.append(f"payload {payload} is not a directory")
        return
    expected: dict[str, str] = {}
    for rel in walk_files(payload):
        if rel in (SETTINGS, MANIFEST, CONFIG):
            continue
        if _matches(exclude, rel):
            # Exclude means not delivered, never replaced (spec 5.2); apply refuses this too.
            if (root / rel).exists() or (root / rel).is_symlink():
                problems.append(f"{rel} is excluded but exists in the tree")
            continue
        try:
            digest = file_hash((payload / rel).read_bytes())
        except OSError as exc:
            problems.append(f"cannot read payload file {rel}: {exc}")
            continue
        if _matches(local, rel):  # apply's local match is case-sensitive; a differing local file would have refused
            _check_local_copy(root, rel, digest, problems)
            continue
        expected[rel] = digest
    for rel, digest in sorted(expected.items()):
        if rel not in files:
            problems.append(f"{rel} is a payload file but {MANIFEST} does not list it")
        elif files[rel] != digest:
            problems.append(f"{rel} is listed in {MANIFEST} with a hash that is not the payload's")
    for rel in sorted(files):
        if rel != SETTINGS and rel not in expected:
            problems.append(f"{rel} is in {MANIFEST} but is not a payload file")


def _render_module(
    root: Path, files: dict[str, str] | None, payload: Path | None, problems: list[str]
) -> types.ModuleType | None:
    """The tree's renderer, executed only if its bytes are the manifest's and, with a payload, the payload's."""
    reason = None
    if files is None or RENDER not in files:
        reason = f"{MANIFEST} does not list it"
    else:
        reason = _link_problem(root, RENDER)
    data = b""
    if reason is None:
        try:
            data = (root / RENDER).read_bytes()
        except OSError as exc:
            reason = f"cannot read it: {exc}"
    if reason is None and file_hash(data) != files[RENDER]:  # type: ignore[index]
        reason = f"it differs from {MANIFEST}"
    if reason is None and payload is not None:
        try:
            if file_hash((payload / RENDER).read_bytes()) != file_hash(data):
                reason = "it differs from the payload's"
        except OSError:
            reason = "the payload has none"
    if reason:
        problems.append(f"{SETTINGS} not checked: {RENDER} is not trusted ({reason})")
        return None
    module = types.ModuleType("_harness_render")
    try:
        # Executed from the bytes that were hashed, not re-read, so the file cannot change in between.
        exec(compile(data, str(root / RENDER), "exec"), module.__dict__)
    except Exception as exc:  # the verified renderer still failing to load is a finding, not a crash
        problems.append(f"cannot load {RENDER}: {exc!r}")
        return None
    return module


def _check_settings(
    root: Path,
    files: dict[str, str] | None,
    payload: Path | None,
    render_module: types.ModuleType | None,
    problems: list[str],
) -> None:
    base = _load_json(root, BASE, problems)
    config = _load_json(root, CONFIG, [])  # an unreadable config was already reported by _globs
    if base is None or config is None:
        return
    if not isinstance(base, dict) or not isinstance(config, dict):
        problems.append(f"{BASE} and {CONFIG} must be JSON objects")
        return
    render = render_module or _render_module(root, files, payload, problems)
    if render is None:
        return
    try:
        cfg = types.SimpleNamespace(settings=config.get("settings", {}))
        want = render.dump_settings(render.render_settings(base, cfg))
    except Exception as exc:  # RenderError, or a config shape the renderer did not expect
        problems.append(f"cannot render {SETTINGS}: {exc}")
        return
    problem = _link_problem(root, SETTINGS)
    if problem:
        problems.append(problem)
        return
    try:
        have = (root / SETTINGS).read_bytes().decode("utf-8")
    except (OSError, ValueError) as exc:
        problems.append(f"cannot read {SETTINGS}: {exc}")
        return
    if have.replace("\r\n", "\n") != want.replace("\r\n", "\n"):
        problems.append(f"{SETTINGS} is not what {RENDER} renders from {BASE} and {CONFIG}")


def check(root: Path, payload: Path | None = None, render_module: types.ModuleType | None = None) -> list[str]:
    """Problems with the tree at `root`, empty when it is exactly what its manifest records.

    `payload`, the harness payload/ at the manifest's commit, is what the manifest is compared with; without it the
    tree is judged against itself. `render_module` renders in place of the tree's own renderer, for a caller that
    must not execute that (harness_sync check)."""
    root = Path(root)
    payload = Path(payload) if payload is not None else None
    problems: list[str] = []
    local, exclude = _globs(root, problems)
    _check_protected(root, local, exclude, problems)
    files = _manifest_files(root, problems)
    if files is not None:
        _check_listed(root, files, problems)
        _check_unlisted(root, files, local, problems)
        if payload is None:
            _check_required(files, problems)
        else:
            _check_payload(root, payload, files, local, exclude, problems)
    _check_settings(root, files, payload, render_module, problems)
    return problems


def main(argv: list[str] | None = None, *, render_module: types.ModuleType | None = None) -> int:
    parser = argparse.ArgumentParser(description="Fail when a managed harness file differs from the manifest.")
    parser.add_argument("--root", type=Path, default=Path("."), help="the tree to check (default: current directory)")
    parser.add_argument("--payload", type=Path, help="the harness payload/ at the commit the manifest names")
    args = parser.parse_args(argv)
    problems = check(args.root, args.payload, render_module)
    for problem in problems:
        print(problem)
    if problems:
        return 1
    print("harness-drift: clean")
    return 0


if __name__ == "__main__":
    sys.exit(main())
