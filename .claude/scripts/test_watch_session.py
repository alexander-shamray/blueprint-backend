import contextlib
import io
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import watch_session as ws


class RenderTests(unittest.TestCase):
    def test_a_table_with_an_emoji_cell_keeps_its_columns_by_display_width(self):
        out = ws.render_table(["| Step | State |", "|---|---|", "| push | ✅ done |", "| CI | ⏳ waiting |"])
        # Top rule, header, header rule, two rows, bottom rule.
        self.assertEqual(6, len(out))
        self.assertEqual(1, len({ws.cell_width(line) for line in out}), out)
        # The column bars sit at the same display offset on every row, the emoji counted as two columns.
        bars = {tuple(ws.cell_width(line[:i]) for i, ch in enumerate(line) if ch in "│┌┬┐├┼┤└┴┘") for line in out}
        self.assertEqual(1, len(bars), out)
        self.assertEqual(2, ws.cell_width("✅"))

    def test_a_wrapped_cell_of_wide_characters_keeps_its_columns(self):
        # Wrapped by display columns: `textwrap` counts characters, so a line of two-column ones overruns the cell.
        wide = " ".join(["漢字漢字漢字"] * 30)
        with mock.patch.object(ws, "WIDTH", 60):
            out = ws.render_table(["| Step | Note |", "|---|---|", f"| push | {wide} |"])
        self.assertGreater(len(out), 6, out)
        self.assertEqual(1, len({ws.cell_width(line) for line in out}), out)

    def test_a_bash_call_shows_its_description_else_its_command_without_the_cd(self):
        self.assertEqual("Show working tree status",
                         ws.tool_summary("Bash", {"command": "git status", "description": "Show working tree status"}))
        self.assertEqual("dotnet build Platform.slnx",
                         ws.tool_summary("Bash", {"command": "cd C:/dev/x && dotnet build Platform.slnx"}))


class CleanTests(unittest.TestCase):
    HOSTILE = "fix \x1b]0;pwned\x07 the \x1b[2Jgrant\x9b31m\x00\x08 now"

    def assert_inert(self, text):
        for bad in ("\x1b", "\x07", "\x9b", "\x00", "\x08", "pwned"):
            self.assertNotIn(bad, text)

    def test_a_description_carrying_escape_sequences_prints_neither_esc_nor_bel(self):
        summary = ws.tool_summary("Bash", {"command": "x", "description": self.HOSTILE})
        self.assert_inert(summary)
        self.assertEqual("fix  the grant now", summary)
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            ws.show({"type": "assistant", "message": {"content": [
                {"type": "tool_use", "name": "Bash\x1b[2J", "input": {"description": self.HOSTILE}},
                {"type": "text", "text": f"| a | {self.HOSTILE} |\n|---|---|\n| b | c |\n\n{self.HOSTILE}"},
                {"type": "tool_result", "is_error": True, "content": [{"type": "text", "text": self.HOSTILE}]}]}})
        printed = out.getvalue()
        # The follower's own colours are the only escapes left: none of the transcript's reaches the terminal.
        self.assertNotIn("\x1b]", printed)
        self.assertNotIn("\x07", printed)
        self.assertNotIn("\x1b[2J", printed)
        self.assertNotIn("pwned", printed)
        self.assertEqual("a\tb\nc", ws.clean("a\tb\nc"))
        self.assertEqual("ab", ws.clean("a\tb\n", keep=""))

    def test_a_text_part_with_null_text_is_skipped(self):
        with contextlib.redirect_stdout(io.StringIO()) as out:
            ws.show({"type": "assistant", "message": {"content": [{"type": "text", "text": None},
                                                                  {"type": "tool_use", "name": "Read", "input": []}]}})
        self.assertIn("Read", out.getvalue())

    def test_no_argument_prints_the_usage_and_exits_2(self):
        done = subprocess.run([sys.executable, str(Path(ws.__file__))], capture_output=True)
        self.assertEqual(2, done.returncode)
        self.assertIn("Usage: py -3.12 watch_session.py", done.stderr.decode("utf-8", errors="replace"))


class Stop(Exception):
    pass


