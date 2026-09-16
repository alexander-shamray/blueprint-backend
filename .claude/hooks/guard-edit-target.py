#!/usr/bin/env python3
"""Judge an edit target by the file it resolves to, not by the path it spells.

A permission rule matches a spelling and an edit lands on a file.
`.claude/settings.json` and `/review-grok`'s frontmatter deny edits by the path
a caller typed, and a symbolic link — or, on Windows, a junction — inside an
allowed tree is a spelling no deny matches while the write lands wherever the
link points: inside a denied tree, or out of the checkout altogether. A branch
under review can introduce such a link before CI has judged it.

So the rule is one predicate that names no tree: an edit target must be the
file its path spells. Resolve the target, re-anchor it on the resolved checkout
root, and refuse it if the two disagree. This file holds no copy of any deny
list, so it cannot go stale as one changes, and an edit spelled at the file it
actually is passes here and is then judged by the rules that already exist.

The anchoring has three consequences that are not bugs. The checkout root is
itself resolved, so a worktree under a linked temp root — `/tmp` on macOS, an
8.3 or `subst` path on Windows — is judged against its own real spelling. An
anchor is a checkout root and every anchor containing the target must agree,
because an anchor excuses the one link traversal on its own prefix. And the
comparison folds case and Unicode normalisation where the filesystem does,
asked of the mount rather than read off the platform.

The residual: a path no anchor can place is admitted only when it also resolves
outside every anchor — an absolute path into a scratch directory, or into the
user's own `~/.claude`, which the harness writes its own state through. A
spelling no anchor recognises that lands inside a checkout is refused.
`/review-grok`'s site contract admits only plain repository-relative paths, so
the exposure this closes cannot spell an out-of-tree target; which out-of-tree
paths are legitimate is a different file's argument.

One grammar is refused rather than judged: on Windows a spelling beginning `\\`
skips the normalisation a permission rule's matcher depends on
(`alternate_alphabet`), unless a checkout named in that grammar contains it.

Protocol: PreToolUse, matcher `Edit|Write|NotebookEdit|MultiEdit`. Exit 0 and
print nothing to allow; print the deny JSON to refuse, because a guard that
refuses without saying why gets worked around rather than fixed.
"""

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

    # `realpath` is taken of the original spelling and `normpath` of the joined
    # one, because `normpath` collapses `..` lexically, which is wrong for a
    # `..` that follows a link; the lexical form only locates the target under
    # an anchor, and where the two disagree the call is refused.
    #
    # A `..` that traverses no link is admitted: the harness normalises a path
    # before matching it, so `docs/../.claude/sandbox/x` is denied by a
    # `.claude/sandbox/**` rule, and refusing every `..` here would buy nothing
    # against the deny list while refusing innocent traffic.
    joined = spelled if os.path.isabs(spelled) else os.path.join(cwd, spelled)
    lexical = os.path.normpath(os.path.abspath(joined))
    resolved = os.path.realpath(joined)

    # Every anchor containing the target must agree, so an extra anchor cannot
    # excuse what another refuses — the property `anchors` rests its trust in
    # `CLAUDE_PROJECT_DIR` on.
    judged = False
    for spelled_root, real_root, traits in checkouts:
        if under(lexical, spelled_root, traits):
            base = spelled_root
        elif under(lexical, real_root, traits):
            base = real_root
        else:
            continue
        judged = True

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

    # A spelling no anchor recognises, naming a file inside one, is refused: the
    # residual is for a file genuinely outside every checkout, not for one
    # inside a checkout under a name the anchors do not match, such as an 8.3
    # alias of the whole prefix. Whatever the spelling, if it resolves into a
    # checkout that did not recognise it, the matcher judged a string that is
    # not this file. A target resolving outside every anchor still falls
    # through, which keeps the session's own memory and scratch writes working.
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

    # Reached when every anchor containing the target agreed, or when the
    # target is outside every one of them — the residual the module docstring
    # states.
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
