r"""Windows' other path grammar, which a hook must not open on a proposed call's behalf.

A hook runs before the permission prompt, so opening `//host/share` offers that host the user's NTLM response
whatever the user answers: `docs/harness-boundaries.md`, *Paths on another machine*.
"""

import ntpath
import os

# A name rather than `os.name` read in place, so a suite on POSIX can judge the Windows grammar by setting it.
WINDOWS = os.name == "nt"


def other_grammar(path):
    r"""Whether `path` is in Windows' non-drive grammar: a UNC share, `\\?\`, `\\.\`, or the NT namespace's `\??\`.

    Matched by two leading separators, either way round, rather than a list of prefixes, which would miss one; Win32
    passes `\??\` through as it is. Windows alone, because `//x` on POSIX is an ordinary local path."""
    text = os.fspath(path).replace("/", "\\")
    return WINDOWS and (text[:2] == "\\\\" or text[:4] == "\\??\\")


def foreign(path, *own):
    r"""Whether `path` is in the other grammar and on none of the shares the home and `own` stand on.

    A share the home, the cwd or the project stands on is the user's own, a worktree's main checkout there included;
    a model can name any other. Compared by drive as Windows spells it, so `\\?\UNC\host\…` is foreign.
    """
    if not other_grammar(path):
        return False
    drive = ntpath.normcase(ntpath.splitdrive(ntpath.normpath(os.fspath(path)))[0])
    for root in (os.path.expanduser("~"), os.environ.get("HOME"), *own):
        if root and other_grammar(root) and drive and drive == ntpath.normcase(
                ntpath.splitdrive(ntpath.normpath(os.fspath(root)))[0]):
            return False
    return True
