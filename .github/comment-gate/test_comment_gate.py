"""The comment gate's subject: what it must find, and what it must not judge.

Each reader's class pairs the comments it must find with the literals it must
leave alone, because a literal is code; the rule classes pair each finding
with its innocent neighbour; the last two run the gate over real git history.
"""

import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

import comment_gate as gate

GATE = Path(__file__).with_name("comment_gate.py")
ROOT = Path(__file__).resolve().parents[2]


def said(reader, text):
    return [text[c.body_start:c.body_end].strip() for c in reader(text)]


def judged(path, text, added=None):
    every = set(range(1, text.count("\n") + 2))
    return gate.judge(path, text, every if added is None else added)


class CSharpComments(unittest.TestCase):
    def test_line_trailing_doc_and_block_comments_are_found(self):
        text = ("/// <summary>doc</summary>\n"
                "Call(); // trailing\n"
                "// line\n"
                "/* block */ Call();\n")
        self.assertEqual(said(gate.csharp, text),
                         ["<summary>doc</summary>", "trailing", "line",
                          "block"])

    def test_strings_of_every_kind_are_code(self):
        text = ('var a = "// no";\n'
                'var b = @"x "" // no";\n'
                'var c = """\n// no\n""";\n'
                'var d = $"{Name("// no")} // no";\n'
                'var e = $$"""{{Name("// no")}} // no""";\n'
                'var f = \'"\'; // yes\n')
        self.assertEqual(said(gate.csharp, text), ["yes"])

    def test_an_interpolation_hole_is_code_again(self):
        text = 'var a = $"{(x ? "a" : "b")}"; // yes\n'
        self.assertEqual(said(gate.csharp, text), ["yes"])

    def test_a_format_clause_is_literal_text(self):
        text = ('var a = $"{value:// #12}"; // yes\n'
                'var b = $$"""{{value:// no}}"""; // also\n'
                'var c = $"{global::A.B /* hole */}";\n')
        self.assertEqual(said(gate.csharp, text), ["yes", "also", "hole"])

    def test_a_directive_string_argument_is_not_a_comment(self):
        text = ('#line 1 "https://host/#12" // yes\n'
                '#pragma checksum "a.cs" "{406EA660}" "ab" // also\n'
                '#line 2 "https://host/#13"\n')
        self.assertEqual(said(gate.csharp, text), ["yes", "also"])

    def test_a_directive_message_is_code_and_a_trailing_comment_is_not(self):
        text = ("#region see http://example\n"
                "#pragma warning disable CA1822 // why\n"
                "#if DEBUG // also\n")
        self.assertEqual(said(gate.csharp, text), ["why", "also"])


class PythonComments(unittest.TestCase):
    def test_comments_and_docstrings_are_found(self):
        text = ('"""module"""\n'
                "def f():\n"
                "    '''function'''\n"
                "    return 1  # trailing\n")
        self.assertEqual(sorted(said(gate.python, text)),
                         ["function", "module", "trailing"])

    def test_other_strings_are_code(self):
        text = ('x = 1\n'
                '"""not first, so not a docstring"""\n'
                'y = "# no"\n'
                'z = f"{y} # no"\n')
        self.assertEqual(said(gate.python, text), [])

    def test_a_docstring_ends_where_its_expression_does(self):
        self.assertEqual(said(gate.python, 'def f(): "doc"; v = "PR-7"\n'),
                         ["doc"])
        self.assertEqual(said(gate.python, 'def f(): "é"; v = "#1"\n'),
                         ["é"])
        self.assertEqual(judged("m.py", 'def f(): "doc"; v = "PR-7"\n'), [])

    def test_python_that_does_not_parse_refuses_the_file(self):
        with self.assertRaises(gate.Unreadable):
            gate.python("def (:\n")


