"""grok-ledger.sh: the count it publishes and the ceiling it enforces.

The shared harness and the reason it shells out rather than
re-implementing are `review_helpers.py`'s.
"""

import json
import os
import re
import shutil
import subprocess
import tempfile
import textwrap
import unittest
from pathlib import Path

from review_helpers import (
    SCRIPTS,
    REVIEW,
    LEDGER,
    BASH,
    setUpModule,
    run_bash,
    grep_matches,
    code_lines,
)


class LedgerStub:
    """A `gh` on PATH that answers the calls grok-ledger.sh makes.

    Which intake mode a case picks is a statement about what it tests. `rows=`
    supplies rows after jq, which exercises the trust check and the fold and
    says nothing about the shape filter. `comments=` supplies whole comment
    objects and runs the script's own `--jq` program over them, so the filter
    itself is the subject: a stub that hands back post-filter rows cannot notice
    a filter that dropped them.
    """

    def __init__(self, rows=None, permissions=None, poster="self", poster_id=900,
                 comments=None, script=None):
        if (rows is None) == (comments is None):
            raise AssertionError("supply exactly one of rows= or comments=")
        # `script=` lets a case drive a modified copy of the ledger, for a
        # property that is what a write and a read agree on after `CEILING`
        # moves.
        self.script = script or LEDGER
        self.dir = tempfile.mkdtemp(prefix="ledger-stub-")
        permissions = permissions or {}
        rows_file = Path(self.dir) / "rows"
        rows_file.write_text(
            "".join(f"{r}\n" for r in (rows or ())), encoding="utf-8"
        )
        self.rows_file = rows_file
        # The raw feed, when a case asks for one. `posted` is appended to by the
        # `pr comment` verb so the election still sees its own reservation.
        self.comments = list(comments) if comments is not None else None
        json_file = Path(self.dir) / "comments.json"
        self.json_file = json_file
        if self.comments is not None:
            json_file.write_text(json.dumps(self.comments), encoding="utf-8")
        perms_file = Path(self.dir) / "perms"
        perms_file.write_text(
            "".join(f"{k} {v}\n" for k, v in permissions.items()), encoding="utf-8"
        )
        gh = Path(self.dir) / "gh"
        gh.write_text(
            textwrap.dedent(
                f"""\
                #!/usr/bin/env bash
                # A stand-in for gh, driven by two files. Deliberately dumb:
                # it dispatches on the API path and nothing else.
                #
                # `pr comment` is the one WRITING verb, and it appends to the
                # same rows file the read serves — which is what lets the
                # election be exercised at all. Posting order is file order, and
                # the REST endpoint the real ledger reads returns issue comments
                # in posting order, so the stub agrees with the thing it stands
                # in for on the one property the election depends on.
                json={json_file.as_posix()!r}
                rows={rows_file.as_posix()!r}

                if [ "${{1:-}}" = "pr" ] && [ "${{2:-}}" = "comment" ]; then
                  body=""
                  while [ "$#" -gt 0 ]; do
                    if [ "$1" = "--body" ]; then body="$2"; shift 2; continue; fi
                    shift
                  done
                  if [ -f "$json" ]; then
                    tmp=$(mktemp)
                    jq --arg b "$body" --argjson i {poster_id} --arg l {poster!r} \\
                      '. + [{{id: $i, user: {{login: $l}}, body: $b}}]' "$json" > "$tmp"
                    mv "$tmp" "$json"
                  else
                    printf '%s\\t%s\\t%s\\n' {poster_id} {poster!r} "$body" >> "$rows"
                  fi
                  echo "https://github.com/o/r/pull/42#issuecomment-{poster_id}"
                  exit 0
                fi
                for arg in "$@"; do
                  case "$arg" in
                    */comments)
                      # The raw feed runs the script's OWN --jq program, so the
                      # shape filter is exercised rather than assumed. The rows
                      # feed skips it, which is what the two modes are for.
                      if [ -f "$json" ]; then
                        prog=""; prev=""
                        for a in "$@"; do
                          [ "$prev" = "--jq" ] && prog="$a"
                          prev="$a"
                        done
                        exec jq -r "$prog" "$json"
                      fi
                      exec cat "$rows"
                      ;;
                    */collaborators/*/permission)
                      login="${{arg#*/collaborators/}}"
                      login="${{login%/permission}}"
                      verdict=$(awk -v l="$login" '$1 == l {{ print $2 }}' {perms_file.as_posix()!r})
                      case "$verdict" in
                        admin|maintain|write|read|none) echo "$verdict"; exit 0 ;;
                        404) echo "gh: HTTP 404: Not Found" >&2; exit 1 ;;
                        *) echo "error connecting to api.github.com" >&2; exit 1 ;;
                      esac
                      ;;
                  esac
                done
                echo "stub gh: unexpected call: $*" >&2
                exit 99
                """
            ),
            encoding="utf-8",
        )
        gh.chmod(0o755)

    def run(self, *args):
        env = dict(os.environ)
        env["PATH"] = self.dir + os.pathsep + env["PATH"]
        return subprocess.run(
            [BASH, str(self.script), *args],
            capture_output=True,
            text=True,
            env=env,
        )

    def cleanup(self):
        shutil.rmtree(self.dir, ignore_errors=True)


