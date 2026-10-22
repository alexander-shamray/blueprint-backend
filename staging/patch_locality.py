"""Patch pr-locality.sh and test_pr_helpers.py for a CRLF body and a rename's source, one fix at a time.

Run by apply.sh as `patch_locality.py <root> <part>`, each fix's case before its code, so the case is seen to fail
first; the parts are the branches of main below.
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


TEST_ANCHOR = "    def test_a_body_without_rows_is_empty_success(self):\n"

# ---- crlf: a body whose lines end in CR is read as its rows ----

CRLF_TEST = r'''    def test_a_crlf_body_is_read_as_its_rows(self):
        # A body saved on Windows or through the web form ends its lines in CR;
        # the CI gate reads it, so the review round's verdict has to as well.
        # printf makes the CR at run time, so no host's line endings can drop it.
        script = (
            "case \"$*\" in *\"/files\"*) echo '\"docs/x.md\"' ;;\n"
            "*) printf '| Class | D |\\r\\n| Touch set | docs/x.md |\\r\\n' ;; esac\n"
        )
        r = self._run_locality_with_gh(script)
        self.assertEqual(0, r.returncode, r.stderr)
        self.assertEqual(["class D", "inside docs/x.md"], r.stdout.splitlines())

'''

BODY_READ = "body=$(gh_read pr view \"$pr\" --json body --jq .body)\n"

BODY_STRIP = (
    "# A body saved on Windows or through the web form ends each line in CR, which no row grammar below expects.\n"
    "body=${body//$'\\r'/}\n"
)

# ---- rename: a rename's source is judged beside its destination ----

RENAME_TEST = r'''    def test_a_rename_is_judged_at_both_ends(self):
        # The files API gives a rename's source as previous_filename, and the CI
        # gate judges both ends, so the stub runs the helper's own --jq on it.
        body = "| Class | D |\n| Touch set | docs/** |\n"
        api = json.dumps([
            {"filename": "docs/foo.md", "previous_filename": "src/Foo.cs", "status": "renamed"},
            {"filename": "docs/x.md", "status": "modified"},
        ])
        script = (
            'case "$*" in *"/files"*)\n'
            '  while [ $# -gt 0 ]; do if [ "$1" = --jq ]; then q=$2; fi; shift; done\n'
            "  jq -r \"$q\" <<'FILES' | tr -d '\\r'\n" + api + "\nFILES\n"
            ";; *) cat <<'STUB'\n" + body + "STUB\n;; esac\n"
        )
        r = self._run_locality_with_gh(script)
        self.assertEqual(0, r.returncode, r.stderr)
        self.assertEqual(
            ["class D", "inside docs/foo.md", "outside src/Foo.cs", "inside docs/x.md"],
            r.stdout.splitlines())

'''

FILES_OLD = """# The changed paths are the diff's own, and each gets the one word this
# script chooses for it. `filename` is the whole of what is read, and it is
# read as a JSON string so that a newline inside a name cannot be a second
# line: a name that needed an escape is refused rather than decoded.
files=$(gh_read api "repos/{owner}/{repo}/pulls/$pr/files" --paginate --jq '.[].filename | @json')
"""

FILES_NEW = """# The changed paths are the diff's own, and each gets the one word this
# script chooses for it: `filename`, and a rename's `previous_filename`, since a
# move out of a tree changes that tree, as the CI gate judges it. Each is read
# as a JSON string so that a newline inside a name cannot be a second line: a
# name that needed an escape is refused rather than decoded.
files=$(gh_read api "repos/{owner}/{repo}/pulls/$pr/files" --paginate \\
  --jq '.[] | (.filename, (.previous_filename // empty)) | @json')
"""


def main(root, part):
    scripts = Path(root) / ".claude" / "scripts"
    helper = scripts / "pr-locality.sh"
    tests = scripts / "test_pr_helpers.py"
    if part == "crlf-tests":
        rewrite(tests, lambda t: once(t, TEST_ANCHOR, CRLF_TEST + TEST_ANCHOR))
    elif part == "crlf-script":
        rewrite(helper, lambda t: once(t, BODY_READ, BODY_READ + BODY_STRIP))
    elif part == "rename-tests":
        rewrite(tests, lambda t: once(t, TEST_ANCHOR, RENAME_TEST + TEST_ANCHOR))
    elif part == "rename-script":
        rewrite(helper, lambda t: once(t, FILES_OLD, FILES_NEW))
    else:
        raise SystemExit(f"unknown part {part!r}")


if __name__ == "__main__":
    main(*sys.argv[1:])
