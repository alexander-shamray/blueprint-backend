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


# Built in to Claude Code and each carrying Edit, Write and Bash, so only a deny refuses a spawn of one.
BUILT_IN_AGENTS = {"general-purpose", "claude", "Explore", "Plan", "claude-code-guide", "statusline-setup"}


def undenied_agents(text, profiles):
    """The agent types a command that grants some neither grants nor denies, or None if it grants none."""
    granted = {m for t in frontmatter_list(text, "allowed-tools")
               for m in re.findall(r"^Agent\((.+)\)$", t)}
    if not granted:
        return None
    denied = set(frontmatter_list(text, "disallowed-tools"))
    return sorted(n for n in (profiles | BUILT_IN_AGENTS) - granted if f"Agent({n})" not in denied)


class AnAgentIsGrantedByExactType(unittest.TestCase):

    def test_the_reviewer_holds_no_shell_no_edit_and_no_agent(self):
        # The diff it reads is untrusted, and this list is what keeps it read-only.
        profile = (AGENTS / "branch-reviewer.md").read_text(encoding="utf-8")
        self.assertRegex(profile, r"(?m)^name:\s*branch-reviewer\s*$")
        self.assertEqual({"Read", "Grep", "Glob"},
                         set(frontmatter_list(profile, "tools")))
        self.assertNotRegex(profile, r"(?m)^skills:")

    def test_the_reviewer_and_the_auditors_run_on_opus(self):
        # An error from any of the three is a silent miss, so none takes the session's model (token-plan step 7d).
        for name in ("branch-reviewer", "bug-auditor", "security-auditor"):
            with self.subTest(profile=name):
                profile = (AGENTS / f"{name}.md").read_text(encoding="utf-8")
                self.assertEqual(["opus"], frontmatter_list(profile, "model"))

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
            missing = undenied_agents(path.read_text(encoding="utf-8"), profiles)
            if missing is None:
                continue
            granting += 1
            with self.subTest(command=path.name):
                self.assertEqual([], missing, "granted an agent type, and these others are neither granted nor denied")
        # The two sweeps: fewer means the grant pattern stopped matching and the loop passed over nothing.
        self.assertGreaterEqual(granting, 2)


class ABuiltInDenyIsHeldToo(unittest.TestCase):
    def test_a_sweep_that_drops_a_built_in_deny_is_seen(self):
        text = (COMMANDS / "bug-sweep.md").read_text(encoding="utf-8")
        profiles = {"branch-reviewer", "bug-auditor", "security-auditor"}
        self.assertEqual([], undenied_agents(text, profiles))
        stripped = text.replace("Agent(general-purpose), ", "")
        self.assertNotEqual(text, stripped)
        self.assertEqual(["general-purpose"], undenied_agents(stripped, profiles))
        self.assertIsNone(undenied_agents("---\nname: x\n---\n", profiles))


class TheSweepsDenyEveryGhCommand(unittest.TestCase):
    """Raw filing is denied as a whole, not as the one spelling a deny list named (`docs/harness-boundaries.md`)."""

    FILINGS = ("gh issue create --repo a/b --title t", "gh issue new --repo a/b --title t",
               "gh api repos/a/b/issues -f title=t", "gh issue view 1 --json author,body")
    HELPERS = ("bash .claude/scripts/gh-issue-create.sh hand", "bash .claude/scripts/gh-issue-list.sh")

    def denies(self, name):
        return frontmatter_list((COMMANDS / name).read_text(encoding="utf-8"), "disallowed-tools")

    def test_each_sweep_refuses_each_spelling(self):
        for name in ("bug-sweep.md", "security-sweep.md"):
            for command in self.FILINGS:
                with self.subTest(command=name, filing=command):
                    self.assertTrue(any(bash_deny_matches(rule, command) for rule in self.denies(name)))

    def test_the_helpers_stay_reachable(self):
        # The control: a deny that refused these would take the sweeps' only route with it.
        for name in ("bug-sweep.md", "security-sweep.md"):
            for command in self.HELPERS:
                with self.subTest(command=name, helper=command):
                    self.assertFalse(any(bash_deny_matches(rule, command) for rule in self.denies(name)))


class NoCommandGrantsEveryShellCommand(unittest.TestCase):
    # The bare tool and a leading wildcard each grant what the per-command checks look for by name.
    BROAD_BASH = re.compile(r"Bash(?:\(\*.*\))?")

    def test_no_allowed_tools_entry_is_a_bare_bash_or_a_leading_wildcard(self):
        for path in sorted(COMMANDS.glob("*.md")):
            for entry in frontmatter_list(path.read_text(encoding="utf-8"), "allowed-tools"):
                with self.subTest(command=path.name, entry=entry):
                    self.assertIsNone(self.BROAD_BASH.fullmatch(entry))

    def test_the_pattern_sees_each_broad_grant(self):
        for entry in ("Bash", "Bash(*)", "Bash(* gh *)", "Bash(*:*)"):
            with self.subTest(entry=entry):
                self.assertIsNotNone(self.BROAD_BASH.fullmatch(entry))
        self.assertIsNone(self.BROAD_BASH.fullmatch("Bash(bash .claude/scripts/gh-issue-list.sh)"))


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
