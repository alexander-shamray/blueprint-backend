"""The quality sets' suite: the parsers over issues and comments written inline, the score, and
the committed sets' own invariants.

    cd tools/quality-sets && python -m unittest
"""

import collections
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


def comment(cid, pr, path="a.cs", line=10, login="Copilot", head="h1", reply=None, original_line=None):
    return {"id": cid, "user": {"login": login}, "pull_request_url": f"https://api.github.com/repos/o/r/pulls/{pr}",
            "path": path, "line": line, "original_line": original_line, "original_commit_id": head,
            "body": "  a\n  finding ", "in_reply_to_id": reply}


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

    def test_a_hash_quoted_before_the_pin_is_not_the_pin(self):
        action = "b" * 40
        quoted = f"## Affected lines\n\n`uses: actions/checkout@{action}` in ci.yml\n\n## Fix\n\ny\n\n"
        with mock.patch.object(qs, "resolve_commit", lambda text: SHA if text == SHA else None):
            for pin in (f"at the pinned commit {SHA}.", f"Pinned commit: {SHA}", f"Found by the sweep at {SHA}."):
                self.assertEqual(qs.pinned_commit(quoted + pin), SHA, pin)
            self.assertEqual(qs.pinned_commit(f"{quoted}the tree was {SHA}"), SHA, "an unlabelled hash the clone holds")
            self.assertIsNone(qs.pinned_commit(quoted), "a quoted hash the clone lacks is no pin")
            echoed = (f"## Affected lines\n\nFound by the #433 sweep at {'c' * 40}.\n\n## Fix\n\ny\n\n"
                      f"Found by the #433 sweep at {SHA}.")
            self.assertEqual(qs.pinned_commit(echoed), SHA, "a found-by line quoted in the affected lines")
            last = f"## Summary\n\nx\n\n## Affected lines\n\nci.yml\n\nPinned commit: {SHA}."
            self.assertEqual(qs.pinned_commit(last), SHA, "the pin follows the last heading and no later one ends it")

    def test_a_bare_hash_outside_the_affected_lines_beats_one_quoted_in_them(self):
        quoted = "d" * 40
        body = f"the tree was {SHA}\n\n## Affected lines\n\nregressed in {quoted}\n\n## Fix\n\ny"
        with mock.patch.object(qs, "resolve_commit", lambda text: text if text in (SHA, quoted) else None):
            self.assertEqual(qs.pinned_commit(body), SHA)
            self.assertEqual(qs.pinned_commit(f"## Affected lines\n\nregressed in {quoted}\n"), quoted)

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

    def test_the_line_is_the_one_the_head_was_reviewed_at_not_the_latest_commits(self):
        moved = comment(1, 5, line=55, original_line=40)
        self.assertEqual(qs.review_entries([moved], {5: "b"})[0]["line"], 40)
        outdated = comment(2, 5, line=None, original_line=74)
        self.assertEqual(qs.review_entries([outdated], {5: "b"})[0]["line"], 74)
        self.assertEqual(qs.review_entries([comment(3, 5, line=12)], {5: "b"})[0]["line"], 12)

    def test_replies_other_authors_unmerged_prs_and_lost_commits_are_dropped(self):
        comments = [comment(1, 5, reply=99), comment(2, 5, login="alexander-shamray"),
                    comment(3, 6), comment(4, 5, head="gone"), comment(5, 5, line=None)]
        self.assertEqual(qs.review_entries(comments, {5: "b"}), [])

    def test_a_value_after_a_password_key_is_masked_in_any_spacing_and_case(self):
        sample = "e.g. `Pass" + "word = abc`, pw" + "d=x;"
        self.assertEqual(qs.clean(sample), "e.g. `Password = <value> pwd=<value>")

    def test_a_key_that_ends_in_the_word_is_masked_too(self):
        sample = "DB_PASS" + "WORD=x and MyPass" + "word=y"
        self.assertEqual(qs.clean(sample), "DB_PASSWORD=<value> and MyPassword=<value>")

    def test_the_committed_set_holds_no_value_after_a_password_key(self):
        for entry in qs.load(qs.REVIEWS):
            self.assertEqual(qs.clean(entry["text"]), entry["text"], entry["id"])

    def test_text_is_capped(self):
        long = dict(comment(1, 5), body="word " * 400)
        self.assertIn(len(qs.review_entries([long], {5: "b"})[0]["text"]), (qs.TEXT_CAP, qs.TEXT_CAP - 1))


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

    def test_a_head_the_set_does_not_record_for_the_pr_is_refused_by_name(self):
        entries = [{"pr": 5, "head": "h1", "base": "b"}]
        with self.assertRaisesRegex(SystemExit, "no entry for PR 5 at h9"):
            qs.review_findings("claude", entries, 5, "h9")

    def test_git_runs_in_the_checkout_the_script_stands_in_unless_told_otherwise(self):
        with mock.patch.object(qs.subprocess, "run", return_value=mock.Mock(returncode=0)) as call:
            qs.run("git", "status")
            qs.run("git", "status", cwd="elsewhere")
        self.assertEqual([c.kwargs["cwd"] for c in call.call_args_list], [qs.REPO, "elsewhere"])

    def test_an_agents_definition_is_the_checkouts_own_with_its_tool_grant(self):
        for name, tools in (("branch-reviewer", ["Read", "Grep", "Glob"]), ("security-auditor", ["Read", "Grep", "Glob"])):
            got = json.loads(qs.agent_definition(name))[name]
            self.assertEqual(got["tools"], tools)
            self.assertTrue(got["description"] and got["prompt"])


