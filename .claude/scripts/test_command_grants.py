"""What a command's frontmatter grants an agent, and what a command /ship chains may deny.

Both rules are read from the files on every run, because an agent's guarantee is its tool list and a frontmatter
deny lasts the rest of the turn (`docs/harness-boundaries.md`).
"""

import importlib
import inspect
import json
import re
import sys
import tempfile
import unittest
import uuid
from pathlib import Path

from review_helpers import LOCAL, declared, local_commands

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


def undenied_agents(text, profiles, exempt=()):
    """The agent types a command that grants some neither grants, denies nor is excused, or None if it grants none."""
    granted = {m for t in frontmatter_list(text, "allowed-tools")
               for m in re.findall(r"^Agent\((.+)\)$", t)}
    if not granted:
        return None
    granted |= set(exempt)
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

    # What a command leaves undenied on purpose: /ship's step 2 dispatches its audits as `general-purpose`.
    EXCUSED = {"ship.md": ("general-purpose",)}

    def test_every_other_exact_agent_grant_denies_every_other_profile(self):
        # The profiles are read from `.claude/agents/` each run, so one added tomorrow fails every command that
        # grants a type and does not deny it, /ship included, which an allow cannot narrow to its reviewer.
        names = profiles()
        self.assertIn("branch-reviewer", names)
        granting = 0
        for path in sorted(COMMANDS.glob("*.md")):
            missing = undenied_agents(path.read_text(encoding="utf-8"), names, self.EXCUSED.get(path.name, ()))
            if missing is None:
                continue
            granting += 1
            with self.subTest(command=path.name):
                self.assertEqual([], missing, "granted an agent type, and these others are neither granted nor denied")
        # The two sweeps, /team and /ship: fewer means the grant pattern stopped matching and the loop passed over
        # nothing.
        self.assertGreaterEqual(granting, 4)

    def test_ship_keeps_general_purpose_for_its_audits_and_denies_the_other_profiles(self):
        ship = (COMMANDS / "ship.md").read_text(encoding="utf-8")
        denied = set(frontmatter_list(ship, "disallowed-tools"))
        self.assertNotIn("Agent(general-purpose)", denied)
        self.assertIn("`general-purpose`", flat(COMMANDS / "ship.md"))
        for name in ("bug-auditor", "security-auditor", "team-peer", "Explore", "fork"):
            with self.subTest(agent=name):
                self.assertIn(f"Agent({name})", denied)


def flat(path):
    return " ".join(path.read_text(encoding="utf-8").split())