class LedgerDoesNotFailOpen(unittest.TestCase):
    """The ledger publishes no count when its trust check fails.

    A legitimately empty ledger prints `0`, so a `0` on the error path would
    tell a model reading stdout "nothing spent" when the trust check never
    completed, and the cap would re-arm. An `exit` inside the last stage of a
    pipeline ends a subshell rather than the script, which is the shape that
    prints it.
    """

    def ledger(self, rows, permissions):
        stub = LedgerStub(rows, permissions)
        self.addCleanup(stub.cleanup)
        return stub

    def test_a_failed_permission_lookup_prints_nothing_at_all(self):
        # Both halves matter: a non-zero exit and an empty stdout.
        stub = self.ledger(
            ["101\talice\tGrok check 3/12 — reserved (full)"],
            {"alice": "network-error"},
        )
        result = stub.run("42", "count")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("", result.stdout.strip(), "a count was published on the error path")
        self.assertIn("permission", result.stderr)

    def test_status_does_not_publish_a_verdict_on_the_error_path(self):
        # The same shape one verb over: `unconverged` re-enters a loop that had
        # already converged.
        stub = self.ledger(
            ["101\talice\tGrok check 3/12 — converged: loop clean"],
            {"alice": "network-error"},
        )
        result = stub.run("42", "status")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("", result.stdout.strip())

    def test_an_empty_ledger_still_legitimately_counts_zero(self):
        # A fresh PR's ledger really is empty, so the error path is separated
        # upstream of the fold, not inside it.
        stub = self.ledger([], {})
        result = stub.run("42", "count")
        self.assertEqual(0, result.returncode)
        self.assertEqual("0", result.stdout.strip())

    def test_a_trusted_reservation_counts_as_spent(self):
        stub = self.ledger(
            [
                "101\talice\tGrok check 1/12 — reserved (full)",
                "102\talice\tGrok check 2/12 — reserved (recheck)",
            ],
            {"alice": "write"},
        )
        self.assertEqual("2", stub.run("42", "count").stdout.strip())

    def test_a_released_slot_is_not_spent(self):
        stub = self.ledger(
            [
                "101\talice\tGrok check 1/12 — reserved (full)",
                "102\talice\tGrok check 2/12 — reserved (full)",
                "103\talice\tGrok check 2/12 — released: skipped on limits",
            ],
            {"alice": "admin"},
        )
        self.assertEqual("1", stub.run("42", "count").stdout.strip())

    def test_an_untrusted_author_is_not_state(self):
        # A 404 is an outside author and their rows are dropped — the case the
        # trust check exists for, and the one that must not stop the helper.
        stub = self.ledger(
            [
                "101\tdrive-by\tGrok check 12/12 — reserved (full)",
                "102\talice\tGrok check 1/12 — reserved (full)",
            ],
            {"drive-by": "404", "alice": "write"},
        )
        result = stub.run("42", "count")
        self.assertEqual(0, result.returncode)
        self.assertEqual("1", result.stdout.strip())

    def test_a_read_permission_is_not_write_and_is_dropped(self):
        stub = self.ledger(
            ["101\tobserver\tGrok check 9/12 — reserved (full)"],
            {"observer": "read"},
        )
        result = stub.run("42", "count")
        self.assertEqual(0, result.returncode)
        self.assertEqual("0", result.stdout.strip())

    def test_a_converged_marker_is_reported_and_a_later_reservation_supersedes(self):
        stub = self.ledger(
            ["101\talice\tGrok check 4/12 — converged: loop clean"],
            {"alice": "write"},
        )
        self.assertEqual("converged", stub.run("42", "status").stdout.strip())

        stub = self.ledger(
            [
                "101\talice\tGrok check 4/12 — converged: loop clean",
                "102\talice\tGrok check 5/12 — reserved (full)",
            ],
            {"alice": "write"},
        )
        self.assertEqual("unconverged", stub.run("42", "status").stdout.strip())

    def test_no_consumer_pipes_the_row_reader_directly(self):
        # The structural half: the behavioural tests prove the consumers that
        # exist are safe, and this keeps a new one from piping the reader in the
        # obvious way.
        text = LEDGER.read_text(encoding="utf-8")
        code = "\n".join(
            line for line in text.splitlines() if not line.lstrip().startswith("#")
        )
        self.assertNotIn("ledger_rows |", code)
        self.assertEqual(1, code.count("rows=$(ledger_rows)"))


