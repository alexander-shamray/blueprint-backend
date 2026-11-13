#!/usr/bin/env python3
"""Judge an edit target by the file it resolves to, not by the path it spells.

A link inside an allowed tree is a write no path deny sees, so a target not the file it spells is refused. An unentered
worktree is judged by the target's denies, and the home's keys and control files by name (*The edit-target guard*)."""

import functools
import importlib.util
import json
import os
import sys
import unicodedata

# The tools that write a file. `MultiEdit` is listed although this repository's
# harness does not surface it: the matcher in `.claude/settings.json` is a
# regular expression over the tool name, so a tool registered later arrives
# here judged rather than unjudged.
EDITING_TOOLS = ("Edit", "Write", "NotebookEdit", "MultiEdit")

# Where each of those carries its target. `Edit` and `Write` use `file_path`;
# `NotebookEdit` uses `notebook_path`. Both are read, and a matched call
# carrying neither is refused rather than waved through — a write whose target
# this file cannot see is one it has established nothing about.
PATH_KEYS = ("file_path", "notebook_path")

GIT_GUARD = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                         "guard-git-argv.py")

# Windows names the same file in more than one alphabet, and a permission rule
# reads only one of them. Every spelling beginning `\\` is the other: the
# extended-length prefix `\\?\` and the device prefix `\\.\`, which skip the
# normalisation a matcher depends on, and the UNC form `\\server\share\...`,
# which can reach the local disk through an administrative share such as
# `\\localhost\C$` — so a write spelled either way can land in a directory
# whose plain spelling is denied.
#
# The whole family is refused rather than a list of prefixes, which would miss
# a spelling, and refused rather than resolved, because a hook can only allow
# or deny: it cannot hand the matcher the plain spelling. A repository on a
# network share is the one legitimate case, and the caller exempts a target
# such a checkout contains. Scoped to Windows because `//x` on POSIX is an
# ordinary path.
def alternate_alphabet(path):
    """Whether `path` is spelled in Windows' non-drive path grammar."""
    return os.name == "nt" and path[:2].replace("/", "\\") == "\\\\"


def case_insensitive(path):
    """Whether `path`'s filesystem resolves a differently-cased spelling to it.

    `os.path.normcase` folds case on Windows alone, which is a statement about
    the platform where what matters is the filesystem: macOS mounts APFS
    case-insensitively by default, and a Linux mount can be too, so a
    comparison built on `normcase` would place a differently-cased target under
    no anchor — the branch that admits.

    A component is case-flipped and both spellings are `stat`ed; one device
    and inode under two spellings is the answer. Every component is tried,
    deepest first, because a last component with nothing to flip
    (`/Users/me/123`) would leave the answer to the platform default. A path
    with no cased component anywhere cannot arise under a root that holds a
    `.git`.
    """
    normalised = os.path.normpath(path)
    parts = normalised.split(os.sep)
    for index in range(len(parts) - 1, -1, -1):
        flipped = parts[index].swapcase()
        if flipped == parts[index]:
            continue
        other = os.sep.join(parts[:index] + [flipped] + parts[index + 1:])
        try:
            here = os.stat(normalised)
            there = os.stat(other)
        except OSError:
            return False
        return (here.st_dev, here.st_ino) == (there.st_dev, there.st_ino)
    return os.name == "nt"


def form_insensitive(path):
    """Whether the filesystem resolves NFC and NFD spellings to one file.

    The twin of `case_insensitive`: on a normalisation-sensitive filesystem,
    ext4 among them, a composed name and its decomposed sibling are two
    directories that can coexist, so composing unconditionally would let a link
    into the sibling compare equal to a path inside the checkout. Where no
    component has a distinct alternate form the answer is `False` by
    construction: a path that re-normalises to itself has no
    differently-normalised spelling to be confused with.
    """
    normalised = os.path.normpath(path)
    parts = normalised.split(os.sep)
    for index in range(len(parts) - 1, -1, -1):
        part = parts[index]
        other = unicodedata.normalize(
            "NFD" if unicodedata.is_normalized("NFC", part) else "NFC", part)
        if other == part:
            continue
        candidate = os.sep.join(parts[:index] + [other] + parts[index + 1:])
        try:
            here = os.stat(normalised)
            there = os.stat(candidate)
        except OSError:
            return False
        return (here.st_dev, here.st_ino) == (there.st_dev, there.st_ino)
    return False


