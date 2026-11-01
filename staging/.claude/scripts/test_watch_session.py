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


class FollowTests(unittest.TestCase):
    def test_the_newest_transcript_is_one_created_after_since_not_an_older_one_still_written(self):
        with tempfile.TemporaryDirectory() as tmp:
            created = {"old.jsonl": 100.0, "new.jsonl": 210.0, "notes.txt": 500.0}
            for name in created:
                Path(tmp, name).write_text("{}\n", encoding="utf-8")
            # The old transcript is the one appended to last; only its creation time may count.
            os.utime(Path(tmp, "old.jsonl"), (900.0, 900.0))
            with mock.patch.object(ws.os.path, "getctime", lambda path: created[os.path.basename(path)]), \
                    mock.patch.object(ws.time, "sleep", side_effect=AssertionError("waited")):
                self.assertEqual(os.path.join(tmp, "new.jsonl"), ws.newest_transcript(tmp, 200.0))


if __name__ == "__main__":
    unittest.main()
