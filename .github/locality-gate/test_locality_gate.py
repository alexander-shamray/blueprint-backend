#!/usr/bin/env python3
"""Negative cases for the locality gate.

Running the gate against a pull request whose diff sits inside its declared
set passes, which says nothing about what it would catch. Every test below is
a defect the gate has to find, a malformed input it has to refuse rather than
judge, or a well-formed input it must not invent a finding for. The last
block reads the map this repository actually ships, because a gate whose
tests only ever see a fixture map has not been observed looking at the file
CI hands it.

    py -3.12 -m unittest discover -s .github/locality-gate
"""

from __future__ import annotations

import unittest
from pathlib import Path

import locality_gate
from locality_gate import (
    InputRefused,
    check,
    matcher,
    read_map,
    read_rows,
)

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent

# A small map with the shape of the shipped one. Two classes are enough for
# every case below that is about the gate rather than about the map.
MAP = """\
# a comment
A:
  - 'src/Services/**'
  - 'tests/**'
D:
  - 'docs/**'
  - 'CLAUDE.md'
B:
  - 'src/BuildingBlocks/**'
C:
  - 'src/**'
E:
  - 'Directory.Packages.props'
  - '**/*.csproj'
"""


def body(class_cell: str | None = "A", touch_cell: str | None = "`src/Services/Catalog/**`") -> str:
    rows = ["| | |", "|---|---|"]
    if class_cell is not None:
        rows.append(f"| Class | {class_cell} |")
    if touch_cell is not None:
        rows.append(f"| Touch set | {touch_cell} |")
    rows.append("| Closes | nothing |")
    return "Some prose first.\n\n" + "\n".join(rows) + "\n\nMore prose after.\n"


def payload(files: list, **kwargs) -> dict:
    # `changedFiles` is GitHub's count of the same list and the gate requires
    # it, so the helper supplies a matching one; a test about the count sets
    # its own afterwards.
    return {"number": 999, "body": body(**kwargs), "files": files, "changedFiles": len(files)}


