"""store_check.py against a local server that answers as a store configured to taste."""

import datetime
import http.server
import pathlib
import re
import threading
import unittest

import store_check

DENIED = ("403", "<Error><Code>AccessDenied</Code></Error>")
UNKNOWN_KEY = ("403", "<Error><Code>InvalidAccessKeyId</Code></Error>")
ALLOWED = ("204", "")


class Store:
    """A server whose answer is chosen per (method, path prefix) and recorded."""

    def __init__(self, answers):
        self.answers = answers
        self.seen = []
        owner = self

        class Handler(http.server.BaseHTTPRequestHandler):
            def _answer(self):
                owner.seen.append((self.command, self.path, self.headers.get("Authorization", "")))
                kind = "list" if "list-type" in self.path else self.command
                status, body = owner.answers[kind]
                self.send_response(int(status))
                self.end_headers()
                self.wfile.write(body.encode())

            do_GET = do_DELETE = _answer

            def log_message(self, *args):
                pass

        self.server = http.server.HTTPServer(("127.0.0.1", 0), Handler)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    @property
    def endpoint(self):
        return f"http://127.0.0.1:{self.server.server_port}"

    def close(self):
        self.server.shutdown()
        self.server.server_close()

    def env(self, **over):
        env = {name: "x" for name in store_check.REQUIRED}
        env.update(
            {
                store_check.ENDPOINT: self.endpoint,
                store_check.BUCKET: "product-images",
                store_check.REGION: "us-east-1",
            }
        )
        env.update(over)
        return env


class SigningTests(unittest.TestCase):
    def test_the_derived_key_is_the_one_aws_publishes(self):
        key = store_check.signing_key("wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", "20150830", "us-east-1", "iam")
        self.assertEqual(key.hex(), "c4afb1cc5771d871763a393e44b703571b55cc28424d1a5e86da6ed3c154a4b9")

    def test_a_signed_request_carries_its_scope_and_signed_headers(self):
        now = datetime.datetime(2026, 10, 13, 8, 0, 0, tzinfo=datetime.timezone.utc)
        headers = store_check.sign("DELETE", "https://s.example/b/uploads/k", "AK", "SK", "eu-1", now)
        self.assertIn("Credential=AK/20261013/eu-1/s3/aws4_request", headers["Authorization"])
        self.assertIn("SignedHeaders=host;x-amz-content-sha256;x-amz-date", headers["Authorization"])
        self.assertEqual(headers["Host"], "s.example")

    def test_the_signature_changes_with_the_secret(self):
        now = datetime.datetime(2026, 10, 13, 8, 0, 0, tzinfo=datetime.timezone.utc)
        one = store_check.sign("GET", "https://s.example/b", "AK", "one", "r", now)["Authorization"]
        two = store_check.sign("GET", "https://s.example/b", "AK", "two", "r", now)["Authorization"]
        self.assertNotEqual(one, two)


class CheckTests(unittest.TestCase):
    def run_against(self, answers, **over):
        store = Store(answers)
        self.addCleanup(store.close)
        return store, store_check.check(store.env(**over))

    def test_a_store_that_refuses_every_probe_has_no_violation(self):
        store, violations = self.run_against({"DELETE": DENIED, "GET": DENIED, "list": DENIED})
        self.assertEqual(violations, [])
        # Two pairs, each asked two deletes and a list: the check asked, rather than found nothing to ask.
        self.assertEqual(len(store.seen), 6)
        self.assertTrue(all(auth.startswith("AWS4-HMAC-SHA256") for _, _, auth in store.seen))

    def test_a_delete_the_store_allows_is_a_violation_for_each_pair_and_prefix(self):
        _, violations = self.run_against({"DELETE": ALLOWED, "GET": DENIED, "list": DENIED})
        self.assertEqual(len(violations), 4)
        self.assertTrue(any("bff pair, delete under uploads/" in v for v in violations))
        self.assertTrue(any("catalog pair, delete under products/" in v for v in violations))

    def test_a_list_the_store_allows_is_a_violation(self):
        _, violations = self.run_against({"DELETE": DENIED, "GET": DENIED, "list": ("200", "<ListBucketResult/>")})
        self.assertEqual(len(violations), 2)
        self.assertTrue(all("list the bucket" in v for v in violations))

    def test_a_pair_the_store_does_not_know_is_not_a_refusal(self):
        _, violations = self.run_against({"DELETE": UNKNOWN_KEY, "GET": UNKNOWN_KEY, "list": UNKNOWN_KEY})
        self.assertEqual(len(violations), 6)
        self.assertTrue(all("InvalidAccessKeyId" in v for v in violations))

    def test_a_missing_bucket_is_not_a_refusal(self):
        missing = ("404", "<Error><Code>NoSuchBucket</Code></Error>")
        _, violations = self.run_against({"DELETE": missing, "GET": DENIED, "list": DENIED})
        self.assertEqual(len(violations), 4)

    def test_the_delete_names_a_key_nothing_holds(self):
        store, _ = self.run_against({"DELETE": DENIED, "GET": DENIED, "list": DENIED})
        deletes = [path for method, path, _ in store.seen if method == "DELETE"]
        self.assertEqual(len(deletes), 4)
        self.assertEqual(len(set(deletes)), 4)  # a fresh key per probe
        self.assertTrue(all("/check-" in path for path in deletes))