class ShellComments(unittest.TestCase):
    def test_a_hash_that_begins_a_word_is_a_comment(self):
        text = "echo a # yes\necho a#no\n"
        self.assertEqual(said(gate.shell, text), ["yes"])

    def test_quotes_and_expansions_are_code(self):
        text = ('echo "# no" \'# no\' $\'# no\' ${#arr[@]} $# '
                '"$(echo "#no")" ${x#no}\n')
        self.assertEqual(said(gate.shell, text), [])

    def test_a_heredoc_body_is_code_and_the_line_after_it_is_not(self):
        text = ("cat <<'EOF' # yes\n# no\nEOF\n"
                "cat <<-EOF\n\t# no\n\tEOF\n"
                "# after\n")
        self.assertEqual(said(gate.shell, text), ["yes", "after"])

    def test_a_heredoc_word_is_read_whole(self):
        for opener in ["END-OF-FILE", "END-OF'-FILE'", 'EN"D"-OF-FILE']:
            with self.subTest(opener=opener):
                text = f"cat <<{opener}\n# no\nEND-OF-FILE\n# after\n"
                self.assertEqual(said(gate.shell, text), ["after"])

    def test_an_arithmetic_shift_is_not_a_heredoc(self):
        for line in ["((mask << shift))", "x=$((1 << width))",
                     "if ((a << (b + 1))); then :; fi"]:
            with self.subTest(line=line):
                text = f"{line} # yes\n# after\n"
                self.assertEqual(said(gate.shell, text), ["yes", "after"])

    def test_nested_subshells_are_not_arithmetic(self):
        text = "((cat <<END\n# no\nEND\n); true) # yes\n"
        self.assertEqual(said(gate.shell, text), ["yes"])

    def test_a_here_string_is_not_a_heredoc(self):
        text = 'grep x <<<"$y" # yes\n# also\n'
        self.assertEqual(said(gate.shell, text), ["yes", "also"])

    def test_a_command_substitution_holds_comments_of_its_own(self):
        text = "x=$(\n  echo a # yes\n)\n"
        self.assertEqual(said(gate.shell, text), ["yes"])


class YamlComments(unittest.TestCase):
    def test_a_hash_after_a_space_is_a_comment(self):
        text = "key: value # yes\nurl: http://x#no\n# line\n"
        self.assertEqual(said(gate.yaml, text), ["yes", "line"])

    def test_quoted_scalars_and_block_scalars_are_code(self):
        text = ("a: '# no'\n"
                'b: "x # no"\n'
                "c: it's # yes\n"
                "d: |\n  # no\n  text\n"
                "e: done # after\n")
        self.assertEqual(said(gate.yaml, text), ["yes", "after"])

    def test_a_run_block_is_read_as_the_shell_it_is(self):
        text = ("steps:\n"
                "  - run: |\n"
                "      echo \"# no\" # yes\n"
                "      cat <<EOF\n"
                "      # no\n"
                "      EOF\n"
                "      # also\n"
                "    name: x # sibling\n")
        self.assertEqual(said(gate.yaml, text), ["yes", "also", "sibling"])

    def test_every_spelling_of_the_run_key_is_shell(self):
        for key in ["run", '"run"', "'run'", "run ", '"run" ']:
            with self.subTest(key=key):
                text = f"- {key}: |\n    echo '# no' # yes\n"
                self.assertEqual(said(gate.yaml, text), ["yes"])

    def test_a_run_block_comment_lands_on_its_own_line(self):
        text = "- run: |\n    true\n    # PR-1\n"
        self.assertEqual([line for _, line, _ in judged("w.yml", text)], [3])


class MsbuildComments(unittest.TestCase):
    def test_a_comment_is_found_and_cdata_is_code(self):
        text = ("<Project><!-- yes -->\n"
                "<X><![CDATA[ <!-- no --> ]]></X></Project>\n")
        self.assertEqual(said(gate.msbuild, text), ["yes"])


class EditorconfigComments(unittest.TestCase):
    def test_whole_lines_only(self):
        text = "# yes\n; also\nkey = a # no\n"
        self.assertEqual(said(gate.editorconfig, text), ["yes", "also"])


class WhichFilesAreRead(unittest.TestCase):
    def test_each_language_the_rule_names_has_a_reader(self):
        cases = {"a/B.cs": gate.csharp, "x.py": gate.python,
                 "run.sh": gate.shell, "ci.yml": gate.yaml,
                 "v.yaml": gate.yaml, "A.csproj": gate.msbuild,
                 "Directory.Build.props": gate.msbuild,
                 "x.targets": gate.msbuild, ".editorconfig": gate.editorconfig,
                 "docs/README.md": None, "Dockerfile": None}
        for path, reader in cases.items():
            with self.subTest(path=path):
                self.assertIs(gate.reader_for(path), reader)


