"""What `.claude/hooks/guard-sed-crlf.py` refuses, and what it must not.

Verdict cases call `offence` with `strips_cr=True`, since only Git Bash's sed drops the CR and they run on every
`payload-suites` leg; process cases run the hook as the harness does, and only the Windows leg reaches its refusal and
`/c/...` translation. The last class has the registration as its subject."""

import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from review_helpers import assert_launched_on_the_floor

SCRIPTS = Path(__file__).resolve().parent
HOOK = SCRIPTS.parent / "hooks" / "guard-sed-crlf.py"
SETTINGS = SCRIPTS.parent / "settings.json"
GIT = shutil.which("git")
EXECUTABLE = "git.exe" if os.name == "nt" else "git"


def setUpModule():
    # Not a skip: a skip on a missing tool reports a pass, and without git no case can make a tracked file.
    if GIT is None:
        raise AssertionError("git is not on PATH, so no tracked file can be made and nothing here is judged")
    if not HOOK.exists():
        raise AssertionError(f"the hook is missing: {HOOK}")


def guard_module():
    spec = importlib.util.spec_from_file_location("guard_sed_crlf", HOOK)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


GUARD = guard_module()


class ACheckout(unittest.TestCase):
    """A checkout holding tracked CRLF and LF files, an untracked CRLF file, and a tracked CRLF file one level down."""

    def setUp(self):
        holder = tempfile.TemporaryDirectory()
        self.addCleanup(holder.cleanup)
        self.root = holder.name
        self.git("init", "-q")
        self.put("crlf.txt", b"one\r\ntwo\r\n")
        self.put("lf.txt", b"one\ntwo\n")
        self.put(os.path.join("sub", "inner.txt"), b"one\r\ntwo\r\n")
        self.git("-c", "core.autocrlf=false", "add", "crlf.txt", "lf.txt", "sub/inner.txt")
        self.put("loose.txt", b"one\r\ntwo\r\n")

    def git(self, *arguments):
        subprocess.run([GIT, "-C", self.root, *arguments], check=True, capture_output=True)

    def put(self, name, data):
        path = os.path.join(self.root, name)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as handle:
            handle.write(data)

    def verdict(self, command, strips_cr=True, tool="Bash"):
        event = {"hook_event_name": "PreToolUse", "cwd": self.root, "tool_name": tool,
                 "tool_input": {"command": command}}
        return GUARD.offence(event, strips_cr=strips_cr)

    def assertRefused(self, command, names):
        reason = self.verdict(command)
        self.assertIsNotNone(reason, f"admitted: {command}")
        self.assertIn(names, reason)
        return reason

    def assertAdmitted(self, command, **kwargs):
        self.assertIsNone(self.verdict(command, **kwargs), f"refused: {command}")


