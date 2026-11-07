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


# Built in to Claude Code, so no profile under .claude/agents lists them and only a deny refuses a spawn of one.
# fork inherits the parent's tools; Agent(fork) was seen refusing one from a command's list on Claude Code 2.1.316.
BUILT_IN_AGENTS = {"general-purpose", "claude", "Explore", "Plan", "claude-code-guide", "statusline-setup", "fork"}


def profiles():
    """Every agent type a profile under `.claude/agents/` names, read on each run so one added tomorrow counts."""
    found = set()
    for path in sorted(AGENTS.glob("*.md")):
        front = path.read_text(encoding="utf-8").split("\n---", 1)[0]
        names = re.findall(r"^name:\s*(\S+)\s*$", front, re.MULTILINE)
        if len(names) != 1:
            raise AssertionError(f"{path.name} names {len(names)} agent types, not exactly one")
        found.update(names)
    return found


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
        # An error from any of them is a silent miss, so none takes the session's model
        # (`docs/harness/token-rules.md`, *Which model a subagent runs on*).
        for name in ("branch-reviewer", "bug-auditor", "security-auditor", "team-peer"):
            with self.subTest(profile=name):
                profile = (AGENTS / f"{name}.md").read_text(encoding="utf-8")
                self.assertEqual(["opus"], frontmatter_list(profile, "model"))

    def test_the_team_peer_holds_no_shell_no_edit_and_no_agent(self):
        # The board it reads holds other peers' text, and this list is what keeps it read-only.
        profile = (AGENTS / "team-peer.md").read_text(encoding="utf-8")
        self.assertRegex(profile, r"(?m)^name:\s*team-peer\s*$")
        self.assertEqual({"Read", "Grep", "Glob"}, set(frontmatter_list(profile, "tools")))
        self.assertNotRegex(profile, r"(?m)^skills:")

    def test_team_grants_its_peer_by_exact_type_and_nothing_broader(self):
        allowed = frontmatter_list((COMMANDS / "team.md").read_text(encoding="utf-8"), "allowed-tools")
        self.assertEqual(["Agent(team-peer)"], [t for t in allowed if t == "Agent" or t.startswith("Agent(")])

    def test_ship_grants_the_reviewer_by_exact_type_and_nothing_broader(self):
        ship = (COMMANDS / "ship.md").read_text(encoding="utf-8")
        allowed = frontmatter_list(ship, "allowed-tools")
        self.assertEqual(
            ["Agent(branch-reviewer)"],
            [t for t in allowed if t == "Agent" or t.startswith("Agent(")])

    def test_every_other_exact_agent_grant_denies_every_other_profile(self):
        # The profiles are read from `.claude/agents/` each run, so one added tomorrow fails every command that
        # grants a type and does not deny it. /ship states no deny list: it grants one type, the reviewer above.
        names = profiles()
        self.assertIn("branch-reviewer", names)
        granting = 0
        for path in sorted(COMMANDS.glob("*.md")):
            if path.name == "ship.md":
                continue
            missing = undenied_agents(path.read_text(encoding="utf-8"), names)
            if missing is None:
                continue
            granting += 1
            with self.subTest(command=path.name):
                self.assertEqual([], missing, "granted an agent type, and these others are neither granted nor denied")
        # The two sweeps and /team: fewer means the grant pattern stopped matching and the loop passed over
        # nothing.
        self.assertGreaterEqual(granting, 3)


class ABuiltInDenyIsHeldToo(unittest.TestCase):
    def test_a_sweep_that_drops_a_built_in_deny_is_seen(self):
        text = (COMMANDS / "bug-sweep.md").read_text(encoding="utf-8")
        profiles = {"branch-reviewer", "bug-auditor", "security-auditor"}
        self.assertEqual([], undenied_agents(text, profiles))
        stripped = text.replace("Agent(general-purpose), ", "")
        self.assertNotEqual(text, stripped)
        self.assertEqual(["general-purpose"], undenied_agents(stripped, profiles))
        self.assertIsNone(undenied_agents("---\nname: x\n---\n", profiles))