class TouchSetGrammar(unittest.TestCase):
    """The rows are the author's text, and each is read on the helper's terms."""

    def test_the_house_rows_are_read(self) -> None:
        klass, tokens = read_rows(body("C+E", "`src/**`, `Directory.Packages.props`"))
        self.assertEqual(klass, ["C", "E"])
        self.assertEqual(tokens, ["src/**", "Directory.Packages.props"])

    def test_bare_tokens_are_accepted_beside_backticked_ones(self) -> None:
        _, tokens = read_rows(body("A", "src/Services/Catalog/**, `tests/Catalog.*`"))
        self.assertEqual(tokens, ["src/Services/Catalog/**", "tests/Catalog.*"])

    def test_a_brace_glob_is_one_token_not_two(self) -> None:
        _, tokens = read_rows(body("D", "`.claude/commands/{pr,ship}.md`, `docs/testing.md`"))
        self.assertEqual(tokens, [".claude/commands/{pr,ship}.md", "docs/testing.md"])

    def test_a_crlf_body_reads_the_same(self) -> None:
        klass, tokens = read_rows(body("A", "`src/Services/Catalog/**`").replace("\n", "\r\n"))
        self.assertEqual((klass, tokens), (["A"], ["src/Services/Catalog/**"]))

    def test_no_rows_is_refused_not_passed(self) -> None:
        with self.assertRaisesRegex(InputRefused, "no `\\| Class \\|` row"):
            read_rows(body(None, None))

    def test_a_class_row_without_a_touch_row_is_refused(self) -> None:
        with self.assertRaisesRegex(InputRefused, "no `\\| Touch set \\|` row"):
            read_rows(body("A", None))

    def test_a_touch_row_without_a_class_row_is_refused(self) -> None:
        with self.assertRaisesRegex(InputRefused, "no `\\| Class \\|` row"):
            read_rows(body(None, "`docs/**`"))

    def test_two_class_rows_are_refused_before_either_is_read(self) -> None:
        text = body("A", "`src/**`") + "\n| Class | D |\n"
        with self.assertRaisesRegex(InputRefused, "more than one `\\| Class \\|` row"):
            read_rows(text)

    def test_two_touch_rows_are_refused(self) -> None:
        text = body("A", "`src/**`") + "\n| Touch set | `docs/**` |\n"
        with self.assertRaisesRegex(InputRefused, "more than one `\\| Touch set \\|` row"):
            read_rows(text)

    def test_a_class_letter_outside_a_to_e_is_refused(self) -> None:
        with self.assertRaisesRegex(InputRefused, "Class row is not a class"):
            read_rows(body("Z", "`docs/**`"))

    def test_a_lower_case_class_is_refused(self) -> None:
        with self.assertRaisesRegex(InputRefused, "Class row is not a class"):
            read_rows(body("a", "`docs/**`"))

    def test_a_class_joined_with_itself_is_refused(self) -> None:
        with self.assertRaisesRegex(InputRefused, "repeats a class"):
            read_rows(body("A+A", "`docs/**`"))

    def test_three_classes_other_than_a_d_e_are_refused(self) -> None:
        for cell in ("A+B+C", "A+E+D", "D+A+E", "A+D+E+B", "A+D+D"):
            with self.subTest(cell=cell), self.assertRaisesRegex(InputRefused, "Class row is not a class"):
                read_rows(body(cell, "`docs/**`"))

    def test_a_d_e_is_read_as_its_three_members(self) -> None:
        self.assertEqual(read_rows(body("A+D+E", "`docs/**`"))[0], ["A", "D", "E"])

    def test_an_empty_touch_set_is_refused(self) -> None:
        with self.assertRaisesRegex(InputRefused, "Touch set row is empty"):
            read_rows(body("A", ""))

    def test_prose_in_the_touch_set_is_refused(self) -> None:
        with self.assertRaisesRegex(InputRefused, "not a path list"):
            read_rows(body("A", "the Catalog slice and its tests"))

    def test_a_token_with_no_slash_or_dot_is_refused(self) -> None:
        with self.assertRaisesRegex(InputRefused, "not a path list"):
            read_rows(body("A", "`src`"))

    def test_an_unbalanced_backtick_is_refused(self) -> None:
        with self.assertRaisesRegex(InputRefused, "unbalanced backtick"):
            read_rows(body("A", "`src/**"))

    def test_an_unbalanced_brace_is_refused(self) -> None:
        with self.assertRaisesRegex(InputRefused, "unbalanced brace"):
            read_rows(body("A", "`src/{a,b/**`"))

    def test_a_leading_slash_is_refused(self) -> None:
        with self.assertRaisesRegex(InputRefused, "outside the repository"):
            read_rows(body("A", "`/etc/passwd`"))

    def test_a_dot_dot_segment_is_refused(self) -> None:
        with self.assertRaisesRegex(InputRefused, "outside the repository"):
            read_rows(body("A", "`src/../../outside.md`"))

    def test_a_dot_dot_inside_a_brace_alternative_is_refused(self) -> None:
        with self.assertRaisesRegex(InputRefused, "outside the repository"):
            read_rows(body("A", "`{../outside,docs/x.md}`"))

    def test_a_second_cell_in_the_touch_row_is_refused(self) -> None:
        with self.assertRaisesRegex(InputRefused, "not one cell"):
            read_rows(body("A", "`src/**` | and more"))

    def test_a_doubled_trailing_slash_is_refused_not_read_as_the_directory(self) -> None:
        # pr-locality.sh drops one slash and refuses the empty segment left;
        # stripping every slash would read `docs//` as `docs`.
        with self.assertRaisesRegex(InputRefused, "outside the repository"):
            read_rows(body("D", "`docs//`"))
        with self.assertRaisesRegex(InputRefused, "outside the repository"):
            read_map(MAP.replace("'docs/**'", "'docs//'"))

    def test_a_token_ending_in_a_newline_is_refused(self) -> None:
        # Neither the row nor the map can deliver one, since both are read
        # line by line and the map's grammar refuses the broken line first,
        # so the normaliser is asked directly: `$` would have accepted it.
        with self.assertRaisesRegex(InputRefused, "not a path list"):
            locality_gate._normalise_token("docs/**\n", where="the Touch set row")


