"""main-red-open.sh: how `/ship` step 6 sees a red `main`.

The rows go through the real `jq` filter behind a stubbed `gh`. The title rule is the helper's whole risk: a target
whose workflow writes a title it does not match reads green while `main` is red.
"""

import json
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import dispatch_board as db
from review_helpers import BASH, COMMANDS, SCRIPTS, setUpModule  # noqa: F401

HELPER = SCRIPTS / "main-red-open.sh"
STUB = (
    "#!/usr/bin/env bash\n"
    'echo "$*" >> "$STUB_LOG"\n'
    'if [ "$2" = view ]; then\n'
    '  [ -z "${STUB_VIEW_FAIL:-}" ] || exit 1\n'
    '  case " ${STUB_CLOSED:-} " in *" $3 "*) echo CLOSED;; *) echo "${STUB_STATE-OPEN}";; esac\n'
    '  exit 0\n'
    'fi\n'
    '[ -z "${STUB_FAIL:-}" ] || exit 1\n'
    'printf "%s" "$STUB_ROWS"\n'
)


def rows(*titles):
    return json.dumps([{"number": n, "title": t, "url": f"u{n}"} for n, t in enumerate(titles, start=1)])


class MainRedOpen(unittest.TestCase):
    def setUp(self):
        self.bin = Path(tempfile.mkdtemp(prefix="ghstub-"))
        self.addCleanup(shutil.rmtree, self.bin, ignore_errors=True)
        self.log = self.bin / "calls"
        stub = self.bin / "gh"
        stub.write_text(STUB, encoding="utf-8", newline="\n")
        stub.chmod(0o755)

    def run_helper(self, listing="[]", args=(), **env_extra):
        env = {**os.environ, "PATH": str(self.bin) + os.pathsep + os.environ["PATH"],
               "STUB_LOG": str(self.log), "STUB_ROWS": listing, **env_extra}
        return subprocess.run([BASH, str(HELPER), *args], capture_output=True, text=True, encoding="utf-8", env=env)

    def test_a_jq_that_does_not_finish_is_not_a_green_main(self):
        # Git Bash reported a tool fed by a process substitution as `Child exited`. A `jq` dying on the
        # count, then only on the title filter, stands in: either is the read failing (exit 2), never "main is green".
        for dies_on in ("length", "-r"):
            with self.subTest(dies_on=dies_on):
                shim = Path(self.bin, "jq")
                shim.write_text(f'#!/usr/bin/env bash\ncase "$1" in {dies_on}) echo "Child exited" >&2; exit 148 ;; esac\n'
                                'PATH=${PATH#*:}\nexec jq "$@"\n', encoding="utf-8", newline="\n")
                shim.chmod(0o755)
                result = self.run_helper(rows("main is red"))
                self.assertEqual(2, result.returncode, result.stderr)
                self.assertIn("Child exited", result.stderr, "the stand-in jq was not the one the helper ran")
                self.assertEqual("", result.stdout)

    def test_no_open_issue_exits_0_printing_nothing(self):
        result = self.run_helper(rows())
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("", result.stdout)

    def test_an_open_bare_title_exits_3(self):
        result = self.run_helper(rows("other", "main is red"))
        self.assertEqual(3, result.returncode, result.stderr)
        self.assertEqual("main is red: #2 u2\n", result.stdout)

    def test_a_workflow_suffixed_title_exits_3(self):
        result = self.run_helper(rows("main is red: ci"))
        self.assertEqual(3, result.returncode, result.stderr)
        self.assertEqual("main is red: #1 u1\n", result.stdout)

    def test_every_open_one_is_named(self):
        # Backend keeps one per watched workflow, and step 6's exemption asks whether its own number is the only one.
        result = self.run_helper(rows("main is red: CI", "main is red: Helm"))
        self.assertEqual(3, result.returncode, result.stderr)
        self.assertEqual("main is red: #1 u1\nmain is red: #2 u2\n", result.stdout)

    def test_an_issue_the_search_lists_but_is_closed_exits_0(self):
        # The search index lags a closure: the listing still has the issue, its own state says CLOSED.
        result = self.run_helper(rows("main is red"), STUB_CLOSED="1")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("", result.stdout)

    def test_only_the_still_open_one_is_named(self):
        result = self.run_helper(rows("main is red: CI", "main is red: Helm"), STUB_CLOSED="1")
        self.assertEqual(3, result.returncode, result.stderr)
        self.assertEqual("main is red: #2 u2\n", result.stdout)

    def test_a_failed_state_read_exits_2(self):
        result = self.run_helper(rows("main is red"), STUB_VIEW_FAIL="1")
        self.assertEqual(2, result.returncode)
        self.assertIn("cannot read the state of #1", result.stderr)
        self.assertEqual("", result.stdout)

    def test_a_state_that_is_neither_open_nor_closed_exits_2(self):
        result = self.run_helper(rows("main is red"), STUB_STATE="")
        self.assertEqual(2, result.returncode)
        self.assertIn("state of #1 reads ''", result.stderr)
        self.assertEqual("", result.stdout)

    def test_a_title_that_only_contains_the_phrase_exits_0(self):
        # The search is full text, so it returns these. The last has the words and no separator.
        result = self.run_helper(rows("why main is red yesterday", "Fix: main is red", "main is reddish", "main is red:"))
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("", result.stdout)

    def test_a_failed_listing_exits_2(self):
        result = self.run_helper(STUB_FAIL="1")
        self.assertEqual(2, result.returncode)
        self.assertEqual("", result.stdout)

    def test_a_full_page_exits_2(self):
        result = self.run_helper(rows(*["other"] * 100))
        self.assertEqual(2, result.returncode)
        self.assertEqual("", result.stdout)

    def test_a_listing_that_is_not_json_exits_2(self):
        # Green is exit 0 alone, so what `gh` printed must have been read.
        result = self.run_helper("<html>rate limited</html>")
        self.assertEqual(2, result.returncode)
        self.assertEqual("", result.stdout)

    def test_a_stalled_listing_exits_2_at_the_bound(self):
        stub = self.bin / "gh"
        stub.write_text("#!/usr/bin/env bash\nexec sleep 30\n", encoding="utf-8", newline="\n")
        result = self.run_helper(GH_READ_BOUND_SECONDS="1")
        self.assertEqual(2, result.returncode)
        self.assertIn("did not answer within 1 s", result.stderr)

    def test_an_argument_is_refused_before_gh_runs(self):
        # An argument is how a field set widens, and the grant is bare so that none reaches the helper.
        result = self.run_helper(args=("--json",))
        self.assertEqual(2, result.returncode)
        self.assertFalse(self.log.exists())

    def test_it_reads_open_bugs_with_no_field_beyond_the_three(self):
        self.run_helper(rows())
        call = self.log.read_text(encoding="utf-8")
        for part in ("issue list ", "--state open", "--label bug", "--json number,title,url"):
            with self.subTest(part=part):
                self.assertIn(part, call)

    def board_read(self, listing="[]", **env_extra):
        # `dispatch_board.main_red` over the real helper at its real path, as a `wake` runs it in a target.
        env = {"PATH": str(self.bin) + os.pathsep + os.environ["PATH"], "STUB_LOG": str(self.log),
               "STUB_ROWS": listing, **env_extra}
        with mock.patch.dict(os.environ, env):
            return db.main_red(str(SCRIPTS.parent.parent))

    def test_the_board_reads_a_green_main_as_no_line(self):
        self.assertEqual([], self.board_read(rows("other")))

    def test_the_board_reads_each_open_one_as_a_line(self):
        self.assertEqual(["main is red: #1 u1", "main is red: #2 u2"],
                         self.board_read(rows("main is red: CI", "main is red: Helm")))

    def test_the_board_refuses_on_exit_2_and_never_reads_it_green(self):
        with self.assertRaisesRegex(db.Refusal, "main-red-open.sh exited 2: cannot list the open issues"):
            self.board_read(STUB_FAIL="1")

    def test_the_board_refuses_any_other_answer_too(self):
        # A stubbed child stands for a helper a later edit changes: only 0 with nothing and 3 with a line are answers.
        for answer in ((0, "main is red: #1 u", ""), (3, "", ""), (1, "", "boom"), (127, "", "no such file")):
            with self.subTest(answer=answer), mock.patch.object(db, "_child", return_value=answer):
                with self.assertRaisesRegex(db.Refusal, f"main-red-open.sh exited {answer[0]}"):
                    db.main_red(".")


class ShipReadsIt(unittest.TestCase):
    def test_ship_runs_the_helper_bare(self):
        _, front, body = (COMMANDS / "ship.md").read_text(encoding="utf-8").split("---", 2)
        self.assertIn("Bash(bash .claude/scripts/main-red-open.sh)", front)
        self.assertNotIn("main-red-open.sh:*", front)
        self.assertNotIn("Bash(gh issue list", front)
        self.assertIn("\n   bash .claude/scripts/main-red-open.sh\n", body)

    def test_the_read_follows_the_checks_wait_and_stops_the_chain(self):
        body = (COMMANDS / "ship.md").read_text(encoding="utf-8").split("6. **Merge, then tear the workspace down.**")[1]
        self.assertLess(body.index("gh pr checks <n> --watch"), body.index("bash .claude/scripts/main-red-open.sh"))
        self.assertIn("**An open `main is red` issue is a red check**", body)


if __name__ == "__main__":
    unittest.main()