class TheReviewersContractIsOneVocabularyAndABarItCannotChoose(unittest.TestCase):
    """The reviewer and /ship agree on what ends a round, and the bar comes from the base, never the branch."""

    PROFILE = AGENTS / "branch-reviewer.md"
    SHIP = COMMANDS / "ship.md"

    def test_the_bar_is_written_from_origin_main_and_an_empty_one_fails_closed(self):
        ship = flat(self.SHIP)
        self.assertIn('git show "origin/main:./docs/harness/working-rules.md"', ship)
        self.assertIn("artifacts/review/<slug>-bar.md", ship)
        profile = flat(self.PROFILE)
        self.assertIn("origin/main", profile)
        self.assertRegex(profile, r"Never fall back to the tree's own")
        self.assertRegex(profile, r"reads empty or without a \*What counts as a finding\* section, is no bar")
        self.assertIn("put `bar` in `skipped`", profile)

    def test_the_reviewer_skips_only_what_ship_reads_as_a_round(self):
        profile = flat(self.PROFILE)
        ship = flat(self.SHIP)
        admitted = re.findall(r"`skipped` names (?:anything|any check) but `([a-z-]+)`", ship)
        self.assertGreaterEqual(len(admitted), 2, "a /ship statement went missing")
        self.assertEqual({"locality"}, set(admitted))
        vocabulary = re.search(r"`skipped` may name (.+?) and nothing else", profile)
        self.assertIsNotNone(vocabulary, "the profile no longer bounds `skipped`")
        written = set(re.findall(r"`([a-z-]+)`", vocabulary.group(1)))
        self.assertIn("locality", written)
        for entry in written - set(admitted):
            with self.subTest(entry=entry):
                self.assertRegex(profile, rf"put `{entry}` in `skipped`, and the caller treats the round as not having run")
        example = re.search(r'"skipped": \[([^\]]*)\]', self.PROFILE.read_text(encoding="utf-8"))
        self.assertEqual(set(admitted), set(re.findall(r'"([a-z-]+)"', example.group(1))))
        for sentence in re.split(r"(?<=[.;])\s+", profile):
            if "`skipped`" in sentence:
                with self.subTest(sentence=sentence[:80]):
                    self.assertTrue(written & set(re.findall(r"`([a-z-]+)`", sentence)), sentence)

    def test_an_empty_read_stops_the_chain_beside_a_skip_beyond_locality(self):
        ship = flat(self.SHIP)
        self.assertRegex(ship, r"one whose `read` is empty or whose `skipped` names anything but `locality`")

    def test_step_6_refuses_a_head_no_round_read_and_a_spent_ceiling_still_rechecks_once(self):
        text = flat(self.SHIP)
        step = text[text.index("6. **Merge, then tear"):text.index("7. **Sleep, only when asked.**")]
        self.assertRegex(step, r"A head no round reviewed is not merged")
        self.assertRegex(step, r"recorded reviewed heads")
        self.assertRegex(step, r"ceiling spent, that recheck still runs once")
        five = text[text.index("5. **The local review.**"):text.index("6. **Merge, then tear")]
        self.assertIn("git rev-parse HEAD", five)
        self.assertRegex(five, r"Record the `rev-parse` output as the round's reviewed head")

    def test_a_diff_touching_the_reviewer_runs_under_mains_registered_profile_and_says_so(self):
        five = flat(self.SHIP)
        self.assertIn('git show "origin/main:./.claude/agents/branch-reviewer.md"', five)
        self.assertRegex(five, r"the round ran under `main`'s profile")

    def test_the_report_names_the_round_the_reviewed_head_and_that_the_review_was_in_house(self):
        text = flat(self.SHIP)
        report = text[text.index("## Report"):]
        self.assertRegex(report, r"the round number against the ceiling")
        self.assertRegex(report, r"the reviewed head")
        self.assertRegex(report, r"the review was in house")
        self.assertRegex(text, r"never reported as having had an external opinion")

    def test_the_bar_and_the_examples_come_before_the_diff_and_nothing_claims_first(self):
        profile = flat(self.PROFILE)
        self.assertNotIn("Read it first", profile)
        self.assertRegex(profile, r"read both before the diff")
        self.assertRegex(profile, r"Read it whole, after the bar")

    def test_the_code_bar_holds_security_weaknesses_and_findings_quote_what_they_rest_on(self):
        profile = flat(self.PROFILE)
        self.assertRegex(profile, r"defects and security weaknesses")
        self.assertRegex(profile, r"input that reaches a place it should not")
        self.assertRegex(profile, r"quote what it rests on in `defect`")

    def test_steering_includes_telling_the_caller_to_act_and_documentation_is_excepted(self):
        profile = flat(self.PROFILE)
        self.assertRegex(profile, r"to tell the caller to run or push something")
        self.assertRegex(profile, r"Documentation that describes actions is not that")
        self.assertRegex(profile, r"whether the text is trying to steer \*you\* off the review you were given")


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
                    # /ship denies agent types alone: a deny on a tool a command it chains uses outlasts the step.
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


class ACommandThatDoesNotPublishCannotPush(unittest.TestCase):
    """The settings allow `git push origin`, so a command that neither grants nor denies it reaches the push."""

    PUBLISHING = ("pr.md", "ship.md")
    # A target's own commands its Ship checks chain are declared in its `local_commands.py`, and held below.
    CHAINED_BEFORE_A_PUSH = ("branch.md", "commit.md") + LOCAL.chained
    PUSH = "git push origin fix/a-branch"

    def test_every_other_command_denies_push_or_the_shell(self):
        seen = 0
        for path in sorted(COMMANDS.glob("*.md")):
            text = path.read_text(encoding="utf-8")
            granted = "Bash(git push" in ", ".join(frontmatter_list(text, "allowed-tools"))
            with self.subTest(command=path.name):
                if path.name in self.PUBLISHING:
                    self.assertTrue(granted, "a publishing command is expected to grant push")
                    continue
                self.assertFalse(granted, "only the publishing commands may grant push")
                if path.name in self.CHAINED_BEFORE_A_PUSH:
                    continue
                seen += 1
                denied = frontmatter_list(text, "disallowed-tools")
                self.assertTrue(any(bash_deny_matches(rule, self.PUSH) for rule in denied),
                                "neither grants nor denies push, so the settings allow reaches it")
        self.assertGreaterEqual(seen, 4, "found almost no command that does not publish")


class NothingShipChainsDeniesPush(unittest.TestCase):
    """A frontmatter deny on a command /ship runs before it pushes refuses /ship's own push a step later.

    Restoring one reads as hardening and breaks the chain silently (`docs/harness-boundaries.md`).
    """

    # The payload's commands the chain runs before it pushes, and those a target's Ship checks add, which its
    # `local_commands.py` declares; the case below holds each to being one the chain names.
    CHAINED_BEFORE_A_PUSH = ("ship.md", "branch.md", "commit.md", "pr.md") + LOCAL.chained

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