class Matching(unittest.TestCase):
    """The glob dialect is pr-locality.sh's, and each rule is pinned on its own."""

    def test_double_star_crosses_directories(self) -> None:
        self.assertTrue(matcher("src/**")("src/Services/Catalog/Catalog.Api/Program.cs"))

    def test_single_star_does_not_cross_a_directory(self) -> None:
        self.assertFalse(matcher("src/*.cs")("src/Services/Program.cs"))
        self.assertTrue(matcher("src/*.cs")("src/Program.cs"))

    def test_a_question_mark_is_one_character(self) -> None:
        self.assertTrue(matcher("docs/0?-x.md")("docs/09-x.md"))
        self.assertFalse(matcher("docs/0?-x.md")("docs/010-x.md"))

    def test_a_directory_token_covers_everything_beneath_it(self) -> None:
        self.assertTrue(matcher("tests/Catalog.*")("tests/Catalog.Api.Tests/Products/GetProductTests.cs"))
        self.assertTrue(matcher("docs")("docs/testing.md"))

    def test_a_trailing_slash_names_the_directory(self) -> None:
        self.assertTrue(matcher("docs/runbooks/")("docs/runbooks/high-latency.md"))

    def test_a_file_token_matches_only_that_file(self) -> None:
        self.assertTrue(matcher("CLAUDE.md")("CLAUDE.md"))
        self.assertFalse(matcher("CLAUDE.md")("docs/CLAUDE.md"))
        self.assertFalse(matcher("CLAUDE.md")("CLAUDE.md.bak"))

    def test_a_dot_is_literal(self) -> None:
        self.assertFalse(matcher("tests/Catalog.*")("tests/CatalogX/y.cs"))

    def test_braces_are_alternation(self) -> None:
        match = matcher(".claude/commands/{pr,ship}.md")
        self.assertTrue(match(".claude/commands/pr.md"))
        self.assertTrue(match(".claude/commands/ship.md"))
        self.assertFalse(match(".claude/commands/branch.md"))

    def test_a_leading_double_star_reaches_every_depth_below_the_root(self) -> None:
        # The dialect is pr-locality.sh's, where `**` is `.*` and the `/`
        # after it is literal, so `**/x` needs at least one directory. A
        # root-level file is named by its own path, as Directory.Packages.props
        # is in the shipped map; parity with the helper matters more than
        # gitignore's reading, because a row is judged by both.
        match = matcher("**/*.csproj")
        self.assertTrue(match("src/Services/Catalog/Catalog.Api/Catalog.Api.csproj"))
        self.assertFalse(match("Root.csproj"))
        self.assertFalse(match("src/Services/Catalog/Catalog.Api/Program.cs"))

    def test_a_prefix_is_not_a_match(self) -> None:
        self.assertFalse(matcher("src/Services/Catalog")("src/Services/CatalogTwo/x.cs"))


