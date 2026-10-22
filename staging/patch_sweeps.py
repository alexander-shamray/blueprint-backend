"""Patch the sweeps' verifier-record rule and gh-issue-list.sh's truncation check, each fix's case before its code.

Run by apply.sh as `patch_sweeps.py <root> <part>`; the parts are the branches of main below.
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


# ---- records: a verifier record that is not a verdict is retried once, then a round error ----

RECORDS_TEST_ANCHOR = "    def test_the_clean_round_rule_agrees_with_the_gate(self):\n"

RECORDS_TEST = '''    def test_a_verifier_record_that_is_not_a_verdict_is_retried_then_an_error(self):
        # A dropped record let a round end clean and mark its commit, and the
        # unverified candidate's file then left every incremental sweep.
        for name in self.SWEEPS:
            with self.subTest(sweep=name):
                text = self.sweep(name)
                self.assertIn("as dispatched, is not a verdict", text)
                self.assertIn("a second such record is a round error", text)
                self.assertIn("a verifier whose record was twice not a verdict", text)
                self.assertNotIn("dropped as malformed", text)
                self.assertNotIn("drop and count a malformed record", text)

'''

BUG_STEP_OLD = """   a round error under *Never fail open*. `refuted` or `outside-root` drops the
   candidate; a record not in the declared shape is dropped as malformed and
   counted; **a record whose `file` and `line` are not the candidate's as
   dispatched is dropped the same way**, whatever its verdict. Say how many did
   not survive. (why: docs/commands/bug-sweep.md, *The round: verify*)
"""

BUG_STEP_NEW = """   a round error under *Never fail open*. `refuted` or `outside-root` drops the
   candidate. **A record not in the declared shape, or whose `file` and `line`
   are not the candidate's as dispatched, is not a verdict**: dispatch that
   verifier once more with the same candidate, and a second such record is a
   round error under *Never fail open*. Say how many did not survive. (why:
   docs/commands/bug-sweep.md, *The round: verify*)
"""

SECURITY_STEP_OLD = """   `unreadable-root` first: a round error under *Never fail open*. Drop
   `refuted` and `outside-root`; drop and count a malformed record; **drop a
   record whose `file` and `line` are not the candidate's as dispatched**,
   whatever its verdict. (why: docs/commands/security-sweep.md, *Verify*)
"""

SECURITY_STEP_NEW = """   `unreadable-root` first: a round error under *Never fail open*. Drop
   `refuted` and `outside-root`. **A record not in the declared shape, or whose
   `file` and `line` are not the candidate's as dispatched, is not a verdict**:
   dispatch that verifier once more with the same candidate, and a second such
   record is a round error under *Never fail open*. (why:
   docs/commands/security-sweep.md, *Verify*)
"""

FAIL_OPEN_OLD = "`empty-scope`, a\nworktree path git had to quote"

FAIL_OPEN_NEW = "`empty-scope`, a\nverifier whose record was twice not a verdict, a worktree path git had to\nquote"


def sweep(step_old, step_new):
    def change(text):
        text = once(text, step_old, step_new)
        return once(text, FAIL_OPEN_OLD, FAIL_OPEN_NEW)
    return change


# ---- list: a list as long as its limit may be cut, so it is refused ----

LIST_TEST_ANCHOR = "    def test_both_sweeps_reach_the_issue_set_only_through_the_helper(self):\n"

LIST_TEST = r'''    def test_a_list_as_long_as_its_limit_is_refused_as_cut(self):
        # gh stops at --limit without a word, so a stub answering with as many
        # issues as were asked for is a cut list, and the helper refuses it.
        stub_dir = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, stub_dir, ignore_errors=True)
        stub = stub_dir / "gh"
        stub.write_text(
            "#!/usr/bin/env bash\n"
            'while [ $# -gt 0 ]; do if [ "$1" = --limit ]; then n=$2; fi; shift; done\n'
            '[ "$FULL" = 1 ] || n=2\n'
            "printf '['\n"
            "for ((i = 1; i <= n; i++)); do [ $i -eq 1 ] || printf ','; "
            """printf '{"number":%d,"title":"t","state":"OPEN","labels":[]}' $i; done\n"""
            "printf ']\\n'\n",
            encoding="utf-8", newline="\n")
        stub.chmod(0o755)
        for full, code in (("1", 3), ("0", 0)):
            with self.subTest(full=full):
                env = dict(os.environ, FULL=full, PATH=str(stub_dir) + os.pathsep + os.environ["PATH"])
                result = subprocess.run([BASH, str(ISSUE_LIST)], capture_output=True, text=True, env=env)
                self.assertEqual(code, result.returncode, result.stderr)
                if code:
                    self.assertIn("may be cut", result.stderr)
                    self.assertEqual("", result.stdout)
                else:
                    self.assertIn('"number":2', result.stdout)

'''

LIST_OLD = """# `--state all`, because the sweeps de-duplicate against closed issues too: a
# finding already filed and fixed must not be re-filed. `--limit 1000`, because
# the default 30 hides older issues and a de-duplication gate that cannot see an
# issue reports a duplicate as new.
gh issue list --state all --limit 1000 --json number,title,state,labels
"""

LIST_NEW = """# `--state all`, because the sweeps de-duplicate against closed issues too. gh
# cuts a list at `--limit` without a word, and a de-duplication gate that cannot
# see an issue reports a duplicate as new, so a list as long as the limit is
# refused rather than read as whole.
limit=5000
list=$(gh issue list --state all --limit "$limit" --json number,title,state,labels)
count=$(jq length <<<"$list" | tr -d '\\r')
[ "$count" -lt "$limit" ] ||
  { echo "gh-issue-list: $count issues is the limit, so the list may be cut; raise it here" >&2; exit 3; }
printf '%s\\n' "$list"
"""


def main(root, part):
    claude = Path(root) / ".claude"
    tests = claude / "scripts" / "test_gh_issue_suppresses.py"
    if part == "records-tests":
        rewrite(tests, lambda t: once(t, RECORDS_TEST_ANCHOR, RECORDS_TEST + RECORDS_TEST_ANCHOR))
    elif part == "records":
        rewrite(claude / "commands" / "bug-sweep.md", sweep(BUG_STEP_OLD, BUG_STEP_NEW))
        rewrite(claude / "commands" / "security-sweep.md", sweep(SECURITY_STEP_OLD, SECURITY_STEP_NEW))
    elif part == "list-tests":
        rewrite(tests, lambda t: once(t, LIST_TEST_ANCHOR, LIST_TEST + LIST_TEST_ANCHOR))
    elif part == "list":
        rewrite(claude / "scripts" / "gh-issue-list.sh", lambda t: once(t, LIST_OLD, LIST_NEW))
    else:
        raise SystemExit(f"unknown part {part!r}")


if __name__ == "__main__":
    main(*sys.argv[1:])