class TheDispatcherSpawnsSessionsNotAgents(unittest.TestCase):
    """/dispatch grants no agent type and starts `claude -p` only through its helper (spec section 11)."""

    def frontmatter(self, key):
        return frontmatter_list((COMMANDS / "dispatch.md").read_text(encoding="utf-8"), key)

    def test_dispatch_grants_no_agent_and_denies_every_profile(self):
        # undenied_agents passes a command that grants none, so this deny list is held by its own case.
        allowed = self.frontmatter("allowed-tools")
        self.assertTrue(allowed, "dispatch.md gave no allowed-tools entries, so nothing was checked")
        self.assertEqual([], [t for t in allowed if t == "Agent" or t.startswith("Agent(")])
        denied = set(self.frontmatter("disallowed-tools"))
        self.assertEqual([], sorted(n for n in profiles() | BUILT_IN_AGENTS if f"Agent({n})" not in denied))

    def test_dispatch_launches_sessions_only_through_its_helper(self):
        allowed = self.frontmatter("allowed-tools")
        self.assertEqual([], [t for t in allowed if t.startswith("Bash(claude")])
        denied = self.frontmatter("disallowed-tools")
        self.assertIn("Bash(claude:*)", denied)
        for command in ("claude -p --model opus", "gh issue edit 1 --body x", "gh issue close 1",
                        "gh issue create --title t"):
            with self.subTest(refused=command):
                self.assertTrue(any(bash_deny_matches(rule, command) for rule in denied))
        # The control: a deny that refused these would take the night's only launcher and writer with it.
        for command in ("py -3.12 .claude/scripts/dispatch_board.py run 20261101-220000 S1",
                        "py -3.12 .claude/scripts/issue_decisions.py record 1 --question-file q --answer-file a "
                        "--run 20261101-220000",
                        "bash .claude/scripts/gh-issue-hand.sh bug low code"):
            with self.subTest(reachable=command):
                self.assertFalse(any(bash_deny_matches(rule, command) for rule in denied))

    def test_dispatch_says_its_grants_only_auto_approve(self):
        # A grant is not a whitelist, so a runbook claiming its frontmatter bounds the session misstates its guard.
        text = " ".join((COMMANDS / "dispatch.md").read_text(encoding="utf-8").split())
        self.assertNotIn("is the whole of what this session may do", text)
        self.assertIn("grants auto-approve and its denies refuse", text)
        self.assertIn("docs/harness-boundaries.md, *Grant semantics*", text)
        boundaries = (SCRIPTS.parent.parent / "docs" / "harness-boundaries.md").read_text(encoding="utf-8")
        self.assertRegex(boundaries, r"(?m)^## Grant semantics\s*$")


class TheSweepsDenyEveryGhCommand(unittest.TestCase):
    """Raw filing is denied as a whole, not as the one spelling a deny list named (`docs/harness-boundaries.md`).

    `SWEEPS` names the payload's sweeps; a target with a sweep of its own subclasses this and names that one.
    """

    SWEEPS = ("bug-sweep.md", "security-sweep.md")

    FILINGS = ("gh issue create --repo a/b --title t", "gh issue new --repo a/b --title t",
               "gh api repos/a/b/issues -f title=t", "gh issue view 1 --json author,body")
    HELPERS = ("bash .claude/scripts/gh-issue-sweep.sh bug low code", "bash .claude/scripts/gh-issue-list.sh")

    def denies(self, name):
        return frontmatter_list((COMMANDS / name).read_text(encoding="utf-8"), "disallowed-tools")

    def test_each_sweep_refuses_each_spelling(self):
        for name in self.SWEEPS:
            for command in self.FILINGS:
                with self.subTest(command=name, filing=command):
                    self.assertTrue(any(bash_deny_matches(rule, command) for rule in self.denies(name)))

    def test_the_helpers_stay_reachable(self):
        # The control: a deny that refused these would take the sweeps' only route with it.
        for name in self.SWEEPS:
            for command in self.HELPERS:
                with self.subTest(command=name, helper=command):
                    self.assertFalse(any(bash_deny_matches(rule, command) for rule in self.denies(name)))


    def test_each_sweep_refuses_the_harness_filing_helper(self):
        # /ship alone files in the harness, and a sweep reads a tree it cannot trust; the settings do not allow
        # the helper, and this deny holds the line if one ever did (as the force-push helpers' deny does).
        for name in self.SWEEPS:
            with self.subTest(command=name):
                self.assertTrue(any(bash_deny_matches(rule, "bash .claude/scripts/gh-issue-harness.sh bug high harness")
                                    for rule in self.denies(name)))

    def test_only_ship_grants_the_harness_filing_helper(self):
        grants = {p.name for p in COMMANDS.glob("*.md")
                  if any("gh-issue-harness.sh" in e
                         for e in frontmatter_list(p.read_text(encoding="utf-8"), "allowed-tools"))}
        self.assertEqual({"ship.md"}, grants)