class MapGrammar(unittest.TestCase):
    """The map is read by a parser that accepts one shape, and an empty or
    half-read map is a gate that passes everything under a class it never
    loaded, so every departure from the shape is a refusal."""

    def test_the_fixture_map_reads(self) -> None:
        loaded = read_map(MAP)
        self.assertEqual(sorted(loaded), ["A", "B", "C", "D", "E"])
        self.assertEqual(loaded["A"], ["src/Services/**", "tests/**"])

    def test_a_crlf_map_reads_the_same(self) -> None:
        self.assertEqual(read_map(MAP.replace("\n", "\r\n")), read_map(MAP))

    def test_a_missing_class_is_refused(self) -> None:
        with self.assertRaisesRegex(InputRefused, "no entry for class E"):
            read_map(MAP.replace("E:\n  - 'Directory.Packages.props'\n  - '**/*.csproj'\n", ""))

    def test_a_repeated_class_is_refused(self) -> None:
        with self.assertRaisesRegex(InputRefused, "class A twice"):
            read_map(MAP + "A:\n  - 'deploy/**'\n")

    def test_a_class_with_no_items_is_refused(self) -> None:
        with self.assertRaisesRegex(InputRefused, "class B has no items"):
            read_map(MAP.replace("  - 'src/BuildingBlocks/**'\n", ""))

    def test_an_item_before_any_class_is_refused(self) -> None:
        with self.assertRaisesRegex(InputRefused, "line 1"):
            read_map("  - 'docs/**'\n" + MAP)

    def test_an_unquoted_item_is_refused(self) -> None:
        with self.assertRaisesRegex(InputRefused, "line 3"):
            read_map(MAP.replace("  - 'src/Services/**'", "  - src/Services/**"))

    def test_a_line_outside_the_grammar_is_refused(self) -> None:
        with self.assertRaisesRegex(InputRefused, "line 2"):
            read_map("# ok\nclasses:\n" + MAP)

    def test_a_class_outside_a_to_e_is_refused(self) -> None:
        with self.assertRaisesRegex(InputRefused, "line 1"):
            read_map("F:\n  - 'docs/**'\n" + MAP)

    def test_an_item_naming_a_path_outside_the_repository_is_refused(self) -> None:
        with self.assertRaisesRegex(InputRefused, "outside the repository"):
            read_map(MAP.replace("'tests/**'", "'../tests/**'"))

    def test_an_item_with_a_comma_outside_braces_is_refused_not_widened(self) -> None:
        # A count of braces accepts `docs/a,docs/b`; matcher() would turn the
        # comma into an ungrouped `|` and `docs/a|docs/b(/.*)?` matches
        # `docs/admin`. The map is the one route such a token can take,
        # because the touch-set row is split on that comma first.
        with self.assertRaisesRegex(InputRefused, "comma outside a brace"):
            read_map(MAP.replace("'docs/**'", "'docs/a,docs/b'"))

    def test_an_item_with_reversed_braces_is_refused(self) -> None:
        with self.assertRaisesRegex(InputRefused, "unbalanced brace"):
            read_map(MAP.replace("'docs/**'", "'docs/}a{'"))

    def test_an_item_with_a_brace_alternation_is_still_read(self) -> None:
        loaded = read_map(MAP.replace("'docs/**'", "'docs/{a,b}/**'"))
        self.assertIn("docs/{a,b}/**", loaded["D"])
        self.assertTrue(matcher("docs/{a,b}/**")("docs/a/x.md"))
        self.assertFalse(matcher("docs/{a,b}/**")("docs/admin/x.md"))

    def test_an_empty_map_is_refused(self) -> None:
        with self.assertRaisesRegex(InputRefused, "no entry for class A"):
            read_map("# nothing but a comment\n")