class TheCeilingBindsAndTheReadStaysWider(unittest.TestCase):
    """The ceiling refuses a reservation above it, and the read stays wider.

    The denominator is part of the comment shape `count` folds on, so a read
    narrowed to the current ceiling stops matching rows posted under a retired
    one, `count` answers zero, and the cap re-arms on a pull request that has
    spent it. The read stays wide and only the write moves, and the cases pin
    both directions.
    """

    def ledger(self, rows, permissions, **kw):
        stub = LedgerStub(rows, permissions, **kw)
        self.addCleanup(stub.cleanup)
        return stub

    def raw(self, comments, permissions, **kw):
        """A ledger fed whole comments, so the script's own jq filter decides.

        The read-side cases use this rather than `ledger()`, because post-jq
        rows have already been through the filter under test.
        """
        stub = LedgerStub(comments=comments, permissions=permissions, **kw)
        self.addCleanup(stub.cleanup)
        return stub

    @staticmethod
    def comment(cid, login, body):
        return {"id": cid, "user": {"login": login}, "body": body}

    @staticmethod
    def ceiling():
        """The ceiling, read out of the ledger rather than restated here.

        A literal in this file would be another copy of the number the ledger
        owns.
        """
        found = re.findall(
            r"^CEILING=([1-9][0-9]*)$", LEDGER.read_text(encoding="utf-8"), re.MULTILINE
        )
        if len(found) != 1:
            raise AssertionError(
                f"expected exactly one CEILING declaration in {LEDGER.name}, "
                f"found {len(found)}"
            )
        return int(found[0])

    def run_review(self, *args):
        return subprocess.run(
            [BASH, str(REVIEW), *args],
            capture_output=True,
            text=True,
            cwd=str(SCRIPTS),
        )

    # ---- the write side: the ceiling is what refuses -----------------------

    def test_the_ledger_refuses_a_reservation_above_the_ceiling(self):
        stub = self.ledger([], {"self": "write"})
        result = stub.run("42", "reserve", str(self.ceiling() + 1), "full")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(
            "", stub.rows_file.read_text(encoding="utf-8").strip(),
            "a refused reservation still posted a row",
        )

    def test_the_review_helper_refuses_the_same_slot(self):
        # The command the loop invokes refuses the same slot, or a check above
        # the ceiling stays reachable through it.
        result = self.run_review(str(self.ceiling() + 1), "full")
        self.assertEqual(2, result.returncode)
        self.assertIn(f"1..{self.ceiling()}", result.stderr)

    def test_the_ceiling_at_its_own_value_is_still_admitted(self):
        # The positive control: a refusal that fires
        # on every slot would satisfy both cases above while breaking the loop.
        stub = self.ledger([], {"self": "write"})
        result = stub.run("42", "reserve", str(self.ceiling()), "full")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn(f"{self.ceiling()}/{self.ceiling()}", result.stdout)

    def test_the_review_helper_derives_the_ceiling_rather_than_restating_it(self):
        # The structural claim, tested behaviourally. Copying the pair into a
        # scratch directory and moving only the ledger's declaration proves the
        # review helper reads it: a second literal would keep refusing at the
        # old value and this case would fail.
        scratch = Path(tempfile.mkdtemp(prefix="ceiling-"))
        self.addCleanup(shutil.rmtree, scratch, True)
        moved = 3
        self.assertNotEqual(moved, self.ceiling(), "pick a value the repo does not use")
        (scratch / LEDGER.name).write_text(
            re.sub(
                r"^CEILING=[1-9][0-9]*$",
                f"CEILING={moved}",
                LEDGER.read_text(encoding="utf-8"),
                count=1,
                flags=re.MULTILINE,
            ),
            encoding="utf-8",
        )
        review = scratch / REVIEW.name
        review.write_text(REVIEW.read_text(encoding="utf-8"), encoding="utf-8")
        result = subprocess.run(
            [BASH, str(review), str(moved + 1), "full"],
            capture_output=True, text=True, cwd=str(scratch),
        )
        self.assertEqual(2, result.returncode)
        self.assertIn(f"1..{moved}", result.stderr)

    def test_an_unreadable_ceiling_refuses_rather_than_admits(self):
        # Fails closed. The failure mode of a cap is the direction that must
        # never be the quiet one, and an empty `$ceiling` in a `-le` test is an
        # error rather than an unbounded pass.
        scratch = Path(tempfile.mkdtemp(prefix="ceiling-"))
        self.addCleanup(shutil.rmtree, scratch, True)
        (scratch / LEDGER.name).write_text(
            re.sub(
                r"^CEILING=[1-9][0-9]*$", "# CEILING removed",
                LEDGER.read_text(encoding="utf-8"), count=1, flags=re.MULTILINE,
            ),
            encoding="utf-8",
        )
        review = scratch / REVIEW.name
        review.write_text(REVIEW.read_text(encoding="utf-8"), encoding="utf-8")
        result = subprocess.run(
            [BASH, str(review), "1", "full"],
            capture_output=True, text=True, cwd=str(scratch),
        )
        self.assertEqual(2, result.returncode)
        self.assertIn("CEILING", result.stderr)

    # ---- the read side: the migration hazard -------------------------------

    def test_a_row_posted_under_the_old_ceiling_is_still_spent(self):
        # The reason the read is wider than the write: narrow the filter and
        # `count` answers 0 for a pull request that has spent nine checks.
        stub = self.raw(
            [self.comment(101, "alice", "Grok check 9/12 — reserved (full)")],
            {"alice": "write"},
        )
        result = stub.run("42", "count")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("9", result.stdout.strip())

    def test_a_ledger_holding_both_shapes_folds_into_one_count(self):
        # A loop that started under a retired ceiling and resumed under the
        # current one.
        stub = self.raw(
            [
                self.comment(101, "alice", "Grok check 3/12 — reserved (full)"),
                self.comment(102, "alice", "Grok check 4/6 — reserved (recheck)"),
            ],
            {"alice": "write"},
        )
        self.assertEqual("4", stub.run("42", "count").stdout.strip())

    def test_a_release_in_one_shape_frees_a_slot_claimed_in_the_other(self):
        # The fold takes the last event per slot, and the slot is parsed rather
        # than matched against a denominator — so a `4/12` release settles a
        # `4/6` reservation. Keyed on the literal instead, the two would be
        # different slots and the release would free nothing.
        stub = self.raw(
            [
                self.comment(101, "alice", "Grok check 3/12 — reserved (full)"),
                self.comment(102, "alice", "Grok check 4/6 — reserved (recheck)"),
                self.comment(103, "alice",
                             "Grok check 4/12 — released: skipped on limits"),
            ],
            {"alice": "write"},
        )
        self.assertEqual("3", stub.run("42", "count").stdout.strip())

    def test_a_shape_this_ledger_never_wrote_is_not_state(self):
        # The filter's own job. `3/7` is not a denominator this ledger
        # writes, and a reader that accepted it
        # would be counting an arithmetic nobody here chose — which on a public
        # pull request is anyone's.
        stub = self.raw(
            [
                self.comment(101, "alice", "Grok check 3/7 — reserved (full)"),
                self.comment(102, "alice", "Grok check 13/12 — reserved (full)"),
            ],
            {"alice": "write"},
        )
        self.assertEqual("0", stub.run("42", "count").stdout.strip())

    def test_a_slot_above_its_own_denominator_is_not_state(self):
        # The shape filter admits the cross-product of the two alternations, so
        # `9/6` matches, and no writer can emit it because the write side caps
        # a slot at its own ceiling. A trusted-looking manual row would
        # otherwise make `count` report 9 and jam the cap.
        #
        # Checked in arithmetic rather than as paired regex ranges, which would
        # need one alternation per retired ceiling and would rot with the next.
        stub = self.raw(
            [
                self.comment(101, "alice", "Grok check 9/6 — reserved (full)"),
                self.comment(102, "alice", "Grok check 12/6 — reserved (full)"),
                self.comment(103, "alice", "Grok check 2/6 — reserved (full)"),
            ],
            {"alice": "write"},
        )
        self.assertEqual("2", stub.run("42", "count").stdout.strip())

    def test_an_impossible_pair_cannot_report_convergence(self):
        # `status` is where the cost is highest: a `converged` read from a row
        # no writer can emit lets a resumed run skip review entirely. The check
        # lives in the shared reader, so every consumer has it.
        stub = self.raw(
            [self.comment(101, "alice", "Grok check 9/6 — converged: loop clean")],
            {"alice": "write"},
        )
        self.assertEqual("unconverged", stub.run("42", "status").stdout.strip())

    def test_a_legitimate_convergence_marker_still_reports(self):
        # The positive control: a reader that dropped every row would satisfy
        # the case above while breaking the marker the loop depends on.
        stub = self.raw(
            [self.comment(101, "alice", "Grok check 3/6 — converged: loop clean")],
            {"alice": "write"},
        )
        self.assertEqual("converged", stub.run("42", "status").stdout.strip())

    def test_an_impossible_pair_cannot_win_an_election_either(self):
        # The same guard on the other consumer. A row `count` refuses to see
        # must not be able to take a slot from a legitimate claimant.
        stub = self.raw(
            [self.comment(101, "alice", "Grok check 5/4 — reserved (full)")],
            {"alice": "write", "self": "write"},
        )
        self.assertEqual(0, stub.run("42", "reserve", "5", "full").returncode)

    def test_a_ledger_line_inside_a_longer_comment_is_not_state(self):
        # The filter's `test()` is anchored. A review body quoting a ledger line
        # is the ordinary way here, not an attack.
        stub = self.raw(
            [
                self.comment(
                    101, "alice",
                    "I think we are at\nGrok check 9/12 — reserved (full)\nalready.",
                ),
            ],
            {"alice": "write"},
        )
        self.assertEqual("0", stub.run("42", "count").stdout.strip())

    def test_an_untrusted_author_is_still_dropped_on_the_raw_feed(self):
        # The trust check and the shape filter are two gates, and the raw intake
        # mode passes through both.
        stub = self.raw(
            [self.comment(101, "mallory", "Grok check 6/6 — reserved (full)")],
            {"mallory": "404"},
        )
        self.assertEqual("0", stub.run("42", "count").stdout.strip())

    @staticmethod
    def declared_in_ledger(name, script=None):
        """One declaration's value, as bash composes it.

        Evaluated rather than pattern-matched, because `LEDGER_DENOMINATORS` is
        derived from `$CEILING` and a regex over the source would read the
        expression instead of the value.
        """
        path = (script or LEDGER).as_posix()
        out = run_bash(
            'eval "$(grep -E \'^(CEILING|LEDGER_)[A-Z_]*=\' "$L")"; '
            'printf %s "${!N}"',
            L=path, N=name,
        )
        return out.stdout

    def test_the_read_pattern_is_declared_once_and_actually_wired_in(self):
        # Declared away from the code that applies them, and asserted to reach
        # it. A denominator list
        # that drifted out of the jq filter would fail closed and silently —
        # `count` reading zero looks exactly like a fresh pull request.
        text = LEDGER.read_text(encoding="utf-8")
        for name in ("LEDGER_READ_SLOTS", "LEDGER_DENOMINATORS"):
            with self.subTest(name=name):
                self.assertEqual(
                    1,
                    len(re.findall(rf"^{name}=\S", text, re.MULTILINE)),
                    f"{name} is not declared exactly once",
                )
                self.assertIn(f'\'"${name}"\'', text, f"{name} is declared and unused")

    def test_the_current_denominator_is_derived_and_not_restated(self):
        # Only retired denominators are listed; the current one comes from
        # `$CEILING`, so the bound has one literal.
        text = LEDGER.read_text(encoding="utf-8")
        ceiling = self.declared_in_ledger("CEILING")
        retired = self.declared_in_ledger("LEDGER_RETIRED_DENOMINATORS")
        self.assertNotIn(
            ceiling, retired,
            "the current ceiling is listed as retired; it must be derived",
        )
        # `re.MULTILINE`, because `assertRegex` searches without it and `^` would
        # anchor at the start of the whole file rather than at a line.
        self.assertTrue(
            re.search(r'^LEDGER_DENOMINATORS="\$CEILING\|', text, re.MULTILINE),
            "the readable set must be composed from $CEILING, not restated",
        )

    def test_the_denominator_alternation_admits_both_and_nothing_else(self):
        # Run on the same engine the script does, over the value bash composes
        # rather than a restatement of it.
        pattern = f"^({self.declared_in_ledger('LEDGER_DENOMINATORS')})$"
        # Derived rather than restated, so this case holds no copy of the
        # numbers.
        accepted = {self.declared_in_ledger("CEILING")} | set(
            self.declared_in_ledger("LEDGER_RETIRED_DENOMINATORS").split("|")
        )
        self.assertGreater(len(accepted), 1, "the accepted set is not vacuous")
        for good in accepted:
            with self.subTest(value=good):
                self.assertTrue(grep_matches(pattern, good))
        for bad in ("1", "2", "7", "60", "126", ""):
            with self.subTest(value=bad):
                self.assertNotIn(bad, accepted)
                self.assertFalse(grep_matches(pattern, bad))

    def test_moving_the_ceiling_keeps_the_write_readable(self):
        # A write followed by a read through a ledger whose `CEILING` moved: a
        # read that did not follow the move would make every reservation
        # invisible to `count` and re-arm the cap. The composed pattern alone is
        # not enough, so this reserves and then counts.
        scratch = Path(tempfile.mkdtemp(prefix="ceiling-rt-"))
        self.addCleanup(shutil.rmtree, scratch, True)
        moved = 4
        self.assertNotEqual(moved, self.ceiling())
        script = scratch / LEDGER.name
        script.write_text(
            re.sub(
                r"^CEILING=[1-9][0-9]*$", f"CEILING={moved}",
                LEDGER.read_text(encoding="utf-8"), count=1, flags=re.MULTILINE,
            ),
            encoding="utf-8",
        )
        stub = self.raw([], {"self": "write"}, script=script)
        reserved = stub.run("42", "reserve", "3", "full")
        self.assertEqual(0, reserved.returncode, reserved.stderr)
        self.assertIn(f"3/{moved}", reserved.stdout)
        # The read half.
        self.assertEqual("3", stub.run("42", "count").stdout.strip())

    def test_moving_the_ceiling_still_keeps_retired_rows_spent(self):
        # Retired rows stay spent after the move too, or the cap re-arms the
        # other way.
        scratch = Path(tempfile.mkdtemp(prefix="ceiling-rt-"))
        self.addCleanup(shutil.rmtree, scratch, True)
        script = scratch / LEDGER.name
        script.write_text(
            re.sub(
                r"^CEILING=[1-9][0-9]*$", "CEILING=4",
                LEDGER.read_text(encoding="utf-8"), count=1, flags=re.MULTILINE,
            ),
            encoding="utf-8",
        )
        stub = self.raw(
            [self.comment(101, "alice", "Grok check 9/12 — reserved (full)")],
            {"alice": "write"}, script=script,
        )
        self.assertEqual("9", stub.run("42", "count").stdout.strip())

    def test_the_write_never_emits_a_retired_denominator(self):
        # The other direction: reading `/12` forever is deliberate, writing it
        # again is the drift. Every body this file composes takes $CEILING.
        code = "\n".join(code_lines(LEDGER.read_text(encoding="utf-8")))
        self.assertNotIn("/12 —", code)
        self.assertEqual(3, code.count('body="Grok check $n/$CEILING — '))

    # ---- the election, which the migration could have split ----------------

    def test_a_first_claim_wins_its_slot(self):
        # The positive control for the two cases below. Without it, an election
        # that refused everything would satisfy them both.
        stub = self.raw([], {"self": "write"})
        result = stub.run("42", "reserve", "3", "full")
        self.assertEqual(0, result.returncode, result.stderr)

    def test_the_election_sees_a_standing_claim_in_the_old_shape(self):
        # The slot is parsed: keyed on the literal prefix, an election under the
        # current ceiling cannot see a claim posted under a retired one, and two
        # runs would both believe they had won slot 3.
        stub = self.raw(
            [self.comment(101, "alice", "Grok check 3/12 — reserved (full)")],
            {"alice": "write", "self": "write"},
        )
        result = stub.run("42", "reserve", "3", "full")
        self.assertEqual(4, result.returncode)
        self.assertIn("claimed first", result.stderr)

    def test_a_release_in_the_old_shape_reopens_the_slot(self):
        # And the converse, which is why the election resets on a release rather
        # than honouring the first claim ever made: a released slot is
        # legitimately re-spent, in whichever shape the release was written.
        stub = self.raw(
            [
                self.comment(101, "alice", "Grok check 3/12 — reserved (full)"),
                self.comment(102, "alice",
                             "Grok check 3/12 — released: skipped on limits"),
            ],
            {"alice": "write", "self": "write"},
        )
        result = stub.run("42", "reserve", "3", "full")
        self.assertEqual(0, result.returncode, result.stderr)



if __name__ == "__main__":
    unittest.main()
