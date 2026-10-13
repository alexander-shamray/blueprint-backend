"""hint-bound.sh: a hint hook that stalls or fails leaves a visible line, and one that answers is passed through.

The harness discards the output of a hook it kills at its timeout, so the line has to come from a bound this wrapper
holds below that timeout; `settings.json` must route both hint hooks through it and give them room above the bound.
"""

import json
import os
import re
import shutil
import subprocess
import tempfile
import time
import unittest
from pathlib import Path

from review_helpers import BASH, SCRIPTS, SETTINGS, setUpModule

WRAPPER = SCRIPTS.parent / "hooks" / "hint-bound.sh"
HINTS = (("UserPromptSubmit", "index-query-hint.py"), ("PreToolUse", "index-search-hint.py"))


class HintBound(unittest.TestCase):
    def run_wrapper(self, event, hook, py_body, stdin="", bound="10"):
        # A `py` shim on PATH, the shape every stubbed helper test here uses.
        d = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, d, ignore_errors=True)
        py = Path(d) / "py"
        py.write_text("#!/usr/bin/env bash\n" + py_body, encoding="utf-8")
        py.chmod(0o755)
        env = dict(os.environ)
        env["PATH"] = d + os.pathsep + env["PATH"]
        env["HINT_BOUND_SECONDS"] = bound
        env["CLAUDE_PROJECT_DIR"] = str(SCRIPTS.parent.parent)
        start = time.monotonic()
        r = subprocess.run(
            [BASH, str(WRAPPER), event, hook],
            input=stdin, capture_output=True, text=True, encoding="utf-8", env=env,
        )
        return r, time.monotonic() - start

    def test_a_stalled_hook_leaves_a_line_naming_it_at_the_bound(self):
        for event, hook in HINTS:
            with self.subTest(hook=hook):
                r, took = self.run_wrapper(event, hook, "exec sleep 90\n", bound="1")
                self.assertLess(took, 60)
                self.assertEqual(0, r.returncode, r.stderr)
                out = json.loads(r.stdout)["hookSpecificOutput"]
                self.assertEqual(event, out["hookEventName"])
                self.assertIn(hook, out["additionalContext"])
                self.assertIn("did not run", out["additionalContext"])

    def test_a_hook_that_fails_leaves_the_line_and_still_exits_zero(self):
        # Exit 2 under these events blocks the prompt or the call.
        r, _ = self.run_wrapper("UserPromptSubmit", "index-query-hint.py", "echo half; exit 2\n")
        self.assertEqual(0, r.returncode, r.stderr)
        self.assertIn("did not run", r.stdout)
        self.assertNotIn("half", r.stdout)

    def test_a_hook_that_answers_is_passed_through_with_its_input(self):
        # The positive control: `cat` is a hook that returns its input, so the payload crosses both ways.
        payload = '{"prompt":"where is the saga"}\n'
        r, _ = self.run_wrapper("UserPromptSubmit", "index-query-hint.py", "cat\n", stdin=payload)
        self.assertEqual(0, r.returncode, r.stderr)
        self.assertEqual(payload, r.stdout)

    def test_a_hook_that_answers_with_nothing_leaves_nothing(self):
        r, _ = self.run_wrapper("PreToolUse", "index-search-hint.py", "exit 0\n")
        self.assertEqual((0, ""), (r.returncode, r.stdout))


class HintHooksAreWired(unittest.TestCase):
    def commands(self):
        events = json.loads(SETTINGS.read_text(encoding="utf-8"))["hooks"]
        for event, groups in events.items():
            for group in groups:
                for hook in group["hooks"]:
                    yield event, hook

    # Which hooks are routed through it, and how they start, is held by the two hint suites' wiring cases.
    def test_the_default_bound_is_below_every_wrapped_hooks_timeout(self):
        default = int(re.search(r"HINT_BOUND_SECONDS:-(\d+)", WRAPPER.read_text(encoding="utf-8")).group(1))
        wrapped = [h for _, h in self.commands() if "hint-bound.sh" in h["command"]]
        self.assertEqual(2, len(wrapped))
        for hook in wrapped:
            with self.subTest(command=hook["command"]):
                self.assertLess(default, hook["timeout"])


if __name__ == "__main__":
    unittest.main()