class Verdicts(unittest.TestCase):
    """Both checks, each one on its own, and the case only the second catches."""

    def setUp(self) -> None:
        self.map = read_map(MAP)

    def test_a_diff_inside_both_sets_passes(self) -> None:
        problems = check(payload(["src/Services/Catalog/Catalog.Api/Program.cs"]), self.map)
        self.assertEqual(problems, [])

    def test_a_file_outside_the_class_map_is_caught_and_named(self) -> None:
        problems = check(payload(["src/Services/Catalog/X.cs", "CLAUDE.md"],
                                 touch_cell="`src/Services/Catalog/**`, `CLAUDE.md`"), self.map)
        self.assertEqual(len(problems), 1)
        self.assertIn("CLAUDE.md", problems[0])
        self.assertIn("class A", problems[0])

    def test_a_file_inside_the_class_map_but_outside_the_declared_set_is_caught(self) -> None:
        # The Catalog change that also edits Ordering: inside A's tree set,
        # outside its own row. This is the case the second check exists for.
        problems = check(payload(["src/Services/Catalog/X.cs", "src/Services/Ordering/Y.cs"]), self.map)
        self.assertEqual(len(problems), 1)
        self.assertIn("src/Services/Ordering/Y.cs", problems[0])
        self.assertIn("declared touch set", problems[0])

    def test_a_file_outside_both_reports_both(self) -> None:
        problems = check(payload(["docs/testing.md"]), self.map)
        self.assertEqual(len(problems), 2)
        self.assertTrue(any("class A" in p for p in problems))
        self.assertTrue(any("declared touch set" in p for p in problems))

    def test_every_offending_file_is_named_not_just_the_first(self) -> None:
        problems = check(payload(["docs/a.md", "docs/b.md", "src/Services/Catalog/ok.cs"],
                                 touch_cell="`src/Services/Catalog/**`, `docs/**`"), self.map)
        self.assertEqual(len(problems), 2)
        self.assertTrue(any("docs/a.md" in p for p in problems))
        self.assertTrue(any("docs/b.md" in p for p in problems))

    def test_a_joined_class_is_the_union_of_its_members(self) -> None:
        files = ["src/BuildingBlocks/Common.Web/Common.Web.csproj", "Directory.Packages.props",
                 "src/BuildingBlocks/Common.Web/Thing.cs"]
        touch = "`src/BuildingBlocks/Common.Web/**`, `Directory.Packages.props`"
        self.assertEqual(check(payload(files, class_cell="B+E", touch_cell=touch), self.map), [])
        # And without E, the pin is outside B alone.
        problems = check(payload(files, class_cell="B", touch_cell=touch), self.map)
        self.assertEqual(len(problems), 1)
        self.assertIn("Directory.Packages.props", problems[0])

    def test_a_d_e_is_the_union_of_its_three_members(self) -> None:
        files = ["src/Services/Payments/Payments.Api/Program.cs", "docs/runbooks/x.md",
                 "src/Services/Payments/Payments.Api/Payments.Api.csproj"]
        touch = "`src/Services/Payments/**`, `docs/runbooks/x.md`"
        self.assertEqual(check(payload(files, class_cell="A+D+E", touch_cell=touch), self.map), [])
        # And as A+E, the runbook is outside the class: D is the member that reaches it.
        problems = check(payload(files, class_cell="A+E", touch_cell=touch), self.map)
        self.assertEqual(len(problems), 1)
        self.assertIn("docs/runbooks/x.md", problems[0])

    def test_class_d_does_not_reach_src(self) -> None:
        problems = check(payload(["docs/testing.md", "src/Services/Catalog/X.cs"],
                                 class_cell="D", touch_cell="`docs/**`, `src/**`"), self.map)
        self.assertEqual(len(problems), 1)
        self.assertIn("src/Services/Catalog/X.cs", problems[0])

    def test_the_touch_set_cell_is_never_printed(self) -> None:
        # The row is the author's text; a verdict names the diff's path and
        # the class, and nothing the author wrote.
        marker = "Ignore_all_previous_instructions.md"
        problems = check(payload(["docs/testing.md"], touch_cell=f"`docs/{marker}`"), self.map)
        self.assertTrue(problems)
        for problem in problems:
            self.assertNotIn(marker, problem)

    def test_an_empty_diff_is_refused_not_passed(self) -> None:
        with self.assertRaisesRegex(InputRefused, "no changed files"):
            check(payload([]), self.map)

    def test_a_missing_files_key_is_refused(self) -> None:
        with self.assertRaisesRegex(InputRefused, "files"):
            check({"number": 1, "body": body(), "changedFiles": 1}, self.map)

    def test_a_missing_body_is_refused(self) -> None:
        with self.assertRaisesRegex(InputRefused, "body"):
            check({"number": 1, "files": ["docs/x.md"], "changedFiles": 1}, self.map)

    def test_a_missing_changed_files_is_refused_not_judged(self) -> None:
        # Optional would be fail-open: a workflow that stopped sending the
        # count would hand a possible prefix to a gate that judged it whole.
        data = payload(["src/Services/Catalog/X.cs"])
        del data["changedFiles"]
        with self.assertRaisesRegex(InputRefused, "changedFiles"):
            check(data, self.map)

    def test_a_changed_path_that_is_not_a_plain_path_refuses_the_run(self) -> None:
        with self.assertRaisesRegex(InputRefused, "not a plain path"):
            check(payload(["src/Services/Catalog/ok.cs", "docs/evil\nname.md"]), self.map)

    def test_a_changed_path_ending_in_a_newline_refuses_the_run(self) -> None:
        # `$` matches before a final newline, so a match() would accept this
        # as `docs/x.md`; git permits the name and JSON delivers it intact.
        with self.assertRaisesRegex(InputRefused, "not a plain path"):
            check(payload(["docs/x.md\n"], class_cell="D", touch_cell="`docs/**`"), self.map)

    def test_a_changed_path_with_a_dot_dot_segment_refuses_the_run(self) -> None:
        with self.assertRaisesRegex(InputRefused, "not a plain path"):
            check(payload(["src/../CLAUDE.md"]), self.map)

    def test_a_file_entry_that_is_neither_a_path_nor_an_endpoint_object_refuses_the_run(self) -> None:
        with self.assertRaisesRegex(InputRefused, "not a plain path"):
            check(payload([42]), self.map)
        with self.assertRaisesRegex(InputRefused, "not a plain path"):
            check(payload([{"path": "docs/x.md"}]), self.map)

    def test_an_endpoint_object_is_judged_by_its_filename(self) -> None:
        entries = [{"filename": "src/Services/Catalog/X.cs", "previous_filename": None}]
        self.assertEqual(check(payload(entries), self.map), [])
        problems = check(payload([{"filename": "CLAUDE.md"}]), self.map)
        self.assertEqual(len(problems), 2)

    def test_a_rename_is_judged_at_both_ends(self) -> None:
        # Class D moving a code file into docs/: the destination is inside D
        # and the source is not, and only the source says a code path went.
        entries = [{"filename": "docs/X.cs", "previous_filename": "src/Services/Catalog/X.cs"}]
        problems = check(payload(entries, class_cell="D", touch_cell="`docs/**`"), self.map)
        self.assertEqual(len(problems), 2)
        self.assertTrue(all("src/Services/Catalog/X.cs" in p for p in problems))
        # And a rename inside the set passes on both ends.
        entries = [{"filename": "docs/b.md", "previous_filename": "docs/a.md"}]
        self.assertEqual(check(payload(entries, class_cell="D", touch_cell="`docs/**`"), self.map), [])

    def test_a_rename_source_that_is_not_a_plain_path_refuses_the_run(self) -> None:
        entries = [{"filename": "docs/b.md", "previous_filename": "docs/../CLAUDE.md"}]
        with self.assertRaisesRegex(InputRefused, "not a plain path"):
            check(payload(entries, class_cell="D", touch_cell="`docs/**`"), self.map)

    def test_a_file_list_shorter_than_changed_files_is_refused_as_a_prefix(self) -> None:
        # The endpoint returns at most 3,000 entries; a prefix of a longer diff
        # is the fail-open shape and is refused, not judged.
        data = payload(["src/Services/Catalog/X.cs"])
        data["changedFiles"] = 3001
        with self.assertRaisesRegex(InputRefused, "prefix"):
            check(data, self.map)

    def test_a_file_list_matching_changed_files_is_judged(self) -> None:
        data = payload(["src/Services/Catalog/X.cs", "src/Services/Catalog/Y.cs"])
        data["changedFiles"] = 2
        self.assertEqual(check(data, self.map), [])

    def test_a_rename_counts_once_against_changed_files(self) -> None:
        # GitHub counts a rename as one changed file and the endpoint returns
        # one entry for it, so the comparison is over entries, not paths.
        data = payload([{"filename": "docs/b.md", "previous_filename": "docs/a.md"}],
                       class_cell="D", touch_cell="`docs/**`")
        data["changedFiles"] = 1
        self.assertEqual(check(data, self.map), [])

    def test_a_changed_files_that_is_not_a_number_is_refused(self) -> None:
        data = payload(["docs/x.md"], class_cell="D", touch_cell="`docs/**`")
        data["changedFiles"] = "9"
        with self.assertRaisesRegex(InputRefused, "changedFiles"):
            check(data, self.map)


