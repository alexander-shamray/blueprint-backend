"""No hook opens a path on another machine that a proposed call names (*Paths on another machine*).

Each case runs a hook in process with every filesystem and process entry point fenced, so a call naming the share
fails it; the grammar is switched on there, so POSIX runners judge it too."""

import builtins
import contextlib
import glob
import importlib.util
import os
import pathlib
import shutil
import subprocess
import tempfile
import unittest
from unittest import mock

HOOKS = pathlib.Path(__file__).resolve().parent.parent / "hooks"
HOST = "guard-host.invalid"
SHARE = f"//{HOST}/share"
# The NT namespace opens with one separator and reaches the share all the same.
NT_SHARE = rf"\??\UNC\{HOST}\share"
# A `.git` file's pointer into each, which a join must not hide.
POINTERS = (f"{SHARE}/repo/.git/worktrees/x", NT_SHARE + r"\repo\.git\worktrees\x")


def load(name):
    """The hook `name` as a module, with its own copy of `path_grammar.py`."""
    spec = importlib.util.spec_from_file_location(name.replace("-", "_").removesuffix(".py"), HOOKS / name)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def names_the_host(value):
    """Whether `value`, a path or an argv, spells the share this suite names."""
    if isinstance(value, (list, tuple)):
        return any(names_the_host(item) for item in value)
    try:
        text = os.fspath(value)
    except TypeError:
        return False
    if isinstance(text, bytes):
        text = text.decode("utf-8", "replace")
    # Anywhere in it: a join puts the cwd before `\??\`, which Windows then reads as the share.
    text = text.replace("/", "\\")
    return HOST in text.lower() and (text.startswith("\\\\") or "\\??\\" in text)


ENTRY_POINTS = [
    (os.path, ("exists", "lexists", "isdir", "isfile", "islink", "realpath", "samefile")),
    (os, ("stat", "lstat", "scandir", "listdir", "access")),
    (glob, ("glob", "iglob")),
    (builtins, ("open",)),
    (subprocess, ("run", "Popen", "check_output")),
    (pathlib.Path, ("resolve", "exists", "is_file", "is_dir", "stat", "open", "read_text", "samefile")),
]


@contextlib.contextmanager
def grammar_on(*modules):
    """The Windows grammar judged in each hook module, whatever this host is."""
    with contextlib.ExitStack() as stack:
        for module in modules:
            stack.enter_context(mock.patch.object(module.GRAMMAR, "WINDOWS", True))
        yield


@contextlib.contextmanager
def nothing_leaves(case):
    """Fail `case` for any filesystem or process call that names the share."""
    reached = []

    def fenced(real, label):
        def call(*args, **kwargs):
            if names_the_host(args) or names_the_host(list(kwargs.values())):
                reached.append(f"{label}{args!r}")
                raise OSError("this case's share is never opened")
            return real(*args, **kwargs)
        return call

    with contextlib.ExitStack() as stack:
        for owner, names in ENTRY_POINTS:
            for name in names:
                real = getattr(owner, name, None)
                if real is not None:
                    stack.enter_context(mock.patch.object(owner, name, fenced(real, name)))
        yield
    case.assertEqual([], reached, "a hook opened a path on another machine")


def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="") as handle:
        handle.write(text)


class TheGrammar(unittest.TestCase):
    """The one predicate every hook asks."""

    def setUp(self):
        self.grammar = load("path_grammar.py")

    def test_the_whole_family_is_the_other_grammar_on_windows(self):
        with mock.patch.object(self.grammar, "WINDOWS", True):
            for spelling in (SHARE, "\\\\host\\share", "/\\host\\share", "\\/host/share",
                             "\\\\?\\C:\\x", "\\\\.\\C:\\x", "//?/UNC/host/share", NT_SHARE):
                with self.subTest(spelling=spelling):
                    self.assertTrue(self.grammar.other_grammar(spelling))
            for spelling in ("/c/dev/x", "C:\\dev\\x", "C:/dev/x", "/x", "\\x", "x", ""):
                with self.subTest(spelling=spelling):
                    self.assertFalse(self.grammar.other_grammar(spelling))

    def test_posix_has_no_other_grammar(self):
        with mock.patch.object(self.grammar, "WINDOWS", False):
            self.assertFalse(self.grammar.other_grammar(SHARE))
            self.assertFalse(self.grammar.foreign(SHARE))

    def test_a_share_the_session_stands_on_is_its_own_and_an_alias_of_it_is_not(self):
        # The whole share: a worktree's `gitdir` sits beside it, in the main checkout on the same share.
        own = "\\\\nas\\projects\\main\\.claude\\worktrees\\x"
        with mock.patch.object(self.grammar, "WINDOWS", True), mock.patch.dict(os.environ, {"HOME": "/home/x"}):
            for path in (own, own + "\\docs\\x.md", "//NAS/projects/main/.git/worktrees/x", own + "\\..\\..\\..\\.."):
                with self.subTest(path=path):
                    self.assertFalse(self.grammar.foreign(path, own))
            for path in ("\\\\nas\\other\\x", "\\\\?\\UNC\\nas\\projects\\main\\x", "\\??\\UNC\\nas\\projects\\x",
                         "\\\\?\\C:\\x", SHARE + "/x"):
                with self.subTest(path=path):
                    self.assertTrue(self.grammar.foreign(path, own))

    def test_a_home_on_a_share_is_the_users_own(self):
        with mock.patch.object(self.grammar, "WINDOWS", True), \
                mock.patch.dict(os.environ, {"HOME": "\\\\nas\\home\\x"}):
            self.assertFalse(self.grammar.foreign("\\\\nas\\home\\x\\.ssh\\config"))
            self.assertTrue(self.grammar.foreign("\\\\nas\\other\\.ssh\\config"))


