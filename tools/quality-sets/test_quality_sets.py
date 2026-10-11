"""The quality sets' suite: the parsers over issues and comments written inline, the score, and
the committed sets' own invariants.

    cd tools/quality-sets && python -m unittest
"""

import contextlib
import io
import json
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import quality_sets as qs

SHA = "5bea8b26d506f396175852909dff880d93b7f7d3"
TREE = {"deploy/helm/smoke.sh", "deploy/compose/rabbitmq/check_permissions.py", "README.md"}
TRAILER = "Filed by an authorised sweep and verified at filing by a second read-only auditor."


def issue(number, body, *labels):
    return {"number": number, "title": f"title {number}", "body": body, "labels": [{"name": n} for n in labels]}


def sweep_body(affected, found=f"Found by the #433 security sweep (round 6) at {SHA}."):
    return f"## Summary\n\nx\n\n## Affected lines\n\n{affected}\n\n## Fix\n\ny `README.md`\n\n{found}\n\n{TRAILER}"


def comment(cid, pr, path="a.cs", line=10, login="Copilot", head="h1", reply=None):
    return {"id": cid, "user": {"login": login}, "pull_request_url": f"https://api.github.com/repos/o/r/pulls/{pr}",
            "path": path, "line": line, "original_commit_id": head, "body": "  a\n  finding ",
            "in_reply_to_id": reply}


class Sweeps(unittest.TestCase):
    def setUp(self):
        patch = mock.patch.object(qs, "tree_paths", lambda commit: TREE if commit == SHA else None)
        patch.start()
        self.addCleanup(patch.stop)

    def test_the_commit_is_the_pinned_sha_or_the_found_at_abbreviation(self):
        self.assertEqual(qs.pinned_commit(f"Pinned commit: {SHA}. Found by the bug sweep's round 2"), SHA)
        with mock.patch.object(qs, "resolve_commit", lambda text: SHA if text == "5bea8b2" else None):
            self.assertEqual(qs.pinned_commit("Found by the #433 security sweep at 5bea8b2."), SHA)
            self.assertIsNone(qs.pinned_commit("Found by hand, no commit."))

    def test_paths_come_from_affected_lines_and_only_those_that_exist_at_the_commit(self):
        body = sweep_body("deploy/helm/smoke.sh line 390 and docs/gone.md and check_permissions.py")
        entry = qs.sweep_entry(issue(7, body, "bug", "low"))
        self.assertEqual(entry["paths"], ["deploy/helm/smoke.sh", "deploy/compose/rabbitmq/check_permissions.py"])
        self.assertEqual((entry["kind"], entry["severity"], entry["commit"]), ("bug", "low", SHA))

    def test_a_bare_file_name_resolves_only_when_it_is_unique_in_the_tree(self):
        self.assertEqual(qs.issue_paths("t", "## Affected lines\n\nsmoke.sh", TREE), ["deploy/helm/smoke.sh"])
        two = TREE | {"other/smoke.sh"}
        self.assertEqual(qs.issue_paths("t", "## Affected lines\n\nsmoke.sh", two), [])

    def test_a_body_without_the_section_is_read_whole(self):
        self.assertEqual(qs.issue_paths("t", "see README.md", TREE), ["README.md"])

    def test_only_an_issue_a_sweep_filed_is_an_entry(self):
        by_hand = issue(8, "x\n\nFiled by hand rather than by a sweep: no second auditor verified it at filing.")
        self.assertIsNone(qs.sweep_entry(by_hand))
        self.assertEqual([e["issue"] for e in qs.build_sweeps([issue(9, sweep_body("README.md")), by_hand])], [9])

    def test_a_commit_this_clone_lacks_leaves_the_entry_unscorable_rather_than_dropped(self):
        body = sweep_body("README.md", found="Pinned commit: " + "a" * 40 + ".")
        entry = qs.sweep_entry(issue(10, body, "security", "medium"))
        self.assertEqual((entry["commit"], entry["paths"]), (None, []))


class Reviews(unittest.TestCase):
    def setUp(self):
        patch = mock.patch.object(qs, "tree_paths", lambda commit: set() if commit != "gone" else None)
        patch.start()
        self.addCleanup(patch.stop)

    def test_a_top_level_reviewer_comment_on_a_merged_pr_becomes_an_entry(self):
        got = qs.review_entries([comment(1, 5)], {5: "base5"})
        self.assertEqual(got, [{"id": 1, "pr": 5, "base": "base5", "head": "h1", "reviewer": "Copilot",
                                "path": "a.cs", "line": 10, "text": "a finding"}])

    def test_replies_other_authors_unmerged_prs_and_lost_commits_are_dropped(self):
        comments = [comment(1, 5, reply=99), comment(2, 5, login="alexander-shamray"),
                    comment(3, 6), comment(4, 5, head="gone"), comment(5, 5, line=None)]
        self.assertEqual(qs.review_entries(comments, {5: "b"}), [])

    def test_text_is_capped(self):
        long = dict(comment(1, 5), body="word " * 400)
        self.assertEqual(len(qs.review_entries([long], {5: "b"})[0]["text"]), qs.TEXT_CAP)