class ARewriteOfATrackedCrlfFileIsRefused(ACheckout):

    def test_the_plain_form_is_refused_with_the_safe_forms_named(self):
        reason = self.assertRefused("sed -i 's/one/uno/' crlf.txt", "crlf.txt")
        self.assertIn("guard-sed-crlf", reason)
        self.assertIn("Edit tool", reason)
        self.assertIn('newline=""', reason)
        self.assertIn("--binary", reason)
        # Review R4.7: the diff shows every line only where the index holds CRLF, as the section says.
        self.assertIn("where the index holds CRLF", reason)

    def test_every_spelling_of_in_place_is_refused(self):
        for command in (
            "sed -i.bak 's/one/uno/' crlf.txt",
            "sed --in-place 's/one/uno/' crlf.txt",
            "sed --in-place=.orig 's/one/uno/' crlf.txt",
            "sed --in-pl 's/one/uno/' crlf.txt",
            "sed -ni 's/one/uno/p' crlf.txt",
            "sed -E -i 's/(o)ne/\\1/' crlf.txt",
            "sed -e 's/one/uno/' -i crlf.txt",
            "sed -i -e 's/one/uno/' -e 's/two/dos/' crlf.txt",
            "sed -i -- 's/one/uno/' crlf.txt",
            # `-ie` is `-i` with the suffix `e`, so the script is the next word and the file is still rewritten.
            "sed -ie 's/one/uno/' crlf.txt",
        ):
            with self.subTest(command=command):
                self.assertRefused(command, "crlf.txt")

    def test_a_file_among_others_is_found(self):
        self.assertRefused("sed -i 's/one/uno/' lf.txt loose.txt crlf.txt", "crlf.txt")

    def test_a_sed_after_a_separator_or_a_prefix_is_judged(self):
        for command in (
            "git status && sed -i 's/one/uno/' crlf.txt",
            "true; sed -i 's/one/uno/' crlf.txt",
            "true\nsed -i 's/one/uno/' crlf.txt",
            "LC_ALL=C sed -i 's/one/uno/' crlf.txt",
            "command sed -i 's/one/uno/' crlf.txt",
            "/usr/bin/sed -i 's/one/uno/' crlf.txt",
            "sed -i 's/one/uno/' crlf.txt 2>/dev/null",
        ):
            with self.subTest(command=command):
                self.assertRefused(command, "crlf.txt")

    def test_a_continued_line_and_a_comment_are_read_as_bash_reads_them(self):
        # Review R1: an indented continuation reached `shlex` as a newline token, read as a separator, and an
        # apostrophe in a comment as an unbalanced quote, so both admitted the whole command.
        for command in (
            "sed -i \\\n  -e 's/one/uno/' \\\n  crlf.txt",
            "# don't touch the build\nsed -i 's/one/uno/' crlf.txt",
            "true # it's fine\nsed -i 's/one/uno/' crlf.txt",
        ):
            with self.subTest(command=command):
                self.assertRefused(command, "crlf.txt")

    def test_what_only_looks_like_a_heredoc_leaves_the_lines_after_it_judged(self):
        # Review R2: a here-string and an arithmetic shift matched the heredoc opener, and every later line went
        # unjudged.
        for command in (
            "cat <<< 'one'\nsed -i 's/one/uno/' crlf.txt",
            "echo $((1<<2))\nsed -i 's/one/uno/' crlf.txt",
            # Review R8: a digit-only rule left a named shift opening a heredoc.
            "echo $((x<<n))\nsed -i 's/one/uno/' crlf.txt",
            "(( y = x << n ))\nsed -i 's/one/uno/' crlf.txt",
            "echo '<<EOF'\nsed -i 's/one/uno/' crlf.txt",
        ):
            with self.subTest(command=command):
                self.assertRefused(command, "crlf.txt")

    def test_a_shell_s_command_line_is_judged(self):
        # Review R3: a `bash -c` line is an ordinary launcher, and its text is all there to read.
        for command in (
            "bash -c \"sed -i 's/one/uno/' crlf.txt\"",
            "sh -lc \"cd sub && sed -i 's/one/uno/' inner.txt\"",
            "bash -c 'bash -c \"sed -i s/one/uno/ crlf.txt\"'",
        ):
            with self.subTest(command=command):
                self.assertRefused(command, ".txt")

    def test_a_sed_behind_a_wrapper_is_judged(self):
        # Review R3, round 2: the wrappers the threat model names run the command after their own options.
        for command in (
            "env LC_ALL=C sed -i 's/one/uno/' crlf.txt",
            "env -u LANG sed -i 's/one/uno/' crlf.txt",
            "nice -n 5 sed -i 's/one/uno/' crlf.txt",
            "timeout 10 sed -i 's/one/uno/' crlf.txt",
            "timeout -s KILL 10 sed -i 's/one/uno/' crlf.txt",
            "stdbuf -oL sed -i 's/one/uno/' crlf.txt",
        ):
            with self.subTest(command=command):
                self.assertRefused(command, "crlf.txt")

    def test_a_sed_behind_a_prefix_word_s_options_is_judged(self):
        # A prefix word's own option was read as the command word, so the sed behind it went unjudged.
        for command in (
            "time -p sed -i 's/one/uno/' crlf.txt",
            "command -p sed -i 's/one/uno/' crlf.txt",
            "exec -a name sed -i 's/one/uno/' crlf.txt",
            "exec -cl sed -i 's/one/uno/' crlf.txt",
            "nohup -- sed -i 's/one/uno/' crlf.txt",
            "time -p command -p sed -i 's/one/uno/' crlf.txt",
            # A cluster whose last letter takes a value takes the next word with it.
            "exec -ca name sed -i 's/one/uno/' crlf.txt",
            "exec -cla name sed -i 's/one/uno/' crlf.txt",
            "env -iu LANG sed -i 's/one/uno/' crlf.txt",
            # GNU time, reached as a program, takes its format and output file as separate words.
            "env time -o log sed -i 's/one/uno/' crlf.txt",
            "command time -f %e sed -i 's/one/uno/' crlf.txt",
            # A long option's unique prefix is that option, as getopt_long reads it.
            "env --un LANG sed -i 's/one/uno/' crlf.txt",
            "env time --out log sed -i 's/one/uno/' crlf.txt",
            "env -- sed -i 's/one/uno/' crlf.txt",
        ):
            with self.subTest(command=command):
                self.assertRefused(command, "crlf.txt")

    def test_a_line_fed_to_a_shell_is_judged(self):
        # Review R3, round 2: a heredoc, a here-string or an echo piped into a shell is a command line it runs.
        for command in (
            "bash <<'EOF'\nsed -i 's/one/uno/' crlf.txt\nEOF\n",
            "bash -s <<EOF\ncd sub\nsed -i 's/one/uno/' inner.txt\nEOF\n",
            "bash <<< \"sed -i 's/one/uno/' crlf.txt\"",
            "echo \"sed -i 's/one/uno/' crlf.txt\" | bash",
            # Review R3.2: a `cat` passes its heredoc or here-string down the pipe.
            "cat <<'EOF' | bash\nsed -i 's/one/uno/' crlf.txt\nEOF\n",
            "cat <<< \"sed -i 's/one/uno/' crlf.txt\" | sh",
            "bash -o pipefail -c \"sed -i 's/one/uno/' crlf.txt\"",
        ):
            with self.subTest(command=command):
                self.assertRefused(command, ".txt")

    def test_a_shell_option_s_value_is_not_its_operand(self):
        # Issue 528: only `-o` and `+o` took a value, so `-O extglob` read as a script and the line went unjudged.
        for command in (
            "bash -O extglob -c \"sed -i 's/one/uno/' crlf.txt\"",
            "bash +O extglob -c \"sed -i 's/one/uno/' crlf.txt\"",
            "bash -O extglob <<'EOF'\nsed -i 's/one/uno/' crlf.txt\nEOF\n",
            # Each `o` or `O` in a cluster takes a word, and the `-c` line is the first operand after them all.
            "bash -eo pipefail -c \"sed -i 's/one/uno/' crlf.txt\"",
            "bash -oO pipefail extglob -c \"sed -i 's/one/uno/' crlf.txt\"",
            "bash -co pipefail \"sed -i 's/one/uno/' crlf.txt\"",
            "bash -c -O extglob \"sed -i 's/one/uno/' crlf.txt\"",
            "bash +c \"sed -i 's/one/uno/' crlf.txt\"",
            "bash --rcfile rc -c \"sed -i 's/one/uno/' crlf.txt\"",
            # Bash reads its long options first, with one dash or two, so the `o` in `-login` takes no word.
            "bash -login -c \"sed -i 's/one/uno/' crlf.txt\"",
            "bash -noprofile -norc -O extglob -c \"sed -i 's/one/uno/' crlf.txt\"",
            "bash -rcfile rc -c \"sed -i 's/one/uno/' crlf.txt\"",
        ):
            with self.subTest(command=command):
                self.assertRefused(command, "crlf.txt")
        # A script after the value still reads its input as data.
        self.assertAdmitted("bash -O extglob run.sh <<EOF\nsed -i 's/one/uno/' crlf.txt\nEOF\n")

    def test_a_cd_moves_the_files_after_it(self):
        self.assertRefused("cd sub && sed -i 's/one/uno/' inner.txt", "inner.txt")
        self.assertRefused("(cd sub && sed -i 's/one/uno/' inner.txt)", "inner.txt")

    def test_a_cd_in_a_subshell_or_a_pipeline_stays_there(self):
        # Review R4: bash runs a `( … )` group and each pipeline stage in a subshell, so the `cd` is gone after it.
        for command in (
            "(cd sub && true); sed -i 's/one/uno/' crlf.txt",
            "(cd sub; (cd ..)); sed -i 's/one/uno/' crlf.txt",
            "cd sub | true; sed -i 's/one/uno/' crlf.txt",
            # Review R3.3: `shlex` returns `)>` as one token, which read as a redirection never closed the group.
            "(cd sub && true)>out.log; sed -i 's/one/uno/' crlf.txt",
            "(cd sub && true)2>&1; sed -i 's/one/uno/' crlf.txt",
        ):
            with self.subTest(command=command):
                self.assertRefused(command, "crlf.txt")

    def test_a_sed_inside_a_clause_or_a_group_is_judged(self):
        for command in (
            "if true; then sed -i 's/one/uno/' crlf.txt; fi",
            "{ sed -i 's/one/uno/' crlf.txt; }",
            "! sed -i 's/one/uno/' crlf.txt",
        ):
            with self.subTest(command=command):
                self.assertRefused(command, "crlf.txt")

    def test_a_path_spelled_from_elsewhere_is_judged(self):
        self.assertRefused("sed -i 's/one/uno/' sub/inner.txt", "sub/inner.txt")
        absolute = os.path.join(self.root, "crlf.txt")
        self.assertRefused(f"sed -i 's/one/uno/' '{absolute}'", absolute)

    def test_a_git_bash_drive_path_is_the_windows_one(self):
        # Review R6: Git Bash spells an absolute path `/c/...`, which Python on Windows reads as `C:\c\...`. On
        # another platform that spelling is an ordinary path, and the translation must leave it alone.
        if os.name != "nt":
            self.assertEqual("/c/dev/x.txt", GUARD.native("/c/dev/x.txt"))
            return
        drive, rest = os.path.splitdrive(self.root)
        msys = "/" + drive[0].lower() + rest.replace("\\", "/")
        self.assertRefused(f"sed -i 's/one/uno/' '{msys}/crlf.txt'", f"{msys}/crlf.txt")
        self.assertRefused(f"cd '{msys}/sub' && sed -i 's/one/uno/' inner.txt", "inner.txt")

    def test_a_glob_is_expanded_as_bash_would(self):
        # Review R7: the refusal names the file the glob matched, since the pattern is not what is tracked.
        reason = self.assertRefused("sed -i 's/one/uno/' *.txt", os.path.join(self.root, "crlf.txt"))
        self.assertIn("which *.txt matches", reason)

    def test_a_brace_list_is_expanded_as_bash_would(self):
        # The operand was looked up as one literal path, found missing and admitted, though bash expands it.
        self.put("n02.txt", b"one\r\n")
        self.git("-c", "core.autocrlf=false", "add", "n02.txt")
        for command, names in (
            ("sed -i 's/one/uno/' {lf,crlf}.txt", "crlf.txt, which {lf,crlf}.txt matches"),
            ("sed -i 's/one/uno/' sub/{other,inner}.txt", "inner.txt, which sub/{other,inner}.txt matches"),
            ("sed -i 's/one/uno/' *.{md,txt}", "which *.{md,txt} matches"),
            ("sed -i 's/one/uno/' n{01..03}.txt", "n02.txt, which n{01..03}.txt matches"),
            ("sed -i 's/one/uno/' {a..c}rlf.txt", "crlf.txt, which {a..c}rlf.txt matches"),
        ):
            with self.subTest(command=command):
                self.assertRefused(command, names)

    def test_a_brace_list_s_literal_name_is_read_as_well(self):
        # The hook cannot tell a quoted brace list from a bare one, so it reads both, which can only refuse more.
        self.put("{x,y}.txt", b"one\r\n")
        self.git("-c", "core.autocrlf=false", "add", "{x,y}.txt")
        self.assertRefused("sed -i 's/one/uno/' '{x,y}.txt'", "{x,y}.txt")

    def test_a_pathspec_character_in_a_name_is_literal(self):
        # Review R7: as a pathspec, an untracked `crlf[x].txt` would match the tracked `crlfx.txt`.
        self.put("crlfx.txt", b"one\r\n")
        self.git("-c", "core.autocrlf=false", "add", "crlfx.txt")
        self.put("crlf[x].txt", b"one\r\n")
        self.assertTrue(GUARD.tracked(os.path.join(self.root, "crlfx.txt"), GIT))
        self.assertFalse(GUARD.tracked(os.path.join(self.root, "crlf[x].txt"), GIT))


