"""Tests for issue_decisions.py: the entry shape, the body edit, and the gh calls a stub records.

The stub wraps a Python recorder: gh.cmd on Windows, where subprocess runs a .cmd found through PATHEXT but not a
shell script, and an executable gh script elsewhere, where the helper looks up the bare name.
"""

import io
import json
import os
import tempfile
import unittest
from contextlib import redirect_stderr
from datetime import date
from pathlib import Path
from unittest import mock

import issue_decisions as ID

RUN = "20261101-220000"
RECORDER = """\
import json
import pathlib
import sys

here = pathlib.Path(__file__).parent
args = sys.argv[1:]
with open(here / "calls.jsonl", "a", encoding="utf-8") as log:
    log.write(json.dumps(args) + "\\n")
# A file swapped after the helper checked it and before gh opened it.
if (here / "swap.json").exists():
    swap = json.loads((here / "swap.json").read_text(encoding="utf-8"))
    pathlib.Path(swap["path"]).write_bytes(swap["text"].encode("utf-8"))


def body_file():
    name = args[args.index("--body-file") + 1]
    return sys.stdin.buffer.read() if name == "-" else pathlib.Path(name).read_bytes()


if args[:2] == ["repo", "view"]:
    print("acme/widgets")
elif args[:2] == ["issue", "view"]:
    body = (here / "body.txt").read_bytes().decode("utf-8")
    sys.stdout.buffer.write(json.dumps({"body": body}, ensure_ascii=False).encode("utf-8"))
elif args[:2] == ["issue", "edit"] and "--body-file" in args:
    (here / "written.bin").write_bytes(body_file())
elif args[:2] == ["issue", "comment"]:
    (here / "comment.bin").write_bytes(body_file())
if (here / "fail").exists() and args[:2] in (["issue", "edit"], ["issue", "close"]):
    sys.stderr.write("boom\\n")
    sys.exit(1)
"""


