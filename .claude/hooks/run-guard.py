#!/usr/bin/env python3
"""Run one guard in this interpreter and prove by the exit status that it ran: `run-guard.py <guard>.py`.

For an interpreter `run-guard.sh` remembered rather than probed; it checks the floor and answers in three statuses
only it leaves. Why: docs/harness-boundaries.md (*The guard launcher*)."""

import os
import sys

FLOOR = (3, 12)

# Clear of what a candidate that never ran this file leaves with: 0 to 2, the Store alias's 49, the `py` launcher's
# 100 and up, a failed flush's 120, the shell's 126 and 127, and a signal's over 128.
ALLOWED = 91
REFUSED = 92
ERRORED = 93


def judged(name):
    """Run the guard called `name` as `__main__`, as `python <guard>` would, and return its proven status."""
    here = os.path.dirname(os.path.abspath(__file__))
    path = os.path.join(here, name)
    # Outside the `try`: a guard that cannot be opened ran nothing, so this leaves with a status that proves nothing.
    with open(path, "rb") as handle:
        source = handle.read()

    # Its own `__main__`, `__file__` and `argv[0]`, which `guard-edit-target.py` finds its checkout from.
    module = type(sys)("__main__")
    module.__file__ = path
    sys.modules["__main__"] = module
    sys.argv = [path]

    try:
        exec(compile(source, path, "exec"), module.__dict__)
        status = 0
    except SystemExit as stop:
        status = stop.code
    except BaseException:  # noqa: BLE001 - reported as the interpreter would
        sys.excepthook(*sys.exc_info())
        status = 1

    # `sys.exit`'s own rules: nothing is success, a number is itself, and anything else is printed and is a failure.
    if status is None:
        status = 0
    elif not isinstance(status, int):
        sys.stderr.write("%s\n" % (status,))
        status = 1

    # No flush here: the interpreter's own, on the way out, leaves with 120 when it fails, which proves nothing.
    if status == 0:
        return ALLOWED
    if status == 2:
        return REFUSED
    return ERRORED


def main(argv):
    if sys.version_info < FLOOR:
        sys.stderr.write(
            "run-guard.py: Python %d.%d or newer is required and this is "
            "%d.%d; judging nothing\n" % (FLOOR + tuple(sys.version_info[:2])))
        return 1
    if len(argv) != 2 or os.path.basename(argv[1]) != argv[1] or not argv[1]:
        sys.stderr.write("usage: run-guard.py <guard file name>\n")
        return 1
    return judged(argv[1])


if __name__ == "__main__":
    sys.exit(main(sys.argv))
