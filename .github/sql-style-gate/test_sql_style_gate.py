"""The SQL style gate: each refusal with its positive control, then reads of the real tree."""

from __future__ import annotations

import subprocess
import tempfile
import unittest
from pathlib import Path

import sql_style_gate as gate


def messages(path: str, text: str) -> list[str]:
    return [finding.message for finding in gate.judge(path, text)[0]]


class BareJoin(unittest.TestCase):
    def test_a_bare_join_is_refused(self) -> None:
        self.assertEqual(
            messages("A.cs", "FROM a\nJOIN b\n    ON b.Id = a.Id"),
            ["a bare JOIN: name its kind, INNER JOIN or LEFT JOIN"])

    def test_a_bare_join_after_a_table_alias_is_refused(self) -> None:
        self.assertEqual(
            messages("A.cs", '"FROM sys.tables t JOIN sys.schemas s"'),
            ["a bare JOIN: name its kind, INNER JOIN or LEFT JOIN"])

    def test_every_named_kind_passes(self) -> None:
        for kind in ("INNER", "LEFT", "RIGHT", "FULL"):
            with self.subTest(kind=kind):
                self.assertEqual(messages("A.cs", f"FROM a\n{kind} JOIN b\n    ON b.Id = a.Id"), [])

    def test_outer_join_is_refused_and_keeps_its_kind(self) -> None:
        for kind in ("LEFT", "RIGHT", "FULL"):
            with self.subTest(kind=kind):
                self.assertEqual(
                    messages("A.cs", f"FROM a\n{kind} OUTER JOIN b\n    ON b.Id = a.Id"),
                    [f"{kind} OUTER JOIN: write {kind} JOIN, with OUTER left out"])


class OnOnItsOwnLine(unittest.TestCase):
    def test_an_on_on_the_join_line_is_refused(self) -> None:
        for kind in ("INNER", "LEFT"):
            with self.subTest(kind=kind):
                self.assertEqual(
                    messages("A.cs", f"{kind} JOIN b ON b.Id = a.Id"),
                    [f"{kind} JOIN: put its ON on its own line"])

    def test_a_rowset_source_is_judged_as_a_table_is(self) -> None:
        self.assertEqual(
            messages("A.cs", "INNER JOIN OPENJSON(@Ids) WITH (Id BIGINT '$') AS d ON d.Id = o.Id"),
            ["INNER JOIN: put its ON on its own line"])
        self.assertEqual(messages("A.cs", "INNER JOIN OPENJSON(@Ids) WITH (Id BIGINT '$') AS d\n    ON d.Id = o.Id"), [])

    def test_an_interpolated_or_variable_source_is_a_join(self) -> None:
        for source in ("{table} marker", "@Newest AS n", "#staged s", "[dbo].[T] t", "(VALUES (1)) AS v(x)"):
            with self.subTest(source=source):
                self.assertEqual(gate.judge("A.cs", f"INNER JOIN {source}\n    ON 1 = 1")[1], 1)
                self.assertEqual(len(messages("A.cs", f"JOIN {source}")), 1)

    def test_cross_join_and_apply_take_no_on_and_pass(self) -> None:
        self.assertEqual(messages("A.cs", "FROM sys.all_objects a CROSS JOIN sys.all_objects b;"), [])
        self.assertEqual(messages("A.cs", "CROSS APPLY OPENJSON(o.ErasedUnder) AS u"), [])


class AndAlignsWithTheOn(unittest.TestCase):
    JOINED = "INNER JOIN sys.columns c\n"

    def test_an_on_line_packing_an_and_is_refused(self) -> None:
        for connector in ("AND", "OR"):
            with self.subTest(connector=connector):
                self.assertEqual(
                    messages("A.cs", f"{self.JOINED}    ON c.object_id = i.object_id {connector} c.column_id = 1"),
                    [f"an ON line packs a second predicate: give the {connector} its own line"])

    def test_a_rowset_source_wrapping_before_its_on_is_still_judged(self) -> None:
        self.assertEqual(
            messages("A.cs", "INNER JOIN OPENJSON(@Ids)\n    WITH (Id BIGINT '$') AS d\n    ON d.Id = o.Id AND d.Id > 0"),
            ["an ON line packs a second predicate: give the AND its own line"])

    def test_an_and_on_its_own_line_passes(self) -> None:
        self.assertEqual(messages("A.cs", f"{self.JOINED}    ON c.object_id = i.object_id\n    AND c.column_id = 1"), [])

    def test_an_and_inside_parentheses_or_quotes_is_one_predicate(self) -> None:
        self.assertEqual(messages("A.cs", f"{self.JOINED}    ON t.Id = s.Id\n    AND (s.A IS NULL OR s.A < 1)"), [])
        self.assertEqual(messages("A.cs", f"{self.JOINED}    ON (t.A = 1 AND t.B = 2)"), [])
        self.assertEqual(messages("A.cs", f"{self.JOINED}    ON t.Kind = 'A AND B'"), [])

    def test_a_merge_on_is_not_the_join_rule_and_is_not_judged(self) -> None:
        merge = "MERGE t AS target\nUSING (SELECT 1 AS Id) AS source\n    ON target.Id = source.Id AND target.A = 1"
        self.assertEqual(messages("A.cs", merge), [])

    def test_a_join_on_releases_the_next_on_line(self) -> None:
        text = f"{self.JOINED}    ON c.Id = i.Id\nMERGE t AS target\nUSING s AS source\n    ON t.Id = s.Id AND t.A = 1"
        self.assertEqual(messages("A.cs", text), [])