def traits_of(path):
    """What this path's filesystem treats as one name: (case, normalisation)."""
    return (case_insensitive(path), form_insensitive(path))


def key(path, traits):
    """One comparable spelling of `path`, under its filesystem's equivalences.

    Both halves are asked of the mount rather than assumed, because folding
    nothing on a case-insensitive volume lets a differently-cased prefix match
    no anchor, and folding normalisation everywhere collapses two coexisting
    names into one.
    """
    folded, composed = traits
    spelling = os.path.normcase(os.path.normpath(path))
    if composed:
        spelling = unicodedata.normalize("NFC", spelling)
    return spelling.lower() if folded else spelling


def same(left, right, traits):
    """Whether two absolute paths name the same place."""
    return key(left, traits) == key(right, traits)


def under(child, parent, traits):
    """Whether `child` is `parent` or sits beneath it, lexically."""
    child = key(child, traits)
    parent = key(parent, traits)
    if child == parent:
        return True
    if not parent.endswith(os.sep):
        parent += os.sep
    return child.startswith(parent)


def checkout_root(path):
    """The nearest ancestor of `path` holding a `.git`, or `None`.

    An anchor has to be a checkout root rather than any directory the session
    stands in, because an anchor excuses the one link traversal on its own
    prefix: an anchor at `<checkout>/docs/tree`, where `tree` links into
    `.claude/scripts`, would make the spelling `docs/tree/helper.sh` and its
    resolution `.claude/scripts/helper.sh` agree.

    Walked lexically from the spelling, so `<checkout>/docs/tree` walks up to
    `<checkout>`, where the `.git` is. A worktree's `.git` is a file rather
    than a directory, so this asks whether the entry exists at all.
    """
    current = os.path.abspath(path)
    while True:
        if os.path.exists(os.path.join(current, ".git")):
            return current
        parent = os.path.dirname(current)
        if parent == current:
            return None
        current = parent


def verified_gitdir(root):
    """The admin directory `root`'s `.git` file names, once git's backlink agrees.

    `None` for a main checkout and for a `.git` file not pointed back at. A
    relative pointer is read against its own file's directory, as git does.
    """
    marker = os.path.join(root, ".git")
    if not os.path.isfile(marker):
        return None
    try:
        with open(marker, encoding="utf-8") as handle:
            text = handle.read().strip()
        if not text.startswith("gitdir:"):
            return None
        gitdir = os.path.realpath(
            os.path.join(root, text.split(":", 1)[1].strip()))
        with open(os.path.join(gitdir, "gitdir"), encoding="utf-8") as handle:
            backlink = os.path.join(gitdir, handle.read().strip())
    except (OSError, ValueError):
        return None
    if os.path.normcase(os.path.realpath(backlink)) != os.path.normcase(
            os.path.realpath(marker)):
        return None
    return gitdir


def linked_worktree(path, checkouts):
    """`path`'s checkout root, when it is a worktree of an anchor's repository.

    Read from `.git`, since no hook on every write can run git. An anchor reached
    by a link is that anchor; another repository's protections are not judged.
    """
    root = checkout_root(path)
    if root is None:
        return None
    main = os.path.join(root, ".git")
    gitdir = os.path.realpath(main) if os.path.isdir(main) else verified_gitdir(root)
    if gitdir is None or any(same(os.path.realpath(root), real_root, traits)
                             for _, real_root, traits in checkouts):
        return None
    for spelled_root, real_root, traits in checkouts:
        for base in (spelled_root, real_root):
            common = common_git_dir(base)
            if under(gitdir, os.path.join(base, ".git"), traits) or (
                    common is not None and under(gitdir, common, traits)):
                return root
    return None


def common_git_dir(root):
    """The repository's `.git` directory behind checkout `root`, or `None`.

    A linked worktree names it in its admin directory's `commondir`, read only
    once git's backlink agrees, so a session in one still knows its siblings.
    """
    marker = os.path.join(root, ".git")
    if os.path.isdir(marker):
        return os.path.realpath(marker)
    gitdir = verified_gitdir(root)
    if gitdir is None:
        return None
    try:
        with open(os.path.join(gitdir, "commondir"), encoding="utf-8") as handle:
            common = os.path.realpath(os.path.join(gitdir, handle.read().strip()))
    except (OSError, ValueError):
        return None
    return common if os.path.isdir(common) else None


