"""What `.claude/hooks/guard-edit-target.py` refuses, and what it must not.

Every case judges the hook directly, which says nothing about whether the
harness calls it, so the last class has the registration itself as its subject
— the gate-coverage lesson in `CLAUDE.md`.

The link cases run against a real link, and against every primitive the
platform grants: a symbolic link where the session may make one, and a
directory junction on Windows, which is all an unprivileged process gets there.
Both are a path whose spelling is inside an allowed tree and whose resolution
is not. Neither is a skip: a skip on a missing capability reports a pass, so a
platform that grants no primitive fails this module.

`..` after a link, case folding and the link primitives are asserted as the
platform's or filesystem's own answer, because the guard follows each rather
than picking one; CI runs this module on Linux, Windows and macOS for that
reason.
"""

import importlib.util
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unicodedata
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
HOOK = SCRIPTS.parent / "hooks" / "guard-edit-target.py"
SETTINGS = SCRIPTS.parent / "settings.json"

SYMLINKS = False
JUNCTIONS = None


def setUpModule():
    """Find every link primitive this platform grants, not the first one.

    Every case runs against all of them: a Windows account holding
    `SeCreateSymbolicLinkPrivilege` gets symbolic links, and the junction
    fallback an unprivileged Windows session depends on would otherwise go
    unexercised.
    """
    global SYMLINKS, JUNCTIONS
    # Removed at the end of this function, so a run leaves no tree behind.
    probe = tempfile.mkdtemp()
    target = os.path.join(probe, "target")
    os.mkdir(target)
    try:
        os.symlink(target, os.path.join(probe, "link"),
                   target_is_directory=True)
        SYMLINKS = True
    except (OSError, NotImplementedError, AttributeError):
        SYMLINKS = False
    try:
        from _winapi import CreateJunction
    except ImportError:
        CreateJunction = None
    if CreateJunction is not None:
        CreateJunction(target, os.path.join(probe, "junction"))
        JUNCTIONS = CreateJunction

    if not SYMLINKS and JUNCTIONS is None:
        raise AssertionError(
            "this platform grants neither a symbolic link nor a junction, so "
            "the guard's whole subject is unreachable here and a green run "
            "would mean nothing")

    if not HOOK.exists():
        raise AssertionError(f"the hook is missing: {HOOK}")

    # Say what was exercised, because a green run does not: `unittest` names a
    # subtest only when it fails, so a job that never reached the junction
    # fallback reads the same as one that ran both.
    print(f"link primitives exercised: {', '.join(linkers())}", file=sys.stderr)
    shutil.rmtree(probe, ignore_errors=True)


def linkers():
    """The link primitives available here, by name. Never empty."""
    names = []
    if SYMLINKS:
        names.append("symlink")
    if JUNCTIONS is not None:
        names.append("junction")
    return names


# The platforms disagree about `..` after a link, and the discriminator is the
# platform rather than the primitive: POSIX resolves `..` against the link's
# target, and Windows' path parser collapses it first, through a junction and a
# symbolic link alike. A privileged Windows runner has symbolic links too.
DOTDOT_IS_LEXICAL = os.name == "nt"


