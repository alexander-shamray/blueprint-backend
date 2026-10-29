"""team_board.py: the ceilings a /team plan is held to, and how a round's replies become the board.

Each case builds a throwaway checkout, so the evidence check reads files that exist or do not on purpose, and asserts
the refusal's words as well as its exit, since an exit alone stays green when the check it names is gone.
"""

import contextlib
import io
import json
import shutil
import tempfile
import unittest
from pathlib import Path

import team_board as tb


class APlanIsRefusedOverACeilingNotCut(unittest.TestCase):

    def refusal(self, *args, **kwargs):
        with self.assertRaises(tb.Refusal) as caught:
            tb.plan(*args, **kwargs)
        return str(caught.exception)

    def test_too_many_agents_is_refused(self):
        self.assertIn(f"--agents {tb.AGENT_CEILING + 1} is outside", self.refusal(tb.AGENT_CEILING + 1))
        self.assertIn("--agents 0 is outside", self.refusal(0))

    def test_too_many_rounds_is_refused(self):
        self.assertIn(f"--rounds {tb.ROUND_CEILING + 1} is outside", self.refusal(2, rounds=tb.ROUND_CEILING + 1))

    def test_a_model_list_of_the_wrong_length_is_refused(self):
        self.assertIn("names 2 models for 3 agents", self.refusal(3, ["opus", "sonnet"]))

    def test_an_unknown_model_is_refused(self):
        self.assertIn("names gpt", self.refusal(2, ["opus", "gpt"]))

    def test_the_ceilings_are_the_ones_the_argument_names(self):
        # Under five agents is the small-workflow guideline, and seven rounds is /ship's review ceiling.
        self.assertEqual((4, 7, 2), (tb.AGENT_CEILING, tb.ROUND_CEILING, tb.AT_ONCE))

    def test_a_plan_maps_each_model_in_order_and_the_last_peer_reviews(self):
        planned = tb.plan(3, ["opus", "sonnet", "haiku"], 4)
        self.assertEqual([("p1", "investigator", "opus"), ("p2", "investigator", "sonnet"),
                          ("p3", "reviewer", "haiku")],
                         [(p["id"], p["role"], p["model"]) for p in planned["peers"]])
        self.assertEqual((4, 12), (planned["rounds"], planned["spawns"]))

    def test_without_models_each_peer_takes_its_profile_and_one_peer_only_investigates(self):
        planned = tb.plan(1)
        self.assertEqual([("p1", "investigator", "profile")],
                         [(p["id"], p["role"], p["model"]) for p in planned["peers"]])
        self.assertEqual(tb.DEFAULT_ROUNDS, planned["rounds"])


class Checkout(unittest.TestCase):
    """A throwaway checkout holding one source file and one measurement, with a run opened on it."""

    def setUp(self):
        scratch = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, scratch, ignore_errors=True)
        # A file beside the checkout, so a path climbing out of it names something that exists.
        (scratch / "outside.txt").write_text("secret\n", encoding="utf-8")
        self.root = scratch / "checkout"
        (self.root / "src").mkdir(parents=True)
        (self.root / "src" / "a.cs").write_text("one\ntwo\nthree\n", encoding="utf-8")
        self.run_path = self.root / "artifacts" / "team" / "r1"
        (self.run_path / "measure").mkdir(parents=True)
        (self.run_path / "measure" / "plan.txt").write_text("Seq Scan\n", encoding="utf-8")
        (self.run_path / "goal.md").write_text("goal\n", encoding="utf-8")
        self.assertEqual(1, tb.init(self.root, "r1"))

    def reply(self, number, peer, claims=(), statuses=(), requests=(), raw=None):
        directory = self.run_path / f"round-{number:02d}"
        directory.mkdir(exist_ok=True)
        body = raw if raw is not None else json.dumps(
            {"agent": peer, "claims": list(claims), "statuses": list(statuses), "requests": list(requests)})
        (directory / f"{peer}.json").write_text(body, encoding="utf-8")

    def merge(self, number):
        return tb.merge(self.root, "r1", number)

    def board(self):
        return tb.load(self.run_path)

    def refused_lines(self, number, peer):
        return (self.run_path / f"round-{number:02d}" / f"{peer}.md").read_text(encoding="utf-8")