class StubGh(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.dir = Path(self.tmp.name)
        self.stub = self.dir / "stub"
        self.stub.mkdir()
        (self.stub / "gh_recorder.py").write_text(RECORDER, encoding="utf-8")
        python = __import__("sys").executable
        if os.name == "nt":
            (self.stub / "gh.cmd").write_text(f'@"{python}" "%~dp0gh_recorder.py" %*\r\n', encoding="ascii")
        else:
            script = self.stub / "gh"
            script.write_text(f'#!/bin/sh\nexec "{python}" "$(dirname "$0")/gh_recorder.py" "$@"\n', encoding="ascii")
            script.chmod(0o755)
        patcher = mock.patch.dict(os.environ, {"PATH": str(self.stub) + os.pathsep + os.environ["PATH"]})
        patcher.start()
        self.addCleanup(patcher.stop)
        # The helper resolves a run under its own checkout; here that is the scratch directory.
        root = mock.patch.object(ID, "ROOT", self.dir)
        root.start()
        self.addCleanup(root.stop)
        self.run_path = self.dir / "artifacts" / "dispatch" / RUN
        self.run_path.mkdir(parents=True)
        (self.run_path / "state.json").write_text('{"run": "%s", "groups": {}}\n' % RUN, encoding="utf-8")

    def calls(self):
        log = self.stub / "calls.jsonl"
        if not log.exists():
            return []
        return [json.loads(line) for line in log.read_text(encoding="utf-8").splitlines()]

    def run_main(self, argv):
        err = io.StringIO()
        with redirect_stderr(err):
            code = ID.main(argv)
        return code, err.getvalue()

    def write(self, name, text, under=None):
        """A file the run wrote under its own directory, or under `under` when a case needs one outside it."""
        path = (self.run_path if under is None else under) / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(text.encode("utf-8"))
        return str(path)

    def record(self, number, question, answer, *extra):
        return self.run_main(["record", number, "--question-file", question, "--answer-file", answer, "--run", RUN,
                              *extra])


class Shapes(unittest.TestCase):
    def test_an_entry_has_the_date_the_question_and_the_answer(self):
        self.assertEqual(
            ID.entry(date(2026, 11, 1), "Lift the pause on 1677?", "Yes, after 1671."),
            "- 2026-11-01: Q: Lift the pause on 1677? A: Yes, after 1671.",
        )

    def test_a_multiline_empty_or_heading_part_is_refused(self):
        for question, answer in (
            ("two\nlines", "a"),
            ("q", "two\nlines"),
            ("", "a"),
            ("q", "   "),
            ("# heading", "a"),
            ("q", "## heading"),
            ("x A: y", "a"),
            ("x" * 501, "a"),
            ("q", "x" * 501),
        ):
            with self.subTest(question=question[:12], answer=answer[:12]):
                with self.assertRaises(ID.Refusal):
                    ID.entry(date(2026, 11, 1), question, answer)
        self.assertTrue(ID.ENTRY_RE.fullmatch(ID.entry(date(2026, 11, 1), "x" * 500, "y" * 500)))

    def test_a_question_opening_with_the_answer_marker_is_refused(self):
        with self.assertRaisesRegex(ID.Refusal, "the question holds 'A: '"):
            ID.entry(date(2026, 11, 1), "A: is this the answer?", "no")
        self.assertTrue(ID.entry(date(2026, 11, 1), "AB: a label?", "yes"))

    def test_a_question_closing_on_the_answer_marker_is_refused(self):
        # The entry would read "Q: Pick plan A: A: yes", and a split on the first " A: " answers "A: yes".
        with self.assertRaisesRegex(ID.Refusal, "the question holds 'A: '"):
            ID.entry(date(2026, 11, 1), "Pick plan A:", "yes")
        self.assertTrue(ID.entry(date(2026, 11, 1), "Pick plan A?", "yes"))

    def test_an_empty_body_gets_the_section_with_no_blank_lines_before_it(self):
        line = "- 2026-11-01: Q: q A: a"
        for body in ("", "\n", "  \r\n"):
            with self.subTest(body=body):
                self.assertEqual("## Owner decisions\n\n" + line + "\n", ID.append_decision(body, line))

    def test_the_section_is_appended_when_absent(self):
        line = "- 2026-11-01: Q: q A: a"
        self.assertEqual(
            ID.append_decision("Body text.\n", line), "Body text.\n\n## Owner decisions\n\n" + line + "\n"
        )

    def test_an_entry_extends_the_section_before_the_next_heading(self):
        first, second = "- 2026-11-01: Q: q A: a", "- 2026-11-02: Q: r A: b"
        body = f"Intro\n\n## Owner decisions\n\n{first}\n\n## Notes\n\nTail\n"
        self.assertEqual(
            ID.append_decision(body, second),
            f"Intro\n\n## Owner decisions\n\n{first}\n{second}\n\n## Notes\n\nTail\n",
        )

    def test_an_empty_section_gets_its_first_entry(self):
        self.assertEqual(
            ID.append_decision("## Owner decisions\n", "- 2026-11-01: Q: q A: a"),
            "## Owner decisions\n\n- 2026-11-01: Q: q A: a\n",
        )

    def test_a_body_with_the_section_twice_is_refused(self):
        with self.assertRaises(ID.Refusal):
            ID.append_decision("## Owner decisions\n\ntext\n\n## Owner decisions\n", "- 2026-11-01: Q: q A: a")

    def test_a_label_other_than_question_or_paused_is_refused(self):
        self.assertEqual(ID.unblock_label("question"), "question")
        self.assertEqual(ID.unblock_label("paused"), "paused")
        for label in ("bug", "question,paused", "Question", ""):
            with self.subTest(label=label):
                with self.assertRaises(ID.Refusal):
                    ID.unblock_label(label)


class Cli(StubGh):
    def test_record_reads_then_writes_the_body_with_repo_pinned(self):
        (self.stub / "body.txt").write_bytes(b"Original.\n")
        question = self.write("q.txt", "Lift the pause on 1677?\n")
        answer = self.write("a.txt", "Yes, after 1671.\n")
        code, err = self.record("1677", question, answer, "--date", "2026-11-01")
        self.assertEqual((code, err), (0, ""))
        calls = self.calls()
        self.assertEqual(calls[0], ["repo", "view", "--json", "nameWithOwner", "-q", ".nameWithOwner"])
        self.assertEqual(calls[1], ["issue", "view", "1677", "--repo", "acme/widgets", "--json", "body"])
        self.assertEqual(calls[2], ["issue", "edit", "1677", "--repo", "acme/widgets", "--body-file", "-"])
        self.assertEqual(len(calls), 3)
        self.assertEqual(
            (self.stub / "written.bin").read_bytes().decode("utf-8"),
            "Original.\n\n## Owner decisions\n\n- 2026-11-01: Q: Lift the pause on 1677? A: Yes, after 1671.\n",
        )

    def test_a_russian_answer_with_an_em_dash_reaches_the_body_byte_for_byte(self):
        (self.stub / "body.txt").write_bytes("Текст.\n".encode("utf-8"))
        question = self.write("q.txt", "Снять паузу — после 1671?\n")
        answer = self.write("a.txt", "Да — после 1671, не раньше.\n")
        code, err = self.record("1677", question, answer, "--date", "2026-11-01")
        self.assertEqual((code, err), (0, ""))
        expected = ("Текст.\n\n## Owner decisions\n\n"
                    "- 2026-11-01: Q: Снять паузу — после 1671? A: Да — после 1671, не раньше.\n")
        self.assertEqual((self.stub / "written.bin").read_bytes(), expected.encode("utf-8"))

    def test_a_non_numeric_issue_is_refused_before_gh_runs(self):
        question = self.write("q.txt", "q")
        answer = self.write("a.txt", "a")
        for number in ("abc", "12x", "-1", "0", "1677 --web"):
            with self.subTest(number=number):
                code, err = self.record(number, question, answer)
                self.assertEqual(code, 2)
                self.assertTrue(err.startswith("refused: "))
        self.assertEqual(self.calls(), [])

    def test_a_double_section_body_is_refused_and_nothing_is_written(self):
        (self.stub / "body.txt").write_text("## Owner decisions\n\n## Owner decisions\n", encoding="utf-8")
        code, err = self.record("5", self.write("q", "q"), self.write("a", "a"))
        self.assertEqual(code, 2)
        self.assertFalse(any(call[:2] == ["issue", "edit"] for call in self.calls()))

    def test_unblock_removes_the_label_with_repo_pinned(self):
        self.assertEqual(self.run_main(["unblock", "1677", "paused"]), (0, ""))
        self.assertEqual(
            self.calls()[1], ["issue", "edit", "1677", "--repo", "acme/widgets", "--remove-label", "paused"]
        )
        self.assertEqual(self.run_main(["unblock", "1677", "bug"])[0], 2)

    def test_close_comments_then_closes_as_not_planned(self):
        comment = self.write("interview/9-close.txt", "Нет работы — закрываю.\n")
        self.assertEqual(self.run_main(["close", "9", "--comment-file", comment, "--run", RUN]), (0, ""))
        calls = self.calls()
        self.assertEqual(calls[1], ["issue", "comment", "9", "--repo", "acme/widgets", "--body-file", "-"])
        self.assertEqual((self.stub / "comment.bin").read_bytes(), "Нет работы — закрываю.\n".encode("utf-8"))
        self.assertEqual(
            calls[2], ["issue", "close", "9", "--repo", "acme/widgets", "--reason", "not planned"]
        )

    def test_comment_posts_and_leaves_the_issue_open(self):
        comment = self.write("interview/9-failure.txt", "Tried the hook edit; a deny stopped it.\n")
        self.assertEqual(self.run_main(["comment", "9", "--comment-file", comment, "--run", RUN]), (0, ""))
        calls = self.calls()
        self.assertEqual(calls[1], ["issue", "comment", "9", "--repo", "acme/widgets", "--body-file", "-"])
        self.assertEqual(2, len(calls), "a point of failure closes nothing")

    def test_comment_takes_only_a_file_the_run_wrote_under_its_directory(self):
        outside = self.write("9-failure.txt", "elsewhere", under=self.dir)
        code, err = self.run_main(["comment", "9", "--comment-file", outside, "--run", RUN])
        self.assertEqual(2, code)
        self.assertIn("is not under the run's directory", err)
        self.assertEqual([], self.calls())

    def test_close_takes_only_a_short_comment_the_run_wrote_under_its_directory(self):
        # The comment is posted to GitHub as it stands, so the file is held to the run and to an answer's size.
        cases = {
            "no --run": ([], self.write("interview/a.txt", "No work.")),
            "outside the run": (["--run", RUN], self.write("elsewhere.txt", "No work.", under=self.dir)),
            "a sibling with the run's name as a prefix": (["--run", RUN], self.write(
                f"{RUN}-other.txt", "No work.", under=self.run_path.parent)),
            "over the cap": (["--run", RUN], self.write("interview/b.txt", "x" * (ID.COMMENT_LIMIT + 1))),
            "empty": (["--run", RUN], self.write("interview/c.txt", " \n")),
        }
        for name, (extra, comment) in cases.items():
            with self.subTest(case=name):
                code, _ = self.run_main(["close", "9", "--comment-file", comment, *extra])
                self.assertEqual(code, 2)
                self.assertEqual(self.calls(), [])
        within = self.write("interview/e.txt", "x" * ID.COMMENT_LIMIT)
        self.assertEqual(self.run_main(["close", "9", "--comment-file", within, "--run", RUN]), (0, ""))

    def test_the_run_is_a_name_the_helper_resolves_never_a_directory_the_caller_names(self):
        # A caller-chosen directory would let any file be called the run's; only a run `start` opened is one.
        elsewhere = self.dir / "elsewhere"
        comment = self.write("c.txt", "No work.", under=elsewhere)
        question, answer = self.write("q.txt", "q", under=elsewhere), self.write("a.txt", "a", under=elsewhere)
        unstarted = "20261101-230000"
        (self.run_path.parent / unstarted).mkdir()
        unstarted_comment = self.write("c.txt", "No work.", under=self.run_path.parent / unstarted)
        cases = {
            "a directory for a name": [str(elsewhere), comment],
            "a path-shaped name": ["../../elsewhere", comment],
            "a run start never opened": [unstarted, unstarted_comment],
            "the old --run-dir": None,
        }
        for name, given in cases.items():
            with self.subTest(case=name):
                if given is None:
                    code, err = self.run_main(["close", "9", "--comment-file", comment, "--run-dir", str(elsewhere)])
                    record = self.run_main(["record", "9", "--question-file", question, "--answer-file", answer,
                                            "--run-dir", str(elsewhere)])
                else:
                    run, path = given
                    code, err = self.run_main(["close", "9", "--comment-file", path, "--run", run])
                    record = self.run_main(["record", "9", "--question-file", question, "--answer-file", answer,
                                            "--run", run])
                self.assertEqual((2, 2), (code, record[0]))
                self.assertEqual(self.calls(), [])

    def test_record_reads_its_question_and_answer_only_from_the_run(self):
        # Both reach a public issue body, so a one-line secret elsewhere on disk is never posted as an answer.
        inside = self.write("q.txt", "q")
        outside = self.write("token.txt", "ghp_notarealtoken", under=self.dir)
        for question, answer in ((outside, self.write("a.txt", "a")), (inside, outside)):
            with self.subTest(question=Path(question).name, answer=Path(answer).name):
                code, err = self.record("5", question, answer)
                self.assertEqual(code, 2)
                self.assertIn("is not under the run's directory", err)
        self.assertEqual(self.calls(), [])

    def test_close_posts_the_text_it_checked_even_when_the_file_is_swapped_after(self):
        # gh reopening the path would read whatever is there by then; the checked text goes on stdin instead.
        comment = self.write("interview/9-close.txt", "No work is needed.\n")
        swap = {"path": comment, "text": "x" * (10 * ID.COMMENT_LIMIT)}
        (self.stub / "swap.json").write_text(json.dumps(swap), encoding="utf-8")
        self.assertEqual(self.run_main(["close", "9", "--comment-file", comment, "--run", RUN]), (0, ""))
        self.assertEqual((self.stub / "comment.bin").read_bytes(), b"No work is needed.\n")
        self.assertNotIn(comment, [arg for call in self.calls() for arg in call])

    def test_a_gh_planted_in_the_cwd_is_not_run_and_the_one_on_path_is(self):
        work = self.dir / "work"
        work.mkdir()
        if os.name == "nt":
            (work / "gh.cmd").write_text(f'@echo planted> "{work}\\ran"\r\n', encoding="ascii")
        else:
            planted = work / "gh"
            planted.write_text(f'#!/bin/sh\necho planted > "{work}/ran"\n', encoding="ascii")
            planted.chmod(0o755)
        here = os.getcwd()
        os.chdir(work)
        self.addCleanup(os.chdir, here)
        self.assertEqual(self.run_main(["unblock", "5", "question"]), (0, ""))
        self.assertFalse((work / "ran").exists())
        self.assertEqual(len(self.calls()), 2)
        with mock.patch.dict(os.environ, {"PATH": os.pathsep.join(["", ".", str(work)])}):
            code, err = self.run_main(["unblock", "5", "question"])
        self.assertEqual(code, 3)
        self.assertIn("gh is not on PATH", err)
        self.assertFalse((work / "ran").exists())

    def test_a_question_holding_the_answer_marker_is_refused_by_the_cli(self):
        code, err = self.record("5", self.write("q", "x A: y"), self.write("a", "z"))
        self.assertEqual(code, 2)
        self.assertEqual(self.calls(), [])

    def test_a_missing_run_is_refused_before_gh_runs(self):
        code, err = self.run_main(
            ["record", "5", "--question-file", self.write("q", "q"), "--answer-file", self.write("a", "a"),
             "--run", "20261101-230000"]
        )
        self.assertEqual(code, 2)
        self.assertIn("state.json is missing", err)
        self.assertEqual(self.calls(), [])

    def test_a_failed_edit_exits_3_and_leaves_nothing_in_the_run(self):
        (self.stub / "body.txt").write_text("x\n", encoding="utf-8")
        (self.stub / "fail").write_text("", encoding="utf-8")
        question, answer = self.write("q", "q"), self.write("a", "a")
        before = sorted(p.name for p in self.run_path.iterdir())
        code, err = self.record("5", question, answer)
        self.assertEqual(code, 3)
        self.assertEqual(before, sorted(p.name for p in self.run_path.iterdir()))

    def test_the_default_date_is_today(self):
        (self.stub / "body.txt").write_text("x\n", encoding="utf-8")
        self.record("5", self.write("q", "q"), self.write("a", "a"))
        written = (self.stub / "written.bin").read_text(encoding="utf-8")
        self.assertIn(f"- {date.today().isoformat()}: Q: q A: a", written)

    def test_a_crlf_body_keeps_crlf(self):
        question, answer = self.write("q", "q"), self.write("a", "a")
        (self.stub / "body.txt").write_bytes(b"One.\r\n\r\n## Owner decisions\r\n\r\n- 2026-10-31: Q: a A: b\r\n")
        self.record("5", question, answer, "--date", "2026-11-01")
        self.assertEqual(
            (self.stub / "written.bin").read_bytes(),
            b"One.\r\n\r\n## Owner decisions\r\n\r\n- 2026-10-31: Q: a A: b\r\n- 2026-11-01: Q: q A: a\r\n",
        )
        (self.stub / "body.txt").write_bytes(b"One.\r\n")
        self.record("5", question, answer, "--date", "2026-11-01")
        self.assertEqual(
            (self.stub / "written.bin").read_bytes(),
            b"One.\r\n\r\n## Owner decisions\r\n\r\n- 2026-11-01: Q: q A: a\r\n",
        )

    def test_a_failed_gh_exits_3(self):
        (self.stub / "body.txt").write_text("x\n", encoding="utf-8")
        (self.stub / "fail").write_text("", encoding="utf-8")
        code, err = self.run_main(["unblock", "5", "question"])
        self.assertEqual(code, 3)
        self.assertIn("boom", err)

    def test_a_gh_that_cannot_start_exits_3(self):
        with mock.patch.object(ID.subprocess, "run", side_effect=OSError("exec format error")):
            code, err = self.run_main(["unblock", "5", "question"])
        self.assertEqual(code, 3)
        self.assertIn("gh failed: gh repo view could not start: exec format error", err)

    def test_a_usage_error_exits_2_with_the_refused_prefix(self):
        for argv in ([], ["record", "5"], ["fly", "5"]):
            with self.subTest(argv=argv):
                code, err = self.run_main(argv)
                self.assertEqual(code, 2)
                self.assertIn("refused: the command line does not match the usage above", err)
        self.assertEqual(self.calls(), [])


class PathWhichParity(unittest.TestCase):
    # issue_decisions keeps its own copy so it imports nothing of the dispatcher's; the two must answer alike.
    def test_both_copies_find_the_same_executable_and_skip_the_same_planted_one(self):
        import dispatch_board
        name = "tool.cmd" if os.name == "nt" else "tool"
        with tempfile.TemporaryDirectory() as cwd, tempfile.TemporaryDirectory() as other:
            for folder in (cwd, other):
                (Path(folder) / name).write_text("x", encoding="ascii")
                (Path(folder) / name).chmod(0o755)
            here = os.getcwd()
            os.chdir(cwd)
            # Back out before the directories go: Windows will not remove the working directory.
            try:
                for path in ("", os.pathsep.join(["", ".", cwd]), os.pathsep.join([cwd, other]), other):
                    with self.subTest(path=path), mock.patch.dict(os.environ, {"PATH": path}):
                        self.assertEqual(dispatch_board.path_which("tool"), ID.path_which("tool"))
                with mock.patch.dict(os.environ, {"PATH": other}):
                    self.assertIsNotNone(ID.path_which("tool"))
            finally:
                os.chdir(here)

    def test_both_copies_skip_a_relative_entry_and_search_from_a_filesystem_root(self):
        import dispatch_board
        name = "tool.cmd" if os.name == "nt" else "tool"
        with tempfile.TemporaryDirectory() as parent:
            sub = Path(parent) / "bin"
            sub.mkdir()
            (sub / name).write_text("x", encoding="ascii")
            (sub / name).chmod(0o755)
            sibling = Path(parent) / "cwd"
            sibling.mkdir()
            here = os.getcwd()
            root = sub.anchor
            try:
                for copy in (dispatch_board.path_which, ID.path_which):
                    # The entry resolves outside the working directory, so only the relative-entry rule skips it.
                    os.chdir(sibling)
                    with mock.patch.dict(os.environ, {"PATH": os.path.join(os.pardir, "bin")}):
                        self.assertIsNone(copy("tool"), "a relative PATH entry is not searched")
                    # At a filesystem root `here` already ends in the separator, so appending one more matched
                    # nothing and a file below the root was taken as outside it.
                    os.chdir(root)
                    with mock.patch.dict(os.environ, {"PATH": str(sub)}):
                        self.assertIsNone(copy("tool"), "a file below a root working directory is skipped")
            finally:
                os.chdir(here)


if __name__ == "__main__":
    unittest.main()
