"""store_check.py against a local server that answers as a store configured to taste."""

import datetime
import http.server
import pathlib
import re
import threading
import unittest
import urllib.parse

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


class NoGrantTests(unittest.TestCase):
    """The limit the README's *What it does not check* states, held to the check's behaviour."""

    README = pathlib.Path(__file__).resolve().parent / "README.md"

    def test_an_existing_bucket_the_pairs_have_no_grant_on_reads_as_a_pass(self):
        store = Store({"DELETE": DENIED, "GET": DENIED, "list": DENIED})
        self.addCleanup(store.close)

        violations = store_check.check(store.env(**{store_check.BUCKET: "another-accounts-bucket"}))

        self.assertEqual(violations, [])
        # The wrong bucket was the one asked, six times, and still passed.
        self.assertEqual(len(store.seen), 6)
        self.assertTrue(all(path.startswith("/another-accounts-bucket") for _, path, _ in store.seen), store.seen)

    def test_the_readme_says_so_rather_than_claiming_a_404_covers_it(self):
        text = " ".join(self.README.read_text(encoding="utf-8").split())

        self.assertIn("a wrong `OBJECT_STORE_BUCKET` reads as a pass", text)
        self.assertIn("a `404` is not a refusal, and `NoSuchBucket` names no bucket the store holds", text)


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


class AddressingTests(unittest.TestCase):
    """A store reachable only by a virtual-hosted name is asked by that name (ADR-114)."""

    def urls(self, endpoint, addressing):
        return [url for _, _, url in store_check.probes(endpoint, "product-images", addressing)]

    def test_path_style_puts_the_bucket_in_the_path(self):
        for url in self.urls("https://s3.example:9000/", "path"):
            parts = urllib.parse.urlsplit(url)
            self.assertEqual((parts.scheme, parts.netloc), ("https", "s3.example:9000"), url)
            self.assertTrue(parts.path.startswith("/product-images"), url)

    def test_virtual_hosted_puts_the_bucket_in_the_host(self):
        urls = self.urls("https://s3.example:9000/", "virtual")
        self.assertEqual(len(urls), 3)
        for url in urls:
            parts = urllib.parse.urlsplit(url)
            self.assertEqual((parts.scheme, parts.netloc), ("https", "product-images.s3.example:9000"), url)
        self.assertRegex(urls[0], r"^https://[^/]+/uploads/check-[0-9a-f]{32}$")
        self.assertEqual(urls[2], "https://product-images.s3.example:9000/?list-type=2&max-keys=1")

    def test_the_signed_host_is_the_virtual_host(self):
        now = datetime.datetime(2026, 10, 13, 8, 0, 0, tzinfo=datetime.timezone.utc)
        url = self.urls("https://s3.example", "virtual")[0]
        self.assertEqual(store_check.sign("DELETE", url, "AK", "SK", "eu-1", now)["Host"], "product-images.s3.example")

    def test_an_unset_addressing_is_path_style(self):
        store = Store({"DELETE": DENIED, "GET": DENIED, "list": DENIED})
        self.addCleanup(store.close)
        self.assertEqual(store_check.check(store.env(**{store_check.ADDRESSING: ""})), [])
        self.assertTrue(all(path.startswith("/product-images") for _, path, _ in store.seen))

    def test_another_addressing_is_not_guessed_at(self):
        with self.assertRaisesRegex(store_check.Refusal, store_check.ADDRESSING):
            store_check.check(Store({}).env(**{store_check.ADDRESSING: "vhost"}))


class HeartbeatTests(unittest.TestCase):
    """A green run tells a monitor outside GitHub it happened (ADR-114)."""

    def beat(self, answer, **over):
        store = Store({"GET": answer})
        self.addCleanup(store.close)
        env = {store_check.HEARTBEAT: store.endpoint + "/ping/the-token"}
        env.update(over)
        return store, env

    def test_the_url_is_requested_once(self):
        store, env = self.beat(("200", "OK"))
        store_check.heartbeat(env)
        self.assertEqual([(method, path) for method, path, _ in store.seen], [("GET", "/ping/the-token")])

    def test_an_unset_url_is_named_and_not_a_pass(self):
        with self.assertRaisesRegex(store_check.Refusal, store_check.HEARTBEAT):
            store_check.heartbeat({})

    def test_an_answer_that_is_not_a_success_fails(self):
        _, env = self.beat(("500", ""))
        with self.assertRaisesRegex(store_check.Refusal, "500"):
            store_check.heartbeat(env)

    def test_a_redirect_is_not_followed(self):
        """The 302 carries a Location: without one a following opener refuses it anyway."""
        elsewhere, _ = self.beat(("200", "OK"))

        class Redirecting(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                self.send_response(302)
                self.send_header("Location", elsewhere.endpoint + self.path)
                self.end_headers()

            def log_message(self, *args):
                pass

        server = http.server.HTTPServer(("127.0.0.1", 0), Redirecting)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        env = {store_check.HEARTBEAT: f"http://127.0.0.1:{server.server_port}/ping/the-token"}
        with self.assertRaisesRegex(store_check.Refusal, "302"):
            store_check.heartbeat(env)
        self.assertEqual(elsewhere.seen, [])

    def test_plain_http_is_refused_off_loopback(self):
        with self.assertRaisesRegex(store_check.Refusal, "https"):
            store_check.heartbeat({store_check.HEARTBEAT: "http://monitor.example/ping/the-token"})

    def test_the_token_is_never_in_the_message(self):
        _, env = self.beat(("500", ""))
        with self.assertRaises(store_check.Refusal) as caught:
            store_check.heartbeat(env)
        self.assertNotIn("the-token", str(caught.exception))
        env[store_check.HEARTBEAT] = "http://127.0.0.1:1/ping/the-token"
        with self.assertRaises(store_check.Refusal) as caught:
            store_check.heartbeat(env)
        self.assertNotIn("the-token", str(caught.exception))


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
        listing = re.search(r"gh issue list ([^\n]*)", job)
        self.assertIsNotNone(listing, "the filing step no longer lists issues")
        for flag in ("--limit 1000", "--state open", "--label security"):
            self.assertIn(flag, listing.group(1))
        # A list as long as its limit may be cut, so it is refused.
        self.assertIn('[ "$(jq length <<<"$LIST")" -lt 1000 ]', job)

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


class HeartbeatStepTests(unittest.TestCase):
    """The deployed job announces a green run, and only a green one (ADR-114)."""

    def step(self):
        job = deployed_job()
        match = re.search(r"^      - name: [^\n]*\n(?:        [^\n]*\n)*?        run: python deploy/objectstore/"
                          r"store_check\.py heartbeat\n(?:        [^\n]*\n)*", job, re.MULTILINE)
        self.assertIsNotNone(match, "no step runs store_check.py heartbeat")
        return job, match

    def test_it_runs_after_the_judgement_and_reads_the_secret(self):
        job, match = self.step()
        self.assertGreater(match.start(), job.find("id: judge"))
        self.assertIn("OBJECT_STORE_HEARTBEAT_URL: ${{ secrets.OBJECT_STORE_HEARTBEAT_URL }}", match.group(0))

    def test_a_red_or_cancelled_run_sends_none(self):
        _, match = self.step()
        self.assertNotRegex(match.group(0), r"\n        if: ")

    def test_the_judge_step_passes_the_addressing_variable(self):
        job = deployed_job()
        judge = job[job.find("id: judge"):job.find("store_check.py heartbeat")]
        self.assertIn("OBJECT_STORE_ADDRESSING: ${{ vars.OBJECT_STORE_ADDRESSING }}", judge)


if __name__ == "__main__":
    unittest.main()
