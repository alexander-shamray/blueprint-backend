"""What `.claude/hooks/guard-sed-crlf.py` refuses, and what it must not.

The verdict cases call the hook's `offence` with `strips_cr=True`, because only Git Bash's sed drops the CR and the
cases run on every `payload-suites` leg, Linux and macOS as well as Windows; the process cases run the hook as the
harness does and assert the host's own answer, and the Windows leg is the one that reaches the hook's refusal and its
`/c/...` translation. The last class has the registration as its subject, since every case above passes against a hook
the harness never calls.
"""

import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
HOOK = SCRIPTS.parent / "hooks" / "guard-sed-crlf.py"
SETTINGS = SCRIPTS.parent / "settings.json"
GIT = shutil.which("git")


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
        self.root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)
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
        original = GUARD.shutil.which
        GUARD.shutil.which = lambda name: None
        self.addCleanup(setattr, GUARD.shutil, "which", original)
        self.assertAdmitted("sed -i 's/one/uno/' crlf.txt")


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
        self.assertIn("py -3.12", mine[0])
        self.assertIn("CLAUDE_PROJECT_DIR", mine[0])

    def test_the_guards_beside_it_are_still_registered(self):
        # An entry added to a list is the shape most likely to be written as a replacement.
        commands = self.commands_for("Bash")
        for guard in ("guard-git-argv.py", "guard-index-argv.py"):
            with self.subTest(guard=guard):
                self.assertTrue(any(guard in command for command in commands), commands)


if __name__ == "__main__":
    unittest.main()
