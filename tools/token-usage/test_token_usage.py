"""The report's suite, over transcripts written to a temporary directory.

    cd tools/token-usage && python -m unittest
"""

import contextlib
import io
import json
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import token_usage
from token_usage import MAIN, NO_COMMAND, SUBAGENT, Report


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

    def test_text_the_harness_writes_as_a_user_entry_does_not_end_the_command(self):
        injected = [
            {"type": "user", "isCompactSummary": True, "timestamp": "2026-10-07T09:10:00.000Z",
             "message": {"content": "This session is being continued from a previous conversation..."}},
            prompt("<bash-input>git status</bash-input>", "2026-10-07T09:11:00.000Z"),
            prompt("<bash-stdout>On branch main</bash-stdout><bash-stderr></bash-stderr>", "2026-10-07T09:12:00.000Z"),
            prompt("<local-command-stdout>Compacted</local-command-stdout>", "2026-10-07T09:13:00.000Z"),
            {"type": "user", "timestamp": "2026-10-07T09:13:30.000Z",
             "message": {"content": [{"type": "text", "text": "[Request interrupted by user]"}]}},
        ]
        for entry in injected:
            entry.pop("origin", None)
        self.files.write("s.jsonl", [ship(), *injected, reply("m1", at="2026-10-07T09:14:00.000Z", read=7)])
        rows, _ = self.rows()
        self.assertEqual(rows["/ship", "main"]["cache_read"], 7)
        self.assertNotIn((NO_COMMAND, "main"), rows)

    def test_a_wake_keeps_the_command_and_its_work_is_counted_beside_it(self):
        def wake(at):
            return {"type": "user", "timestamp": at, "origin": {"kind": "task-notification"},
                    "message": {"content": "<task-notification>pending</task-notification>"}}
        self.files.write("s.jsonl", [
            ship(), reply("m1", read=10),
            wake("2026-10-07T11:00:00.000Z"), reply("m2", at="2026-10-07T11:01:00.000Z", read=20),
            wake("2026-10-07T12:00:00.000Z"), reply("m3", at="2026-10-07T12:01:00.000Z", read=40),
            prompt("thanks", "2026-10-07T13:00:00.000Z"), reply("m4", at="2026-10-07T13:01:00.000Z", read=80)])
        self.files.write("s/subagents/agent-w.jsonl", [reply("x1", at="2026-10-07T11:30:00.000Z", read=160)])
        rows, _ = self.rows()
        ship_main = rows["/ship", "main"]
        self.assertEqual((ship_main["cache_read"], ship_main["contexts"], ship_main["woken_equivalent"]), (70, 1, 6))
        self.assertEqual(rows["/ship", "subagent"]["woken_equivalent"], 16)
        plain = rows[NO_COMMAND, "main"]
        self.assertEqual((plain["cache_read"], plain["woken_equivalent"]), (80, 0))
        self.assertEqual(rows["TOTAL", ""]["woken_equivalent"], 22)

    def test_a_harness_command_typed_mid_run_leaves_the_run_its_command(self):
        compact = prompt("<command-name>/compact</command-name>", "2026-10-07T10:00:00.000Z")
        reload = prompt("<command-name>/reload-plugins</command-name>", "2026-10-07T10:30:00.000Z")
        self.files.write("s.jsonl", [
            ship(), reply("m1", read=1),
            compact, reply("m2", at="2026-10-07T10:01:00.000Z", read=2),
            reload, reply("m3", at="2026-10-07T10:31:00.000Z", read=4)])
        rows, _ = self.rows()
        self.assertEqual((rows["/ship", "main"]["cache_read"], rows["/ship", "main"]["contexts"]), (7, 1))
        self.assertEqual(set(rows), {("/ship", "main"), ("TOTAL", "")})

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

    def test_a_skill_loaded_without_a_typed_command_names_the_work_after_it(self):
        loads = {"type": "assistant", "timestamp": "2026-10-07T09:05:00.000Z", "message": {
            "id": "m1", "usage": usage(read=1),
            "content": [{"type": "tool_use", "name": "Skill", "input": {"skill": "bug-sweep"}}]}}
        self.files.write("s.jsonl", [prompt("sweep the tree"), loads, reply("m2", read=2)])
        self.files.write("s/subagents/agent-a5.jsonl", [reply("x5", at="2026-10-07T09:06:00.000Z", read=4)])
        rows, _ = self.rows()
        self.assertEqual(rows["skill:bug-sweep", "main"]["cache_read"], 3)
        self.assertEqual(rows["skill:bug-sweep", "subagent"]["cache_read"], 4)

    def test_a_later_skill_relabels_a_run_a_skill_labelled(self):
        def loads(message_id, at, skill):
            return {"type": "assistant", "timestamp": at, "message": {
                "id": message_id, "usage": usage(read=1),
                "content": [{"type": "tool_use", "name": "Skill", "input": {"skill": skill}}]}}
        self.files.write("s.jsonl", [
            prompt("plan it"), loads("m1", "2026-10-07T09:05:00.000Z", "superpowers:writing-plans"),
            loads("m2", "2026-10-07T10:00:00.000Z", "superpowers:executing-plans"),
            reply("m3", at="2026-10-07T10:01:00.000Z", read=2)])
        rows, _ = self.rows()
        self.assertEqual(rows["skill:superpowers:writing-plans", "main"]["cache_read"], 1)
        self.assertEqual(rows["skill:superpowers:executing-plans", "main"]["cache_read"], 3)

    def test_a_repository_command_loaded_as_a_skill_keeps_the_commands_it_loads(self):
        def loads(message_id, at, skill):
            return {"type": "assistant", "timestamp": at, "message": {
                "id": message_id, "usage": usage(read=1),
                "content": [{"type": "tool_use", "name": "Skill", "input": {"skill": skill}}]}}
        self.files.write("s.jsonl", [
            prompt("ship it"), loads("m1", "2026-10-07T09:05:00.000Z", "ship"),
            loads("m2", "2026-10-07T10:00:00.000Z", "branch"),
            reply("m3", at="2026-10-07T10:01:00.000Z", read=2)])
        rows, _ = self.rows()
        self.assertEqual(rows["skill:ship", "main"]["cache_read"], 4)
        self.assertNotIn(("skill:branch", "main"), rows)

    def test_a_typed_plugin_command_keeps_the_skills_it_loads(self):
        loads = {"type": "assistant", "timestamp": "2026-10-07T09:05:00.000Z", "message": {
            "id": "m1", "usage": usage(read=1),
            "content": [{"type": "tool_use", "name": "Skill", "input": {"skill": "superpowers:writing-plans"}}]}}
        typed = prompt("<command-name>/superpowers:brainstorm</command-name>")
        self.files.write("s.jsonl", [typed, loads, reply("m2", at="2026-10-07T09:06:00.000Z", read=2)])
        rows, _ = self.rows()
        self.assertEqual(rows["/superpowers:brainstorm", "main"]["cache_read"], 3)

    def test_a_skill_loaded_by_a_typed_command_stays_that_command(self):
        loads = {"type": "assistant", "message": {
            "id": "m1", "usage": usage(read=1),
            "content": [{"type": "tool_use", "name": "Skill", "input": {"skill": "branch"}}]}}
        self.files.write("s.jsonl", [ship(), loads, reply("m2", read=2)])
        rows, _ = self.rows()
        self.assertEqual(rows["/ship", "main"]["cache_read"], 3)

    def test_the_span_is_the_first_and_last_day_counted(self):
        self.files.write("s.jsonl", [ship(), reply("m1", at="2026-09-01T00:00:00.000Z"),
                                     reply("m2", at="2026-10-02T00:00:00.000Z")])
        _, report = self.rows()
        self.assertEqual(report.span, ["2026-09-01", "2026-10-02"])

    def test_session_reads_only_the_session_named_with_its_subagents(self):
        self.files.write("aaa1.jsonl", [ship(), reply("m1", read=1)])
        self.files.write("aaa1/subagents/agent-x.jsonl", [reply("x1", at="2026-10-07T09:20:00.000Z", read=2)])
        self.files.write("bbb2.jsonl", [ship(), reply("m2", read=4)])
        report = Report(session="aaa")
        report.read_project(self.root)
        total = report.rows()[-1]
        self.assertEqual((total["cache_read"], total["calls"]), (3, 2))

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

    def test_the_mean_context_is_what_a_call_sent_counting_fresh_input_writes_and_reads(self):
        self.files.write("s.jsonl", [ship(), reply("m1", fresh=10, write_5m=20, write_1h=30, read=140),
                                     reply("m2", read=400), reply("m3", read=0)])
        rows, _ = self.rows()
        self.assertEqual(rows["/ship", "main"]["mean_context"], (10 + 20 + 30 + 140 + 400) // 3)
        self.assertEqual(rows["TOTAL", ""]["mean_context"], 200)

    def test_the_total_sums_every_group(self):
        self.files.write("s.jsonl", [reply("m0", read=1, output=2), ship(), reply("m1", read=4, output=8)])
        rows, _ = self.rows()
        self.assertEqual((rows["TOTAL", ""]["cache_read"], rows["TOTAL", ""]["output"]), (5, 10))


    def test_each_subagent_is_a_spawn_with_its_task_and_its_own_cost(self):
        self.files.write("s.jsonl", [ship(), reply("m1", read=1)])
        self.files.write("s/subagents/agent-a6.jsonl", [reply("x6", at="2026-10-07T09:20:00.000Z", read=10),
                                                        reply("x7", at="2026-10-07T09:21:00.000Z", read=20)])
        (self.root / "s/subagents/agent-a6.meta.json").write_text(
            '{"agentType": "general-purpose", "description": "Review the diff"}')
        _, report = self.rows()
        spawn, = report.spawns
        self.assertEqual((spawn["command"], spawn["agent"], spawn["description"]),
                         ("/ship", "general-purpose", "Review the diff"))
        self.assertEqual((spawn["calls"], spawn["cache_read"], spawn["started"]), (2, 30, "2026-10-07"))


    def test_tool_results_are_counted_per_command_and_tool_in_the_main_session(self):
        calls = {"type": "assistant", "timestamp": "2026-10-07T09:05:00.000Z", "message": {
            "id": "m1", "usage": usage(read=1), "content": [
                {"type": "tool_use", "id": "t1", "name": "Bash", "input": {}},
                {"type": "tool_use", "id": "t2", "name": "Read", "input": {}}]}}
        results = {"type": "user", "timestamp": "2026-10-07T09:06:00.000Z", "message": {"content": [
            {"type": "tool_result", "tool_use_id": "t1", "content": "x" * 40},
            {"type": "tool_result", "tool_use_id": "t2", "content": [{"type": "text", "text": "y" * 8}]}]}}
        sidechain = {**results, "isSidechain": True}
        self.files.write("s.jsonl", [ship(), calls, results, sidechain])
        _, report = self.rows()
        self.assertEqual(report.results, {("/ship", "Bash"): [1, 40], ("/ship", "Read"): [1, 8]})


def call(tool, at="2026-10-07T09:10:00.000Z", cwd="/r/repo", call_id=None, **given):
    return {"type": "assistant", "timestamp": at, "cwd": cwd, "message": {
        "id": call_id or tool + json.dumps(given), "content": [
            {"type": "tool_use", "id": call_id or tool + json.dumps(given), "name": tool, "input": given}]}}


class DocsTests(unittest.TestCase):
    def test_read_and_the_shell_printers_are_reads_and_grep_is_not(self):
        read = token_usage.markdown_read
        self.assertEqual(read("Read", {"file_path": "/r/repo/docs/a.md"}), ["/r/repo/docs/a.md"])
        self.assertEqual(read("Read", {"file_path": "/r/repo/src/A.cs"}), [])
        self.assertEqual(read("Bash", {"command": "cd x && sed -n 1,5p docs/a.md | head -3; cat 'docs/b.md'"}),
                         ["docs/a.md", "docs/b.md"])
        self.assertEqual(read("Bash", {"command": "grep -n x docs/a.md"}), [])
        self.assertEqual(read("PowerShell", {"command": "Get-Content docs\\c.md"}), ["docs\\c.md"])
        self.assertEqual(read("Grep", {"path": "docs/a.md"}), [])
        self.assertEqual(read("Bash", {"command": "sed -i 's/a/b/' docs/a.md; sed --in-place s/a/b/ docs/a.md"}), [])
        self.assertEqual(read("Bash", {"command": "sed -ni p docs/a.md; sed --in-place=.bak p docs/a.md"}), [])
        self.assertEqual(read("Bash", {"command": "sed -n 1p docs/a.md"}), ["docs/a.md"])
        self.assertEqual(read("Bash", {"command": "cat docs/a.md > docs/b.md; cat > docs/c.md <<EOF"}), ["docs/a.md"])

    def test_a_read_is_placed_in_the_checkout_its_worktrees_and_forks_and_nowhere_else(self):
        inside = token_usage.repo_relative
        self.assertEqual(inside("/r/repo/docs/a.md", "/r/repo"), "docs/a.md")
        self.assertEqual(inside("/r/repo/.claude/worktrees/feature/docs/a.md", "/r/repo"), "docs/a.md")
        self.assertEqual(inside("/r/repo-fork/docs/a.md", "/r/repo"), "docs/a.md")
        self.assertIsNone(inside("/r/other/docs/a.md", "/r/repo"))
        self.assertEqual(inside(token_usage.absolute("Docs\\A.md", "c:\\Dev\\Repo"), "C:\\dev\\repo"), "Docs/A.md")
        self.assertEqual(token_usage.absolute("../docs/a.md", "/r/repo/src"), "/r/repo/docs/a.md")

    def test_a_document_is_named_by_path_by_a_relative_link_by_a_unique_name_or_by_a_template(self):
        docs = dict.fromkeys(["docs/a.md", "docs/b.md", "docs/c/README.md", "README.md", "docs/commands/ship.md",
                              "docs/commands/pr.md", "skill/references/x.md", "docs/unique.md", "skill/SKILL.md"], "")
        named = token_usage.named_in
        self.assertEqual(named("skill/SKILL.md", "see docs/a.md and references/x.md", docs),
                         {"docs/a.md", "skill/references/x.md"})
        self.assertEqual(named("x/s.md", "unique.md, but README.md is two files", docs), {"docs/unique.md"})
        self.assertEqual(named("s.md", "docs/commands/<name>.md; <next-file>.md", docs),
                         {"docs/commands/ship.md", "docs/commands/pr.md"})
        dotted = dict.fromkeys([".claude/commands/go.md", ".claude/commands/stop.md", "docs/commands/ship.md"], "")
        self.assertEqual(named("s.md", ".claude/commands/<name>.md", dotted),
                         {".claude/commands/go.md", ".claude/commands/stop.md"})

    def test_each_document_is_grouped_by_who_names_it_and_counted_by_who_opened_it(self):
        docs = {"CLAUDE.md": "docs/claude.md", ".claude/commands/go.md": "read docs/named.md",
                "docs/named.md": "", "docs/claude.md": "", "docs/orphan.md": "", ".claude/notes/x.md": ""}
        reads = {("/r/repo/docs/named.md", MAIN): 2, ("/r/repo/docs/named.md", SUBAGENT): 3,
                 ("/r/repo/CLAUDE.md", MAIN): 9, ("/r/other/docs/orphan.md", MAIN): 4}
        rows = {r["doc"]: r for r in token_usage.doc_rows(docs, reads, "/r/repo")}
        self.assertNotIn("CLAUDE.md", rows)
        self.assertEqual(rows[".claude/commands/go.md"]["named_by"], "entry point")
        self.assertEqual({k: rows["docs/named.md"][k] for k in ("named_by", "reads", "main", "subagents")},
                         {"named_by": "named by entry", "reads": 5, "main": 2, "subagents": 3})
        self.assertEqual(rows["docs/claude.md"]["named_by"], "CLAUDE.md only")
        self.assertEqual((rows["docs/orphan.md"]["named_by"], rows["docs/orphan.md"]["reads"]), ("none", 0))
        self.assertEqual(rows[".claude/notes/x.md"]["named_by"], "none")
        self.assertIn("none: 2 files, 0.0 reads each, 2 never read", token_usage.link_summary(list(rows.values())))


class DocReadTests(unittest.TestCase):
    def test_reads_are_counted_once_per_call_across_main_sidechain_and_subagent_and_after_since(self):
        with tempfile.TemporaryDirectory() as directory:
            files = Transcripts(Path(directory))
            sidechain = {**call("Read", call_id="c2", file_path="/r/repo/docs/a.md"), "isSidechain": True}
            files.write("s.jsonl", [ship(), call("Read", file_path="/r/repo/docs/a.md"),
                                    call("Bash", command="sed -n 1,9p docs/a.md", cwd="/r/repo/.claude"),
                                    call("Read", at="2026-10-01T00:00:00.000Z", file_path="/r/repo/docs/a.md"),
                                    sidechain])
            files.write("s/subagents/agent-a.jsonl", [sidechain, call("Read", call_id="c3",
                                                                      file_path="/r/repo/docs/a.md")])
            report = Report("2026-10-07")
            report.read_project(Path(directory))
        self.assertEqual(report.reads, {("/r/repo/docs/a.md", MAIN): 1, ("/r/repo/.claude/docs/a.md", MAIN): 1,
                                        ("/r/repo/docs/a.md", SUBAGENT): 2})

    def test_docs_lists_every_tracked_markdown_file_of_the_checkout(self):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory).resolve()
            checkout, transcripts = base / "repo", base / "transcripts"
            (checkout / "docs").mkdir(parents=True)
            (checkout / "docs" / "read.md").write_text("", encoding="utf-8")
            (checkout / "docs" / "unread.md").write_text("", encoding="utf-8")
            subprocess.run(["git", "init", "-q"], cwd=checkout, check=True)
            subprocess.run(["git", "add", "."], cwd=checkout, check=True)
            Transcripts(transcripts).write("s.jsonl", [ship(), call("Read", file_path=str(checkout / "docs/read.md"))])
            out = io.StringIO()
            with mock.patch.object(token_usage.Path, "cwd", return_value=checkout), \
                    contextlib.redirect_stdout(out), contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(token_usage.main([str(transcripts), "--docs", "--json"]), 0)
        rows = {r["doc"]: r["reads"] for r in json.loads(out.getvalue())}
        self.assertEqual(rows, {"docs/read.md": 1, "docs/unread.md": 0})