class WhatIsNotAJoin(unittest.TestCase):
    def test_an_identifier_or_prose_is_not_counted(self) -> None:
        for line in ("    JOIN = re.compile(", "# Compensating now finalises on a JOIN — the stock half",
                     "LINQ's join x in y on a equals b", "a `LEFT JOIN` makes the reader stop"):
            with self.subTest(line=line):
                self.assertEqual(gate.judge("a.py", line), ([], 0))

    def test_markdown_is_judged_only_inside_its_fences(self) -> None:
        prose = "A bare JOIN orders ON the page.\n"
        fenced = "```sql\nFROM a\nJOIN b ON b.Id = a.Id\n```\n"
        self.assertEqual(messages("x.md", prose), [])
        self.assertEqual(messages("x.md", fenced), ["a bare JOIN: name its kind, INNER JOIN or LEFT JOIN"])
        self.assertEqual(gate.judge("x.md", fenced)[0][0].line, 3)

    def test_the_same_prose_outside_markdown_is_judged(self) -> None:
        self.assertEqual(gate.judge("x.cs", "A bare JOIN orders ON the page.")[1], 1)


class Surface(unittest.TestCase):
    def test_the_plans_and_this_suite_are_not_read_and_the_rest_is(self) -> None:
        with tempfile.TemporaryDirectory() as root:
            for name in ("docs/superpowers/plans/p.md", ".github/sql-style-gate/test_sql_style_gate.py",
                         "docs/superpowersish.md", "src/A.cs"):
                path = Path(root, name)
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("JOIN b\n", encoding="utf-8")
            result = gate.scan(Path(root), ["docs/superpowers/plans/p.md",
                                            ".github/sql-style-gate/test_sql_style_gate.py",
                                            "docs/superpowersish.md", "src/A.cs"])
            self.assertEqual(result.read, ["docs/superpowersish.md", "src/A.cs"])
            self.assertEqual([f.path for f in result.findings], ["src/A.cs"])

    def test_a_binary_or_missing_file_is_not_read(self) -> None:
        with tempfile.TemporaryDirectory() as root:
            Path(root, "a.bin").write_bytes(b"JOIN b\0")
            self.assertEqual(gate.scan(Path(root), ["a.bin", "gone.cs"]).read, [])

    def test_a_tree_with_no_tracked_file_fails(self) -> None:
        with tempfile.TemporaryDirectory() as root:
            subprocess.run(["git", "init", "-q", root], check=True)
            self.assertEqual(gate.main(["--root", root]), 1)

    def test_a_tracked_bare_join_fails_the_run(self) -> None:
        with tempfile.TemporaryDirectory() as root:
            subprocess.run(["git", "init", "-q", root], check=True)
            Path(root, "A.cs").write_text('const string Sql = """\nFROM a\nJOIN b\n    ON 1 = 1\n""";\n',
                                          encoding="utf-8")
            subprocess.run(["git", "-C", root, "add", "A.cs"], check=True)
            self.assertEqual(gate.main(["--root", root]), 1)


class TheRealTree(unittest.TestCase):
    """The subject is what the gate reads in this repository, not only what it found."""

    @classmethod
    def setUpClass(cls) -> None:
        cls.paths = gate.tracked(gate.REPO_ROOT)
        cls.result = gate.scan(gate.REPO_ROOT, cls.paths)

    def test_every_tracked_file_naming_a_join_is_read(self) -> None:
        # Listed here rather than by gate.tracked, so a narrowed listing there cannot narrow this side too.
        listing = subprocess.run(["git", "ls-files", "-z"], cwd=gate.REPO_ROOT, capture_output=True,
                                 check=True).stdout
        holding = []
        for path in (name.decode("utf-8") for name in listing.split(b"\0") if name):
            data = b"" if gate.excluded(path) else (gate.REPO_ROOT / path).read_bytes()
            if b"JOIN" in data and b"\0" not in data:
                holding.append(path)
        self.assertGreater(len(holding), 20)
        self.assertEqual(sorted(set(holding) - set(self.result.read)), [])

    def test_raw_string_literals_and_sql_fences_are_judged(self) -> None:
        sites = ("src/BuildingBlocks/Common.Infrastructure/Outbox/OutboxDispatcher.cs",
                 "tests/Platform.IntegrationTests/Journey/PersonalDataScan.cs",
                 "docs/backend-architecture/09-messaging.md",
                 "docs/backend-architecture/06-cqrs.md")
        for site in sites:
            with self.subTest(site=site):
                text = (gate.REPO_ROOT / site).read_text(encoding="utf-8")
                self.assertGreaterEqual(gate.judge(site, text)[1], 1)

    def test_the_tree_holds_no_refused_join(self) -> None:
        self.assertEqual([str(finding) for finding in self.result.findings], [])


if __name__ == "__main__":
    unittest.main()
