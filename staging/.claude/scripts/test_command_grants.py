"""What a command's frontmatter grants an agent, and what a command /ship chains may deny.

Both rules are read from the files on every run, because an agent's guarantee is its tool list and a frontmatter
deny lasts the rest of the turn (`docs/harness-boundaries.md`).
"""

import re
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
COMMANDS = SCRIPTS.parent / "commands"
AGENTS = SCRIPTS.parent / "agents"


def frontmatter_list(text, key):
    # The closed opening block only: a body line that opens with the key is
    # prose, and reading it would pass a command whose real list had gone.
    block = re.match(r"---\n(.*?)\n---[ \t]*(?:\n|\Z)", text, re.DOTALL)
    return [item.strip() for line in
            re.findall(rf"^{key}:\s*(.+)$", block.group(1) if block else "",
                       re.MULTILINE)
            for item in line.split(",") if item.strip()]


def bash_deny_matches(rule, command):
    """Whether a `Bash(...)` deny refuses `command`: `:*` is a prefix, and `*` matches at any position."""
    inner = re.fullmatch(r"Bash\((.*)\)", rule)
    if inner is None:
        return rule == "Bash"
    spelled = inner.group(1)
    prefix = spelled.endswith(":*")
    if prefix:
        spelled = spelled[:-2]
    body = ".*".join(re.escape(part) for part in spelled.split("*"))
    return re.fullmatch(body + (".*" if prefix else ""), command,
                        re.DOTALL) is not None


class AnAgentIsGrantedByExactType(unittest.TestCase):

    def test_the_reviewer_holds_no_shell_no_edit_and_no_agent(self):
        # The diff it reads is untrusted, and this list is what keeps it read-only.
        profile = (AGENTS / "branch-reviewer.md").read_text(encoding="utf-8")
        self.assertRegex(profile, r"(?m)^name:\s*branch-reviewer\s*$")
        self.assertEqual({"Read", "Grep", "Glob"},
                         set(frontmatter_list(profile, "tools")))
        self.assertNotRegex(profile, r"(?m)^skills:")

    def test_ship_grants_the_reviewer_by_exact_type_and_nothing_broader(self):
        ship = (COMMANDS / "ship.md").read_text(encoding="utf-8")
        allowed = frontmatter_list(ship, "allowed-tools")
        self.assertEqual(
            ["Agent(branch-reviewer)"],
            [t for t in allowed if t == "Agent" or t.startswith("Agent(")])

    def test_every_other_exact_agent_grant_denies_every_other_profile(self):
        # The profiles are read from `.claude/agents/` each run, so one added tomorrow fails every command that
        # grants a type and does not deny it. /ship states no deny list: it grants one type, the reviewer above.
        profiles = set()
        for path in sorted(AGENTS.glob("*.md")):
            front = path.read_text(encoding="utf-8").split("\n---", 1)[0]
            names = re.findall(r"^name:\s*(\S+)\s*$", front, re.MULTILINE)
            with self.subTest(profile=path.name):
                self.assertEqual(1, len(names), "exactly one `name:`")
            profiles.update(names)
        self.assertIn("branch-reviewer", profiles)
        granting = 0
        for path in sorted(COMMANDS.glob("*.md")):
            if path.name == "ship.md":
                continue
            text = path.read_text(encoding="utf-8")
            granted = {m for t in frontmatter_list(text, "allowed-tools")
                       for m in re.findall(r"^Agent\((.+)\)$", t)}
            if not granted:
                continue
            granting += 1
            denied = set(frontmatter_list(text, "disallowed-tools"))
            for name in sorted(profiles - granted):
                with self.subTest(command=path.name, agent=name):
                    self.assertIn(f"Agent({name})", denied)
        # The two sweeps: fewer means the grant pattern stopped matching and the loop passed over nothing.
        self.assertGreaterEqual(granting, 2)


class NothingShipChainsDeniesPush(unittest.TestCase):
    """A frontmatter deny on a command /ship runs before it pushes refuses /ship's own push a step later.

    Restoring one reads as hardening and breaks the chain silently (`docs/harness-boundaries.md`).
    """

    CHAINED_BEFORE_A_PUSH = ("ship.md", "branch.md", "validate-blueprint.md",
                             "check-links.md", "commit.md", "pr.md")

    PUSHES = ("git push origin fix/a-branch",
              "git push -u origin fix/a-branch")

    def test_none_of_them_denies_push_or_the_shell(self):
        ship = (COMMANDS / "ship.md").read_text(encoding="utf-8")
        for name in self.CHAINED_BEFORE_A_PUSH:
            text = (COMMANDS / name).read_text(encoding="utf-8")
            denied = ", ".join(frontmatter_list(text, "disallowed-tools"))
            with self.subTest(command=name):
                # An exemption nobody needs quietly widens, so the command has to be one ship.md names.
                self.assertIn(f"`/{name.removesuffix('.md')}`", ship)
                self.assertNotIn("Bash(git push", denied)
                self.assertIsNone(
                    re.search(r"(^|,\s*)Bash(\s*,|\s*$)", denied))
                for rule in frontmatter_list(text, "disallowed-tools"):
                    for push in self.PUSHES:
                        self.assertFalse(bash_deny_matches(rule, push),
                                         f"{rule} refuses {push}")

    def test_a_push_deny_is_seen_however_it_is_spelled(self):
        # The positive control for the matcher: a wildcard takes any position in a deny.
        for rule in ("Bash", "Bash(git push:*)", "Bash(git *push*)",
                     "Bash(git push origin:*)", "Bash(*)"):
            with self.subTest(rule=rule):
                self.assertTrue(bash_deny_matches(rule, self.PUSHES[0]))
        for rule in ("Edit(.git/**)", "Bash(git commit:*)", "Bash(gh *)"):
            with self.subTest(rule=rule):
                self.assertFalse(bash_deny_matches(rule, self.PUSHES[0]))

    def test_a_body_line_is_not_frontmatter(self):
        text = "---\nname: x\n---\n\ndisallowed-tools: Bash\n"
        self.assertEqual([], frontmatter_list(text, "disallowed-tools"))
        self.assertEqual(["x"], frontmatter_list(text, "name"))
        self.assertEqual([], frontmatter_list("name: x\n", "name"))


if __name__ == "__main__":
    unittest.main()
