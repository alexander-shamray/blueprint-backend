"""Patch the git and index argv guards, their suites and settings.json, one fix at a time.

Run by apply.sh as `patch_guards.py <root> <part>`, each fix's tests before its code, so every new case is seen to
fail first; the parts are the branches of main below.
"""

import sys
from pathlib import Path


def rewrite(path, change):
    """Apply `change` to the LF text of `path`, keeping the file's own line endings."""
    text = path.read_bytes().decode("utf-8")
    newline = "\r\n" if "\r\n" in text else "\n"
    lf = text.replace("\r\n", "\n")
    out = change(lf)
    assert out != lf, f"{path}: nothing changed"
    path.write_bytes(out.replace("\n", newline).encode("utf-8"))


def once(text, anchor, replacement):
    assert text.count(anchor) == 1, f"anchor not found exactly once: {anchor[:60]!r}"
    return text.replace(anchor, replacement)


def after(text, anchor, addition):
    return once(text, anchor, anchor + addition)


def before(text, anchor, addition):
    return once(text, anchor, addition + anchor)


def main_body(text):
    """The text from `def main():` up to the `__main__` guard."""
    start = text.index("\ndef main():\n") + 1
    end = text.index('\n\nif __name__ == "__main__":')
    return start, end


# ---- decode: read the event's bytes as UTF-8 and refuse an event that is not ----

REFUSE = '''def refuse(reason):
    """Print the deny JSON for `reason`; the exit status stays 0 either way."""
    json.dump(
        {
            "hookSpecificOutput": {
                "hookEventName": "PreToolUse",
                "permissionDecision": "deny",
                "permissionDecisionReason": reason,
            }
        },
        sys.stdout,
    )
    return 0


'''

READ_BYTES = '''    # The bytes, decoded as UTF-8: Python reads a Windows pipe in the ANSI code
    # page, which raises on bytes UTF-8 uses, and that raise reached the
    # fail-open below. An event that is not UTF-8 holds a command nothing read.
    try:
        text = sys.stdin.buffer.read().decode("utf-8")
    except UnicodeDecodeError:
        return refuse(
            "this hook event is not UTF-8, so the command in it cannot be "
            "read; refusing rather than admitting what nothing judged.")
'''

DENY_TAIL = '''    json.dump(
        {
            "hookSpecificOutput": {
                "hookEventName": "PreToolUse",
                "permissionDecision": "deny",
                "permissionDecisionReason": reason,
            }
        },
        sys.stdout,
    )
    return 0
'''


def decode_main(text):
    start, end = main_body(text)
    body = text[start:end]
    assert "    try:\n        event = json.load(sys.stdin)\n" in body, "main no longer reads sys.stdin as text"
    body = once(body, "def main():\n    try:\n        event = json.load(sys.stdin)\n",
                "def main():\n" + READ_BYTES + "    try:\n        event = json.loads(text)\n")
    body = once(body, DENY_TAIL, "    return refuse(reason)\n")
    return text[:start] + REFUSE + body + text[end:]


GIT_IMPORTS = "import importlib.util\nimport json\n"

GIT_RAW = '''
    def judge_raw(self, payload):
        """The verdict on raw event bytes, read as this host's pipe reads them."""
        # PYTHONIOENCODING stands in for the Windows pipe: text read from stdin
        # is cp1252, strict, so a reader that decodes text instead of bytes raises.
        result = subprocess.run(
            [sys.executable, str(HOOK)], input=payload, capture_output=True,
            timeout=HOOK_TIMEOUT, env=dict(os.environ, PYTHONIOENCODING="cp1252"))
        self.assertEqual(0, result.returncode, result.stderr)
        if not result.stdout.strip():
            return None
        decision = json.loads(result.stdout)["hookSpecificOutput"]
        self.assertEqual("deny", decision["permissionDecision"])
        return decision["permissionDecisionReason"]

    @staticmethod
    def raw_event(command):
        """`command` in an event as the harness sends it, unescaped UTF-8."""
        event = {"tool_name": "Bash", "tool_input": {"command": command}}
        return json.dumps(event, ensure_ascii=False).encode("utf-8")

    def test_a_character_cp1252_cannot_decode_does_not_skip_the_judgement(self):
        # U+23F3 is E2 8F B3 in UTF-8, and 0x8F is undefined in cp1252.
        hourglass = "\\u23f3"
        self.assertIsNotNone(self.judge_raw(self.raw_event(f"git push origin +HEAD:main # {hourglass}")))
        self.assertIsNone(self.judge_raw(self.raw_event(f"git status # {hourglass}")))

    def test_an_event_that_is_not_utf8_is_refused(self):
        reason = self.judge_raw(self.raw_event("git status X").replace(b"X", b"\\xff"))
        self.assertIsNotNone(reason)
        self.assertIn("UTF-8", reason)
'''

