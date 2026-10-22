"""Patch review round 1's two findings into the git argv guard and its suite.

Run by apply.sh as `patch_review.py <root> <part>`, each finding's case before its code, so the case is seen to fail
first; the parts are the branches of main below.
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


# ---- R1: PowerShell hands git to another interpreter, or runs a computed name ----

R1_TEST_ANCHOR = '''        for command in ("Get-ChildItem", "Get-Content .gitignore"):
            with self.subTest(command=command):
                self.assertIsNone(self.judge(command, tool="PowerShell"))
'''

R1_TEST = r'''
    def test_powershell_is_admitted_only_as_a_read_cmdlet_over_plain_words(self):
        # Another interpreter has escapes of its own, a launcher's arguments can
        # encode git, and a computed name shows nothing to match.
        for command in ("cmd /c g^it push origin +HEAD:main", "bash -c 'g\\it push origin +HEAD:main'",
                        "wsl gi\\t push origin +HEAD:main", "$c = 'gi' + 't'; & $c push origin +HEAD:main",
                        "iex $command", "Start-Process git", "Write-Output done; Get-ChildItem",
                        "pwsh -EncodedCommand ZwBpAHQAIABzAHQAYQB0AHUAcwA=", "cmd /c run.bat"):
            with self.subTest(command=command):
                self.assertIsNotNone(self.judge(command, tool="PowerShell"))
        for command in ("Write-Output done", "Get-ChildItem -Path docs/backend-architecture"):
            with self.subTest(command=command):
                self.assertIsNone(self.judge(command, tool="PowerShell"))
'''

R1_OLD = r'''# PowerShell quotes and escapes in a grammar `offence` does not parse, so a git
# call through that tool cannot be judged and is refused; Bash runs git here.
# Backticks and quotes go first, since PowerShell runs g`it as git.
POWERSHELL_GIT = re.compile(r"(?i)(?<![a-z0-9_])git(?![a-z0-9_])")


def powershell_git(command):
    """The refusal for a PowerShell command that names git, or None."""
    if not POWERSHELL_GIT.search(re.sub("[`'\"]", "", command)):
        return None
    return (
        "git through PowerShell is refused: this guard reads Bash's grammar, "
        "not PowerShell's, so what the call would run cannot be checked. Run "
        "git through the Bash tool."
    )
'''

R1_NEW = r'''# PowerShell is a language `offence` does not parse, so a command through that
# tool is admitted only as one read-only cmdlet over plain words: no quote,
# escape, variable, call, pipe, separator or wildcard, and no launcher, whose
# arguments (an encoded command, say) can carry git in characters this allows.
POWERSHELL_PLAIN = re.compile(r"[A-Za-z0-9 _./:,=-]*")
POWERSHELL_GIT = re.compile(r"(?i)(?<![a-z0-9_])git(?![a-z0-9_])")
POWERSHELL_READS = frozenset((
    "get-childitem", "get-content", "get-item", "get-location", "resolve-path",
    "select-string", "test-path", "write-output",
))


def powershell_git(command):
    """The refusal for a PowerShell command that is not one read-only cmdlet over plain words, or None."""
    words = command.split()
    if (words and words[0].lower() in POWERSHELL_READS and POWERSHELL_PLAIN.fullmatch(command)
            and not POWERSHELL_GIT.search(command)):
        return None
    return (
        "a PowerShell command is admitted only as one read-only cmdlet over "
        "plain words: this guard reads Bash's grammar, not PowerShell's, so a "
        "launcher, quote, escape, variable, call or separator could run git "
        "unseen. Run git, and anything else, through the Bash tool."
    )
'''


def r1_hook(text):
    return once(text, R1_OLD, R1_NEW)


# ---- R2: a quote-split push ahead of a line that breaks the parse ----

R2_TEST_ANCHOR = '''        self.assertAdmitted(f"git commit -F - <<'EOF'{NEWLINE}it's done{NEWLINE}EOF")
'''

R2_TEST = r'''
    def test_a_quote_split_push_before_an_untokenisable_line_is_refused(self):
        # Bash runs the first line before the second's syntax error, so the
        # fallback has to read the push through its quotes and backslashes.
        self.assertRefused(f"git pu''sh origin +HEAD:main{NEWLINE}echo \"")
        self.assertRefused(f"git pu\\sh origin +HEAD:main{NEWLINE}echo \"")
'''

R2_LOOP = '''        for needle in FORBIDDEN_FLAGS + FORBIDDEN_SUBSTRINGS:
            if needle in stripped:
                return (
                    f"`{needle}` appears in a command this guard could not "
                    "tokenise; refusing on the raw string, which is the weaker "
                    "check the settings deny already performs."
                )
'''

R2_LOOP_NEW = '''        # Bash removes quotes and backslashes too, so `pu''sh` is still push here.
        flat = re.sub(r"[\\\\'\\"]", "", stripped)
        for needle in FORBIDDEN_FLAGS + FORBIDDEN_SUBSTRINGS:
            if needle in flat:
                return (
                    f"`{needle}` appears in a command this guard could not "
                    "tokenise; refusing on the string with its quotes removed, "
                    "a cruder check than the parsed one."
                )
'''

R2_PUSH = '''        if re.search(r"\\bgit\\b[^;&|\\n]*\\bpush\\b", stripped):
'''


R2_PINNED_OLD = """    def test_the_degraded_check_is_the_settings_denys_and_no_stronger(self):
        # The fallback is honestly weaker: a quoted `--output` is exactly what
        # a raw-string scan cannot see, so an unparseable command carrying one
        # is admitted. Pinned, because a guard whose fallback is silently
        # weaker than its main path is one nobody knows the reach of.
        self.assertAdmitted('git log --out""put=/tmp/x "unbalanced')