class GuardCase(unittest.TestCase):
    """A scratch checkout, the shapes a link can take in it, and the verdict."""

    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="guard-root-")
        self.outside = tempfile.mkdtemp(prefix="guard-outside-")
        # The checkout links into `outside`, so it is removed first:
        # `addCleanup` runs last-registered-first. `ignore_errors` because a
        # link `rmtree` declines to follow is the behaviour wanted, not an
        # error.
        self.addCleanup(shutil.rmtree, self.outside, ignore_errors=True)
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)
        # The fixture is a real checkout, and the `.git` is load-bearing.
        # An anchor is a checkout root, so a scratch tree without one has no
        # root to derive from `cwd` — and the case below that stands the
        # session inside a linked directory would then pass because the anchor
        # was dropped rather than because the guard refused. A marker directory
        # is all `checkout_root` looks for.
        for tree in ("docs", os.path.join(".claude", "scripts"), ".git"):
            os.makedirs(os.path.join(self.root, tree), exist_ok=True)
        self.write(os.path.join(self.root, "docs", "chapter.md"), "prose\n")
        self.write(
            os.path.join(self.root, ".claude", "scripts", "helper.sh"), "ok\n")
        self.write(os.path.join(self.outside, "loot.txt"), "secret\n")

    @staticmethod
    def write(path, text):
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(text)

    def link_to(self, name, real_target, linker):
        """A path spelled under `docs/` whose resolution is `real_target`.

        With symbolic links the link is the target's spelling. Junctions take a
        directory only, so the link is the target's directory and the file is
        named beneath it. Both produce a path inside an allowed tree that
        resolves outside it, which is the property every case is about.
        """
        linkpath = os.path.join(self.root, "docs", f"{name}-{linker}")
        if linker == "symlink":
            os.symlink(real_target, linkpath,
                       target_is_directory=os.path.isdir(real_target))
            return linkpath
        if os.path.isdir(real_target):
            JUNCTIONS(real_target, linkpath)
            return linkpath
        JUNCTIONS(os.path.dirname(real_target), linkpath)
        return os.path.join(linkpath, os.path.basename(real_target))

    def link_dir(self, name, real_target, linker):
        """The same for a directory target, where both primitives agree."""
        linkpath = os.path.join(self.root, "docs", f"{name}-{linker}")
        if linker == "symlink":
            os.symlink(real_target, linkpath, target_is_directory=True)
        else:
            JUNCTIONS(real_target, linkpath)
        return linkpath

    def judge(self, file_path, tool="Edit", cwd=None, key="file_path",
              project=None):
        """The hook's verdict on one call: the reason, or `None` for allowed."""
        event = {
            "hook_event_name": "PreToolUse",
            "cwd": self.root if cwd is None else cwd,
            "tool_name": tool,
            "tool_input": {} if file_path is None else {key: file_path},
        }
        result = subprocess.run(
            [sys.executable, str(HOOK)],
            input=json.dumps(event), capture_output=True, text=True,
            env={**os.environ,
                 "CLAUDE_PROJECT_DIR": self.root if project is None else project},
        )
        self.assertEqual(
            0, result.returncode,
            f"the hook must return a decision, not a traceback: {result.stderr}")
        if not result.stdout.strip():
            return None
        payload = json.loads(result.stdout)["hookSpecificOutput"]
        self.assertEqual("PreToolUse", payload["hookEventName"])
        self.assertEqual("deny", payload["permissionDecision"])
        return payload["permissionDecisionReason"]

    def assertRefused(self, file_path, **kwargs):
        reason = self.judge(file_path, **kwargs)
        self.assertIsNotNone(reason, f"admitted: {file_path}")
        return reason

    def assertAdmitted(self, file_path, **kwargs):
        reason = self.judge(file_path, **kwargs)
        self.assertIsNone(reason, f"refused: {file_path} — {reason}")

    def guard_module(self):
        """The hook imported directly, for the predicates a verdict hides."""
        spec = importlib.util.spec_from_file_location("guard_edit_target", HOOK)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        return module