class CommandLineTests(unittest.TestCase):
    def test_a_project_is_named_by_its_path_with_dashes_on_either_platform(self):
        self.assertEqual(token_usage.project_name("/home/user/blueprint-backend"), "-home-user-blueprint-backend")
        self.assertEqual(token_usage.project_name("C:\\dev\\ashamray\\blueprint-backend"),
                         "C--dev-ashamray-blueprint-backend")

    def test_the_default_directory_is_under_the_home_projects_directory(self):
        root = Path.cwd().resolve()
        self.assertEqual(token_usage.default_project(root),
                         Path.home() / ".claude" / "projects" / token_usage.project_name(str(root)))

    def test_the_default_reads_every_worktree_and_sibling_fork_from_the_checkout_or_a_worktree(self):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory).resolve()
            checkout = base / "repo"
            worktree = checkout / ".claude" / "worktrees" / "feature"
            worktree.mkdir(parents=True)
            projects = base / "home" / ".claude" / "projects"
            main = projects / token_usage.project_name(str(checkout))
            beside = [projects / (main.name + suffix) for suffix in ("--claude-worktrees-feature", "-fork")]
            for directory in (main, *beside, projects / "unrelated"):
                directory.mkdir(parents=True)
            with mock.patch.object(token_usage.Path, "home", return_value=base / "home"):
                expected = [main, *beside]
                self.assertEqual(token_usage.default_projects(checkout), expected)
                self.assertEqual(token_usage.default_projects(worktree), expected)

    def test_the_spawns_tools_and_docs_views_cannot_be_asked_for_together(self):
        for views in (["--spawns", "5", "--tools", "5"], ["--tools", "5", "--docs"]):
            with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as refused:
                token_usage.main(views)
            self.assertEqual(refused.exception.code, 2)

    def test_a_missing_directory_exits_2(self):
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(token_usage.main(["/no/such/transcripts"]), 2)

    def test_spawns_lists_the_costliest_first(self):
        with tempfile.TemporaryDirectory() as directory:
            files = Transcripts(Path(directory))
            files.write("s.jsonl", [ship()])
            files.write("s/subagents/agent-a.jsonl", [reply("x1", at="2026-10-07T09:20:00.000Z", read=10)])
            files.write("s/subagents/agent-b.jsonl", [reply("x2", at="2026-10-07T09:20:00.000Z", read=990)])
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                self.assertEqual(token_usage.main([directory, "--spawns", "1", "--json"]), 0)
        spawns = json.loads(out.getvalue())
        self.assertEqual([s["cache_read"] for s in spawns], [990])

    def test_a_description_the_console_cannot_encode_is_replaced_rather_than_fatal(self):
        with tempfile.TemporaryDirectory() as directory:
            files = Transcripts(Path(directory))
            files.write("s.jsonl", [ship()])
            files.write("s/subagents/agent-a.jsonl", [reply("x1", at="2026-10-07T09:20:00.000Z", read=1)])
            (Path(directory) / "s/subagents/agent-a.meta.json").write_text(
                '{"agentType": "Explore", "description": "Check \\u2192 ship"}', encoding="utf-8")
            raw = io.BytesIO()
            console = io.TextIOWrapper(raw, encoding="cp1252")
            with mock.patch.object(token_usage.sys, "stdout", console), contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(token_usage.main([directory, "--spawns", "1"]), 0)
            console.flush()
        self.assertIn(b"Check ? ship", raw.getvalue())

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
