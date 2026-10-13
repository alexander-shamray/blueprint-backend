"""Route the two index hint hooks through hint-bound.sh and teach test_hook_timeouts that the wrapper launches py too.

Usage: patch_harness.py <root>, the checkout holding .claude/. Text edits, so settings.json keeps its formatting.
Each replacement must match exactly once, or this exits non-zero and writes nothing.
"""

import sys
from pathlib import Path

RUN = "py -3.12 -P -c \\\"import os, runpy; runpy.run_path(os.environ['CLAUDE_PROJECT_DIR'] + '/.claude/hooks/{}', run_name='__main__')\\\""
WRAP = "bash \\\"${{CLAUDE_PROJECT_DIR}}/.claude/hooks/hint-bound.sh\\\" {} {}"
WRAPPER_PREFIX = 'bash "${CLAUDE_PROJECT_DIR}/.claude/hooks/hint-bound.sh"'

EDITS = {
    ".claude/settings.json": [
        (RUN.format("index-query-hint.py"), WRAP.format("UserPromptSubmit", "index-query-hint.py")),
        (RUN.format("index-search-hint.py"), WRAP.format("PreToolUse", "index-search-hint.py")),
    ],
    ".claude/scripts/test_hook_timeouts.py": [
        ("# The slowest `py` start", "# A hint hook runs under hint-bound.sh, which launches `py` itself, so it is held to the same floor.\n"
         "LAUNCHERS = (\"py \", '" + WRAPPER_PREFIX + "')\n\n# The slowest `py` start"),
        ('hook["command"].startswith("py ")]', 'hook["command"].startswith(LAUNCHERS)]'),
        ('if not hook["command"].startswith("py ") or', 'if not hook["command"].startswith(LAUNCHERS) or'),
    ],
    ".claude/scripts/test_index_query_hint.py": [
        (r'''COMMAND = ("py -3.12 -P -c \"import os, runpy; runpy.run_path(os.environ['CLAUDE_PROJECT_DIR']"
           " + '/.claude/hooks/index-query-hint.py', run_name='__main__')\"")''',
         "COMMAND = '" + WRAPPER_PREFIX + " UserPromptSubmit index-query-hint.py'"),
        ("argv = [sys.executable, *shlex.split(COMMAND)[2:]]",
         'argv = [shutil.which("bash"), str(CLAUDE / "hooks" / "hint-bound.sh"), EVENT, HINT.name]'),
        ("                    self.assertEqual(hinted, bool(context(done)), done.stderr)\n",
         "                    # A hook that cannot run leaves hint-bound.sh's line, and a prompt is never erased.\n"
         "                    if hinted:\n"
         "                        self.assertTrue(context(done), done.stderr)\n"
         "                        self.assertNotIn(\"did not run\", context(done))\n"
         "                    else:\n"
         "                        self.assertIn(\"did not run\", context(done))\n"),
        ("import shlex\n", ""),
    ],
    ".claude/scripts/test_index_search_hint.py": [
        (r'''COMMAND = ("py -3.12 -P -c \"import os, runpy; runpy.run_path(os.environ['CLAUDE_PROJECT_DIR']"
           " + '/.claude/hooks/index-search-hint.py', run_name='__main__')\"")''',
         "COMMAND = '" + WRAPPER_PREFIX + " PreToolUse index-search-hint.py'"),
        ("argv = [sys.executable, *shlex.split(COMMAND)[2:]]",
         'argv = [shutil.which("bash"), str(CLAUDE / "hooks" / "hint-bound.sh"), EVENT, HINT.name]'),
        ("                    self.assertEqual(refused, bool(done.stdout.strip()), done.stderr)\n",
         "                    # A hook that cannot run leaves hint-bound.sh's line, and the call is never blocked.\n"
         "                    if refused:\n"
         "                        self.assertTrue(done.stdout.strip(), done.stderr)\n"
         "                        self.assertNotIn(b\"did not run\", done.stdout)\n"
         "                    else:\n"
         "                        self.assertIn(b\"did not run\", done.stdout)\n"),
        ("import shlex\n", ""),
    ],
}


def main(root: Path) -> int:
    staged = {}
    for rel, pairs in EDITS.items():
        with open(root / rel, encoding="utf-8", newline="") as f:
            text = f.read()
        for old, new in pairs:
            if text.count(old) != 1:
                print(f"{rel}: expected one match, found {text.count(old)}: {old[:60]}", file=sys.stderr)
                return 1
            text = text.replace(old, new)
        staged[rel] = text
    for rel, text in staged.items():
        with open(root / rel, "w", encoding="utf-8", newline="") as f:
            f.write(text)
    return 0


if __name__ == "__main__":
    sys.exit(main(Path(sys.argv[1])))