class WhatSedLeavesAloneIsAdmitted(ACheckout):

    def test_a_tracked_lf_file(self):
        self.assertAdmitted("sed -i 's/one/uno/' lf.txt")

    def test_an_untracked_crlf_file(self):
        self.assertAdmitted("sed -i 's/one/uno/' loose.txt")

    def test_a_brace_list_naming_no_tracked_crlf_file(self):
        for command in (
            "sed -i 's/one/uno/' {lf,loose}.txt",
            # No comma and no sequence: bash passes it literally, and no file has that name.
            "sed -i 's/one/uno/' {crlf}.txt",
        ):
            with self.subTest(command=command):
                self.assertAdmitted(command)

    def test_the_binary_forms_that_keep_the_cr(self):
        for command in (
            "sed --binary -i 's/one/uno/' crlf.txt",
            "sed -b -i 's/one/uno/' crlf.txt",
            "sed -bi 's/one/uno/' crlf.txt",
            "sed -i --bin 's/one/uno/' crlf.txt",
        ):
            with self.subTest(command=command):
                self.assertAdmitted(command)

    def test_a_sed_that_writes_nothing_in_place(self):
        for command in (
            "sed 's/one/uno/' crlf.txt",
            "sed -n '1p' crlf.txt > out.txt",
            # With no -e the first operand is the script, so this names no file at all.
            "sed -i crlf.txt",
            "sed --help",
        ):
            with self.subTest(command=command):
                self.assertAdmitted(command)

    def test_a_command_that_only_mentions_sed(self):
        for command in (
            "grep -i one crlf.txt",
            "echo sed -i 's/one/uno/' crlf.txt",
            "git log --grep='sed -i' -- crlf.txt",
        ):
            with self.subTest(command=command):
                self.assertAdmitted(command)

    def test_a_heredoc_body_is_data(self):
        for opener in ("<<'EOF'", "<<EOF", '<<"EOF"', "<<\\EOF", "<< EOF"):
            with self.subTest(opener=opener):
                self.assertAdmitted(f"cat > fix.sh {opener}\nsed -i 's/one/uno/' crlf.txt\nEOF\n")
        self.assertAdmitted("cat > fix.sh <<-EOF\n\tsed -i 's/one/uno/' crlf.txt\n\tEOF\n")
        # A script reads its input as data, so a body handed to one is not its command line.
        self.assertAdmitted("bash run.sh <<EOF\nsed -i 's/one/uno/' crlf.txt\nEOF\n")
        self.assertAdmitted("echo \"sed -i 's/one/uno/' crlf.txt\" | cat")

    def test_a_platform_whose_sed_keeps_the_cr(self):
        # GNU sed on Linux and macOS treats the CR as an ordinary character, so a refusal there would block a
        # harmless edit: the guard's criterion 3.
        self.assertAdmitted("sed -i 's/one/uno/' crlf.txt", strips_cr=False)

    def test_another_tool(self):
        self.assertAdmitted("sed -i 's/one/uno/' crlf.txt", tool="PowerShell")