class EachCommandFilesUnderOneRoute(unittest.TestCase):
    """The route is the entry point a command is granted, never an argument it types (`docs/harness-boundaries.md`).

    `gh-issue-create.sh` takes the route as its fourth argument, so a grant on it lets a command claim any route's
    trailer. An allow withholds nothing from the other entry points, so each command denies them by name.
    """

    CREATE = "Bash(bash .claude/scripts/gh-issue-create.sh:*)"
    SWEEP = "Bash(bash .claude/scripts/gh-issue-sweep.sh:*)"
    HAND = "Bash(bash .claude/scripts/gh-issue-hand.sh:*)"
    MUTATION = "Bash(bash .claude/scripts/gh-issue-mutation.sh:*)"
    BY_HAND = ("ship.md", "team.md", "dispatch.md")
    BY_SWEEP = ("bug-sweep.md", "security-sweep.md")

    def lists(self, name):
        text = (COMMANDS / name).read_text(encoding="utf-8")
        return frontmatter_list(text, "allowed-tools"), frontmatter_list(text, "disallowed-tools")

    def test_a_sweep_files_through_the_sweep_entry_and_cannot_reach_the_hand_route(self):
        for name in self.BY_SWEEP:
            allowed, denied = self.lists(name)
            with self.subTest(command=name):
                self.assertIn(self.SWEEP, allowed)
                self.assertNotIn(self.HAND, allowed)
                self.assertNotIn(self.MUTATION, allowed)
                self.assertIn(self.CREATE, denied)
                self.assertIn(self.HAND, denied)
                self.assertIn(self.MUTATION, denied)

    def test_a_command_that_files_by_hand_cannot_reach_the_sweep_route(self):
        for name in self.BY_HAND:
            allowed, denied = self.lists(name)
            with self.subTest(command=name):
                self.assertIn(self.HAND, allowed)
                self.assertNotIn(self.SWEEP, allowed)
                self.assertNotIn(self.MUTATION, allowed)
                if name != "ship.md":
                    # /ship states no deny list: a deny on a command it chains outlasts the step.
                    self.assertIn(self.SWEEP, denied)
                    self.assertIn(self.CREATE, denied)
                    self.assertIn(self.MUTATION, denied)

    def test_no_command_grants_the_helper_that_takes_a_route(self):
        for path in sorted(COMMANDS.glob("*.md")):
            allowed, _ = self.lists(path.name)
            with self.subTest(command=path.name):
                self.assertEqual([], [e for e in allowed if "gh-issue-create.sh" in e])

    def test_a_sweep_deny_spares_its_own_entry_and_refuses_the_other(self):
        _, denied = self.lists("bug-sweep.md")
        self.assertTrue(any(bash_deny_matches(r, "bash .claude/scripts/gh-issue-hand.sh bug low code") for r in denied))
        self.assertFalse(any(bash_deny_matches(r, "bash .claude/scripts/gh-issue-sweep.sh bug low code")
                             for r in denied))


class NoCommandGrantsABareBashOrALeadingWildcard(unittest.TestCase):
    # The bare tool and a leading wildcard each grant what the per-command checks look for by name.
    BROAD_BASH = re.compile(r"Bash(?:\(\*.*\))?")

    def test_no_allowed_tools_entry_is_a_bare_bash_or_a_leading_wildcard(self):
        paths = sorted(COMMANDS.glob("*.md"))
        # A frontmatter the parser cannot read gives no entries and would pass in silence.
        silent = [p.name for p in paths if not frontmatter_list(p.read_text(encoding="utf-8"), "allowed-tools")]
        self.assertEqual([], silent, "these commands gave no allowed-tools entries, so nothing was checked")
        for path in paths:
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

    # The payload's commands the chain runs before it pushes; a target whose Ship checks name commands of its own
    # holds them to the same case.
    CHAINED_BEFORE_A_PUSH = ("ship.md", "branch.md", "commit.md", "pr.md")

    PUSHES = ("git push origin fix/a-branch",
              "git push -u origin fix/a-branch")

    def test_none_of_them_denies_push_or_the_shell(self):
        ship = (COMMANDS / "ship.md").read_text(encoding="utf-8")
        # ship.md step 2 names its checks, or cites docs/harness-local.md's Ship checks once a sync moves them.
        local = (COMMANDS.parent.parent / "docs" / "harness-local.md").read_text(encoding="utf-8")
        checks = local.partition("\n## Ship checks\n")[2].partition("\n## ")[0]
        self.assertTrue(checks.strip(), "docs/harness-local.md has no Ship checks section")
        ship += checks
        for name in self.CHAINED_BEFORE_A_PUSH:
            text = (COMMANDS / name).read_text(encoding="utf-8")
            denied = ", ".join(frontmatter_list(text, "disallowed-tools"))
            with self.subTest(command=name):
                # An exemption nobody needs quietly widens, so the command has to be one the chain names.
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