class ALinkIsNotTheFileItIsSpelledAs(GuardCase):

    def test_a_link_into_a_denied_tree_is_refused(self):
        # `/review-grok` holds `Edit` for `docs/` and denies `.claude/**`, and
        # the deny is matched on the spelling. A link under `docs/` pointing
        # into the machinery is a path the deny never sees and a write the
        # machinery receives.
        target = os.path.join(self.root, ".claude", "scripts", "helper.sh")
        for linker in linkers():
            with self.subTest(link=linker):
                reason = self.assertRefused(
                    self.link_to("pwned", target, linker))
                self.assertIn("resolves elsewhere in it", reason)
                self.assertIn("helper.sh", reason)

    def test_a_link_out_of_the_checkout_is_refused(self):
        # No tree deny covers this: a repository-relative pattern says nothing
        # about a path that stops being repository-relative on resolution.
        loot = os.path.join(self.outside, "loot.txt")
        for linker in linkers():
            with self.subTest(link=linker):
                reason = self.assertRefused(
                    self.link_to("escape", loot, linker))
                self.assertIn("outside the checkout", reason)

    def test_a_link_in_the_middle_of_the_path_is_refused(self):
        # The component that is a link need not be the last one, and a guard
        # that only stats the leaf would admit this. Under junctions this is
        # the shape every case takes; under symlinks it is a case of its own,
        # which is why it is written out rather than left to the helper.
        machinery = os.path.join(self.root, ".claude")
        for linker in linkers():
            with self.subTest(link=linker):
                linkdir = self.link_dir("tree", machinery, linker)
                reason = self.assertRefused(
                    os.path.join(linkdir, "scripts", "helper.sh"))
                self.assertIn("resolves elsewhere in it", reason)

    def test_a_dotdot_after_a_link_is_judged_the_way_the_kernel_resolves_it(self):
        # The guard follows each platform. POSIX resolves `..` against the
        # link's target, so `docs/tree/../settings.json` through a link into
        # `.claude/scripts` lands on `.claude/settings.json`, where a lexical
        # reader would say `docs/`. Windows' path parser collapses `..` before
        # the filesystem sees it, so the same spelling writes
        # `docs/settings.json`, and refusing it there would refuse a write that
        # is what it says. `DOTDOT_IS_LEXICAL` says why the discriminator is
        # `os.name`.
        real = os.path.join(self.root, ".claude", "scripts")
        for linker in linkers():
            with self.subTest(link=linker):
                linkdir = self.link_dir("updir", real, linker)
                spelled = os.path.join(linkdir, "..", "settings.json")
                if DOTDOT_IS_LEXICAL:
                    self.assertAdmitted(spelled)
                else:
                    self.assertIn("resolves elsewhere in it",
                                  self.assertRefused(spelled))

    def test_a_cwd_inside_a_link_does_not_excuse_that_link(self):
        # An anchor excuses the link traversal on its own root prefix, so an
        # anchor at `docs/tree`, where `tree` links into `.claude/scripts`,
        # would excuse the traversal this guard exists to refuse. The case
        # fails unless `cwd` is walked up to its checkout root and every anchor
        # containing the target agrees.
        real = os.path.join(self.root, ".claude", "scripts")
        for linker in linkers():
            with self.subTest(link=linker):
                linkdir = self.link_dir("standing-in", real, linker)
                reason = self.assertRefused(
                    os.path.join(linkdir, "helper.sh"), cwd=linkdir)
                self.assertIn("resolves elsewhere in it", reason)

    def test_a_device_prefixed_spelling_is_refused(self):
        # Windows' extended-length, device and UNC spellings skip the path
        # normalisation a permission matcher depends on, so a denied target
        # spelled that way is judged by nothing. The whole grammar is refused
        # rather than a list of prefixes, and refused rather than resolved,
        # because a hook can only allow or deny.
        guard = self.guard_module()
        plain = os.path.join(self.root, "docs", "chapter.md")
        spellings = ["\\\\?\\" + plain, "\\\\.\\" + plain, "//?/" + plain,
                     "\\\\localhost\\C$\\dev\\x\\docs\\chapter.md"]
        if os.name != "nt":
            # No second alphabet exists here, and the predicate says so; `//x`
            # on POSIX is an ordinary path and refusing it would be a rule
            # about nothing.
            for spelling in spellings:
                self.assertFalse(guard.alternate_alphabet(spelling))
            return
        for spelling in spellings:
            with self.subTest(spelling=spelling):
                self.assertTrue(guard.alternate_alphabet(spelling))
                self.assertIn("other path grammar",
                              self.assertRefused(spelling))

        # Two controls. The same file named the ordinary way is admitted, so
        # the case is about the grammar rather than about the path — and a
        # session whose own checkout is `\\`-spelled is not refused wholesale,
        # which is the one legitimate use of that grammar.
        self.assertAdmitted(plain)
        self.assertTrue(guard.alternate_alphabet("\\\\nas\\projects\\repo"))

    def test_an_eight_dot_three_spelling_is_refused_where_one_exists(self):
        # The same class in Windows' other alphabet: `CLAUDE~1` is a different
        # string from `.claude`, so a matcher comparing strings does not see
        # the denied tree — and this guard refuses it without a special case,
        # because `realpath` answers with the long name and the spelling
        # therefore disagrees with the file.
        #
        # 8.3 alias creation can be disabled per volume, so the case reports
        # when the platform gave it nothing to test rather than pretending to
        # have tested it.
        if os.name != "nt":
            return
        import ctypes
        buffer = ctypes.create_unicode_buffer(1024)
        long_name = os.path.join(self.root, "documentation-directory")
        os.makedirs(long_name, exist_ok=True)
        size = ctypes.windll.kernel32.GetShortPathNameW(
            long_name, buffer, len(buffer))
        short = buffer.value if size else long_name
        if short == long_name:
            print("8.3 aliases are disabled on this volume; case has no "
                  "subject", file=sys.stderr)
            return
        # Which refusal fires depends on how much of the path the volume
        # aliases, so the case asserts the outcome rather than the wording:
        # a shortened leaf fails the spelling-versus-resolution test, and a
        # shortened prefix matches no anchor and lands inside a checkout.
        reason = self.assertRefused(os.path.join(short, "a.md"))
        self.assertIn(os.path.basename(long_name), reason)

    def test_a_directory_may_fold_differently_from_its_root(self):
        """Windows sets case sensitivity per directory, and the traits do not.

        A case-sensitive child keeps `Sub` and `sub` apart where the anchor's
        folded key calls them one, but the disagreement is benign for a link:
        a link's resolution is its target, so writing through `docs/Sub/x.md`
        lands on the file that path names. The case asserts that rather than a
        bypass that does not exist.
        """
        if os.name != "nt":
            print("per-directory case sensitivity is Windows'; case has no "
                  "subject here", file=sys.stderr)
            return
        # A fresh, empty directory: the flag silently does not take on one that
        # already holds entries, and `docs/` in this fixture does.
        docs = os.path.join(self.root, "mixed")
        os.makedirs(docs, exist_ok=True)
        made = subprocess.run(
            ["fsutil", "file", "setCaseSensitiveInfo", docs, "enable"],
            capture_output=True, text=True)
        probe_lower = os.path.join(docs, "probe")
        probe_upper = os.path.join(docs, "PROBE")
        os.makedirs(probe_lower, exist_ok=True)
        if made.returncode != 0 or os.path.isdir(probe_upper):
            print("this volume will not take a case-sensitive directory; case "
                  "has no subject here", file=sys.stderr)
            return

        # The pair has to differ only in case, or the guard refuses it for
        # the ordinary reason and the case says nothing about folding. One pair
        # per primitive, since two links cannot share a name.
        names = {"symlink": "alpha", "junction": "beta"}
        for linker in linkers():
            with self.subTest(link=linker):
                lower = os.path.join(docs, names[linker])
                os.makedirs(lower, exist_ok=True)
                self.write(os.path.join(lower, "x.md"), "lower\n")
                upper = os.path.join(docs, names[linker].upper())
                if linker == "symlink":
                    os.symlink(lower, upper, target_is_directory=True)
                else:
                    JUNCTIONS(lower, upper)
                spelled = os.path.join(upper, "x.md")
                self.assertTrue(
                    os.path.samefile(spelled, os.path.join(lower, "x.md")),
                    "the link and its target are one file, which is why "
                    "admitting this is correct")
                self.assertAdmitted(spelled)

    def test_a_notebook_path_is_judged_too(self):
        # `NotebookEdit` carries its target under another key, and a guard that
        # reads only `file_path` would wave the whole tool through while the
        # matcher says it is covered.
        target = os.path.join(self.root, ".claude", "scripts", "helper.sh")
        for linker in linkers():
            with self.subTest(link=linker):
                self.assertRefused(self.link_to("book", target, linker),
                                   tool="NotebookEdit", key="notebook_path")