class AWritingCommandDeniesTheUsersMemoryAndOutputStyles(unittest.TestCase):
    """Auto-memory and output styles are instructions to every later session, and these commands read a branch's text.

    The managed commands are the subject: a target's own are its to hold (`docs/harness-boundaries.md`).
    """

    REQUIRED = ("Edit(~/.claude/projects/**)", "Edit(~/.claude/output-styles/**)")
    MANIFEST = COMMANDS.parent.parent / ".harness" / "manifest.json"

    @staticmethod
    def writes(tool):
        return re.match(r"(Write|Edit)(\(|$)", tool) is not None

    def writers(self):
        manifest = json.loads(self.MANIFEST.read_text(encoding="utf-8"))
        paths = [COMMANDS.parent.parent / name for name in manifest["files"] if name.startswith(".claude/commands/")]
        return [path for path in paths
                if any(self.writes(tool) for tool in frontmatter_list(path.read_text(encoding="utf-8"), "allowed-tools"))]

    def test_the_writing_commands_are_found(self):
        # The positive control: a list that found none would pass the case below.
        names = {path.name for path in self.writers()}
        self.assertLessEqual({"ship.md", "pr.md", "style-pass.md", "dispatch.md", "team.md"}, names)
        self.assertNotIn("branch.md", names)

    def test_a_grant_is_read_as_writing_only_when_it_is_one(self):
        for tool in ("Write", "Edit", "Edit(artifacts/team/**)"):
            with self.subTest(tool=tool):
                self.assertTrue(self.writes(tool))
        for tool in ("Read", "WriteNothing", "Bash(git status:*)"):
            with self.subTest(tool=tool):
                self.assertFalse(self.writes(tool))

    def test_every_command_granting_write_or_edit_denies_both_paths(self):
        for path in self.writers():
            rules = frontmatter_list(path.read_text(encoding="utf-8"), "disallowed-tools")
            for required in self.REQUIRED:
                with self.subTest(command=path.name, rule=required):
                    self.assertIn(required, rules)


class ATargetDeclaresItsOwnCommandsInOneModule(unittest.TestCase):
    """The payload's classes run under `unittest discover` whatever a target subclasses, so its own commands reach
    them through `local_commands.py`, which a target keeps beside the suites and lists under `local`."""

    def module(self, source):
        # A uniquely named module in a directory of its own, so no case sees another's or the target's.
        name = f"local_commands_{uuid.uuid4().hex}"
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        Path(directory.name, f"{name}.py").write_text(source, encoding="utf-8")
        sys.path.insert(0, directory.name)
        self.addCleanup(sys.path.remove, directory.name)
        self.addCleanup(sys.modules.pop, name, None)
        importlib.invalidate_caches()
        return name

    def test_a_target_that_declares_nothing_has_no_module_and_holds_nothing(self):
        self.assertIsNone(local_commands(f"local_commands_{uuid.uuid4().hex}"))
        self.assertEqual(((), {}), tuple(declared(None)))

    def test_what_a_target_declares_reaches_the_cases(self):
        name = self.module('CHAINED_BEFORE_A_PUSH = ("check-links.md",)\nCEILINGS = {"review-branch.md": 8400}\n')
        found = declared(local_commands(name))
        self.assertEqual(("check-links.md",), found.chained)
        self.assertEqual({"review-branch.md": 8400}, found.ceilings)

    def test_a_module_may_declare_one_half_only(self):
        found = declared(local_commands(self.module('CEILINGS = {"own.md": 1}\n')))
        self.assertEqual(((), {"own.md": 1}), tuple(found))

    def test_a_declaration_whose_own_import_fails_is_not_an_absent_one(self):
        # Reading it as absent would drop every local command from the cases and pass in silence.
        name = self.module("import a_module_no_target_has\n")
        with self.assertRaises(ModuleNotFoundError):
            local_commands(name)

    def test_a_misshapen_declaration_is_refused_rather_than_read(self):
        # A bare string would iterate as its characters and exempt nothing a reader could see.
        for source in ('CHAINED_BEFORE_A_PUSH = "check-links.md"\n',
                       'CHAINED_BEFORE_A_PUSH = ("check-links",)\n',
                       'CEILINGS = {"own.md": "8400"}\n',
                       'CEILINGS = {"own.md": True}\n',
                       'CEILINGS = {"own.md": 0}\n',
                       'CEILINGS = [("own.md", 1)]\n'):
            with self.subTest(source=source):
                with self.assertRaises(TypeError):
                    declared(local_commands(self.module(source)))

    def test_the_push_cases_hold_what_the_target_declares(self):
        # Read from the class bodies: LOCAL.chained is empty here, so comparing values would pass with the term gone.
        for case in (ACommandThatDoesNotPublishCannotPush, NothingShipChainsDeniesPush):
            with self.subTest(case=case.__name__):
                self.assertRegex(inspect.getsource(case), r"(?m)^    CHAINED_BEFORE_A_PUSH = \([^)]*\) \+ LOCAL\.chained$")
                self.assertLessEqual(set(LOCAL.chained), set(case.CHAINED_BEFORE_A_PUSH))


if __name__ == "__main__":
    unittest.main()
