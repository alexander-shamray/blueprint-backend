"""Patch review round 1's two findings into test_pr_helpers.py: the pin's comment-then-join order, and a stale quote.

Run by apply.sh as `patch_review.py <root> test` and then `patch_review.py <root> fix`, so the case is seen to fail
first.
"""

import sys
from pathlib import Path


def rewrite(path, change):
    """Apply `change` to the LF text of `path`, keeping the file's own line endings."""
    text = path.read_bytes().decode("utf-8")
    newline = "\r\n" if "\r\n" in text else "\n"
    lf = text.replace("\r\n", "\n")
    out = change(lf)
    assert out != lf, f"{path}: nothing changed"
    path.write_bytes(out.replace("\n", newline).encode("utf-8"))


def once(text, anchor, replacement):
    assert text.count(anchor) == 1, f"anchor not found exactly once: {anchor[:60]!r}"
    return text.replace(anchor, replacement)


PIN_INLINE = r'''        # A continued line is joined first, as bash joins it, so its tail is compared too.
        code = [
            line for line in re.sub(r"\s*\\\n\s*", " ", text).splitlines() if not line.lstrip().startswith("#")
        ]
'''

PIN_CALL = '''        code = code_lines(text)
'''

CLASS_ANCHOR = "class NoCommandReadsAPullRequestFeedUnfiltered(unittest.TestCase):\n"

# The helper as the pin had it, so the new case fails on it before the fix.
HELPER_OLD = r'''def code_lines(text):
    """A script's code lines, each continued line joined as bash joins it."""
    return [line for line in re.sub(r"\s*\\\n\s*", " ", text).splitlines() if not line.lstrip().startswith("#")]


'''

HELPER_NEW = r'''def code_lines(text):
    """A script's code lines, comments dropped first, since bash continues none, then continued lines joined."""
    code = "\n".join(line for line in text.splitlines() if not line.lstrip().startswith("#"))
    return re.sub(r"\s*\\\n\s*", " ", code).splitlines()


'''

CASE_ANCHOR = "    def test_a_stalled_gh_read_fails_at_the_bound_in_every_helper(self):\n"

CASE = r'''    def test_a_comment_ending_in_a_backslash_swallows_no_call(self):
        # The pin above reads code lines; a backslash ending a comment continues
        # nothing to bash, so the call after it must still be one of them.
        self.assertEqual(["x=$(gh_read api y)"], code_lines("# a comment \\\nx=$(gh_read api y)\n"))

'''

STUB_OLD = '''        # answers the files endpoint with each name JSON-encoded on its own
        # line — the shape `--jq '.[].filename | @json'` produces, newline
        # in a name and all — and anything else with the body.
'''

STUB_NEW = '''        # answers the files endpoint with each name JSON-encoded on its own
        # line — the shape the helper's `--jq` produces, newline in a name and
        # all — and anything else with the body.
'''


def test(text):
    text = once(text, PIN_INLINE, PIN_CALL)
    text = once(text, CLASS_ANCHOR, HELPER_OLD + CLASS_ANCHOR)
    return once(text, CASE_ANCHOR, CASE + CASE_ANCHOR)


def fix(text):
    text = once(text, HELPER_OLD, HELPER_NEW)
    return once(text, STUB_OLD, STUB_NEW)


def main(root, part):
    tests = Path(root) / ".claude" / "scripts" / "test_pr_helpers.py"
    if part == "test":
        rewrite(tests, test)
    elif part == "fix":
        rewrite(tests, fix)
    else:
        raise SystemExit(f"unknown part {part!r}")


if __name__ == "__main__":
    main(*sys.argv[1:])