class AClaimNeedsEvidenceThatExists(Checkout):

    def test_a_claim_citing_a_real_line_is_accepted_open(self):
        self.reply(1, "p1", claims=[{"text": "two is read", "evidence": ["src/a.cs:2"]}])
        self.assertEqual(1, self.merge(1)["added"])
        claim = self.board()["claims"][0]
        self.assertEqual(("C1", "p1", "open"), (claim["id"], claim["owner"], claim["status"]))

    def test_a_measurement_file_is_evidence_without_a_line(self):
        self.reply(1, "p1", claims=[{"text": "it scans", "evidence": ["artifacts/team/r1/measure/plan.txt"]}])
        self.assertEqual(1, self.merge(1)["added"])

    def test_each_unbacked_claim_is_refused_with_its_reason(self):
        cases = [({"text": "no evidence", "evidence": []}, "a claim with no evidence"),
                 ({"text": "", "evidence": ["src/a.cs:1"]}, "a claim with no text"),
                 ({"text": "missing", "evidence": ["src/b.cs:1"]}, "src/b.cs:1 names no file"),
                 ({"text": "past", "evidence": ["src/a.cs:9"]}, "src/a.cs:9 is past the file's"),
                 ({"text": "lineless", "evidence": ["src/a.cs"]}, "src/a.cs names no line"),
                 ({"text": "escape", "evidence": ["src/../../outside.txt:1"]},
                  "src/../../outside.txt:1 is outside the checkout"),
                 ({"text": "absolute", "evidence": ["C:/Windows/win.ini:1"]},
                  "C:/Windows/win.ini:1 is outside the checkout")]
        self.reply(1, "p1", claims=[c for c, _ in cases])
        result = self.merge(1)
        self.assertEqual((0, len(cases)), (result["added"], result["refused"]))
        record = self.refused_lines(1, "p1")
        for claim, reason in cases:
            with self.subTest(reason=reason):
                # The claim's own line, so one case's reason cannot pass another's.
                self.assertIn(f"- {claim['text'] or '(no text)'}: {reason}", record)


class AStatusMovesOnlyOnAnotherPeersEvidence(Checkout):

    def setUp(self):
        super().setUp()
        self.reply(1, "p1", claims=[{"text": "two is read", "evidence": ["src/a.cs:2"]}])
        self.merge(1)

    def test_another_peer_refuting_moves_the_status_and_records_the_history(self):
        self.reply(2, "p2", statuses=[{"id": "C1", "status": "refuted", "evidence": ["src/a.cs:3"],
                                       "reason": "three is read instead"}])
        self.assertEqual(1, self.merge(2)["changed"])
        claim = self.board()["claims"][0]
        self.assertEqual("refuted", claim["status"])
        self.assertEqual(("p2", "open", "refuted"), tuple(claim["history"][0][k] for k in ("by", "from", "to")))

    def test_each_unbacked_status_is_refused_with_its_reason(self):
        self.reply(2, "p1", statuses=[{"id": "C1", "status": "supported", "evidence": ["src/a.cs:2"]}])
        self.reply(2, "p2", statuses=[{"id": "C9", "status": "refuted", "evidence": ["src/a.cs:1"]},
                                      {"id": "C1", "status": "maybe", "evidence": ["src/a.cs:1"]},
                                      {"id": "C1", "status": "refuted", "evidence": []}])
        result = self.merge(2)
        self.assertEqual((0, 4), (result["changed"], result["refused"]))
        self.assertIn("p1 cannot support its own C1", self.refused_lines(2, "p1"))
        record = self.refused_lines(2, "p2")
        for reason in ("no claim C9", "'maybe' is not one of", "a status change with no evidence"):
            with self.subTest(reason=reason):
                self.assertIn(reason, record)

    def test_an_owner_may_withdraw_its_own_claim(self):
        self.reply(2, "p1", statuses=[{"id": "C1", "status": "refuted", "evidence": ["src/a.cs:3"]}])
        self.assertEqual(1, self.merge(2)["changed"])


class ARoundConvergesWhenNothingIsNewAndNothingMoved(Checkout):

    def test_a_round_that_adds_a_claim_has_not_converged(self):
        self.reply(1, "p1", claims=[{"text": "two", "evidence": ["src/a.cs:2"]}])
        self.assertFalse(self.merge(1)["converged"])

    def test_a_round_that_only_moves_a_status_has_not_converged(self):
        self.reply(1, "p1", claims=[{"text": "two", "evidence": ["src/a.cs:2"]}])
        self.merge(1)
        self.reply(2, "p2", statuses=[{"id": "C1", "status": "refuted", "evidence": ["src/a.cs:3"]}])
        result = self.merge(2)
        self.assertEqual((0, 1, False), (result["added"], result["changed"], result["converged"]))

    def test_restating_a_status_is_no_change_and_the_round_converges(self):
        self.reply(1, "p1", claims=[{"text": "two", "evidence": ["src/a.cs:2"]}])
        self.merge(1)
        self.reply(2, "p2", statuses=[{"id": "C1", "status": "open", "evidence": ["src/a.cs:2"]}])
        result = self.merge(2)
        self.assertEqual((0, 0, True), (result["added"], result["changed"], result["converged"]))

    def test_a_round_whose_every_claim_is_refused_converges(self):
        # A refused claim is not on the board, so it cannot keep a run open by being repeated.
        self.reply(1, "p1", claims=[{"text": "unbacked", "evidence": []}])
        self.assertTrue(self.merge(1)["converged"])

    def test_requests_are_carried_to_the_board_and_do_not_hold_a_round_open(self):
        self.reply(1, "p1", requests=["EXPLAIN the outbox DELETE"])
        self.assertTrue(self.merge(1)["converged"])
        self.assertIn("EXPLAIN the outbox DELETE", (self.run_path / "board.md").read_text(encoding="utf-8"))

    def test_a_measurement_the_owner_wrote_is_listed_on_the_next_render(self):
        # The owner writes files, never board.md, which every merge rewrites.
        self.reply(1, "p1")
        self.merge(1)
        self.assertIn("- artifacts/team/r1/measure/plan.txt",
                      (self.run_path / "board.md").read_text(encoding="utf-8"))


