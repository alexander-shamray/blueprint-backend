"""A link a suite cannot make skips the case only on a host that grants none:
any other refusal, a missing parent above all, is the suite's own setup failing,
and a skip would leave it green with nothing run. A managed suite beside this
one asks `skip_only_without_links` in its `except`, and the last class reads that.
"""

import ast
import errno
import json
import sys
import unittest
from pathlib import Path
from unittest import mock

SCRIPTS = Path(__file__).resolve().parent
MANIFEST = SCRIPTS.parent.parent / ".harness" / "manifest.json"
NO_LINK = frozenset({errno.EPERM, errno.ENOTSUP, errno.ENOSYS})
# ERROR_INVALID_FUNCTION and ERROR_NOT_SUPPORTED, a volume with no such link;
# ERROR_PRIVILEGE_NOT_HELD, an unprivileged symlink. Read as winerror, since
# Python gives all three the errno EINVAL.
NO_LINK_ON_WINDOWS = frozenset({1, 50, 1314})
LINK_CALLS = frozenset({"symlink", "symlink_to", "link", "hardlink_to"})


def host_grants_no_link(refused):
    if isinstance(refused, NotImplementedError):
        return True
    if sys.platform == "win32":
        return getattr(refused, "winerror", None) in NO_LINK_ON_WINDOWS
    return refused.errno in NO_LINK


def skip_only_without_links(refused, link):
    """Skip the case if `refused` says the host grants no `link`, and raise
    `refused` otherwise. Called from the `except` around the link call."""
    if host_grants_no_link(refused):
        raise unittest.SkipTest(f"this host grants no {link}: {refused}") from refused
    raise refused


def link_tries(source):
    """Every `try` in `source` whose body makes a link."""
    return [node for node in ast.walk(ast.parse(source))
            if isinstance(node, ast.Try)
            and any(isinstance(call, ast.Call) and isinstance(call.func, ast.Attribute)
                    and call.func.attr in LINK_CALLS
                    for statement in node.body for call in ast.walk(statement))]


def skips_by_itself(handler):
    """Whether this `except` skips the case without asking the reading above."""
    for node in ast.walk(handler):
        if isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute) \
                and node.func.attr == "skipTest":
            return True
        if isinstance(node, ast.Raise) and node.exc is not None and "SkipTest" in ast.unparse(node.exc):
            return True
    return False


def own_skips(source):
    """The lines where `source` turns a refused link into a skip on its own."""
    return [handler.lineno for attempt in link_tries(source) for handler in attempt.handlers
            if skips_by_itself(handler)]


def windows_refusal(winerror, code=errno.EINVAL):
    """An OSError as Windows raises it, on any host: POSIX's constructor drops
    the winerror argument, so the attribute is set."""
    refused = OSError(code, "refused")
    refused.winerror = winerror
    return refused


class OnlyAHostThatGrantsNoLinkSkips(unittest.TestCase):

    def attempt(self, platform, refused):
        with mock.patch.object(sys, "platform", platform):
            skip_only_without_links(refused, "link")

    def test_a_posix_host_that_grants_no_link_skips(self):
        for refused in (OSError(errno.EPERM, "no"), OSError(errno.ENOTSUP, "no"),
                        OSError(errno.ENOSYS, "no"), NotImplementedError()):
            with self.subTest(refused=refused), self.assertRaises(unittest.SkipTest):
                self.attempt("linux", refused)

    def test_a_windows_host_that_grants_no_link_skips(self):
        for refused in (windows_refusal(1314), windows_refusal(1), windows_refusal(50),
                        NotImplementedError()):
            with self.subTest(refused=refused), self.assertRaises(unittest.SkipTest):
                self.attempt("win32", refused)

    def test_a_missing_parent_raises_rather_than_skips(self):
        missing = FileNotFoundError(errno.ENOENT, "no parent")
        with self.assertRaises(FileNotFoundError) as caught:
            self.attempt("linux", missing)
        self.assertIs(missing, caught.exception)

        missing = windows_refusal(3, errno.ENOENT)
        with self.assertRaises(OSError) as caught:
            self.attempt("win32", missing)
        self.assertIs(missing, caught.exception)

    def test_windows_is_read_by_winerror_and_not_by_errno(self):
        denied = windows_refusal(5, errno.EPERM)
        with self.assertRaises(OSError) as caught:
            self.attempt("win32", denied)
        self.assertIs(denied, caught.exception)

    def test_the_skip_names_the_link_and_the_refusal(self):
        with self.assertRaises(unittest.SkipTest) as caught:
            with mock.patch.object(sys, "platform", "linux"):
                skip_only_without_links(OSError(errno.EPERM, "refused by the volume"), "hard link")
        self.assertIn("this host grants no hard link", str(caught.exception))
        self.assertIn("refused by the volume", str(caught.exception))


class NoSuiteBesideThisOneSkipsOnALinkByItself(unittest.TestCase):

    BARE = "try:\n    os.{call}(a, b)\nexcept OSError:\n    self.skipTest('no link')\n"

    def test_the_read_flags_each_link_call_a_bare_skip_guards(self):
        for call in sorted(LINK_CALLS):
            with self.subTest(call=call):
                self.assertEqual([3], own_skips(self.BARE.format(call=call)))

    def test_the_read_flags_a_raised_skip_and_a_nested_one(self):
        raised = "try:\n    a.symlink_to(b)\nexcept OSError as e:\n    raise unittest.SkipTest('no') from e\n"
        self.assertEqual([3], own_skips(raised))
        nested = ("try:\n    os.symlink(a, b)\nexcept OSError:\n    if windows:\n"
                  "        junction()\n    else:\n        self.skipTest('no')\n")
        self.assertEqual([3], own_skips(nested))

    def test_the_read_passes_the_reading_and_an_unrelated_try(self):
        asks = ("try:\n    os.symlink(a, b)\nexcept OSError as refused:\n"
                "    skip_only_without_links(refused, 'link')\n")
        self.assertEqual([], own_skips(asks))
        unrelated = "try:\n    open(a)\nexcept OSError:\n    self.skipTest('no file')\n"
        self.assertEqual([], own_skips(unrelated))

    def test_no_managed_suite_beside_this_one_skips_on_a_link_by_itself(self):
        manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
        suites = [path for path in manifest["files"]
                  if path.startswith(".claude/scripts/test_") and path.endswith(".py")]
        read = {Path(path).name: (SCRIPTS.parent.parent / path).read_text(encoding="utf-8")
                for path in suites}
        for known in ("test_refresh_index.py", "test_gh_pr_create.py", "test_index_gate.py"):
            self.assertTrue(link_tries(read[known]), f"{known} makes no link this read can see")
        found = {name: lines for name, lines in ((name, own_skips(source)) for name, source in read.items())
                 if lines}
        self.assertEqual({}, found)


if __name__ == "__main__":
    unittest.main()