class ThePatterns(unittest.TestCase):
    def test_each_pattern_fails_a_comment_and_passes_as_code(self):
        samples = ["see #12", "PR-7", "Copilot said so", "found in review",
                   "it used to", "went stale", "this comment said",
                   "**stress**", "<b>stress</b>"]
        for sample in samples:
            with self.subTest(sample=sample):
                self.assertEqual(len(judged("A.cs", f"x(); // {sample}\n")),
                                 1)
                self.assertEqual(judged("A.cs", f'x("{sample}");\n'), [])

    def test_innocent_neighbours_pass(self):
        for sample in ["refused to", "C# 14", "§13.2", "a*b*c",
                       "the key is used for signing"]:
            with self.subTest(sample=sample):
                self.assertEqual(judged("A.cs", f"// {sample}\n"), [])

    def test_the_harness_may_name_its_reviewer_and_nothing_else_may(self):
        text = "# Copilot's login\n"
        self.assertEqual(judged(".claude/scripts/a.sh", text), [])
        self.assertEqual(len(judged("deploy/a.sh", text)), 1)
        self.assertEqual(len(judged(".claude/scripts/a.sh", "# PR-1\n")), 1)

    def test_only_added_lines_are_judged(self):
        text = "// PR-1\n// PR-2\n"
        self.assertEqual([line for _, line, _ in judged("A.cs", text, {2})],
                         [2])


class TheBlockRule(unittest.TestCase):
    def block(self, n, marker="//"):
        return "".join(f"{marker} line {k}\n" for k in range(n))

    def test_ten_lines_pass_and_eleven_fail_at_the_first_line(self):
        self.assertEqual(judged("A.cs", self.block(10)), [])
        self.assertEqual([line for _, line, _ in
                          judged("A.cs", "x();\n" + self.block(11))], [2])

    def test_one_added_line_judges_the_whole_old_block(self):
        self.assertEqual(len(judged("A.cs", self.block(11), {5})), 1)
        self.assertEqual(judged("A.cs", self.block(11) + "x();\n", {12}), [])

    def test_a_blank_line_or_code_ends_a_block(self):
        self.assertEqual(judged("A.cs", self.block(6) + "\n" + self.block(6)),
                         [])
        trailing = "".join(f"x(); // {k}\n" for k in range(12))
        self.assertEqual(judged("A.cs", trailing), [])

    def test_a_docstring_is_a_block_blank_lines_and_all(self):
        body = "\n".join(["x"] * 5 + [""] + ["y"] * 4)
        self.assertEqual(len(judged("m.py", f'"""{body}\n"""\n')), 1)
        self.assertEqual(judged("m.py", '"""one\n\ntwo\n"""\n'), [])


class TheDiff(unittest.TestCase):
    def test_hunks_give_the_added_line_numbers(self):
        diff = ("diff --git a/A.cs b/A.cs\n"
                "--- a/A.cs\n+++ b/A.cs\n"
                "@@ -1,0 +2,2 @@\n+a\n++++ b/looks like a header\n"
                "@@ -9 +10 @@\n-old\n\\ No newline at end of file\n+new\n"
                "@@ -20,2 +20,0 @@\n-x\n-y\n"
                "diff --git a/B.cs b/B.cs\n--- /dev/null\n+++ b/B.cs\n"
                "@@ -0,0 +1 @@\n+z\n")
        self.assertEqual(gate.added_lines(diff),
                         {"A.cs": {2, 3, 10}, "B.cs": {1}})

    def test_a_header_it_does_not_know_refuses_the_run(self):
        for header in ['+++ "b/odd\\tname.cs"', "+++ odd.cs"]:
            with self.subTest(header=header):
                with self.assertRaises(gate.Unreadable):
                    gate.added_lines(f"{header}\n@@ -0,0 +1 @@\n+x\n")


def git(repo, *args):
    subprocess.run(["git", "-C", repo, "-c", "user.name=t",
                    "-c", "user.email=t@t", "-c", "core.autocrlf=false",
                    *args], check=True, capture_output=True)


