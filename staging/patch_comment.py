"""Cut _gh_printing's comment to the comment gate's budget; it changes nothing the suite runs."""

import sys
from pathlib import Path

OLD = """        # Quoted heredocs, because a body carries backticks and a
        # double-quoted `printf` argument would command-substitute them —
        # which is the stub doing what the helper exists to refuse. The shim
        # answers the files endpoint with each name JSON-encoded on its own
        # line — the shape the helper's `--jq` produces, newline in a name and
        # all — and anything else with the body.
"""

NEW = """        # Quoted heredocs, since a double-quoted printf would command-substitute a body's backticks, which is the stub
        # doing what the helper refuses. The files endpoint gets one JSON-encoded name per line, the shape the helper's
        # `--jq` produces, newline in a name and all; anything else gets the body.
"""


def main(root):
    path = Path(root) / ".claude" / "scripts" / "test_pr_helpers.py"
    text = path.read_bytes().decode("utf-8")
    newline = "\r\n" if "\r\n" in text else "\n"
    lf = text.replace("\r\n", "\n")
    assert lf.count(OLD) == 1, "comment not found exactly once"
    path.write_bytes(lf.replace(OLD, NEW).replace("\n", newline).encode("utf-8"))


if __name__ == "__main__":
    main(*sys.argv[1:])
