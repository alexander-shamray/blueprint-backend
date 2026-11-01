import os
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