class TheGitArgvGuard(unittest.TestCase):
    """A write or a git verb on a share is refused unopened, and the share is never placed."""

    def setUp(self):
        self.root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.root, True)
        os.makedirs(os.path.join(self.root, ".git"))
        self.guard = load("guard-git-argv.py")
        self.guard.EVENT_CWD = self.root

    def test_a_write_to_a_share_is_refused_without_opening_it(self):
        for command in (f"echo x > {SHARE}/x.txt", f"cp notes.txt {SHARE}/", f"cp notes.txt {SHARE}/drop",
                        f"rm -f {SHARE}/x.txt", f"cd {SHARE} && rm -rf build",
                        f"git -C {SHARE} rm x.txt", f"git -C {SHARE} checkout other -- x.txt",
                        f"git -C {SHARE} checkout other x.txt",
                        f"cd {SHARE} && echo x > out.txt", f"echo x | tee {SHARE}/x.txt",
                        f"echo x > '{NT_SHARE}\\x.txt'", f"cd '{NT_SHARE}' && echo x > out.txt",
                        f"git -C '{NT_SHARE}' rm x.txt", f"cd '{NT_SHARE}' && rm -rf build"):
            with self.subTest(command=command), grammar_on(self.guard), nothing_leaves(self):
                reason = self.guard.offence(command)
                self.assertIsNotNone(reason)
                self.assertIn("share or device path", reason)

    def test_what_the_guard_judges_as_no_write_is_admitted_unopened(self):
        # An index checkout writes the file, but this guard judges only `rm`, `mv` and a checkout from another source.
        for command in (f"ls {SHARE}", f"git -C {SHARE} status", f"cd {SHARE} && git log",
                        f"git checkout {SHARE}/x.txt"):
            with self.subTest(command=command), grammar_on(self.guard), nothing_leaves(self):
                self.assertIsNone(self.guard.offence(command))

    def test_a_write_on_the_share_the_session_stands_on_is_judged_as_any_write(self):
        # A missing share on `localhost` answers in milliseconds, where an unknown host waits on name resolution.
        own = "//localhost/guard-no-share/repo"
        for cwd, project, command in ((own, None, "echo x > out.txt"), (self.root, own, f"echo x > {own}/out.txt")):
            self.guard.EVENT_CWD = cwd
            env = {"CLAUDE_PROJECT_DIR": project} if project else {}
            with self.subTest(cwd=cwd, project=project), grammar_on(self.guard), mock.patch.dict(os.environ, env):
                self.assertNotIn("share or device path", self.guard.offence(command) or "")
        # An event with no `cwd` falls back to the hook's own, which is as much the session's.
        self.guard.EVENT_CWD = None
        with grammar_on(self.guard), mock.patch.object(os, "getcwd", return_value=own):
            self.assertNotIn("share or device path", self.guard.offence("echo x > out.txt") or "")


class TheSedGuard(unittest.TestCase):
    """A `sed -i` on a share is not this guard's to place, so it is admitted unopened."""

    def setUp(self):
        self.guard = load("guard-sed-crlf.py")
        self.root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.root, True)

    def test_an_operand_or_a_directory_on_a_share_is_never_opened(self):
        for command in (f"sed -i s/a/b/ {SHARE}/x.cs", f"sed -i s/a/b/ {SHARE}/*.cs",
                        f"sed -i s/a/b/ {SHARE}/{{a,b}}.cs", f"cd {SHARE} && sed -i s/a/b/ x.cs",
                        f"pushd {SHARE} && sed -i s/a/b/ x.cs", f"sed -i s/a/b/ '{NT_SHARE}\\x.cs'",
                        f"cd '{NT_SHARE}' && sed -i s/a/b/ x.cs"):
            event = {"tool_name": "Bash", "tool_input": {"command": command}, "cwd": self.root}
            with self.subTest(command=command), grammar_on(self.guard), nothing_leaves(self):
                self.assertIsNone(self.guard.offence(event, strips_cr=True))

    def test_the_share_the_event_stands_on_is_its_own(self):
        own = "//localhost/guard-no-share/repo"
        event = {"tool_name": "Bash", "tool_input": {"command": "sed -i s/a/b/ x.cs"}, "cwd": own}
        with grammar_on(self.guard), mock.patch.dict(os.environ, {"CLAUDE_PROJECT_DIR": self.root}), \
                mock.patch.object(self.guard, "git_on_path", return_value=None):
            self.guard.offence(event, strips_cr=True)
            self.assertFalse(self.guard.elsewhere(own + "/x.cs"))
            self.assertTrue(self.guard.elsewhere(SHARE + "/x.cs"))

    def test_git_is_not_run_in_a_checkout_whose_gitdir_is_a_share(self):
        # Git would follow the `.git` file's pointer itself, past every fence in this process.
        target = os.path.join(self.root, "sub", "a.cs")
        write(target, "a\r\n")
        refuse = mock.Mock(side_effect=AssertionError("git ran in the checkout"))
        for pointer in POINTERS:
            write(os.path.join(self.root, ".git"), f"gitdir: {pointer}\n")
            with self.subTest(pointer=pointer), grammar_on(self.guard), mock.patch.object(subprocess, "run", refuse):
                self.assertFalse(self.guard.tracked(target, "git"))