"""

R2_PINNED_NEW = """    def test_the_degraded_check_reads_through_quotes(self):
        # The fallback used to read the raw string, so a quoted `--output` in an
        # unparseable command was admitted; it now removes quotes as bash does.
        self.assertRefused('git log --out""put=/tmp/x "unbalanced')
"""


def r2_tests(text):
    text = once(text, R2_TEST_ANCHOR, R2_TEST_ANCHOR + R2_TEST)
    return once(text, R2_PINNED_OLD, R2_PINNED_NEW)


def r2_hook(text):
    text = once(text, R2_LOOP, R2_LOOP_NEW)
    return once(text, R2_PUSH, R2_PUSH.replace("stripped):", "flat):"))


# ---- R2b: the fallback's push pattern does not trust a separator quotes may have hidden ----

R2B_TEST_ANCHOR = '''        self.assertRefused(f"git pu\\\\sh origin +HEAD:main{NEWLINE}echo \\"")
'''

R2B_TEST = r'''
    def test_a_quoted_separator_does_not_hide_a_push_from_the_fallback(self):
        # Removing quotes turns a quoted `;` or newline into what reads as a
        # separator, -c reaches git's config, and GIT is git on Windows, so the
        # fallback refuses whatever it could not tokenise.
        self.assertRefused(f"git --git-dir=\";\" pu''sh origin +HEAD:main{NEWLINE}echo \"")
        self.assertRefused(f"git --git-dir=\"{NEWLINE}\" pu''sh origin +HEAD:main{NEWLINE}echo \"")
        self.assertRefused(f"git -c a.b=x status{NEWLINE}echo \"")
        self.assertRefused(f"GIT pu''sh origin +HEAD:main{NEWLINE}echo \"")
        self.assertRefused("ls 'unbalanced")
'''

R2B_OLD = '''        # The push check reaches this path too, because a line is easy to make
        # untokenisable on purpose; here it can only be the crude one.
        if re.search(r"\\bgit\\b[^;&|\\n]*\\bpush\\b", flat):
            return (
                "a `git push` appears in a command this guard could not "
                "tokenise, so its remote and refspec cannot be read; refusing "
                "rather than admitting a push nothing checked."
            )
        return None
'''

R2B_NEW = '''        # Everything left is refused: a line is easy to make untokenisable on
        # purpose, bash runs the lines before the one that breaks, and a glob,
        # an expansion or another case can name git without its letters.
        return (
            "this command could not be tokenised, so what it runs cannot be "
            "read; refusing rather than admitting a command nothing checked. "
            "Balance its quotes, or move a body into a quoted heredoc."
        )
'''

R2B_COMMENT_OLD = '''        # Unparseable is not hostile: `shlex` fails on commands bash runs, an
        # ordinary commit among them. A parse failure degrades to the substring
        # scan the settings deny already performs, never a silent pass.
'''

R2B_COMMENT_NEW = '''        # Unparseable is refused below, whatever it is; the checks before that
        # only give the refusal a sharper reason.
'''

R2B_BACKSLASH_OLD ='''        # A trailing backslash has nothing to escape and must not read past
        # the end of the string.
        self.assertAdmitted("git log \\\\")
'''

R2B_BACKSLASH_NEW = '''        # A trailing backslash has nothing to escape and must not read past
        # the end of the string; it cannot be tokenised, so it is refused.
        self.assertRefused("git log \\\\")
'''


def r2b_tests(text):
    text = once(text, R2B_TEST_ANCHOR, R2B_TEST_ANCHOR + R2B_TEST)
    return once(text, R2B_BACKSLASH_OLD, R2B_BACKSLASH_NEW)


# ---- the pinned case's comment states the rule, not its history ----

COMMENT_OLD = """        # The fallback used to read the raw string, so a quoted `--output` in an
        # unparseable command was admitted; it now removes quotes as bash does.
"""

COMMENT_NEW = """        # A quoted `--output` in an unparseable command is refused, because the
        # fallback removes quotes as bash does before it looks.
"""


def main(root, part):
    claude = Path(root) / ".claude"
    hook = claude / "hooks" / "guard-git-argv.py"
    tests = claude / "scripts" / "test_git_argv_guard.py"
    if part == "r1-tests":
        rewrite(tests, lambda t: once(t, R1_TEST_ANCHOR, R1_TEST_ANCHOR + R1_TEST))
    elif part == "r1-hook":
        rewrite(hook, r1_hook)
    elif part == "r2-tests":
        rewrite(tests, r2_tests)
    elif part == "r2-hook":
        rewrite(hook, r2_hook)
    elif part == "r2b-tests":
        rewrite(tests, r2b_tests)
    elif part == "r2b-hook":
        rewrite(hook, lambda t: once(once(t, R2B_OLD, R2B_NEW), R2B_COMMENT_OLD, R2B_COMMENT_NEW))
    elif part == "comment":
        rewrite(tests, lambda t: once(t, COMMENT_OLD, COMMENT_NEW))
    else:
        raise SystemExit(f"unknown part {part!r}")


if __name__ == "__main__":
    main(*sys.argv[1:])