class WhatTheHookCannotPlaceFailsOpen(ACheckout):
    """The issue's own rule: a path the hook cannot resolve is let through rather than blocking work."""

    def test_unplaceable_operands_and_commands(self):
        for command in (
            "sed -i 's/one/uno/' missing.txt",
            "sed -i 's/one/uno/' \"$FILE\"",
            "sed -i 's/one/uno/' $(git ls-files)",
            "sed -i 's/one/uno/' sub",
            "cd \"$DIR\" && sed -i 's/one/uno/' crlf.txt",
            "sed -i 's/one/uno/ crlf.txt",
        ):
            with self.subTest(command=command):
                self.assertAdmitted(command)

    def test_a_glob_walk_stops_at_the_bound(self):
        # Review R5: the bound was checked after the whole glob had been walked.
        walked = []

        def endless(pattern, *args, **kwargs):
            for number in range(10 * GUARD.MAX_FILES):
                walked.append(number)
                yield os.path.join(self.root, f"absent-{number}.txt")

        original = GUARD.glob.iglob
        GUARD.glob.iglob = endless
        self.addCleanup(setattr, GUARD.glob, "iglob", original)
        self.assertAdmitted("sed -i 's/one/uno/' *.cs")
        self.assertLessEqual(len(walked), GUARD.MAX_FILES + 1)
        # One bound for the operand, not one for each word its braces make.
        walked.clear()
        self.assertAdmitted("sed -i 's/one/uno/' {a,b,c,d}*.cs")
        self.assertLessEqual(len(walked), GUARD.MAX_FILES + 1)

    def test_a_comma_quoted_inside_a_brace_list_is_the_stated_residual(self):
        # Review R1: the quotes are gone before the list is read, so the `a,b.txt` bash passes is never looked up.
        self.put("a,b.txt", b"one\r\n")
        self.git("-c", "core.autocrlf=false", "add", "a,b.txt")
        self.assertRefused("sed -i 's/one/uno/' a,b.txt", "a,b.txt")
        self.assertAdmitted("sed -i 's/one/uno/' {\"a,b\",c}.txt")

    def test_a_long_sequence_is_cut_at_the_bound(self):
        # A sequence is made lazily, so `{1..N}` costs the words read and not N.
        self.assertEqual(["1", "2"], list(GUARD.itertools.islice(GUARD.braces("{1..1000000000000}"), 2)))
        self.assertAdmitted("sed -i 's/one/uno/' n{1..1000000000000}.txt")

    def test_a_file_list_another_program_computes_is_the_stated_residual(self):
        # Pinned as admitted, because a residual nobody can run is one the next reader assumes was closed: the files
        # come from the other program's output, which no text reader is given.
        for command in (
            "git ls-files '*.txt' | xargs sed -i 's/one/uno/'",
            "find . -name '*.txt' -exec sed -i 's/one/uno/' {} +",
        ):
            with self.subTest(command=command):
                self.assertAdmitted(command)

    def test_a_printf_into_a_shell_is_read_as_its_words(self):
        # Review R3.5: the residual the section states, pinned both ways rather than claimed admitted. A format that
        # holds the sed is refused; an argument that carries the whole line is admitted, though bash would run it.
        self.assertRefused("printf \"sed -i 's/one/uno/' %s\" crlf.txt | bash", "crlf.txt")
        self.assertAdmitted("printf '%s\\n' \"sed -i 's/one/uno/' crlf.txt\" | bash")

    def test_no_git_admits(self):
        original = GUARD.git_on_path
        GUARD.git_on_path = lambda cwd: None
        self.addCleanup(setattr, GUARD, "git_on_path", original)
        self.assertAdmitted("sed -i 's/one/uno/' crlf.txt")