class RedirectTests(unittest.TestCase):
    def test_a_redirect_is_judged_and_never_followed(self):
        elsewhere = Store({"DELETE": DENIED, "GET": DENIED, "list": DENIED})
        self.addCleanup(elsewhere.close)

        class Redirecting(http.server.BaseHTTPRequestHandler):
            def _go(self):
                self.send_response(307)
                self.send_header("Location", elsewhere.endpoint + self.path)
                self.end_headers()

            do_GET = do_DELETE = _go

            def log_message(self, *args):
                pass

        server = http.server.HTTPServer(("127.0.0.1", 0), Redirecting)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        env = elsewhere.env(**{store_check.ENDPOINT: f"http://127.0.0.1:{server.server_port}"})
        violations = store_check.check(env)
        self.assertEqual(len(violations), 6)
        self.assertTrue(all("answered 307" in v for v in violations))
        self.assertEqual(elsewhere.seen, [])


class RefusalTests(unittest.TestCase):
    def test_an_unreachable_store_is_not_a_pass(self):
        store = Store({})
        env = store.env()
        store.close()
        with self.assertRaises(store_check.Refusal):
            store_check.check(env)

    def test_an_unset_variable_is_named(self):
        store = Store({})
        self.addCleanup(store.close)
        env = store.env()
        del env["OBJECT_STORE_CATALOG_SECRET_KEY"]
        with self.assertRaisesRegex(store_check.Refusal, "OBJECT_STORE_CATALOG_SECRET_KEY"):
            store_check.check(env)

    def test_plain_http_is_refused_off_loopback(self):
        store = Store({"DELETE": DENIED, "GET": DENIED, "list": DENIED})
        self.addCleanup(store.close)
        with self.assertRaisesRegex(store_check.Refusal, "https"):
            store_check.check(store.env(**{store_check.ENDPOINT: "http://store.example"}))
        self.assertEqual(store.seen, [])


WORKFLOW = pathlib.Path(__file__).resolve().parents[2] / ".github" / "workflows" / "object-store.yml"


def commands() -> str:
    """The workflow's commands alone, comment lines removed, so a raw search
    cannot find `gh issue create` in the comment beside it."""
    return "\n".join(line for line in WORKFLOW.read_text(encoding="utf-8").splitlines()
                     if not line.lstrip().startswith("#"))


def deployed_job() -> str:
    text = commands()
    start = text.find("\n  deployed:")
    if start == -1:
        raise AssertionError(f"{WORKFLOW} has no `deployed` job")
    return text[start:]


class FilingTests(unittest.TestCase):
    """A red run files an issue, because a notification is read once (ADR-110)."""

    def test_only_the_judgement_failing_files_anything(self):
        job = deployed_job()
        match = re.search(r"^      - name: A drifted store is an issue.*\n        if: (.*)$", job, re.MULTILINE)
        self.assertIsNotNone(match, "a red run no longer files anything")
        self.assertIn("failure()", match.group(1))
        self.assertIn("steps.judge.outcome == 'failure'", match.group(1))

    def test_the_judge_id_is_on_the_step_that_asks_the_store(self):
        job = deployed_job()
        match = re.search(r"^      - name: [^\n]*\n        id: judge\n(?:        [^\n]*\n)*?"
                          r"        run: python deploy/objectstore/store_check\.py check$", job, re.MULTILINE)
        self.assertIsNotNone(match, "`judge` is not the step that runs store_check.py check")

    def test_the_issue_is_filed_once_and_commented_on_after(self):
        job = deployed_job()
        self.assertIn("gh issue create", job)
        self.assertIn("--label security --label high", job)
        self.assertIn("gh issue comment", job)
        listing = re.search(r"gh issue list (.*?)\\\n", job)
        self.assertIsNotNone(listing, "the filing step no longer lists issues")
        for flag in ("--limit 1000", "--state open", "--label security"):
            self.assertIn(flag, listing.group(1))

    def test_issues_write_is_the_deployed_jobs_and_no_other(self):
        text = commands()
        self.assertEqual(text.count("issues: write"), 1)
        self.assertGreater(text.find("issues: write"), text.find("\n  deployed:"))

    def test_the_judge_step_times_out_before_the_job_does(self):
        """A job cancelled on its own timeout runs no `failure()` step."""
        job = deployed_job()
        job_cap = re.search(r"^    timeout-minutes: (\d+)$", job, re.MULTILINE)
        self.assertIsNotNone(job_cap, "the deployed job has no timeout")
        judge = job[job.find("id: judge"):]
        step_cap = re.search(r"^        timeout-minutes: (\d+)$", judge, re.MULTILINE)
        self.assertIsNotNone(step_cap, "the judge step has no timeout of its own")
        self.assertLess(int(step_cap.group(1)), int(job_cap.group(1)))


if __name__ == "__main__":
    unittest.main()