class TheOrdinaryWriteIsNotDisturbed(GuardCase):
    """The false-positive half, and it is the half that breaks a session."""

    def test_a_real_file_in_an_allowed_tree_is_admitted(self):
        self.assertAdmitted(os.path.join(self.root, "docs", "chapter.md"))

    def test_a_file_that_does_not_exist_yet_is_admitted(self):
        # `Write` creates, so the commonest target of all is a path with no
        # file behind it. `realpath` resolves the existing prefix and appends
        # the rest, which is what makes this work.
        self.assertAdmitted(os.path.join(self.root, "docs", "new-chapter.md"))
        self.assertAdmitted(os.path.join(self.root, "docs", "sub", "deep.md"))

    def test_a_denied_tree_spelled_as_itself_is_admitted_here(self):
        # The control that keeps this hook from becoming a second deny list.
        # `.claude/scripts/**` is denied on the spelling elsewhere, which is
        # correct when the spelling is true of the file; a copy here would go
        # stale, and a change that lifts the deny would be refused by the
        # guard instead.
        self.assertAdmitted(
            os.path.join(self.root, ".claude", "scripts", "helper.sh"))

    def test_a_dotdot_that_stays_where_it_is_spelled_is_admitted(self):
        self.assertAdmitted(
            os.path.join(self.root, "docs", "..", "docs", "chapter.md"))

    def test_a_dotdot_into_a_denied_tree_is_the_deny_lists_subject_not_this_one(self):
        # The harness normalises a path before matching it against the deny
        # list, so `docs/../.claude/...` is denied there, and no link is
        # traversed here. Refusing every `..` would buy nothing against the
        # deny list and would refuse innocent traffic; if the harness stops
        # normalising, this case is the one to invert.
        self.assertAdmitted(os.path.join(
            self.root, "docs", "..", ".claude", "scripts", "helper.sh"))

    def test_a_relative_path_is_resolved_against_the_events_cwd(self):
        # The harness sends absolute paths today. This is what the hook does if
        # that changes, and the answer must not be "resolve against whatever
        # directory the hook process happens to have inherited".
        self.assertAdmitted(os.path.join("docs", "chapter.md"))

    def test_a_checkout_reached_through_a_link_is_not_refused_wholesale(self):
        # `/tmp` is a link to `/private/tmp` on macOS and a worktree path on
        # Windows can arrive 8.3-shortened or through `subst`, so the session's
        # own root resolves to a different spelling, and a guard comparing the
        # raw resolution against the raw spelling would refuse every edit in
        # it. The anchor is resolved too, which is what makes this pass.
        for linker in linkers():
            with self.subTest(link=linker):
                alias = os.path.join(self.outside, f"checkout-{linker}")
                if linker == "symlink":
                    os.symlink(self.root, alias, target_is_directory=True)
                else:
                    JUNCTIONS(self.root, alias)
                self.assertAdmitted(
                    os.path.join(alias, "docs", "chapter.md"), cwd=alias)

    def test_case_folding_is_asked_of_the_filesystem_not_the_platform(self):
        # `os.path.normcase` folds on Windows and nowhere else, which is a
        # statement about the platform where what matters is the filesystem:
        # macOS mounts APFS case-insensitively by default. The hook probes
        # instead, and this case checks the probe against the same measurement
        # taken here — one file, one device and inode, under two spellings.
        guard = self.guard_module()
        directory, name = os.path.split(self.root)
        flipped = os.path.join(directory, name.swapcase())
        try:
            measured = (os.stat(self.root).st_dev == os.stat(flipped).st_dev
                        and os.stat(self.root).st_ino == os.stat(flipped).st_ino)
        except OSError:
            measured = False
        self.assertEqual(measured, guard.case_insensitive(self.root))

    def test_a_spelling_no_anchor_recognises_is_refused_if_it_lands_inside(self):
        # The residual is for a file outside every checkout, not for one
        # inside under a name no anchor can place — such as a prefix
        # `GetShortPathNameW` shortened whole. This closes the class that case
        # folding and Unicode composition each close one spelling of. The alias
        # stands in for the short prefix, which a volume with 8.3 creation
        # disabled cannot produce.
        for linker in linkers():
            with self.subTest(link=linker):
                alias = os.path.join(self.outside, f"alias-{linker}")
                if linker == "symlink":
                    os.symlink(self.root, alias, target_is_directory=True)
                else:
                    JUNCTIONS(self.root, alias)
                through = os.path.join(alias, "docs", "chapter.md")
                self.assertIn("not a spelling any checkout here recognises",
                              self.assertRefused(through))

                # The control, and it is the worktree case rather than a
                # loophole: a session standing in that alias makes it a
                # checkout root of its own, and then the spelling is one the
                # anchors recognise.
                self.assertAdmitted(through, cwd=alias)

    def test_a_composed_and_a_decomposed_spelling_are_one_key(self):
        # A case-insensitive APFS volume is also insensitive to Unicode
        # normalisation, so `é` composed and `e` followed by a combining
        # accent name one directory there while they are two strings in
        # Python, and a checkout prefix spelled in the other form would match
        # no anchor.
        #
        # `key` composes where the anchor's traits say the mount does, so the
        # predicate is assertable on every platform by passing the traits
        # explicitly; the end-to-end half needs a mount that agrees, and says
        # so when it has none rather than reporting a pass for it.
        guard = self.guard_module()
        name = "caf\u00e9"  # built rather than typed: an editor that normalises
        # this file would otherwise make the two spellings one and the case vacuous.
        composed = os.path.join(self.root, unicodedata.normalize("NFC", name))
        decomposed = os.path.join(self.root, unicodedata.normalize("NFD", name))
        self.assertNotEqual(composed, decomposed)

        # Composed only where the mount composes: composing everywhere folds
        # two names that can coexist on ext4 into one key, so a link resolving
        # into the sibling compares equal to a path inside the checkout. Both
        # directions are a bypass, so both are asserted.
        for folded in (False, True):
            with self.subTest(folded=folded):
                self.assertEqual(guard.key(composed, (folded, True)),
                                 guard.key(decomposed, (folded, True)))
                self.assertNotEqual(guard.key(composed, (folded, False)),
                                    guard.key(decomposed, (folded, False)))

        os.makedirs(composed, exist_ok=True)
        try:
            here, there = os.stat(composed), os.stat(decomposed)
            one_directory = (here.st_dev, here.st_ino) == (there.st_dev,
                                                           there.st_ino)
        except OSError:
            one_directory = False
        if not one_directory:
            print("this filesystem distinguishes NFC from NFD; the end-to-end "
                  "half of the normalisation case has no subject here",
                  file=sys.stderr)
            return

        target = os.path.join(self.root, ".claude", "scripts", "helper.sh")
        for linker in linkers():
            with self.subTest(link=linker):
                link = self.link_to("composed", target, linker)
                through = os.path.join(
                    decomposed, "..", os.path.relpath(link, self.root))
                self.assertRefused(through)

    def test_two_names_that_can_coexist_are_not_folded_into_one(self):
        # On a normalisation-sensitive filesystem, NTFS and ext4 among them, a
        # composed and a decomposed name are two directories that coexist, so
        # composing unconditionally would let a link resolving to the same
        # relative path under the sibling compare equal to a path inside the
        # checkout. Where the mount equates the two names the sibling cannot
        # exist, and the case says so rather than pretending to have tested it.
        name = "caf\u00e9"
        checkout = os.path.join(self.outside,
                                unicodedata.normalize("NFC", name))
        sibling = os.path.join(self.outside,
                               unicodedata.normalize("NFD", name))
        os.makedirs(os.path.join(checkout, ".git"), exist_ok=True)
        os.makedirs(os.path.join(sibling, "docs"), exist_ok=True)
        self.write(os.path.join(sibling, "docs", "a.md"), "secret\n")
        try:
            here, there = os.stat(checkout), os.stat(sibling)
            coexist = (here.st_dev, here.st_ino) != (there.st_dev, there.st_ino)
        except OSError:
            coexist = False
        if not coexist:
            print("this filesystem equates NFC and NFD, so the sibling cannot "
                  "exist and this case has no subject here", file=sys.stderr)
            return

        for linker in linkers():
            with self.subTest(link=linker):
                link = os.path.join(checkout, f"docs-{linker}")
                if linker == "symlink":
                    os.symlink(os.path.join(sibling, "docs"), link,
                               target_is_directory=True)
                else:
                    JUNCTIONS(os.path.join(sibling, "docs"), link)
                self.assertRefused(os.path.join(link, "a.md"),
                                   cwd=checkout, project=checkout)

    def test_a_checkout_whose_name_has_no_letters_is_still_asked(self):
        # The probe has to flip something, and the basename is not always
        # flippable: a checkout at `/Users/me/123` has no cased character in
        # its last component, so a probe that only flips the basename falls to
        # the platform default, `False` on macOS where the mount folds, and a
        # linked target spelled `/users/me/123/...` matches no anchor.
        #
        # The assertion ties the numeric root to a lettered one on the same
        # filesystem rather than to a platform: whatever the mount answers for
        # the parent, it must answer for the child, because they are the same
        # mount. A platform-default fallback breaks that on macOS and on any
        # case-insensitive mount, which is where it mattered.
        guard = self.guard_module()
        numeric = os.path.join(self.outside, "123456")
        os.makedirs(os.path.join(numeric, "docs"), exist_ok=True)
        self.assertEqual(guard.case_insensitive(self.outside),
                         guard.case_insensitive(numeric))
        self.assertEqual(guard.case_insensitive(numeric),
                         guard.case_insensitive(os.path.join(numeric, "docs")))

    def test_a_differently_cased_checkout_prefix_is_still_judged(self):
        # On a folding filesystem `/Users/x/Repo` and `/users/x/repo` are one
        # directory, so a target spelled with the other case is inside the
        # checkout, and a comparison that folds only on Windows finds it under
        # no anchor and admits it.
        #
        # Where the filesystem does not fold, the same spelling names a path
        # that does not exist and is not this guard's subject, so the assertion
        # is the filesystem's answer rather than one platform's.
        guard = self.guard_module()
        directory, name = os.path.split(self.root)
        other_case = os.path.join(directory, name.swapcase())
        folds = guard.case_insensitive(self.root)

        plain = os.path.join(other_case, "docs", "chapter.md")
        target = os.path.join(self.root, ".claude", "scripts", "helper.sh")
        for linker in linkers():
            with self.subTest(link=linker, folds=folds):
                link = self.link_to("cased", target, linker)
                through = os.path.join(
                    other_case, os.path.relpath(link, self.root))
                if folds:
                    self.assertAdmitted(plain)
                    self.assertIn("resolves elsewhere in it",
                                  self.assertRefused(through))
                else:
                    self.assertAdmitted(plain)
                    self.assertAdmitted(through)

    def test_the_case_of_a_windows_spelling_is_not_a_difference(self):
        # Windows' `realpath` answers with the on-disk case, so `DOCS` comes
        # back as `docs` and a case-sensitive comparison would read that as a
        # link. On POSIX the upper-case path simply does not exist and resolves
        # to itself, so the same assertion holds for a different reason.
        self.assertAdmitted(os.path.join(self.root, "DOCS", "chapter.md"))


