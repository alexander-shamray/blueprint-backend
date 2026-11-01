"""Tests for issue_decisions.py: the entry shape, the body edit, and the gh calls a stub records.

The stub is a gh.cmd wrapper around a Python recorder, because the helper is Python on Windows and subprocess runs
a .cmd found through PATHEXT but not a shell script.
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

RECORDER = """\
import json
import pathlib
import sys

here = pathlib.Path(__file__).parent
args = sys.argv[1:]
with open(here / "calls.jsonl", "a", encoding="utf-8") as log:
    log.write(json.dumps(args) + "\\n")
if args[:2] == ["repo", "view"]:
    print("acme/widgets")
elif args[:2] == ["issue", "view"]:
    body = (here / "body.txt").read_bytes().decode("utf-8")
    sys.stdout.buffer.write(json.dumps({"body": body}, ensure_ascii=False).encode("utf-8"))
elif args[:2] == ["issue", "edit"] and "--body-file" in args:
    (here / "written.bin").write_bytes(pathlib.Path(args[args.index("--body-file") + 1]).read_bytes())
elif args[:2] == ["issue", "comment"]:
    (here / "comment.bin").write_bytes(pathlib.Path(args[args.index("--body-file") + 1]).read_bytes())
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
        (self.stub / "gh.cmd").write_text(
            f'@"{__import__("sys").executable}" "%~dp0gh_recorder.py" %*\r\n', encoding="ascii"
        )
        patcher = mock.patch.dict(os.environ, {"PATH": str(self.stub) + os.pathsep + os.environ["PATH"]})
        patcher.start()
        self.addCleanup(patcher.stop)

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

    def write(self, name, text):
        path = self.dir / name
        path.write_bytes(text.encode("utf-8"))
        return str(path)


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
        code, err = self.run_main(
            ["record", "1677", "--question-file", question, "--answer-file", answer, "--date", "2026-11-01",
             "--run-dir", str(self.dir)]
        )
        self.assertEqual((code, err), (0, ""))
        calls = self.calls()
        self.assertEqual(calls[0], ["repo", "view", "--json", "nameWithOwner", "-q", ".nameWithOwner"])
        self.assertEqual(calls[1], ["issue", "view", "1677", "--repo", "acme/widgets", "--json", "body"])
        self.assertEqual(calls[2][:5], ["issue", "edit", "1677", "--repo", "acme/widgets"])
        self.assertEqual(calls[2][5], "--body-file")
        self.assertEqual(len(calls), 3)
        self.assertEqual(
            (self.stub / "written.bin").read_bytes().decode("utf-8"),
            "Original.\n\n## Owner decisions\n\n- 2026-11-01: Q: Lift the pause on 1677? A: Yes, after 1671.\n",
        )
        self.assertFalse(Path(calls[2][6]).exists())

    def test_a_russian_answer_with_an_em_dash_reaches_the_body_byte_for_byte(self):
        (self.stub / "body.txt").write_bytes("Текст.\n".encode("utf-8"))
        question = self.write("q.txt", "Снять паузу — после 1671?\n")
        answer = self.write("a.txt", "Да — после 1671, не раньше.\n")
        code, err = self.run_main(
            ["record", "1677", "--question-file", question, "--answer-file", answer, "--date", "2026-11-01"]
        )
        self.assertEqual((code, err), (0, ""))
        expected = "Текст.\n\n## Owner decisions\n\n- 2026-11-01: Q: Снять паузу — после 1671? A: Да — после 1671, не раньше.\n"
        self.assertEqual((self.stub / "written.bin").read_bytes(), expected.encode("utf-8"))

    def test_a_non_numeric_issue_is_refused_before_gh_runs(self):
        question = self.write("q.txt", "q")
        answer = self.write("a.txt", "a")
        for number in ("abc", "12x", "-1", "0", "1677 --web"):
            with self.subTest(number=number):
                code, err = self.run_main(["record", number, "--question-file", question, "--answer-file", answer])
                self.assertEqual(code, 2)
                self.assertTrue(err.startswith("refused: "))
        self.assertEqual(self.calls(), [])

    def test_a_double_section_body_is_refused_and_nothing_is_written(self):
        (self.stub / "body.txt").write_text("## Owner decisions\n\n## Owner decisions\n", encoding="utf-8")
        code, err = self.run_main(
            ["record", "5", "--question-file", self.write("q", "q"), "--answer-file", self.write("a", "a")]
        )
        self.assertEqual(code, 2)
        self.assertFalse(any(call[:2] == ["issue", "edit"] for call in self.calls()))

    def test_unblock_removes_the_label_with_repo_pinned(self):
        self.assertEqual(self.run_main(["unblock", "1677", "paused"]), (0, ""))
        self.assertEqual(
            self.calls()[1], ["issue", "edit", "1677", "--repo", "acme/widgets", "--remove-label", "paused"]
        )
        self.assertEqual(self.run_main(["unblock", "1677", "bug"])[0], 2)

    def test_close_comments_then_closes_as_not_planned(self):
        (self.dir / "run").mkdir()
        comment = self.write("run/c.txt", "Нет работы — закрываю.\n")
        run = str(self.dir / "run")
        self.assertEqual(self.run_main(["close", "9", "--comment-file", comment, "--run-dir", run]), (0, ""))
        calls = self.calls()
        self.assertEqual(calls[1], ["issue", "comment", "9", "--repo", "acme/widgets", "--body-file", comment])
        self.assertEqual(
            calls[2], ["issue", "close", "9", "--repo", "acme/widgets", "--reason", "not planned"]
        )

    def test_close_takes_only_a_short_comment_the_run_wrote_under_its_directory(self):
        # The comment is posted to GitHub as it stands, so the file is held to the run and to an answer's size.
        run = self.dir / "run"
        (run / "interview").mkdir(parents=True)
        cases = {
            "no --run-dir": ([], self.write("run/interview/a.txt", "No work.")),
            "outside the run": (["--run-dir", str(run)], self.write("elsewhere.txt", "No work.")),
            "a sibling with the run's name as a prefix": (["--run-dir", str(run)],
                                                          self.write("run-other.txt", "No work.")),
            "over the cap": (["--run-dir", str(run)],
                             self.write("run/interview/b.txt", "x" * (ID.COMMENT_LIMIT + 1))),
            "empty": (["--run-dir", str(run)], self.write("run/interview/c.txt", " \n")),
            "a missing run dir": (["--run-dir", str(self.dir / "nope")], self.write("run/interview/d.txt", "ok")),
        }
        for name, (extra, comment) in cases.items():
            with self.subTest(case=name):
                code, _ = self.run_main(["close", "9", "--comment-file", comment, *extra])
                self.assertEqual(code, 2)
                self.assertEqual(self.calls(), [])
        within = self.write("run/interview/e.txt", "x" * ID.COMMENT_LIMIT)
        self.assertEqual(self.run_main(["close", "9", "--comment-file", within, "--run-dir", str(run)]), (0, ""))

    def test_a_gh_planted_in_the_cwd_is_not_run_and_the_one_on_path_is(self):
        work = self.dir / "work"
        work.mkdir()
        (work / "gh.cmd").write_text(f'@echo planted> "{work}\\ran"\r\n', encoding="ascii")
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
        code, err = self.run_main(
            ["record", "5", "--question-file", self.write("q", "x A: y"), "--answer-file", self.write("a", "z")]
        )
        self.assertEqual(code, 2)
        self.assertEqual(self.calls(), [])

    def test_a_missing_run_dir_is_refused_before_gh_runs(self):
        code, err = self.run_main(
            ["record", "5", "--question-file", self.write("q", "q"), "--answer-file", self.write("a", "a"),
             "--run-dir", str(self.dir / "nope")]
        )
        self.assertEqual(code, 2)
        self.assertIn("--run-dir", err)
        self.assertEqual(self.calls(), [])

    def test_a_failed_edit_exits_3_and_leaves_no_temp_file(self):
        (self.stub / "body.txt").write_text("x\n", encoding="utf-8")
        (self.stub / "fail").write_text("", encoding="utf-8")
        run = self.dir / "run"
        run.mkdir()
        code, err = self.run_main(
            ["record", "5", "--question-file", self.write("q", "q"), "--answer-file", self.write("a", "a"),
             "--run-dir", str(run)]
        )
        self.assertEqual(code, 3)
        self.assertEqual(list(run.iterdir()), [])

    def test_the_default_date_is_today(self):
        (self.stub / "body.txt").write_text("x\n", encoding="utf-8")
        self.run_main(["record", "5", "--question-file", self.write("q", "q"), "--answer-file", self.write("a", "a")])
        written = (self.stub / "written.bin").read_text(encoding="utf-8")
        self.assertIn(f"- {date.today().isoformat()}: Q: q A: a", written)

    def test_a_crlf_body_keeps_crlf(self):
        args = ["--question-file", self.write("q", "q"), "--answer-file", self.write("a", "a"), "--date", "2026-11-01"]
        (self.stub / "body.txt").write_bytes(b"One.\r\n\r\n## Owner decisions\r\n\r\n- 2026-10-31: Q: a A: b\r\n")
        self.run_main(["record", "5", *args])
        self.assertEqual(
            (self.stub / "written.bin").read_bytes(),
            b"One.\r\n\r\n## Owner decisions\r\n\r\n- 2026-10-31: Q: a A: b\r\n- 2026-11-01: Q: q A: a\r\n",
        )
        (self.stub / "body.txt").write_bytes(b"One.\r\n")
        self.run_main(["record", "5", *args])
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


if __name__ == "__main__":
    unittest.main()