class FollowTests(unittest.TestCase):
    def test_the_newest_transcript_is_one_created_after_since_not_an_older_one_still_written(self):
        with tempfile.TemporaryDirectory() as tmp:
            created = {"old.jsonl": 100.0, "new.jsonl": 210.0, "notes.txt": 500.0}
            for name in created:
                Path(tmp, name).write_text("{}\n", encoding="utf-8")
            # The old transcript is the one appended to last; only its creation time may count.
            os.utime(Path(tmp, "old.jsonl"), (900.0, 900.0))
            with mock.patch.object(ws, "created", lambda path: created[os.path.basename(path)]), \
                    mock.patch.object(ws.time, "sleep", side_effect=AssertionError("waited")):
                self.assertEqual(os.path.join(tmp, "new.jsonl"), ws.newest_transcript(tmp, 200.0))

    def test_the_change_time_is_creation_time_only_on_windows(self):
        stat = mock.Mock(spec=["st_ctime"], st_ctime=900.0)
        with mock.patch.object(ws.os, "stat", return_value=stat):
            for name, expected in (("posix", None), ("nt", 900.0)):
                with self.subTest(os=name), mock.patch.object(ws.os, "name", name):
                    self.assertEqual(expected, ws.created("any.jsonl"))
        born = mock.Mock(spec=["st_ctime", "st_birthtime"], st_ctime=900.0, st_birthtime=100.0)
        with mock.patch.object(ws.os, "stat", return_value=born):
            self.assertEqual(100.0, ws.created("any.jsonl"))

    def test_without_a_creation_time_a_transcript_there_before_the_watcher_is_passed_over(self):
        with tempfile.TemporaryDirectory() as tmp:
            Path(tmp, "old.jsonl").write_text("{}\n", encoding="utf-8")

            def a_session_starts(_seconds):
                Path(tmp, "new.jsonl").write_text("{}\n", encoding="utf-8")
                Path(tmp, "old.jsonl").write_text("{}\n{}\n", encoding="utf-8")

            with mock.patch.object(ws, "created", return_value=None), \
                    mock.patch.object(ws.time, "sleep", side_effect=a_session_starts):
                self.assertEqual(os.path.join(tmp, "new.jsonl"), ws.newest_transcript(tmp, 0.0))

    def test_a_transcript_gone_before_it_is_read_is_skipped_rather_than_raised(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.assertIsNone(ws.created(os.path.join(tmp, "gone.jsonl")))
            Path(tmp, "new.jsonl").write_text("{}\n", encoding="utf-8")
            listed = ["gone.jsonl", "new.jsonl"]

            def dated(path):
                return 300.0 if os.path.isfile(path) else None

            with mock.patch.object(ws.os, "listdir", return_value=listed), \
                    mock.patch.object(ws, "created", side_effect=dated), \
                    mock.patch.object(ws.time, "sleep", side_effect=AssertionError("waited")):
                self.assertEqual(os.path.join(tmp, "new.jsonl"), ws.newest_transcript(tmp, 200.0))

    def test_a_line_written_in_two_pieces_is_shown_once_whole(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp, "session.jsonl")
            path.write_text('{"type": "user", "mess', encoding="utf-8")
            shown, polls = [], []

            def the_writer_finishes(_seconds):
                polls.append(1)
                if len(polls) == 1:
                    with open(path, "a", encoding="utf-8") as handle:
                        handle.write('age": {"content": "hi"}}\n')
                else:
                    raise Stop

            with mock.patch.object(ws, "show", side_effect=shown.append), \
                    mock.patch.object(ws.time, "sleep", side_effect=the_writer_finishes), \
                    self.assertRaises(Stop):
                ws.main(str(path))
            self.assertEqual([{"type": "user", "message": {"content": "hi"}}], shown)

    def test_a_character_split_between_two_writes_is_shown_whole(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp, "session.jsonl")
            record = '{"type": "user", "message": {"content": "\u2705 done"}}\n'.encode("utf-8")
            cut = record.index("\u2705".encode("utf-8")) + 1
            path.write_bytes(record[:cut])
            shown, polls = [], []

            def the_writer_finishes(_seconds):
                polls.append(1)
                if len(polls) == 1:
                    with open(path, "ab") as handle:
                        handle.write(record[cut:])
                else:
                    raise Stop

            with mock.patch.object(ws, "show", side_effect=shown.append), \
                    mock.patch.object(ws.time, "sleep", side_effect=the_writer_finishes), \
                    self.assertRaises(Stop):
                ws.main(str(path))
            self.assertEqual([{"type": "user", "message": {"content": "\u2705 done"}}], shown)


if __name__ == "__main__":
    unittest.main()
