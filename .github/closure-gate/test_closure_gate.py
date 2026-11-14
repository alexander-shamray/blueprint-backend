#!/usr/bin/env python3
"""Negative cases for the closure gate.

Running the gate against a well-formed pull request passes, which says nothing
about what it would catch. Every test below is a defect the gate has to find,
or a *non*-defect it must not invent — and the largest block is the keyword
parser, because a keyword the parser misses is the one failure shape that
passes silently while the merge closes an issue nobody declared.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import unittest
from pathlib import Path

from closure_gate import check, closing_references, declared_in_table, repository_of

REPO = "alexander-shamray/blueprint-backend"


def payload(
    *,
    body: str = "",
    commit_messages: tuple[str, ...] = ("chore: something",),
    linked: tuple[int, ...] = (),
    number: int = 999,
    url: str | None = None,
    head_ref_oid: str | None = None,
    author: dict | None = None,
) -> dict:
    commits = [
        {"oid": oid(index), "messageHeadline": message.split("\n")[0],
         "messageBody": "\n".join(message.split("\n")[1:])}
        for index, message in enumerate(commit_messages)
    ]
    return {
        "number": number,
        "url": url if url is not None else f"https://github.com/{REPO}/pull/{number}",
        "body": body,
        "commits": commits,
        "closingIssuesReferences": [{"number": n} for n in linked],
        # The default is the newest commit, which is what a fresh read gives.
        # A test wanting a stale read says so by passing something else.
        "headRefOid": head_ref_oid if head_ref_oid is not None
        else (commits[-1]["oid"] if commits else oid(0)),
        "author": author if author is not None else {"is_bot": False, "login": "alexander-shamray"},
    }


def oid(index: int) -> str:
    return f"{index:08x}" + "0" * 32


class KeywordParser(unittest.TestCase):
    """All nine spellings, each one on its own.

    A list that quietly produces eight is the defect this block exists for, so
    the spellings are asserted individually rather than in a loop over the same
    list the parser is built from.
    """

    def assert_finds(self, text: str, expected: set[int]) -> None:
        numbers, unreadable = closing_references(text, REPO)
        self.assertEqual(numbers, expected, text)
        self.assertEqual(unreadable, [], text)

    def test_close(self):
        self.assert_finds("close #1", {1})

    def test_closes(self):
        self.assert_finds("closes #2", {2})

    def test_closed(self):
        self.assert_finds("closed #3", {3})

    def test_fix(self):
        self.assert_finds("fix #4", {4})

    def test_fixes(self):
        self.assert_finds("fixes #5", {5})

    def test_fixed(self):
        self.assert_finds("fixed #6", {6})

    def test_resolve(self):
        self.assert_finds("resolve #7", {7})

    def test_resolves(self):
        self.assert_finds("resolves #8", {8})

    def test_resolved(self):
        self.assert_finds("resolved #9", {9})

    def test_case_is_irrelevant(self):
        self.assert_finds("CLOSES #10 and Fixes #11", {10, 11})

    def test_a_colon_is_permitted(self):
        self.assert_finds("Closes: #12", {12})

    def test_trailing_punctuation_does_not_swallow_the_number(self):
        self.assert_finds("Closes #13.", {13})

    def test_backticks_do_not_hide_a_keyword(self):
        # GitHub's linker does not read markdown, so a keyword in backticks is
        # a live closure and the gate has to say so.
        self.assert_finds("the body says **`Closes #14`** and it means it", {14})

    def test_a_full_issue_url_counts(self):
        self.assert_finds(f"Closes https://github.com/{REPO}/issues/15", {15})

    def test_prose_after_a_keyword_is_not_a_reference(self):
        # A keyword followed by prose is English, and reading it as a closure
        # would fail pull requests that close nothing.
        self.assert_finds("It closes the naive spelling and nothing more.", set())

    def test_a_reference_in_unknown_markup_is_reported_not_dropped(self):
        """`~` is not in WRAPPERS, so the strip leaves a token nothing reads.

        It resolves to no issue and it is not prose, and GitHub may still honour
        it, so dropping it from the commit set would fail open.
        """
        numbers, unreadable = closing_references("Closes ~~#21~~", REPO)
        self.assertEqual(numbers, set())
        self.assertEqual(len(unreadable), 1, unreadable)

    def test_a_single_unknown_wrapper_is_reported_too(self):
        numbers, unreadable = closing_references("Fixes ~#21", REPO)
        self.assertEqual(numbers, set())
        self.assertEqual(len(unreadable), 1, unreadable)

    def test_a_hash_token_that_is_not_issue_shaped_is_still_reported(self):
        """The widening must not narrow the case that already worked."""
        numbers, unreadable = closing_references("Closes #topic", REPO)
        self.assertEqual(numbers, set())
        self.assertEqual(len(unreadable), 1, unreadable)

    def test_a_keyword_inside_a_longer_word_is_not_a_keyword(self):
        self.assert_finds("prefixes #16 and foreclosed #17", set())

    def test_a_bare_issue_number_with_no_keyword_is_not_a_closure(self):
        self.assert_finds("this is the defect #122 describes", set())

    def test_a_cross_repository_reference_is_refused_not_ignored(self):
        numbers, unreadable = closing_references("Closes other/repo#18", REPO)
        self.assertEqual(numbers, set())
        self.assertEqual(len(unreadable), 1)

    def test_a_cross_repository_url_is_refused_not_ignored(self):
        numbers, unreadable = closing_references(
            "Closes https://github.com/other/repo/issues/19", REPO)
        self.assertEqual(numbers, set())
        self.assertEqual(len(unreadable), 1)

    def test_a_keyword_shaped_token_that_names_nothing_is_reported(self):
        numbers, unreadable = closing_references("Closes #-alpha", REPO)
        self.assertEqual(numbers, set())
        self.assertEqual(len(unreadable), 1)

    def test_the_repository_comparison_ignores_case(self):
        self.assert_finds(f"Closes {REPO.upper()}#20", {20})

    def test_gh_n_is_an_issue_reference(self):
        self.assert_finds("Closes GH-42", {42})
        self.assert_finds("closes gh-43.", {43})

    def test_a_struck_through_gh_n_is_reported_not_dropped(self):
        numbers, unreadable = closing_references("Closes ~~GH-42~~", REPO)
        self.assertEqual(numbers, set())
        self.assertEqual(len(unreadable), 1, unreadable)

    def test_gh_inside_a_word_is_prose(self):
        self.assert_finds("Closes high-5 and nothing else", set())

    def test_a_colon_needs_no_space_after_it(self):
        self.assert_finds("Closes:#44", {44})

    def test_markup_between_the_keyword_and_the_token_is_read(self):
        self.assert_finds("**Closes** #45", {45})
        self.assert_finds("**Closes**: #46", {46})
        self.assert_finds("**Closes:** #47", {47})

    def test_an_underscore_is_a_keyword_boundary(self):
        self.assert_finds("_Closes #48", {48})
        self.assert_finds("pre_closes #49", {49})
        self.assert_finds("_Closes_ #50", {50})

    def test_a_scope_in_parentheses_is_not_a_keyword_separator(self):
        self.assert_finds("fix(#51): the parser", set())

    def test_prose_beside_the_widened_forms_stays_prose(self):
        self.assert_finds("**Closes** the door and _fixes_ nothing", set())
        self.assert_finds("closes_over a variable", set())


class AdjacentKeywords(unittest.TestCase):
    def test_a_keyword_before_a_keyword_still_finds_the_reference(self):
        for text in ("This fix closes #812", "the fix resolves #812", "Resolved: fixes #812"):
            with self.subTest(text=text):
                numbers, unreadable = closing_references(text, REPO)
                self.assertEqual(numbers, {812})
                self.assertEqual(unreadable, [])

    def test_an_unreadable_phrase_is_reported_whole(self):
        _, unreadable = closing_references("fix closes elsewhere/repo#5", REPO)
        self.assertEqual(unreadable, ["closes elsewhere/repo#5"])


class CutHeadlines(unittest.TestCase):
    def pull(self, headline: str, body: str) -> dict:
        data = payload()
        data["commits"][0].update(messageHeadline=headline, messageBody=body)
        return data

    def test_a_keyword_cut_from_its_reference_is_read(self):
        data = self.pull("fix(ci): the gate reads the wrapped binding, closes…", "… #812\n\nWhy.")
        problems = check(data)
        self.assertTrue(any("#812" in problem for problem in problems), problems)

    def test_a_number_cut_in_two_is_ambiguous_not_closed(self):
        data = self.pull("fix(ci): the gate reads the wrapped binding, closes #8…", "…12")
        problems = check(data)
        self.assertEqual(len(problems), 2, problems)
        self.assertTrue(all("differ" in problem for problem in problems), problems)
        self.assertFalse(any("is closed by commit" in problem for problem in problems), problems)

    def test_a_cut_reading_the_description_names_is_not_a_problem(self):
        data = self.pull("fix(ci): the gate reads the wrapped binding, closes…", "… #812")
        data["closingIssuesReferences"] = [{"number": 812}]
        data["body"] = "| Closes | #812 |\n\nCloses #812"
        self.assertEqual(check(data), [])

    def test_a_body_without_the_mark_is_not_rejoined(self):
        data = self.pull("fix: a closes…", "x #812")
        self.assertEqual(check(data), [])


class TableRow(unittest.TestCase):
    def test_a_gh_n_in_the_row_is_read(self):
        body = "| | |\n|---|---|\n| Closes | GH-88 (high), #81 |\n"
        self.assertEqual(declared_in_table(body), {88, 81})

    def test_the_house_metadata_row_is_read(self):
        body = "| | |\n|---|---|\n| Closes | #88 (high), #81 (high), #85 (high) |\n"
        self.assertEqual(declared_in_table(body), {88, 81, 85})

    def test_a_row_with_another_label_is_not_a_closure_claim(self):
        self.assertEqual(declared_in_table("| Reviewers | #88 |\n"), set())

    def test_a_bare_keyword_line_is_not_a_table_row(self):
        self.assertEqual(declared_in_table("Closes #88\n"), set())

    def test_a_row_without_a_trailing_pipe_reads_its_own_line_only(self):
        body = "| | |\n|---|---|\n| Closes | #88\n\nCloses #88\nCloses #99\n\n| a | b |\n"
        self.assertEqual(declared_in_table(body), {88})

    def test_a_row_without_a_trailing_pipe_at_the_end_of_the_body_is_read(self):
        self.assertEqual(declared_in_table("| | |\n|---|---|\n| Closes | #88, #81"), {88, 81})

    def test_a_crlf_row_without_a_trailing_pipe_reads_its_own_line_only(self):
        body = "| Closes | #88\r\n\r\nCloses #99\r\n| a | b |\r\n"
        self.assertEqual(declared_in_table(body), {88})

    def test_another_repositorys_issue_in_the_row_is_not_a_local_one(self):
        # A linked local issue beside a row naming another repository's issue of that number is no agreement.
        for cell in ("other/repo#5", "https://github.com/other/repo/issues/5", "#7, other/repo#5"):
            with self.subTest(cell=cell):
                problems = check(payload(body=f"| Closes | {cell} |\n\nCloses #5\nCloses #7\n", linked=(5, 7)))
                self.assertTrue(any("outside" in p and "other/repo" in p for p in problems), problems)
                self.assertTrue(any("#5" in p and "does not say so" in p for p in problems), problems)

    def test_this_repositorys_own_prefix_or_url_in_the_row_is_local(self):
        for cell in (f"{REPO}#5", f"https://github.com/{REPO}/issues/5", f"{REPO.upper()}#5"):
            with self.subTest(cell=cell):
                self.assertEqual([], check(payload(body=f"| Closes | {cell} |\n\nCloses #5\n", linked=(5,))))


class Repository(unittest.TestCase):
    def test_the_repository_comes_from_the_pull_request_url(self):
        self.assertEqual(repository_of(f"https://github.com/{REPO}/pull/117"), REPO)

    def test_an_issue_url_is_not_a_pull_request_url(self):
        self.assertIsNone(repository_of(f"https://github.com/{REPO}/issues/117"))


class Directions(unittest.TestCase):
    def test_over_closing_is_caught(self):
        """A commit keyword the description omits still closes on merge."""
        problems = check(payload(
            body="| Closes | #31, #32, #55 |\n\nCloses #31\nCloses #32\nCloses #55\n",
            commit_messages=(
                "fix: a\n\nCloses #30",
                "fix: b\n\nCloses #31",
                "fix: c\n\nCloses #32",
                "fix: d\n\nCloses #55",
                "fix: e\n\nCloses #56",
            ),
            linked=(31, 32, 55),
        ))
        self.assertEqual(len(problems), 2, problems)
        self.assertTrue(any("#30" in problem for problem in problems), problems)
        self.assertTrue(any("#56" in problem for problem in problems), problems)

    def test_under_closing_is_caught(self):
        """Keywords in the table cell alone link nothing."""
        problems = check(payload(
            body="| | |\n|---|---|\n| Closes | #84, #70, #40 |\n",
            commit_messages=("docs: a", "docs: b"),
            linked=(),
        ))
        self.assertEqual(len(problems), 3, problems)
        for number in (84, 70, 40):
            self.assertTrue(any(f"#{number}" in problem for problem in problems), problems)

    def test_a_linked_issue_the_table_omits_is_caught(self):
        problems = check(payload(
            body="Closes #77\n",
            commit_messages=("docs: a",),
            linked=(77,),
        ))
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("#77", problems[0])

    def test_a_row_without_a_trailing_pipe_cannot_hide_an_omission(self):
        """The cell stops at its line, so a later pipe cannot pad it."""
        problems = check(payload(
            body="| Closes | #88\n\nCloses #88\nCloses #99\n\n| a | b |\n",
            commit_messages=("docs: a",),
            linked=(88, 99),
        ))
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("#99", problems[0])

    def test_a_foreign_closing_reference_does_not_cover_a_local_number(self):
        """A foreign entry linked by hand must not hide a local issue with its number."""
        data = payload(
            body="| Closes | #7 |\n\nCloses #7\n",
            commit_messages=("fix: a\n\nCloses #7",),
        )
        data["closingIssuesReferences"] = [
            {"number": 7, "url": "https://github.com/other/repo/issues/7"}]
        problems = check(data)
        self.assertTrue(any("other/repo#7" in problem for problem in problems), problems)
        self.assertTrue(any("#7 is closed by commit" in problem for problem in problems), problems)

    def test_a_local_entry_with_a_url_is_still_linked(self):
        data = payload(
            body="| Closes | #7 |\n\nCloses #7\n",
            commit_messages=("fix: a\n\nCloses #7",),
        )
        data["closingIssuesReferences"] = [
            {"number": 7, "url": f"https://github.com/{REPO}/issues/7"}]
        self.assertEqual(check(data), [])

    def test_the_house_form_done_right_passes(self):
        self.assertEqual(check(payload(
            body="| | |\n|---|---|\n| Closes | #88 (high) |\n\nCloses #88\n",
            commit_messages=("fix: a\n\nCloses #88",),
            linked=(88,),
        )), [])

    def test_a_description_closure_no_commit_repeats_passes(self):
        """NoCommitRepeatsIt: a closure made only in the description is allowed.

        A `linked - from_commits` check would make a commit keyword mandatory
        and fail this.
        """
        self.assertEqual(check(payload(
            body="| | |\n|---|---|\n| Closes | #77 |\n\nCloses #77\n",
            commit_messages=("fix: a", "docs: b"),
            linked=(77,),
        )), [])

    def test_a_pull_request_closing_nothing_passes(self):
        self.assertEqual(check(payload(
            body="Some prose that closes the naive spelling and nothing more.",
            commit_messages=("docs: a",),
            linked=(),
        )), [])


class FailsClosed(unittest.TestCase):
    """The gate must never report a pass over a subject it did not read."""

    def test_every_required_field_is_reported_when_absent(self):
        """One case per field, spelled out here.

        A loop over `REQUIRED_FIELDS` would pass whatever that list contains,
        including a list an edit shortened.
        """
        for field in ("number", "url", "body", "commits",
                      "closingIssuesReferences", "headRefOid"):
            with self.subTest(field=field):
                broken = payload()
                del broken[field]
                problems = check(broken)
                self.assertEqual(len(problems), 1, (field, problems))
                self.assertIn(field, problems[0])

    def test_a_commit_list_missing_the_head_is_refused(self):
        """A read taken before GitHub has indexed the newest push.

        The list lacks the commit that may carry a keyword, which fails in the
        silent direction.
        """
        stale = payload(commit_messages=("fix: a\n\nCloses #7",), head_ref_oid=oid(9))
        problems = check(stale)
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("stale or truncated", problems[0])

    def test_a_fresh_read_naming_the_newest_commit_is_judged(self):
        """The guard must not refuse the ordinary case it was written beside."""
        self.assertEqual(check(payload(
            body="| Closes | #7 |\n\nCloses #7\n",
            commit_messages=("fix: a", "fix: b\n\nCloses #7"),
            linked=(7,),
        )), [])

    def test_a_commit_list_at_the_page_size_is_refused(self):
        problems = check(payload(
            body="| | |\n|---|---|\n| Closes | #88 |\n\nCloses #88\n",
            commit_messages=tuple(f"fix: {i}\n\nCloses #88" for i in range(100)),
            linked=(88,),
        ))
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("paginated endpoint", problems[0])

    def test_a_commit_list_below_the_page_size_is_judged(self):
        """The boundary is a boundary, not a permanent refusal."""
        self.assertEqual(check(payload(
            body="| | |\n|---|---|\n| Closes | #88 |\n\nCloses #88\n",
            commit_messages=tuple(f"fix: {i}\n\nCloses #88" for i in range(99)),
            linked=(88,),
        )), [])

    def test_a_linked_list_at_the_page_size_is_judged_not_refused(self):
        """The commit guard must not gain a twin here.

        `gh` preloads `closingIssuesReferences` through every page, so a list
        this long is complete.
        """
        numbers = tuple(range(1, 101))
        cell = " ".join(f"#{n}" for n in numbers)
        self.assertEqual(check(payload(
            body=f"| | |\n|---|---|\n| Closes | {cell} |\n",
            commit_messages=("fix: a",),
            linked=numbers,
        )), [])

    def test_a_missing_field_is_a_problem_not_a_pass(self):
        broken = payload()
        del broken["commits"]
        problems = check(broken)
        self.assertEqual(len(problems), 1)
        self.assertIn("commits", problems[0])

    def test_a_pull_request_with_no_commits_is_refused(self):
        empty = payload()
        empty["commits"] = []
        self.assertEqual(len(check(empty)), 1)

    def test_a_url_that_is_not_a_pull_request_is_refused(self):
        self.assertEqual(len(check(payload(url="https://example.com/"))), 1)

    def test_an_unreadable_keyword_in_a_commit_is_a_problem(self):
        problems = check(payload(
            commit_messages=("fix: a\n\nCloses elsewhere/repo#5",),
        ))
        self.assertTrue(any("elsewhere/repo#5" in problem for problem in problems), problems)


class DependabotDescription(unittest.TestCase):
    NOTES = (
        "Updated a from 1 to 2.\n\n<details>\n<summary>Release notes</summary>\n\n"
        "* Fix a range check - Fixes #​7565 #7566\n\n</details>\n"
    )
    DEPENDABOT = {"is_bot": True, "login": "app/dependabot"}

    def test_a_note_in_the_release_notes_is_not_read(self):
        self.assertEqual(check(payload(body=self.NOTES, author=self.DEPENDABOT)), [])

    def test_the_same_note_is_a_problem_for_any_other_author(self):
        problems = check(payload(body=self.NOTES))
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("7565", problems[0])

    def test_a_human_cannot_borrow_the_login(self):
        human = {"is_bot": False, "login": "app/dependabot"}
        self.assertEqual(len(check(payload(body=self.NOTES, author=human))), 1)

    def test_a_keyword_outside_the_details_block_is_still_read(self):
        problems = check(payload(body=self.NOTES + "\nFixes elsewhere/repo#5\n", author=self.DEPENDABOT))
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("elsewhere/repo#5", problems[0])

    def test_a_commit_keyword_is_read_whatever_the_author(self):
        problems = check(payload(
            body=self.NOTES,
            commit_messages=("chore: bump\n\nFixes elsewhere/repo#5",),
            author=self.DEPENDABOT,
        ))
        self.assertTrue(any("elsewhere/repo#5" in problem for problem in problems), problems)

    def test_a_link_github_makes_is_still_compared(self):
        problems = check(payload(body=self.NOTES, linked=(12,), author=self.DEPENDABOT))
        self.assertTrue(any("#12" in problem for problem in problems), problems)

    def test_a_block_nested_in_the_release_notes_is_skipped_whole(self):
        nested = (
            "<details>\n<summary>Release notes</summary>\n"
            "<details>\n<summary>Inner</summary>\nx\n</details>\n"
            "* Fixes #​7565\n</details>\n"
        )
        self.assertEqual(check(payload(body=nested, author=self.DEPENDABOT)), [])

    LINKED_NOTES = (
        "Updated a from 1 to 2.\n\n<details>\n<summary>Release notes</summary>\n\n"
        "* closes [#3227](https://github.com/stryker-mutator/stryker-net/issues/3227)\n\n"
        "</details>\n"
    )
    UPSTREAM = {"number": 3227, "url": "https://github.com/stryker-mutator/stryker-net/issues/3227"}

    def _linked(self, body, author):
        data = payload(body=body, author=author)
        data["closingIssuesReferences"] = [self.UPSTREAM]
        return check(data)

    def test_a_foreign_link_from_the_release_notes_is_excused(self):
        self.assertEqual(self._linked(self.LINKED_NOTES, self.DEPENDABOT), [])

    def test_the_same_foreign_link_is_refused_for_any_other_author(self):
        problems = self._linked(self.LINKED_NOTES, {"is_bot": False, "login": "someone"})
        self.assertTrue(any("stryker-mutator/stryker-net#3227" in p for p in problems), problems)

    def test_a_foreign_link_outside_the_details_block_is_still_refused(self):
        body = self.LINKED_NOTES + "\nsee https://github.com/stryker-mutator/stryker-net/issues/3227\n"
        problems = self._linked(body, self.DEPENDABOT)
        self.assertTrue(any("stryker-mutator/stryker-net#3227" in p for p in problems), problems)

    def test_a_foreign_link_the_notes_do_not_hold_is_still_refused(self):
        problems = self._linked(self.NOTES, self.DEPENDABOT)
        self.assertTrue(any("stryker-mutator/stryker-net#3227" in p for p in problems), problems)

    def test_a_longer_issue_number_in_the_notes_does_not_excuse_a_shorter_one(self):
        data = payload(body=self.LINKED_NOTES, author=self.DEPENDABOT)
        data["closingIssuesReferences"] = [
            {"number": 3, "url": "https://github.com/stryker-mutator/stryker-net/issues/3"}]
        problems = check(data)
        self.assertTrue(any("stryker-mutator/stryker-net#3" in p for p in problems), problems)

    def test_a_payload_without_author_is_read_whole(self):
        legacy = payload(body=self.NOTES)
        del legacy["author"]
        self.assertEqual(len(check(legacy)), 1)


class TheInput(unittest.TestCase):
    """stdin is `gh`'s UTF-8 whatever the locale says, the local pipe included."""

    GATE = Path(__file__).with_name("closure_gate.py")

    def run_gate(self, data: bytes) -> subprocess.CompletedProcess:
        # cp1252 is what a Windows console hands Python, and it has no
        # character for 0x9D, the last byte of a right double quote.
        env = {**os.environ, "PYTHONIOENCODING": "cp1252", "PYTHONUTF8": "0"}
        return subprocess.run([sys.executable, str(self.GATE)], input=data,
                              capture_output=True, env=env)

    def test_utf8_on_stdin_is_read_as_utf8_whatever_the_locale(self):
        text = json.dumps(payload(body="a “quoted” body"), ensure_ascii=False)
        result = self.run_gate(text.encode("utf-8"))
        self.assertEqual(0, result.returncode, result.stderr.decode("utf-8", "replace"))

    def test_stdin_that_is_not_utf8_is_refused_with_a_verdict(self):
        result = self.run_gate(b'{"body": "\xff"}')
        self.assertEqual(2, result.returncode)
        self.assertIn(b"not UTF-8", result.stderr)
        self.assertNotIn(b"Traceback", result.stderr)


if __name__ == "__main__":
    unittest.main()