class TheGateOnARepository(unittest.TestCase):
    def setUp(self):
        self.repo = tempfile.mkdtemp()
        git(self.repo, "init", "-q", "-b", "main")
        self.write("Old.cs", "// PR-1\n" + "".join(
            f"// {k}\n" for k in range(11)))
        self.write("Moved.cs", "// PR-2\n")
        self.commit("base")

    def tearDown(self):
        subprocess.run(["git", "-C", self.repo, "clean", "-qfdx"])

    def write(self, name, text, encoding="utf-8"):
        Path(self.repo, name).write_bytes(text.encode(encoding))

    def commit(self, message):
        git(self.repo, "add", "-A")
        git(self.repo, "commit", "-q", "--allow-empty", "-m", message)
        return subprocess.run(["git", "-C", self.repo, "rev-parse", "HEAD"],
                              capture_output=True, text=True).stdout.strip()

    def run_gate(self, base="main~1", head="HEAD"):
        return subprocess.run(
            [sys.executable, str(GATE), "--base", base, "--head", head],
            cwd=self.repo, capture_output=True, text=True, encoding="utf-8")

    def test_an_added_finding_fails_and_old_ones_stay_unjudged(self):
        self.write("New.cs", "x(); // see #3\n")
        self.write("README.md", "PR-1 in prose is not a comment\n")
        git(self.repo, "mv", "Moved.cs", "Renamed.cs")
        self.commit("change")
        result = self.run_gate()
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("New.cs:1: a comment names an issue", result.stdout)
        self.assertNotIn("Renamed.cs", result.stdout)
        self.assertNotIn("Old.cs", result.stdout)

    def test_a_line_added_inside_an_old_block_fails_it(self):
        text = Path(self.repo, "Old.cs").read_text().replace(
            "// 5\n", "// 5\n// five\n")
        self.write("Old.cs", text)
        self.commit("change")
        result = self.run_gate()
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("Old.cs:1: a comment block runs 13 lines",
                      result.stdout)

    def test_a_clean_change_passes_and_says_what_it_read(self):
        self.write("New.cs", "// why, in one line\n")
        self.commit("change")
        result = self.run_gate()
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertIn("judged the added lines of 1 file(s)", result.stdout)

    def test_a_diff_that_changes_nothing_refuses_the_run(self):
        result = self.run_gate(base="HEAD")
        self.assertEqual(result.returncode, 2, result.stdout)

    def test_a_file_that_is_not_utf8_refuses_the_run(self):
        self.write("Latin.cs", "// café\n", encoding="latin-1")
        self.commit("change")
        self.assertEqual(self.run_gate().returncode, 2)

    def test_a_change_git_calls_binary_refuses_the_run(self):
        Path(self.repo, "Bin.cs").write_bytes(b"\x00\xff\xfe// PR-1\n")
        self.commit("change")
        self.assertEqual(self.run_gate().returncode, 2)

    def test_fused_hunks_judge_only_their_added_lines(self):
        lines = [f"x{k}();\n" for k in range(10)]
        lines[3] = "// PR-3\n"
        self.write("Fused.cs", "".join(lines))
        self.commit("old")
        lines[1] = "y();\n"
        lines[5] = "y();\n"
        self.write("Fused.cs", "".join(lines))
        self.commit("change")
        git(self.repo, "config", "diff.interHunkContext", "5")
        result = self.run_gate(base="HEAD~1")
        self.assertEqual(result.returncode, 0, result.stdout)


class TheShippedTree(unittest.TestCase):
    def test_every_tracked_file_it_reads_can_be_read(self):
        listed = subprocess.run(["git", "-C", str(ROOT), "ls-files", "-z"],
                                capture_output=True, check=True).stdout
        paths = [p for p in listed.decode("utf-8").split("\0")
                 if p and gate.reader_for(p)]
        self.assertGreater(len(paths), 100)
        for path in paths:
            with self.subTest(path=path):
                text = (ROOT / path).read_text(encoding="utf-8-sig")
                gate.judge(path, text, set())


if __name__ == "__main__":
    unittest.main()