class Runs(unittest.TestCase):
    def test_a_sample_spreads_over_heads_with_enough_entries_and_is_deterministic(self):
        entries = [{"pr": pr, "head": f"h{pr}"} for pr in range(1, 41) for _ in range(3 if pr % 2 else 1)]
        got = qs.sample_heads(entries, 4)
        self.assertEqual(got, qs.sample_heads(entries, 4))
        self.assertEqual(len(got), 4)
        self.assertTrue(all(pr % 2 for pr, _ in got))
        self.assertEqual(len(qs.sample_heads(entries, 999)), 20)

    def sweep(self, outcomes, spent=None):
        """sweep_findings over a fake worktree, slicer and auditor; outcomes is one list per ask call."""
        with tempfile.TemporaryDirectory() as tmp:
            def fake_run(*argv, **kwargs):
                if argv[0] == qs.git_bash():
                    slices = Path(f"{argv[3]}.slices")
                    slices.mkdir()
                    for n in (1, 2, 10):
                        (slices / f"{n}.txt").write_text("a.py\n")
                return mock.Mock(stdout="", returncode=0)
            calls = iter(outcomes)

            def fake_ask(claude, agent, prompt, cwd, add_dirs=(), spent=None):
                spent.update({"output_tokens": 5})
                result = next(calls)
                if isinstance(result, BaseException):
                    raise result
                if result is None:
                    raise SystemExit("no json")
                return result
            with mock.patch.object(qs.tempfile, "gettempdir", return_value=tmp), \
                    mock.patch.object(qs, "git"), mock.patch.object(qs, "run", fake_run), \
                    mock.patch.object(qs, "ask", fake_ask), contextlib.redirect_stderr(io.StringIO()):
                return qs.sweep_findings("claude", "bug", "c" * 40, spent)

    def test_slices_run_in_numeric_order_and_their_findings_are_gathered(self):
        found, failed, slices = self.sweep([[{"file": "a"}], [], [{"file": "b"}]])
        self.assertEqual(([f["file"] for f in found], failed, slices), (["a", "b"], [], 3))

    def test_a_slice_is_retried_once_then_reported_failed_without_losing_the_rest(self):
        found, failed, slices = self.sweep([[{"file": "a"}], None, None, [{"file": "b"}]])
        self.assertEqual(([f["file"] for f in found], failed, slices), (["a", "b"], ["2.txt"], 3))

    def test_a_usage_limit_notice_ends_the_run_instead_of_being_retried_as_a_failed_slice(self):
        done = mock.Mock(stdout="Paused: the weekly usage limit is almost used up.", returncode=0)
        with mock.patch.object(qs, "run", return_value=done), self.assertRaises(qs.UsageLimit):
            qs.ask("claude", "bug-auditor", "p", "dir")
        with self.assertRaises(qs.UsageLimit):
            self.sweep([[{"file": "a"}], qs.UsageLimit("limit")])

    def test_the_tokens_of_every_slice_attempt_are_summed_into_the_run(self):
        spent = collections.Counter()
        self.sweep([[{"file": "a"}], None, None, [{"file": "b"}]], spent)
        self.assertEqual(spent["output_tokens"], 5 * 4)

    def test_the_json_result_gives_the_reply_and_its_usage(self):
        envelope = json.dumps({"result": 'x {"findings": []}', "usage": {"input_tokens": 2, "output_tokens": 57}})
        self.assertEqual(qs.unwrap(envelope), ('x {"findings": []}', {"input_tokens": 2, "output_tokens": 57}))
        self.assertEqual(qs.unwrap("plain reply"), ("plain reply", {}))

    def test_an_ask_sums_all_four_counts_even_when_the_reply_is_not_the_json(self):
        usage = dict(zip(qs.TOKEN_KEYS, (2, 39604, 100, 57)))
        done = mock.Mock(stdout=json.dumps({"result": "I could not.", "usage": usage}), returncode=0)
        spent = collections.Counter()
        with mock.patch.object(qs, "run", return_value=done), self.assertRaises(SystemExit):
            qs.ask("claude", "bug-auditor", "p", "dir", spent=spent)
        self.assertEqual(qs.tokens(spent), {**usage, "total": 2 + 39604 + 100 + 57})

    def test_an_error_result_keeps_its_usage_and_a_malformed_count_is_zero(self):
        error = json.dumps({"subtype": "error_max_turns", "usage": {"input_tokens": 7, "output_tokens": "many"}})
        self.assertEqual(qs.unwrap(error), (error, {"input_tokens": 7, "output_tokens": "many"}))
        spent = collections.Counter()
        with mock.patch.object(qs, "run", return_value=mock.Mock(stdout=error, returncode=0)), \
                self.assertRaises(SystemExit):
            qs.ask("claude", "bug-auditor", "p", "dir", spent=spent)
        self.assertEqual(qs.tokens(spent)["total"], 7)

    def test_a_run_with_no_usage_reports_zeros(self):
        self.assertEqual(qs.tokens(collections.Counter())["total"], 0)

    def failed_run(self, stdout, spent=None, stderr="boom"):
        done = mock.Mock(stdout=stdout, stderr=stderr, returncode=1)
        with mock.patch.object(qs, "run", return_value=done) as call:
            result = qs.ask("claude", "bug-auditor", "p", "dir", spent=spent)
        self.assertFalse(call.call_args.kwargs["check"], "a non-zero exit is read, not raised inside run")
        return result

    def test_a_usage_limit_notice_on_a_non_zero_exit_still_ends_the_run_with_its_tokens_counted(self):
        envelope = json.dumps({"result": "Paused: the weekly usage limit is reached.", "usage": {"output_tokens": 9}})
        spent = collections.Counter()
        with self.assertRaises(qs.UsageLimit):
            self.failed_run(envelope, spent)
        self.assertEqual(qs.tokens(spent)["total"], 9)

    def test_a_usage_limit_notice_only_on_stderr_ends_the_run_too(self):
        with self.assertRaisesRegex(qs.UsageLimit, "usage limit reached"):
            self.failed_run("", stderr="Claude AI usage limit reached")

    def test_a_non_zero_exit_with_no_findings_is_refused_by_its_code_and_its_tokens_are_counted(self):
        envelope = json.dumps({"subtype": "error_max_turns", "usage": {"input_tokens": 4}})
        spent = collections.Counter()
        with self.assertRaisesRegex(SystemExit, "claude exited 1: boom"):
            self.failed_run(envelope, spent)
        self.assertEqual(qs.tokens(spent)["total"], 4)

    def test_a_non_zero_exit_that_returned_findings_is_still_a_result(self):
        self.assertEqual(self.failed_run('{"findings": [{"file": "a", "line": 1}]}'), [{"file": "a", "line": 1}])

    def test_the_slice_directory_is_handed_to_the_auditor(self):
        done = mock.Mock(stdout='{"findings": []}', returncode=0)
        with mock.patch.object(qs, "run", return_value=done) as call:
            qs.ask("claude", "bug-auditor", "p", "dir", ["/s"])
        argv = list(call.call_args.args)
        self.assertEqual(argv[argv.index("--add-dir") + 1], "/s")


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
