"""settings.json's hooks: a `py` launcher start outlives a 5 s timeout, so each hook that carries one has room for it.

A measured `py -3.12 -P` start-up reached 2.9 s on a busy machine, and a hook that times out has its output discarded,
which for `index-query-hint.py` means the hint is silently not delivered.
"""

import json
import unittest

from review_helpers import SETTINGS, setUpModule

# A hint runs under hint-bound.sh and a guard under run-guard.sh, each of which launches `py`, so both share the floor.
LAUNCHERS = ("py ", 'bash "${CLAUDE_PROJECT_DIR}/.claude/hooks/hint-bound.sh"',
             'sh "${CLAUDE_PROJECT_DIR}/.claude/hooks/run-guard.sh"')

# The base's hooks by launcher: three `py`, the two hints and six guard commands, guard-git-argv's Bash and PowerShell.
COUNTS = {"py ": 3, LAUNCHERS[1]: 2, LAUNCHERS[2]: 6}

# The slowest `py` start measured was 2.9 s; a hook needs that and its own work, with room to spare.
FLOOR_SECONDS = 15


class PyLauncherHooksOutliveASlowStart(unittest.TestCase):
    def hooks(self):
        events = json.loads(SETTINGS.read_text(encoding="utf-8"))["hooks"]
        for event, groups in events.items():
            for group in groups:
                for hook in group["hooks"]:
                    yield event, hook

    def test_the_hooks_launched_through_py_are_found(self):
        commands = [hook["command"] for _, hook in self.hooks()]
        for launcher, count in COUNTS.items():
            with self.subTest(launcher=launcher):
                found = [command for command in commands if command.startswith(launcher)]
                self.assertGreaterEqual(len(found), count, "the scan matched too few hooks to be looking at the settings")

    def test_a_py_hook_with_a_timeout_allows_a_slow_launcher_start(self):
        for event, hook in self.hooks():
            if not hook["command"].startswith(LAUNCHERS) or "timeout" not in hook:
                continue
            with self.subTest(event=event, command=hook["command"]):
                self.assertGreaterEqual(hook["timeout"], FLOOR_SECONDS)


if __name__ == "__main__":
    unittest.main()