class WhatThisGuardIsNotTheSubjectOf(GuardCase):
    """The residuals, written as passing cases so nobody assumes they closed."""

    def test_a_path_outside_every_checkout_is_not_judged(self):
        # The residual the hook's docstring argues: the harness writes the
        # session's memory and scratchpad by absolute path outside the
        # repository, and refusing those would take them with it.
        self.assertAdmitted(os.path.join(self.outside, "loot.txt"))

    def test_a_tool_that_does_not_write_is_not_judged(self):
        target = os.path.join(self.root, ".claude", "scripts", "helper.sh")
        for linker in linkers():
            with self.subTest(link=linker):
                link = self.link_to("read-me", target, linker)
                self.assertAdmitted(link, tool="Read")
                self.assertAdmitted(link, tool="Bash")

    def test_a_write_with_no_path_to_judge_is_refused(self):
        # The other direction from the fail-open below, and deliberately so: an
        # unreadable event establishes nothing about the session, where a
        # matched tool carrying no path establishes nothing about a write that
        # is about to happen.
        reason = self.assertRefused(None)
        self.assertIn("no file path", reason)

    def test_a_tool_input_that_is_not_an_object_is_refused_the_same_way(self):
        # The same statement as a missing key, that this hook cannot see where
        # the write lands, so it gets the same answer.
        for payload in ([], "file_path", 7):
            with self.subTest(payload=payload):
                event = {
                    "hook_event_name": "PreToolUse",
                    "cwd": self.root,
                    "tool_name": "Edit",
                    "tool_input": payload,
                }
                result = subprocess.run(
                    [sys.executable, str(HOOK)],
                    input=json.dumps(event), capture_output=True, text=True,
                )
                self.assertEqual(0, result.returncode)
                verdict = json.loads(result.stdout)["hookSpecificOutput"]
                self.assertEqual("deny", verdict["permissionDecision"])
                self.assertIn("no file path", verdict["permissionDecisionReason"])

    def test_a_malformed_event_does_not_take_the_session_down(self):
        # The one deliberate fail-open, argued in the hook: refusing every
        # write because this file
        # cannot read its own input would turn a defect in it into a session
        # that can no longer edit anything.
        for payload in ("not json at all", "[1, 2, 3]", ""):
            with self.subTest(payload=payload):
                result = subprocess.run(
                    [sys.executable, str(HOOK)],
                    input=payload, capture_output=True, text=True,
                )
                self.assertEqual(0, result.returncode)
                self.assertEqual("", result.stdout.strip())
                self.assertIn("guard-edit-target", result.stderr)