class BracesExpandAsBashExpandsThem(unittest.TestCase):
    """The words `braces` makes, each against what bash 5's `echo` prints for the same word."""

    def test_the_forms_bash_expands(self):
        for word, words in (
            ("plain.txt", ["plain.txt"]),
            ("a{b,c}d{e,f}", ["abde", "abdf", "acde", "acdf"]),
            ("x{,.bak}", ["x", "x.bak"]),
            ("{a,{b,c}d}", ["a", "bd", "cd"]),
            ("{1..3}", ["1", "2", "3"]),
            ("{3..1}", ["3", "2", "1"]),
            ("{-1..1}", ["-1", "0", "1"]),
            ("{01..3}", ["01", "02", "03"]),
            ("{1..7..3}", ["1", "4", "7"]),
            ("{a..e..2}", ["a", "c", "e"]),
        ):
            with self.subTest(word=word):
                self.assertEqual(words, list(GUARD.braces(word)))

    def test_the_forms_bash_leaves_as_text(self):
        for word, words in (
            ("{a}", ["{a}"]),
            ("{}", ["{}"]),
            ("{a,b", ["{a,b"]),
            ("{1..a}", ["{1..a}"]),
            ("{ab..c}", ["{ab..c}"]),
            # A brace that is not an expansion is text, and one after it still expands.
            ("{a}{b,c}", ["{a}b", "{a}c"]),
            ("{{a,b}}", ["{a}", "{b}"]),
            ("{x{a,b}y}", ["{xay}", "{xby}"]),
        ):
            with self.subTest(word=word):
                self.assertEqual(words, list(GUARD.braces(word)))