class Scoring(unittest.TestCase):
    sweeps = [{"issue": 1, "commit": SHA, "paths": ["a.py"]}, {"issue": 2, "commit": SHA, "paths": ["b.py", "c.py"]},
              {"issue": 3, "commit": "f" * 40, "paths": ["a.py"]}, {"issue": 4, "commit": SHA, "paths": []}]
    reviews = [{"id": 1, "head": "h1", "path": "a.cs", "line": 10}, {"id": 2, "head": "h1", "path": "a.cs", "line": 90},
               {"id": 3, "head": "h2", "path": "a.cs", "line": 10}]

    def test_a_sweep_entry_is_found_by_any_finding_in_any_of_its_files(self):
        got = qs.score(self.sweeps, [{"file": "c.py", "line": 1}], SHA)
        self.assertEqual((got["entries"], got["found"], got["recall"], got["missed"]), (2, 1, 0.5, [1]))

    def test_an_entry_with_no_path_cannot_be_scored_and_is_not_counted(self):
        self.assertEqual(qs.score(self.sweeps, [], SHA)["entries"], 2)

    def test_a_commit_may_be_given_as_a_prefix(self):
        self.assertEqual(qs.score(self.sweeps, [], SHA[:8])["entries"], 2)

    def test_a_review_entry_needs_the_same_file_within_the_window(self):
        near = qs.LINE_WINDOW
        self.assertEqual(qs.score(self.reviews, [{"file": "a.cs", "line": 10 + near}], "h1")["found"], 1)
        self.assertEqual(qs.score(self.reviews, [{"file": "a.cs", "line": 11 + near}], "h1")["found"], 0)
        self.assertEqual(qs.score(self.reviews, [{"file": "b.cs", "line": 10}], "h1")["found"], 0)

    def test_a_review_is_scored_against_its_own_head_only(self):
        got = qs.score(self.reviews, [{"file": "a.cs", "line": 10}], "h2")
        self.assertEqual((got["entries"], got["found"], got["recall"]), (1, 1, 1.0))

    def test_no_entries_is_no_recall_not_zero(self):
        self.assertIsNone(qs.score(self.reviews, [], "nope")["recall"])

    def test_the_cli_scores_a_findings_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            sets, found = Path(tmp, "s.json"), Path(tmp, "f.json")
            sets.write_text(json.dumps(self.sweeps))
            found.write_text(json.dumps({"commit": SHA, "findings": [{"file": "a.py", "line": 1}]}))
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                qs.main(["score", "--set", str(sets), "--findings", str(found)])
        self.assertEqual(json.loads(out.getvalue())["found"], 1)


class Agents(unittest.TestCase):
    def test_json_is_found_inside_prose_and_fences(self):
        text = 'Here:\n```json\n{"scope": "full", "findings": [{"file": "a", "line": 1}]}\n```\n'
        self.assertEqual(qs.agent_json(text)["findings"], [{"file": "a", "line": 1}])

    def test_text_without_findings_json_is_none(self):
        self.assertIsNone(qs.agent_json('prose {"status": "ok"} more'))

    def test_a_reply_that_is_not_the_json_stops_the_run(self):
        done = mock.Mock(stdout="I could not.", returncode=0)
        with mock.patch.object(qs, "run", return_value=done), self.assertRaises(SystemExit):
            qs.ask("claude", "branch-reviewer", "p", ".")

    def test_the_agent_is_named_and_run_non_interactively(self):
        done = mock.Mock(stdout='{"findings": []}', returncode=0)
        with mock.patch.object(qs, "run", return_value=done) as call:
            self.assertEqual(qs.ask("claude", "bug-auditor", "p", "dir"), [])
        argv = list(call.call_args.args)
        self.assertEqual(argv[:3], ["claude", "-p", "p"])
        self.assertEqual(argv[argv.index("--agent") + 1], "bug-auditor")
        self.assertIn("bug-auditor", json.loads(argv[argv.index("--agents") + 1]))

    def test_an_agents_definition_is_the_checkouts_own_with_its_tool_grant(self):
        for name, tools in (("branch-reviewer", ["Read", "Grep", "Glob"]), ("security-auditor", ["Read", "Grep", "Glob"])):
            got = json.loads(qs.agent_definition(name))[name]
            self.assertEqual(got["tools"], tools)
            self.assertTrue(got["description"] and got["prompt"])


class TheCommittedSets(unittest.TestCase):
    def test_every_sweep_entry_with_a_commit_names_a_path_and_issues_are_unique(self):
        sweeps = qs.load(qs.SWEEPS)
        self.assertEqual(len({e["issue"] for e in sweeps}), len(sweeps))
        for entry in sweeps:
            if entry["commit"]:
                self.assertRegex(entry["commit"], r"^[0-9a-f]{40}$")
                self.assertTrue(entry["paths"], f"#{entry['issue']} names no path at its commit")

    def test_every_review_entry_is_a_reviewers_and_carries_a_base_and_head(self):
        reviews = qs.load(qs.REVIEWS)
        self.assertEqual(len({e["id"] for e in reviews}), len(reviews))
        for entry in reviews:
            self.assertIn(entry["reviewer"], qs.REVIEWERS)
            self.assertRegex(entry["base"], r"^[0-9a-f]{40}$")
            self.assertRegex(entry["head"], r"^[0-9a-f]{40}$")
            self.assertGreater(entry["line"], 0)
            self.assertLessEqual(len(entry["text"]), qs.TEXT_CAP)


if __name__ == "__main__":
    unittest.main()