class Main(unittest.TestCase):
    """The exit codes, because the workflow reads nothing else."""

    def run_main(self, payload_text: str, map_text: str = MAP) -> tuple[int, str, str]:
        import io
        import tempfile
        from contextlib import redirect_stderr, redirect_stdout

        with tempfile.TemporaryDirectory() as directory:
            map_path = Path(directory) / "classes.yml"
            map_path.write_text(map_text, encoding="utf-8")
            payload_path = Path(directory) / "payload.json"
            payload_path.write_text(payload_text, encoding="utf-8")
            out, err = io.StringIO(), io.StringIO()
            with redirect_stdout(out), redirect_stderr(err):
                code = locality_gate.main(["locality_gate.py", "--map", str(map_path), str(payload_path)])
        return code, out.getvalue(), err.getvalue()

    def test_a_clean_pull_request_exits_zero(self) -> None:
        import json
        code, out, _ = self.run_main(json.dumps(payload(["src/Services/Catalog/X.cs"])))
        self.assertEqual(code, 0)
        self.assertIn("inside", out)

    def test_a_violation_exits_one_and_names_the_file(self) -> None:
        import json
        code, _, err = self.run_main(json.dumps(payload(["CLAUDE.md"])))
        self.assertEqual(code, 1)
        self.assertIn("CLAUDE.md", err)

    def test_a_refused_input_exits_two(self) -> None:
        import json
        code, _, err = self.run_main(json.dumps({"number": 1, "body": "no rows", "files": ["docs/x.md"]}))
        self.assertEqual(code, 2)
        self.assertIn("Class", err)

    def test_a_refused_map_exits_two(self) -> None:
        import json
        code, _, err = self.run_main(json.dumps(payload(["docs/x.md"])), map_text="nonsense\n")
        self.assertEqual(code, 2)
        self.assertIn("classes.yml", err)

    def test_input_that_is_not_json_exits_two(self) -> None:
        code, _, err = self.run_main("{not json")
        self.assertEqual(code, 2)
        self.assertIn("not JSON", err)