class AGitPlantedWhereTheHookStands(ACheckout):
    """A planted git answers 0 to everything and leaves a mark, so it would refuse the untracked `loose.txt`."""

    def setUp(self):
        super().setUp()
        self.mark = os.path.join(self.root, "planted-git-ran")
        # Held here so a case that stands in it is back out before it goes: Windows keeps a working directory.
        holder = tempfile.TemporaryDirectory()
        self.addCleanup(holder.cleanup)
        self.elsewhere = holder.name
        self.addCleanup(os.chdir, os.getcwd())
        os.chdir(self.root)
        # A Claude Code shell sets this, which stops Windows searching the current directory, and a hook cannot rely
        # on that: a case that inherited it would pass over the search it is here to judge.
        environment = mock.patch.dict(os.environ)
        environment.start()
        self.addCleanup(environment.stop)
        os.environ.pop("NoDefaultCurrentDirectoryInExePath", None)

    def executable(self, folder, name, text="x"):
        os.makedirs(folder, exist_ok=True)
        path = os.path.join(folder, name)
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(text)
        os.chmod(path, 0o755)

    def plant(self, folder):
        lines = [f'@echo ran> "{self.mark}"', "@exit /b 0"] if os.name == "nt" else [
            "#!/bin/sh", f"echo ran > '{self.mark}'"]
        name = "git.cmd" if os.name == "nt" else "git"
        self.executable(folder, name, "".join(line + chr(10) for line in lines))

    def found(self, path, cwd=None):
        with mock.patch.dict(os.environ, {"PATH": path}):
            return GUARD.git_on_path(cwd or os.getcwd())

    def test_it_is_not_run_and_the_git_on_path_answers(self):
        self.plant(self.root)
        real = os.path.dirname(GIT)
        for path in (real, os.pathsep.join(["", ".", real])):
            with self.subTest(path=path), mock.patch.dict(os.environ, {"PATH": path}):
                self.assertAdmitted("sed -i 's/one/uno/' loose.txt")
                self.assertRefused("sed -i 's/one/uno/' crlf.txt", "crlf.txt")
                self.assertFalse(os.path.exists(self.mark), "the planted git ran")

    def test_a_git_elsewhere_on_path_is_found(self):
        self.executable(self.elsewhere, EXECUTABLE)
        self.assertEqual(os.path.join(self.elsewhere, EXECUTABLE), self.found(self.elsewhere))

    def test_a_git_at_or_below_where_the_session_stands_reads_as_no_git(self):
        tools = os.path.join(self.root, "tools")
        for folder in (self.root, tools):
            self.plant(folder)
            self.executable(folder, EXECUTABLE)
        path = os.pathsep.join(["", ".", self.root, tools])
        self.assertIsNone(self.found(path, cwd=self.elsewhere), "the working directory is not fenced")
        os.chdir(self.elsewhere)
        self.assertIsNone(self.found(path, cwd=self.root), "the event's cwd is not fenced")
        with mock.patch.dict(os.environ, {"CLAUDE_PROJECT_DIR": self.root}):
            self.assertIsNone(self.found(path), "the project directory is not fenced")
        self.assertFalse(os.path.exists(self.mark), "the planted git ran")

    def test_a_relative_path_entry_is_not_searched(self):
        self.executable(self.elsewhere, EXECUTABLE)
        self.assertIsNone(self.found(os.path.relpath(self.elsewhere, self.root)))
        if os.name == "nt":
            self.assertIsNone(self.found(os.path.splitdrive(self.elsewhere)[1]), "a rooted entry has no drive")

    def test_a_batch_file_is_not_git_wherever_it_sits(self):
        # `cmd.exe` would read an `&` in the operand as its own, which `subprocess` does not quote.
        for name in ("git.cmd", "git.bat"):
            self.executable(self.elsewhere, name)
        self.assertIsNone(self.found(self.elsewhere))