def left_main_checkout(cwd, path):
    """The checkout holding `path`, when `cwd` stands in a linked worktree of it.

    `None` for a path in the worktree itself, and for one in any other checkout:
    a sibling's own `.git` holds it first, and it is judged as a sibling.
    """
    standing = checkout_root(cwd)
    gitdir = verified_gitdir(standing) if standing is not None else None
    owner = checkout_root(path)
    if gitdir is None or owner is None or under(path, standing, traits_of(standing)):
        return None
    marker = os.path.join(owner, ".git")
    if not os.path.isdir(marker) or not under(gitdir, os.path.realpath(marker), traits_of(owner)):
        return None
    return owner


@functools.lru_cache(maxsize=1)
def git_guard():
    """`guard-git-argv.py` as a module; it owns the denies and the home's names."""
    spec = importlib.util.spec_from_file_location("guard_git_argv", GIT_GUARD)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def protected_in_worktree(path, root):
    """Which `Edit(...)` deny of this target holds `path` beneath `root`, or `None`.

    Asked of `guard-git-argv.py`, which owns reading them. A deny list that
    cannot be read names itself, so the write is refused rather than admitted.
    """
    try:
        relative = os.path.relpath(path, root)
    except ValueError:
        return None
    try:
        return git_guard().denied_path(relative)
    except (OSError, ValueError, TypeError, AttributeError):
        return "this target's deny list, which cannot be read"


def home_offence(spelled, lexical, resolved):
    """The reason to refuse a target no checkout below the home holds, or `None`.

    Spelling and resolution are both judged, so a link into `~/.ssh` is held.
    Only `~/.claude`'s first component is control. Unreadable names refuse.
    """
    home = os.path.abspath(os.path.expanduser("~"))
    traits = traits_of(home)

    def named(name):
        return name.lower() if traits[0] else name

    for path in (lexical, resolved):
        for root in (home, os.path.realpath(home)):
            if not under(path, root, traits) or same(path, root, traits):
                continue
            parts = os.path.relpath(path, root).replace("\\", "/").split("/")
            try:
                place = git_guard().home_place([named(part) for part in parts])
            except (OSError, ValueError, TypeError, AttributeError):
                return (
                    f"guard-edit-target: {spelled} is under the home directory, "
                    "and the names that guard it cannot be read from "
                    f"{GIT_GUARD} (docs/harness-boundaries.md)."
                )
            if place == "login":
                return (
                    f"guard-edit-target: {spelled} resolves to {resolved}, "
                    "which no checkout below the home holds and is a key, a shell "
                    "profile or git's own configuration: what runs at login, "
                    "under every session (docs/harness-boundaries.md)."
                )
            if place == "control":
                return (
                    f"guard-edit-target: {spelled} resolves to {resolved}, "
                    f"inside the harness's own control surface: `{parts[1]}` "
                    "applies to every project, and a change to it is the "
                    "owner's to run (docs/harness-boundaries.md)."
                )
    return None


def holds_home(spelled_root, real_root, traits):
    """Whether an anchor is the home directory or one of its ancestors."""
    home = os.path.abspath(os.path.expanduser("~"))
    return (under(home, spelled_root, traits)
            or under(os.path.realpath(home), real_root, traits))


def anchors(event):
    """The checkouts this guard is standing in: spelling, resolution, traits.

    Three sources, because no one of them is right in every session:
    `CLAUDE_PROJECT_DIR` is what the harness sets; the event's `cwd` is where
    the session is, which differs once `/branch` moves it into a sibling
    worktree; and this file's own location is the checkout that owns the
    guard. The first and last are roots by construction; `cwd` is walked up to
    its checkout root and dropped when it has none (`checkout_root`).

    Each keeps its spelling and its resolution, because the judgement below
    compares the two, and an anchor reached through a link would otherwise make
    every edit under it look like the thing this file refuses.

    Adding an anchor can only narrow this guard, because every anchor
    containing the target must agree; that is what makes an
    environment-supplied `CLAUDE_PROJECT_DIR` safe to trust.
    """
    here = os.path.dirname(os.path.dirname(os.path.dirname(
        os.path.abspath(__file__))))
    cwd = event.get("cwd")
    roots = [
        os.environ.get("CLAUDE_PROJECT_DIR"),
        checkout_root(cwd) if isinstance(cwd, str) and cwd else None,
        here,
    ]
    found = []
    for path in roots:
        if not path or not isinstance(path, str):
            continue
        spelled = os.path.abspath(path)
        traits = traits_of(spelled)
        if any(same(spelled, seen, traits) for seen, _, _ in found):
            continue
        found.append((spelled, os.path.realpath(path), traits))
    return found


