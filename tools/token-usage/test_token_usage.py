"""The report's suite, over transcripts written to a temporary directory.

    cd tools/token-usage && python -m unittest
"""

import contextlib
import io
import json
import tempfile
import unittest
from pathlib import Path

import token_usage
from token_usage import NO_COMMAND, Report


def usage(read=0, write_5m=0, write_1h=0, output=0, fresh=0):
    return {
        "input_tokens": fresh, "cache_read_input_tokens": read, "output_tokens": output,
        "cache_creation_input_tokens": write_5m + write_1h,
        "cache_creation": {"ephemeral_5m_input_tokens": write_5m, "ephemeral_1h_input_tokens": write_1h},
    }


def reply(message_id, at="2026-10-07T10:00:00.000Z", **tokens):
    return {"type": "assistant", "timestamp": at, "message": {"id": message_id, "usage": usage(**tokens)}}


def prompt(text, at="2026-10-07T09:00:00.000Z"):
    return {"type": "user", "timestamp": at, "origin": {"kind": "human"}, "message": {"content": text}}


def ship(at="2026-10-07T09:00:00.000Z"):
    return prompt("<command-message>ship</command-message>\n<command-name>/ship</command-name>", at)


def tool_result(at="2026-10-07T09:30:00.000Z", **result):
    return {"type": "user", "timestamp": at, "toolUseResult": result,
            "message": {"content": [{"type": "tool_result", "tool_use_id": "t1", "content": "done"}]}}


class Transcripts:
    def __init__(self, root: Path):
        self.root = root

    def write(self, relative, entries, extra_lines=()):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        lines = [json.dumps(e) for e in entries] + list(extra_lines)
        path.write_text("\n".join(lines) + "\n", encoding="utf-8")
        return path


class ReportTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.files = Transcripts(self.root)

    def rows(self, since=None):
        report = Report(since)
        report.read_project(self.root)
        return {(r["command"], r["agent"]): r for r in report.rows()}, report

    def test_a_response_written_once_per_block_is_counted_once(self):
        self.files.write("s.jsonl", [ship(), reply("m1", read=100), reply("m1", read=100), reply("m2", read=50)])
        rows, _ = self.rows()
        self.assertEqual(rows["/ship", "main"]["calls"], 2)
        self.assertEqual(rows["/ship", "main"]["cache_read"], 150)

    def test_usage_before_any_prompt_and_after_a_plain_prompt_has_no_command(self):
        self.files.write("s.jsonl", [reply("m0", read=1), ship(), reply("m1", read=2),
                                     prompt("thanks", "2026-10-07T11:00:00.000Z"), reply("m2", read=4)])
        rows, _ = self.rows()
        self.assertEqual(rows[NO_COMMAND, "main"]["cache_read"], 5)
        self.assertEqual(rows["/ship", "main"]["cache_read"], 2)

    def test_a_tool_result_does_not_end_the_command(self):
        self.files.write("s.jsonl", [ship(), tool_result(), reply("m1", read=7)])
        rows, _ = self.rows()
        self.assertEqual(rows["/ship", "main"]["cache_read"], 7)

    def test_each_prompt_is_one_context(self):
        self.files.write("s.jsonl", [ship(), reply("m1"), ship("2026-10-07T12:00:00.000Z"),
                                     reply("m2", at="2026-10-07T12:01:00.000Z")])
        rows, _ = self.rows()
        self.assertEqual(rows["/ship", "main"]["contexts"], 2)

    def test_a_subagent_takes_its_type_from_its_meta_file_and_its_command_from_its_start(self):
        self.files.write("s.jsonl", [ship(), reply("m1"), prompt("other", "2026-10-07T13:00:00.000Z")])
        self.files.write("s/subagents/agent-a1.jsonl", [
            {"type": "user", "isSidechain": True, "timestamp": "2026-10-07T09:10:00.000Z",
             "message": {"content": "task"}},
            reply("x1", at="2026-10-07T09:11:00.000Z", read=30, write_5m=10)])
        (self.root / "s/subagents/agent-a1.meta.json").write_text('{"agentType": "bug-auditor"}')
        rows, _ = self.rows()
        self.assertEqual(rows["/ship", "bug-auditor"]["cache_read"], 30)
        self.assertEqual(rows["/ship", "bug-auditor"]["cache_write"], 10)

    def test_a_subagent_without_a_meta_file_takes_its_type_from_the_parents_tool_result(self):
        self.files.write("s.jsonl", [ship(), tool_result(agentId="a2", agentType="Explore")])
        self.files.write("s/subagents/agent-a2.jsonl", [reply("x2", at="2026-10-07T09:20:00.000Z", read=3)])
        self.files.write("s/subagents/agent-a3.jsonl", [reply("x3", at="2026-10-07T09:20:00.000Z", read=5)])
        rows, _ = self.rows()
        self.assertEqual(rows["/ship", "Explore"]["cache_read"], 3)
        self.assertEqual(rows["/ship", "subagent"]["cache_read"], 5)

    def test_a_sidechain_inside_the_main_transcript_is_a_subagent(self):
        self.files.write("s.jsonl", [ship(), {**reply("x4", read=9), "isSidechain": True, "agentId": "a4"}])
        rows, _ = self.rows()
        self.assertEqual(rows["/ship", "subagent"]["cache_read"], 9)

    def test_since_drops_earlier_responses(self):
        self.files.write("s.jsonl", [ship(), reply("m1", at="2026-10-01T00:00:00.000Z", read=1),
                                     reply("m2", at="2026-10-07T00:00:00.000Z", read=2)])
        rows, _ = self.rows(since="2026-10-05")
        self.assertEqual(rows["/ship", "main"]["cache_read"], 2)

    def test_an_unreadable_line_is_skipped_and_counted(self):
        self.files.write("s.jsonl", [ship(), reply("m1", read=1)], extra_lines=["{not json"])
        rows, report = self.rows()
        self.assertEqual(report.skipped, 1)
        self.assertEqual(rows["TOTAL", ""]["cache_read"], 1)

    def test_the_input_equivalent_prices_writes_and_reads_against_fresh_input(self):
        self.files.write("s.jsonl", [ship(), reply("m1", fresh=10, write_5m=100, write_1h=100, read=1000)])
        rows, _ = self.rows()
        self.assertEqual(rows["/ship", "main"]["input_equivalent"], 10 + 125 + 200 + 100)

    def test_the_total_sums_every_group(self):
        self.files.write("s.jsonl", [reply("m0", read=1, output=2), ship(), reply("m1", read=4, output=8)])
        rows, _ = self.rows()
        self.assertEqual((rows["TOTAL", ""]["cache_read"], rows["TOTAL", ""]["output"]), (5, 10))


class CommandLineTests(unittest.TestCase):
    def test_the_default_directory_is_the_checkout_path_with_dashes(self):
        found = token_usage.default_project(Path("/home/user/blueprint-backend"))
        self.assertEqual(found, Path.home() / ".claude/projects/-home-user-blueprint-backend")

    def test_a_missing_directory_exits_2(self):
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(token_usage.main(["/no/such/transcripts"]), 2)

    def test_json_prints_the_rows(self):
        with tempfile.TemporaryDirectory() as directory:
            Transcripts(Path(directory)).write("s.jsonl", [ship(), reply("m1", read=3)])
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                self.assertEqual(token_usage.main([directory, "--json"]), 0)
        rows = json.loads(out.getvalue())
        self.assertEqual(rows[0]["command"], "/ship")
        self.assertEqual(rows[-1]["command"], "TOTAL")


if __name__ == "__main__":
    unittest.main()