class TheHookAsTheHarnessRunsIt(ACheckout):

    def run_hook(self, payload):
        return subprocess.run([sys.executable, str(HOOK)], input=payload.encode("utf-8"), capture_output=True)

    def test_the_host_gives_its_own_answer(self):
        event = {"hook_event_name": "PreToolUse", "cwd": self.root, "tool_name": "Bash",
                 "tool_input": {"command": "sed -i 's/one/uno/' crlf.txt"}}
        result = self.run_hook(json.dumps(event))
        self.assertEqual(0, result.returncode)
        if os.name == "nt":
            verdict = json.loads(result.stdout)["hookSpecificOutput"]
            self.assertEqual("deny", verdict["permissionDecision"])
            self.assertIn("crlf.txt", verdict["permissionDecisionReason"])
        else:
            self.assertEqual(b"", result.stdout.strip())

    def test_a_malformed_event_does_not_take_the_session_down(self):
        for payload in ("not json at all", "[1, 2, 3]", ""):
            with self.subTest(payload=payload):
                result = self.run_hook(payload)
                self.assertEqual(0, result.returncode)
                self.assertEqual(b"", result.stdout.strip())
                self.assertIn(b"guard-sed-crlf", result.stderr)


class TheWiringWithoutWhichNoneOfTheAboveRuns(unittest.TestCase):

    def commands_for(self, tool):
        settings = json.loads(SETTINGS.read_text(encoding="utf-8"))
        return [
            hook.get("command") or ""
            for entry in settings.get("hooks", {}).get("PreToolUse", [])
            if entry.get("matcher") == tool
            for hook in entry.get("hooks") or []
        ]

    def test_the_hook_is_registered_on_bash_on_the_312_floor(self):
        mine = [command for command in self.commands_for("Bash") if HOOK.name in command]
        self.assertEqual(1, len(mine), f"{HOOK.name} is not registered once on Bash: {self.commands_for('Bash')}")
        assert_launched_on_the_floor(self, mine[0], HOOK.name)

    def test_the_guards_beside_it_are_still_registered(self):
        # An entry added to a list is the shape most likely to be written as a replacement.
        commands = self.commands_for("Bash")
        for guard in ("guard-git-argv.py", "guard-index-argv.py"):
            with self.subTest(guard=guard):
                self.assertTrue(any(guard in command for command in commands), commands)


if __name__ == "__main__":
    unittest.main()