def offence(event):
    """The reason to refuse this call, or `None` to let it through."""
    tool = event.get("tool_name")
    if tool not in EDITING_TOOLS:
        return None

    # A `tool_input` that is not an object is refused like one carrying no
    # path: either way this file cannot see where the write lands. The loop
    # variable is `name`, not `key`, which is a function in this module.
    tool_input = event.get("tool_input")
    spelled = None
    if isinstance(tool_input, dict):
        for name in PATH_KEYS:
            value = tool_input.get(name)
            if isinstance(value, str) and value:
                spelled = value
                break
    if spelled is None:
        return (
            f"guard-edit-target: {tool} carries no file path this guard can "
            "read, so nothing has been established about where it writes. "
            "Refusing rather than waving it through."
        )

    cwd = event.get("cwd")
    if not isinstance(cwd, str) or not cwd:
        cwd = os.getcwd()

    checkouts = anchors(event)
    # The exemption is per anchor and per target: a `\\`-spelled checkout
    # licenses only a target it contains, because `\\?\UNC\server\share\repo\…`
    # is not lexically under `\\server\share\repo`, so the anchor loop would
    # skip it and the fall-through would admit it.
    if alternate_alphabet(spelled):
        joined = (spelled if os.path.isabs(spelled)
                  else os.path.join(cwd, spelled))
        placed = os.path.normpath(os.path.abspath(joined))
        if not any(alternate_alphabet(root)
                   and (under(placed, root, traits)
                        or under(placed, real, traits))
                   for root, real, traits in checkouts):
            return (
                f"guard-edit-target: {spelled} is spelled in Windows' other "
                "path grammar — an extended-length or device prefix, or a UNC "
                "share — and no checkout named that way contains it. A "
                "permission rule matches the string it is given, and measured "
                "here a denied directory accepted a write spelled both of "
                "those ways. Name the file the way the rules are written."
            )

    # `realpath` is taken of the original spelling and `normpath` of the joined one, because `normpath` collapses
    # `..` lexically, which is wrong after a link; where the two disagree the call is refused. A `..` traversing no
    # link is admitted: the harness normalises a path before matching it, so `docs/../.claude/scripts/x` meets the
    # `.claude/scripts/**` deny, and refusing every `..` here would buy nothing against the deny list.
    joined = spelled if os.path.isabs(spelled) else os.path.join(cwd, spelled)
    lexical = os.path.normpath(os.path.abspath(joined))
    resolved = os.path.realpath(joined)

    # A rule anchored at the session's root misses a worktree it has not entered,
    # so the target's denies judge it, and it becomes an anchor so that a link
    # inside it is judged. The one the session stands in is the project.
    sibling = linked_worktree(lexical, checkouts)
    if sibling is not None and not any(
            same(sibling, root, traits) for root, _, traits in checkouts):
        machinery = protected_in_worktree(lexical, sibling)
        if machinery is not None:
            return (
                f"guard-edit-target: {spelled} is held by {machinery} in a "
                "worktree of this repository the session is not standing in, "
                "which a rule anchored at the session's root does not reach. "
                "Move into the worktree to edit it "
                "(docs/harness-boundaries.md)."
            )
        checkouts = checkouts + [
            (sibling, os.path.realpath(sibling), traits_of(sibling))]

    # EnterWorktree moves the permission root and leaves the project at main,
    # so main is an anchor here and no rule names its machinery any more.
    main_checkout = left_main_checkout(cwd, lexical)
    if main_checkout is not None:
        machinery = protected_in_worktree(lexical, main_checkout)
        if machinery is not None:
            return (
                f"guard-edit-target: {spelled} is held by {machinery} in the "
                "main checkout, which this session has left for a worktree of "
                "it, so none of its permission rules name the file. Change it "
                "from the main checkout, or hand it to the owner "
                "(docs/harness-boundaries.md)."
            )

    # Every anchor containing the target must agree, so an extra anchor cannot
    # excuse what another refuses — the property `anchors` rests its trust in
    # `CLAUDE_PROJECT_DIR` on. An anchor holding the home directory, a dotfiles
    # checkout at `~` or a project directory set to it or above, judges its
    # links but leaves `home_offence` to run.
    judged = False
    held = False
    for spelled_root, real_root, traits in checkouts:
        if under(lexical, spelled_root, traits):
            base = spelled_root
        elif under(lexical, real_root, traits):
            base = real_root
        else:
            continue
        judged = True
        held = held or holds_home(spelled_root, real_root, traits)

        expected = os.path.normpath(
            os.path.join(real_root, os.path.relpath(lexical, base)))

        # The traits are measured at the root and a child can disagree: Windows
        # sets case sensitivity per directory (`fsutil file
        # setCaseSensitiveInfo`), and a mount below the root can differ
        # outright, so `docs/Sub/x.md`, a junction beside a real `docs/sub/`, is
        # two files the anchor's folding calls one. Where the two agree only
        # because of an equivalence, `samefile` asks the filesystem, which is
        # safe here because it compares two concrete paths rather than deciding
        # what counts as a root. When either does not exist yet — the ordinary
        # case for `Write` — the folded verdict stands, which is the residual.
        if (same(resolved, expected, traits)
                and os.path.normpath(resolved) != os.path.normpath(expected)):
            try:
                if not os.path.samefile(resolved, expected):
                    return (
                        f"guard-edit-target: {spelled} and the file it names "
                        f"differ only by a spelling this checkout's root folds "
                        f"— but {resolved} and {expected} are two files here. "
                        "A directory may be case- or normalisation-sensitive "
                        "where its root is not (#181, "
                        "docs/harness-boundaries.md)."
                    )
            except OSError:
                pass

        if not same(resolved, expected, traits):
            escaped = not under(resolved, real_root, traits)
            where = "outside the checkout" if escaped else "elsewhere in it"
            return (
                f"guard-edit-target: {spelled} resolves {where} — to "
                f"{resolved}. A permission rule matches the path as written, "
                "so an edit through a link lands where no deny has judged it. "
                "Write the file at its real path, or say why the link is "
                "there (#181, docs/harness-boundaries.md)."
            )

    # A spelling no anchor recognises, naming a file inside one, is refused, such
    # as an 8.3 alias of the whole prefix: if it resolves into a checkout that
    # did not recognise it, the matcher judged a string that is not this file.
    # A target outside every anchor is asked only for what `home_offence` names,
    # which keeps memory and scratch writes working.
    if not judged:
        for _, real_root, traits in checkouts:
            if under(resolved, real_root, traits):
                return (
                    f"guard-edit-target: {spelled} is not a spelling any "
                    f"checkout here recognises, yet it resolves to {resolved}, "
                    "inside one. A permission rule matches the string it is "
                    "given, so a name the rules cannot place is a write "
                    "nothing has judged (#181, docs/harness-boundaries.md)."
                )

    # Reached when every anchor containing the target agreed.
    if held or not judged:
        return home_offence(spelled, lexical, resolved)
    return None


def main():
    try:
        event = json.loads(sys.stdin.buffer.read().decode("utf-8"))
    except (json.JSONDecodeError, ValueError, UnicodeDecodeError):
        # The one deliberate fail-open, as in guard-git-argv.py: a hook that
        # cannot read its own input has established nothing, and refusing every
        # write on a malformed event would turn a defect here into a dead
        # session.
        print("guard-edit-target: unreadable hook event; not judging",
              file=sys.stderr)
        return 0

    if not isinstance(event, dict):
        print("guard-edit-target: hook event is not an object; not judging",
              file=sys.stderr)
        return 0

    reason = offence(event)
    if reason is None:
        return 0

    json.dump(
        {
            "hookSpecificOutput": {
                "hookEventName": "PreToolUse",
                "permissionDecision": "deny",
                "permissionDecisionReason": reason,
            }
        },
        sys.stdout,
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
