"""Patch review round 2's two findings, a docstring and test comments the fallback change made false.

Run by apply.sh as `patch_comments.py <root>`; nothing here changes what the guard decides.
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


# R3: the docstring said the fallback would pass a force push, which it now refuses.
DOCSTRING_OLD = r'''    """Whether a heredoc names a delimiter this file cannot read.

    An undecodable delimiter is refused outright, because leaving its body to
    be judged as commands holds only while the command still tokenises: a body
    carrying an unmatched quote sends `offence` down its `ValueError` path,
    which does not enforce the push allow-list, so a force push after
    `git commit -F - <<$'E\\x4fF'` would pass. Decoding every ANSI-C escape
    instead is a list that trails bash's, and each gap in it reopens this.

    The scan asks `shell_positions` where the `<<` is, so a delimiter quoted
    inside an argument is not one of these; the two guards below are
    `heredoc_spans`', for the same reasons it states.
    """
'''

DOCSTRING_NEW = r'''    """Whether a heredoc names a delimiter this file cannot read, refused with a reason that says so."""
'''

# R4: test names and comments that still described a fallback that only degraded.
TESTS = [
    (
        '''    def test_an_unparseable_command_still_gets_the_weaker_check(self):
        # What a parse failure degrades to, which is what keeps it from being a
        # fail-open: the substring scan the settings deny already performs, so
        # never weaker than the settings alone and never a silent pass.
''',
        '''    def test_an_unparseable_command_is_refused(self):
        # A parse failure is refused outright, never a silent pass.
''',
    ),
    (
        '''        # The body's apostrophe breaks the parse, and the fallback's crude scan
        # cannot see a quote-split update; a closed body tokenises as before.
''',
        '''        # The body's apostrophe breaks the parse, so the command is refused; a
        # closed body tokenises and is judged as before.
''',
    ),
    (
        '''        # An undecodable delimiter is refused outright rather than opening no
        # body. Leaving the lines unstripped makes them commands only while the
        # command still tokenises: a body carrying an unmatched quote takes the
        # `ValueError` path, and that fallback scans for forbidden flags and
        # `ext::` alone without enforcing the push allow-list.
''',
        '''        # An undecodable delimiter is refused outright, with a reason that names
        # it, rather than opening no body and leaving an unmatched quote in the
        # lines to be refused without one.
''',
    ),
    (
        '''    def test_the_fallback_still_reads_the_push_grammar(self):
        # A `ValueError` path scanning for forbidden flags and `ext::` alone
        # switches the push allow-list off for any command this guard cannot
        # tokenise, and a line is easy to make untokenisable on purpose. The
        # check there can only be the crude one, which is the point of the
        # path.
''',
        '''    def test_an_untokenisable_push_is_refused(self):
        # A line is easy to make untokenisable on purpose, so that cannot switch
        # the push allow-list off: whatever fails to tokenise is refused.
''',
    ),
]


def tests(text):
    for old, new in TESTS:
        text = once(text, old, new)
    return text


def main(root):
    claude = Path(root) / ".claude"
    rewrite(claude / "hooks" / "guard-git-argv.py", lambda t: once(t, DOCSTRING_OLD, DOCSTRING_NEW))
    rewrite(claude / "scripts" / "test_git_argv_guard.py", tests)


if __name__ == "__main__":
    main(*sys.argv[1:])