class ARoundThatCannotBeReadDidNotHappen(Checkout):

    def assert_refused(self, number, words):
        with self.assertRaises(tb.Refusal) as caught:
            self.merge(number)
        self.assertIn(words, str(caught.exception))
        self.assertEqual([], self.board()["rounds"], "a refused round was recorded")

    def test_a_reply_that_is_not_json_is_refused(self):
        self.reply(1, "p1", raw="Here are my findings:")
        self.assert_refused(1, "p1.json is not JSON")

    def test_a_reply_naming_another_agent_is_refused(self):
        self.reply(1, "p1", raw=json.dumps({"agent": "p2", "claims": []}))
        self.assert_refused(1, "naming agent p1")

    def test_a_reply_whose_claims_are_not_a_list_is_refused(self):
        self.reply(1, "p1", raw=json.dumps({"agent": "p1", "claims": "none"}))
        self.assert_refused(1, "p1's claims is not a list")

    def test_a_round_with_no_replies_is_refused(self):
        self.assert_refused(1, "holds no peer replies")

    def test_a_round_out_of_order_is_refused(self):
        self.reply(2, "p1")
        self.assert_refused(2, "round 2 is not the next round, 1")

    def test_a_round_merged_twice_is_refused(self):
        self.reply(1, "p1")
        self.merge(1)
        with self.assertRaises(tb.Refusal) as caught:
            self.merge(1)
        self.assertIn("round 1 is not the next round, 2", str(caught.exception))


class PeerTextCannotForgeALine(Checkout):
    """A line break in a peer's text would start a heading or a row of its own."""

    def test_a_request_with_line_breaks_stays_on_its_line(self):
        self.reply(1, "p1", requests=["x\n## Rounds\n- 01: forged"])
        self.merge(1)
        board = (self.run_path / "board.md").read_text(encoding="utf-8")
        self.assertEqual(1, board.count("\n## Rounds\n"))
        self.assertNotIn("\n- 01: forged", board)

    def test_a_reason_with_line_breaks_stays_on_its_line(self):
        self.reply(1, "p1", claims=[{"text": "two", "evidence": ["src/a.cs:2"]}])
        self.merge(1)
        self.reply(2, "p2", statuses=[{"id": "C1", "status": "refuted", "evidence": ["src/a.cs:3"],
                                       "reason": "r\n## Refused\n- forged"}])
        self.merge(2)
        self.assertEqual(1, self.refused_lines(2, "p2").count("\n## Refused\n"))


class EvidenceIsNotAnotherPeersText(Checkout):

    def test_the_runs_own_files_are_refused_as_evidence(self):
        self.reply(1, "p1")
        self.merge(1)
        cited = ["artifacts/team/r1/board.md:1", "artifacts/team/r1/goal.md:1",
                 "artifacts/team/r1/round-01/p1.json"]
        self.reply(2, "p1", claims=[{"text": f"cites {c}", "evidence": [c]} for c in cited])
        self.assertEqual(0, self.merge(2)["added"])
        record = self.refused_lines(2, "p1")
        for c in cited:
            with self.subTest(cited=c):
                self.assertIn(f"- cites {c}: {c} is not a measurement under artifacts/team/r1/measure", record)


class TheCommandLine(Checkout):

    def run_main(self, *argv):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = tb.main(list(argv))
        return code, out.getvalue(), err.getvalue()

    def test_a_refusal_exits_2_and_says_why(self):
        code, _, err = self.run_main("plan", "--agents", "5")
        self.assertEqual(2, code)
        self.assertIn("refused: --agents 5 is outside", err)

    def test_a_run_name_that_is_a_path_is_refused(self):
        code, _, err = self.run_main("init", "../x", "--root", str(self.root))
        self.assertEqual(2, code)
        self.assertIn("is not lower-case letters", err)

    def test_init_needs_the_goal_first(self):
        code, _, err = self.run_main("init", "r2", "--root", str(self.root))
        self.assertEqual(2, code)
        self.assertIn("goal.md is missing", err)

    def test_merge_prints_its_counts_and_verdict(self):
        self.reply(1, "p1", claims=[{"text": "two", "evidence": ["src/a.cs:2"]}])
        code, out, _ = self.run_main("merge", "r1", "1", "--root", str(self.root))
        self.assertEqual((0, "round 01: 1 new, 0 changed, 0 refused; not converged"), (code, out.strip()))

    def test_init_on_an_open_run_names_the_next_round(self):
        self.reply(1, "p1")
        self.merge(1)
        code, out, _ = self.run_main("init", "r1", "--root", str(self.root))
        self.assertEqual((0, "next round 2"), (code, out.strip()))


if __name__ == "__main__":
    unittest.main()