class TheWiringWithoutWhichNoneOfTheAboveRuns(unittest.TestCase):

    def registered(self):
        settings = json.loads(SETTINGS.read_text(encoding="utf-8"))
        return settings.get("hooks", {}).get("PreToolUse", [])

    def guard_module(self):
        """The hook imported directly, for the one list only it holds."""
        spec = importlib.util.spec_from_file_location("guard_edit_target", HOOK)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        return module

    def test_the_hook_is_registered_for_every_tool_that_writes(self):
        # The subject is the matcher, not the verdict: every case above passes
        # against a hook the harness never calls, and a matcher naming `Edit`
        # alone would leave `Write` — the tool that creates the file — unjudged.
        #
        # The asserted set is read from the hook rather than written out here,
        # so a tool added to the hook is a red test rather than a silent gap.
        matchers = [
            (entry.get("matcher") or "", entry.get("hooks") or [])
            for entry in self.registered()
        ]
        mine = [
            matcher for matcher, hooks in matchers
            if any(HOOK.name in (h.get("command") or "") for h in hooks)
        ]
        self.assertTrue(mine, f"{HOOK.name} is registered for nothing")
        tools = self.guard_module().EDITING_TOOLS
        # The positive control: an empty or shrunken list would satisfy the
        # loop below by having nothing to check.
        self.assertGreaterEqual(len(tools), 4, f"EDITING_TOOLS shrank: {tools}")
        for tool in tools:
            with self.subTest(tool=tool):
                self.assertTrue(
                    any(re.fullmatch(matcher, tool) for matcher in mine),
                    f"no registered matcher selects {tool}: {mine}")

        # And the other direction, because a matcher wider than the hook's own
        # list would send it calls it answers by refusing for want of a path.
        for matcher in mine:
            for alternative in matcher.split("|"):
                with self.subTest(matcher=matcher, alternative=alternative):
                    self.assertIn(alternative, tools)

    def test_the_hook_runs_on_the_312_floor(self):
        commands = [
            h.get("command") or ""
            for entry in self.registered() for h in (entry.get("hooks") or [])
            if HOOK.name in (h.get("command") or "")
        ]
        self.assertTrue(commands)
        for command in commands:
            with self.subTest(command=command):
                self.assertIn("py -3.12", command)
                self.assertIn("CLAUDE_PROJECT_DIR", command)

    def test_the_argv_guard_is_still_registered_beside_it(self):
        # A second entry under the same event is the shape most likely to be
        # written as a replacement rather than an addition, and the git guard
        # going quiet would be invisible: its own suite judges it directly too.
        commands = [
            h.get("command") or ""
            for entry in self.registered() for h in (entry.get("hooks") or [])
        ]
        self.assertTrue(any("guard-git-argv.py" in c for c in commands),
                        f"the argv guard is no longer registered: {commands}")


if __name__ == "__main__":
    unittest.main()