class TheShippedMap(unittest.TestCase):
    """What the gate looks at in CI, rather than a fixture shaped like it."""

    def setUp(self) -> None:
        self.map = read_map((HERE / "classes.yml").read_text(encoding="utf-8"))

    def test_the_default_map_path_is_the_shipped_file(self) -> None:
        self.assertEqual(locality_gate.DEFAULT_MAP, HERE / "classes.yml")

    def test_every_class_is_present_with_items(self) -> None:
        for klass in "ABCDE":
            self.assertTrue(self.map[klass], klass)

    def test_class_d_reaches_no_code(self) -> None:
        # Every file actually under src/ and tests/ in this checkout, against
        # every Class D token: a probe of two Catalog paths would stay green
        # with `src/Gateway/**` added to D. Build output is skipped because it
        # is not in the tree the gate judges, and the walk must find something
        # or the assertion is over an empty subject.
        skipped = {"bin", "obj", "TestResults"}
        code = [
            path.relative_to(ROOT).as_posix()
            for top in ("src", "tests")
            for path in (ROOT / top).rglob("*")
            if path.is_file() and not (skipped & set(path.relative_to(ROOT).parts))
        ]
        self.assertGreater(len(code), 100, "the walk over src/ and tests/ found almost nothing")
        matchers = [(token, matcher(token)) for token in self.map["D"]]
        reached = sorted({f"{token} -> {path}" for path in code for token, match in matchers if match(path)})
        self.assertEqual(reached, [])

    def test_a_class_a_change_to_claude_md_is_caught(self) -> None:
        problems = check(payload(["src/Services/Catalog/X.cs", "CLAUDE.md"],
                                 touch_cell="`src/Services/Catalog/**`, `CLAUDE.md`"), self.map)
        self.assertEqual(len(problems), 1)
        self.assertIn("CLAUDE.md", problems[0])

    def test_a_class_a_change_touching_two_services_is_caught_by_the_declared_set(self) -> None:
        problems = check(payload(["src/Services/Catalog/X.cs", "src/Services/Ordering/Y.cs"],
                                 touch_cell="`src/Services/Catalog/**`, `tests/Catalog.*`"), self.map)
        self.assertEqual(len(problems), 1)
        self.assertIn("src/Services/Ordering/Y.cs", problems[0])

    def test_class_e_reaches_a_project_file_at_any_depth(self) -> None:
        problems = check(payload(["Directory.Packages.props",
                                  "src/Services/Ordering/Ordering.Api/Ordering.Api.csproj"],
                                 class_cell="E", touch_cell="`Directory.Packages.props`, `**/*.csproj`"),
                         self.map)
        self.assertEqual(problems, [])

    def test_this_gate_is_a_class_d_change(self) -> None:
        files = [".github/locality-gate/classes.yml", ".github/locality-gate/locality_gate.py",
                 ".github/workflows/locality-gate.yml", "docs/change-locality.md", "CLAUDE.md",
                 ".gitattributes", ".mcp.json", ".codeindexignore"]
        touch = ("`.github/locality-gate/**`, `.github/workflows/locality-gate.yml`, `docs/**`, "
                 "`CLAUDE.md`, `.gitattributes`, `.mcp.json`, `.codeindexignore`")
        self.assertEqual(check(payload(files, class_cell="D", touch_cell=touch), self.map), [])

    def test_the_contract_cites_this_file_rather_than_restating_it(self) -> None:
        contract = (ROOT / "docs" / "change-locality.md").read_text(encoding="utf-8")
        self.assertIn(".github/locality-gate/classes.yml", contract)


if __name__ == "__main__":
    unittest.main()