GIT_MALFORMED_END = '''        self.assertEqual("", result.stdout.strip())
        self.assertIn("guard-git-argv", result.stderr)
'''

INDEX_IMPORTS = "import importlib.util\nimport json\n"

INDEX_RAW = '''    def judge_raw(self, payload):
        """The verdict on raw event bytes, read as this host's pipe reads them."""
        # PYTHONIOENCODING stands in for the Windows pipe: text read from stdin
        # is cp1252, strict, so a reader that decodes text instead of bytes raises.
        result = subprocess.run(
            [sys.executable, str(HOOK)], input=payload, capture_output=True,
            env=dict(os.environ, PYTHONIOENCODING="cp1252"))
        self.assertEqual(0, result.returncode, result.stderr)
        if not result.stdout.strip():
            return None
        payload = json.loads(result.stdout)["hookSpecificOutput"]
        self.assertEqual("deny", payload["permissionDecision"])
        return payload["permissionDecisionReason"]

    @staticmethod
    def raw_event(command):
        """`command` in an event as the harness sends it, unescaped UTF-8."""
        event = {"hook_event_name": "PreToolUse", "tool_name": "Bash", "tool_input": {"command": command}}
        return json.dumps(event, ensure_ascii=False).encode("utf-8")

    def test_a_character_cp1252_cannot_decode_does_not_skip_the_judgement(self):
        # U+23F3 is E2 8F B3 in UTF-8, and 0x8F is undefined in cp1252.
        cbx = "bash .claude/skills/codebase-index/scripts/cbx search"
        self.assertIsNotNone(self.judge_raw(self.raw_event(f'{cbx} "\\u23f3"; rm -rf x')))
        self.assertIsNone(self.judge_raw(self.raw_event(f'{cbx} "\\u23f3" --json')))

    def test_an_event_that_is_not_utf8_is_refused(self):
        cbx = "bash .claude/skills/codebase-index/scripts/cbx search X --json"
        reason = self.judge_raw(self.raw_event(cbx).replace(b"X", b"\\xff"))
        self.assertIsNotNone(reason)
        self.assertIn("UTF-8", reason)

'''

INDEX_REGISTERED = "    def test_the_hook_is_registered_for_bash(self):\n"


def decode_git_tests(text):
    text = once(text, GIT_IMPORTS, GIT_IMPORTS + "import os\n")
    # The in-process runner hands main bytes, the stream it now reads.
    text = once(text, "            _sys.stdin = _io.StringIO(event)\n",
                '            _sys.stdin = _io.TextIOWrapper(_io.BytesIO(event.encode("utf-8")), encoding="utf-8")\n')
    return after(text, GIT_MALFORMED_END, GIT_RAW)


def decode_index_tests(text):
    text = once(text, INDEX_IMPORTS, INDEX_IMPORTS + "import os\n")
    return before(text, INDEX_REGISTERED, INDEX_RAW)


# ---- heredoc: an unterminated body that breaks the parse is refused outright ----

HEREDOC_TEST_ANCHOR = '''        self.assertRefused(
            f"cat <<NEVERCLOSED{NEWLINE}git push origin +HEAD:main")
'''

HEREDOC_TEST = '''
    def test_an_unterminated_heredoc_that_will_not_tokenise_is_refused(self):
        # The body's apostrophe breaks the parse, and the fallback's crude scan
        # cannot see a quote-split update; a closed body tokenises as before.
        self.assertRefused(f"git pu''sh origin +HEAD:main <<'EOF'{NEWLINE}it's open")
        self.assertAdmitted(f"git commit -F - <<'EOF'{NEWLINE}it's done{NEWLINE}EOF")
'''

UNCLOSED_BREAK = '''            # commands. Scanning the tail is wrong only in the safe direction.
            break
'''

FALLBACK_ANCHOR = "        # The push check reaches this path too, because a line is easy to make\n"

FALLBACK = '''        # An unterminated heredoc leaves its body in the string, so a quote in
        # the body is what failed the parse, and the checks below would read the
        # body as commands; refused outright, as an undecodable delimiter is.
        unclosed = []
        heredoc_spans(command, unclosed)
        if unclosed:
            return (
                "a heredoc here has no closing delimiter and the command "
                "cannot be tokenised, so what bash runs cannot be read; "
                "refusing rather than judging its body as commands."
            )
'''


def heredoc_hook(text):
    text = once(text, "def heredoc_spans(command):\n", "def heredoc_spans(command, unclosed=None):\n")
    text = once(text, UNCLOSED_BREAK, UNCLOSED_BREAK.replace(
        "            break\n",
        "            if unclosed is not None:\n                unclosed.append(index)\n            break\n"))
    return before(text, FALLBACK_ANCHOR, FALLBACK)


# ---- powershell: a git call through that tool is refused, and the hook is wired to it ----