class TheSearchHint(unittest.TestCase):
    """A search of a share gets no hint, and the share is never resolved to decide that."""

    def setUp(self):
        self.hint = load("index-search-hint.py")
        self.root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.root, True)

    def test_a_search_of_a_share_is_not_hinted(self):
        cases = [("Grep", {"pattern": "needle", "path": SHARE}),
                 ("Grep", {"pattern": "needle", "path": SHARE + "/src"}),
                 ("Grep", {"pattern": "needle", "path": NT_SHARE})]
        for command in (f"grep -rn needle {SHARE}", f"rg needle {SHARE}/src", f"cd {SHARE} && rg needle",
                        f"git -C {SHARE} grep needle", f"cd {SHARE} && grep -rn needle .",
                        f"cd '{NT_SHARE}' && rg needle", f"git -C '{NT_SHARE}' grep needle"):
            cases.append(("Bash", {"command": command}))
        for name, given in cases:
            with self.subTest(tool=name, given=given), grammar_on(self.hint), nothing_leaves(self):
                self.assertIsNone(self.hint.search(name, given, self.root))

    def test_a_gitdir_pointer_to_a_share_is_not_followed(self):
        for pointer in POINTERS:
            write(os.path.join(self.root, ".git"), f"gitdir: {pointer}\n")
            with self.subTest(pointer=pointer), grammar_on(self.hint), nothing_leaves(self):
                self.assertFalse(self.hint.in_worktree(self.root))


class TheEditGuard(unittest.TestCase):
    """The edit guard refuses a share outright, and follows no pointer into one."""

    def setUp(self):
        self.guard = load("guard-edit-target.py")
        self.root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.root, True)

    def test_an_edit_on_a_share_is_refused_without_opening_it(self):
        for target in (SHARE + "/x.txt", NT_SHARE + "\\x.txt"):
            event = {"tool_name": "Write", "tool_input": {"file_path": target}, "cwd": self.root}
            with self.subTest(target=target), grammar_on(self.guard), nothing_leaves(self):
                self.assertIn("other path grammar", self.guard.offence(event))

    def test_a_gitdir_pointer_to_a_share_is_not_followed(self):
        for pointer in POINTERS:
            write(os.path.join(self.root, ".git"), f"gitdir: {pointer}\n")
            with self.subTest(pointer=pointer), grammar_on(self.guard), nothing_leaves(self):
                self.assertIsNone(self.guard.verified_gitdir(self.root))

    def test_a_commondir_pointer_to_a_share_is_not_followed(self):
        # A worktree whose backlink agrees, so `common_git_dir` reaches the admin directory's `commondir`.
        admin = os.path.join(self.root, "admin")
        checkout = os.path.join(self.root, "checkout")
        write(os.path.join(checkout, ".git"), f"gitdir: {admin}\n")
        write(os.path.join(admin, "gitdir"), os.path.join(checkout, ".git") + "\n")
        self.assertEqual(os.path.realpath(admin), self.guard.verified_gitdir(checkout))
        for common in (f"{SHARE}/repo/.git", NT_SHARE + r"\repo\.git"):
            write(os.path.join(admin, "commondir"), common + "\n")
            with self.subTest(common=common), grammar_on(self.guard), nothing_leaves(self):
                self.assertIsNone(self.guard.common_git_dir(checkout))

    def test_a_backlink_to_a_share_is_not_followed(self):
        # The admin directory is local; only git's backlink from it names the share.
        admin = os.path.join(self.root, "admin")
        checkout = os.path.join(self.root, "checkout")
        write(os.path.join(checkout, ".git"), f"gitdir: {admin}\n")
        for backlink in (f"{SHARE}/x/.git", NT_SHARE + r"\x\.git"):
            write(os.path.join(admin, "gitdir"), backlink + "\n")
            with self.subTest(backlink=backlink), grammar_on(self.guard), nothing_leaves(self):
                self.assertIsNone(self.guard.verified_gitdir(checkout))


if __name__ == "__main__":
    unittest.main()