POWERSHELL_TEST_ANCHOR = '''        self.assertIsNone(self.judge("git push origin +HEAD:main", tool="Read"))
'''

POWERSHELL_TEST = '''
    def test_git_through_powershell_is_refused(self):
        # This guard parses Bash's grammar only, so a PowerShell call naming git
        # is refused whatever its arguments, a backtick-split name included.
        for command in ("git push origin +HEAD:main", "git status", "g`it log", "& 'git' push"):
            with self.subTest(command=command):
                self.assertIsNotNone(self.judge(command, tool="PowerShell"))
        for command in ("Get-ChildItem", "Get-Content .gitignore"):
            with self.subTest(command=command):
                self.assertIsNone(self.judge(command, tool="PowerShell"))
'''

REGISTERED_ANCHOR = '''            "the hook must run on the 3.12 floor, like every other Python here",
        )
'''

REGISTERED_TEST = '''
    def test_the_hook_is_registered_for_powershell_in_settings(self):
        # The Bash case's lesson again: a PowerShell verdict is silent if no
        # matcher selects that tool for this hook.
        settings = json.loads(SETTINGS.read_text(encoding="utf-8"))
        commands = [
            h.get("command", "")
            for entry in settings.get("hooks", {}).get("PreToolUse", [])
            if "PowerShell" in (entry.get("matcher") or "").split("|")
            for h in entry.get("hooks", [])
        ]
        self.assertTrue(
            any(HOOK.name in c and "py -3.12" in c for c in commands),
            f"{HOOK.name} is not among the registered PowerShell hooks: {commands}")
'''

POWERSHELL_GIT = '''# PowerShell quotes and escapes in a grammar `offence` does not parse, so a git
# call through that tool cannot be judged and is refused; Bash runs git here.
# Backticks and quotes go first, since PowerShell runs g`it as git.
POWERSHELL_GIT = re.compile(r"(?i)(?<![a-z0-9_])git(?![a-z0-9_])")


def powershell_git(command):
    """The refusal for a PowerShell command that names git, or None."""
    if not POWERSHELL_GIT.search(re.sub("[`'\\"]", "", command)):
        return None
    return (
        "git through PowerShell is refused: this guard reads Bash's grammar, "
        "not PowerShell's, so what the call would run cannot be checked. Run "
        "git through the Bash tool."
    )


'''


def powershell_tests(text):
    text = after(text, POWERSHELL_TEST_ANCHOR, POWERSHELL_TEST)
    return after(text, REGISTERED_ANCHOR, REGISTERED_TEST)


def powershell_hook(text):
    text = before(text, "def refuse(reason):\n", POWERSHELL_GIT)
    text = once(text, '    if event.get("tool_name") != "Bash":\n        return 0\n',
                '    tool = event.get("tool_name")\n    if tool not in ("Bash", "PowerShell"):\n        return 0\n')
    return once(text, "        reason = scan_budget(command) or offence(command)\n",
                '        reason = (powershell_git(command) if tool == "PowerShell"\n'
                "                  else scan_budget(command) or offence(command))\n")


SETTINGS_ANCHOR = '''      {
        "matcher": "Edit|Write|NotebookEdit|MultiEdit",
'''

SETTINGS_ENTRY = '''      {
        "matcher": "PowerShell",
        "hooks": [
          {
            "type": "command",
            "command": "py -3.12 \\"${CLAUDE_PROJECT_DIR}/.claude/hooks/guard-git-argv.py\\"",
            "statusMessage": "checking git argv"
          }
        ]
      },
'''


def main(root, part):
    claude = Path(root) / ".claude"
    git_hook = claude / "hooks" / "guard-git-argv.py"
    index_hook = claude / "hooks" / "guard-index-argv.py"
    git_tests = claude / "scripts" / "test_git_argv_guard.py"
    index_tests = claude / "scripts" / "test_index_argv_guard.py"
    if part == "decode-tests":
        rewrite(git_tests, decode_git_tests)
        rewrite(index_tests, decode_index_tests)
    elif part == "decode-hooks":
        rewrite(git_hook, decode_main)
        rewrite(index_hook, decode_main)
    elif part == "heredoc-tests":
        rewrite(git_tests, lambda t: after(t, HEREDOC_TEST_ANCHOR, HEREDOC_TEST))
    elif part == "heredoc-hook":
        rewrite(git_hook, heredoc_hook)
    elif part == "powershell-tests":
        rewrite(git_tests, powershell_tests)
    elif part == "powershell-hook":
        rewrite(git_hook, powershell_hook)
    elif part == "settings":
        rewrite(claude / "settings.json", lambda t: before(t, SETTINGS_ANCHOR, SETTINGS_ENTRY))
    else:
        raise SystemExit(f"unknown part {part!r}")


if __name__ == "__main__":
    main(*sys.argv[1:])
