"""The egress proxy, tested at the socket rather than by reading its file.

`.claude/sandbox/egress-proxy.py` is the enforcement boundary for the reviewer's
egress (`docs/harness-boundaries.md`): the reviewer container has no route to
anything but this process, and this process forwards exactly one thing. The
confinement's width is a subject test over `grok-review.sh`'s text, because
nothing in CI can run a review; what that cannot hold is the proxy's own
behaviour, whether a CONNECT to a listed host relays, whether an unlisted one
is refused, whether a plain `GET` through it fetches anything.

They run the proxy in-process on a loopback port, stand up a fake upstream on
another, and drive a client socket at the proxy. The allow-list and the
upstream port are module globals read at request time, so each case sets them
for its own scenario and restores them after; nothing here reaches past
127.0.0.1, which keeps the `review-helpers` job's argument — no Docker, no
network — true of this module too. Because every socket case patches those
two values, a class of its own pins what they are when nothing patches them:
the shipped allow-list, the upstream port and the listening port the reviewer
is pointed at, read with the environment cleared of both variables.

Every negative is paired with the positive it refuses beside: the same fake
upstream that relays for a listed host counts zero connections for an unlisted
one, so a refusal that "passed" because the upstream was never reachable at all
would fail the positive first.
"""

import importlib.util
import os
import socket
import socketserver
import threading
import unittest
from pathlib import Path
from unittest import mock

HERE = Path(__file__).resolve().parent
PROXY_FILE = HERE.parent / "sandbox" / "egress-proxy.py"


def load_proxy():
    # The file name carries a hyphen, so it cannot be imported by name.
    spec = importlib.util.spec_from_file_location("egress_proxy", PROXY_FILE)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


proxy = load_proxy()


class Upstream(socketserver.StreamRequestHandler):
    """Echoes upper-cased, so the relay's direction is visible in the bytes."""

    def handle(self):
        self.server.connections += 1
        while True:
            data = self.connection.recv(65536)
            if not data:
                return
            self.connection.sendall(data.upper())


class UpstreamServer(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True
    connections = 0


def serve(server):
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    return thread


def read_head(sock):
    """Read the proxy's response head, up to and including the blank line."""
    sock.settimeout(5)
    head = b""
    while b"\r\n\r\n" not in head:
        chunk = sock.recv(4096)
        if not chunk:
            break
        head += chunk
    return head


class ProxyCase(unittest.TestCase):
    def setUp(self):
        self.upstream = UpstreamServer(("127.0.0.1", 0), Upstream)
        serve(self.upstream)
        self.upstream_port = self.upstream.server_address[1]
        self.server = proxy.Server(("127.0.0.1", 0), proxy.Tunnel)
        serve(self.server)
        self.proxy_port = self.server.server_address[1]
        self.logged = []
        patches = [
            mock.patch.object(proxy, "ALLOWED", frozenset({"127.0.0.1"})),
            mock.patch.object(proxy, "UPSTREAM_PORT", self.upstream_port),
            mock.patch.object(proxy, "log", lambda verdict, host: self.logged.append((verdict, proxy.token(host)))),
        ]
        for p in patches:
            p.start()
            self.addCleanup(p.stop)
        self.addCleanup(self.server.shutdown)
        self.addCleanup(self.server.server_close)
        self.addCleanup(self.upstream.shutdown)
        self.addCleanup(self.upstream.server_close)

    def connect(self, request):
        sock = socket.create_connection(("127.0.0.1", self.proxy_port), timeout=5)
        self.addCleanup(sock.close)
        sock.sendall(request)
        return sock, read_head(sock)

    def connect_request(self, target, extra_headers=b""):
        return self.connect(
            f"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n".encode("ascii") + extra_headers + b"\r\n"
        )


class TheShippedDefaultsAreWhatTheReviewerIsConfinedTo(unittest.TestCase):
    """The defaults, read as the image runs the module, with neither variable set.

    The socket cases patch the allow-list and the upstream port to reach
    loopback, so none of them would notice the production defaults widening;
    another host or port is a red case here before it is a wider hole in the
    sandbox.
    """

    @staticmethod
    def shipped():
        with mock.patch.dict(os.environ):
            os.environ.pop("EGRESS_ALLOW", None)
            os.environ.pop("EGRESS_PORT", None)
            return load_proxy()

    def test_the_default_allow_list_is_the_two_x_ai_hosts_and_the_port_is_443(self):
        shipped = self.shipped()
        self.assertEqual(frozenset({"api.x.ai", "auth.x.ai"}), shipped.ALLOWED)
        self.assertEqual(443, shipped.UPSTREAM_PORT)

    def test_the_listening_port_is_the_one_the_reviewer_is_pointed_at(self):
        script = (HERE / "grok-review.sh").read_text(encoding="utf-8")
        self.assertIn("HTTPS_PROXY=http://proxy:8888", script)
        self.assertEqual(8888, self.shipped().PORT)

    def test_the_environment_is_what_widens_it_and_nothing_else(self):
        # The positive control for the pin: the same loader answers differently
        # only when the variable is set, so the pin is reading the default
        # rather than a value the host happened to export.
        with mock.patch.dict(os.environ, {"EGRESS_ALLOW": "one.test, Two.TEST,", "EGRESS_PORT": "9"}):
            widened = load_proxy()
        self.assertEqual(frozenset({"one.test", "two.test"}), widened.ALLOWED)
        self.assertEqual(9, widened.PORT)
        self.assertEqual(443, widened.UPSTREAM_PORT)


class AnAllowedTunnelRelays(ProxyCase):
    def test_a_listed_host_on_the_upstream_port_is_established_and_relayed_both_ways(self):
        sock, head = self.connect_request(f"127.0.0.1:{self.upstream_port}")
        self.assertTrue(head.startswith(b"HTTP/1.1 200 Connection established\r\n"), head)
        sock.sendall(b"hello through the tunnel")
        self.assertEqual(b"HELLO THROUGH THE TUNNEL", sock.recv(4096))
        self.assertEqual(1, self.upstream.connections)
        self.assertEqual([("allow", "127.0.0.1")], self.logged)

    def test_request_headers_are_drained_before_the_tunnel_opens(self):
        # A client sends headers after CONNECT; bytes it sends after the blank
        # line belong to the tunnel, and none of the headers must leak into it.
        sock, head = self.connect_request(
            f"127.0.0.1:{self.upstream_port}",
            extra_headers=b"Proxy-Connection: keep-alive\r\nUser-Agent: probe\r\n",
        )
        self.assertTrue(head.startswith(b"HTTP/1.1 200"), head)
        sock.sendall(b"payload")
        self.assertEqual(b"PAYLOAD", sock.recv(4096))

    def test_the_host_is_matched_case_insensitively_and_without_brackets(self):
        # `localhost` rather than a literal address, so the case fold and the
        # bracket strip are both exercised, and it still resolves to loopback.
        with mock.patch.object(proxy, "ALLOWED", frozenset({"localhost"})):
            sock, head = self.connect_request(f"[LOCALHOST]:{self.upstream_port}")
            self.assertTrue(head.startswith(b"HTTP/1.1 200"), head)
            sock.sendall(b"folded")
            self.assertEqual(b"FOLDED", sock.recv(4096))
        self.assertEqual([("allow", "localhost")], self.logged)


class WhatTheProxyRefuses(ProxyCase):
    def assert_refused(self, head):
        self.assertTrue(head.startswith(b"HTTP/1.1 403 Forbidden\r\n"), head)
        self.assertIn(b"Connection: close\r\n", head)
        self.assertEqual(0, self.upstream.connections)

    def test_a_host_off_the_list_is_refused_and_never_dialled(self):
        with mock.patch.object(proxy, "ALLOWED", frozenset({"api.x.ai"})):
            _, head = self.connect_request(f"127.0.0.1:{self.upstream_port}")
        self.assert_refused(head)
        self.assertEqual([("deny", "127.0.0.1")], self.logged)

    def test_a_listed_host_on_another_port_is_refused(self):
        _, head = self.connect_request(f"127.0.0.1:{self.upstream_port + 1}")
        self.assert_refused(head)
        self.assertEqual([("deny", "127.0.0.1")], self.logged)

    def test_a_plain_http_request_is_refused_and_logged_by_method(self):
        _, head = self.connect(b"GET http://127.0.0.1/ HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n")
        self.assert_refused(head)
        self.assertEqual(1, len(self.logged))
        verdict, host = self.logged[0]
        self.assertEqual("deny", verdict)
        self.assertTrue(host.startswith("GET "), host)

    def test_a_malformed_request_line_is_refused_not_crashed(self):
        _, head = self.connect(b"CONNECT\r\n\r\n")
        self.assert_refused(head)
        self.assertEqual([("deny", "CONNECT")], self.logged)

    def test_an_empty_request_is_refused_and_logged_as_nothing(self):
        _, head = self.connect(b"\r\n\r\n")
        self.assert_refused(head)
        self.assertEqual([("deny", "-")], self.logged)

    def test_an_upstream_that_refuses_is_a_502_not_a_hang(self):
        closed = socket.socket()
        closed.bind(("127.0.0.1", 0))
        closed_port = closed.getsockname()[1]
        closed.close()
        with mock.patch.object(proxy, "UPSTREAM_PORT", closed_port):
            _, head = self.connect_request(f"127.0.0.1:{closed_port}")
        self.assertTrue(head.startswith(b"HTTP/1.1 502 Bad Gateway\r\n"), head)
        self.assertEqual([("deny", "127.0.0.1")], self.logged)


class TheLogNeverCarriesTheRequestAsWritten(unittest.TestCase):
    """The request target is reviewer-controlled text and the log is read by a person."""

    def test_the_token_keeps_a_hostname_alphabet_and_nothing_else(self):
        self.assertEqual("api.x.ai", proxy.token("api.x.ai"))
        self.assertEqual("GET http127.0.0.1", proxy.token("GET http://127.0.0.1/"))
        self.assertEqual("evilallow injected", proxy.token("evil\r\nallow injected"))
        self.assertEqual("31m", proxy.token("\x1b[31m"))
        self.assertEqual("-", proxy.token(""))

    def test_the_token_is_truncated(self):
        self.assertEqual(80, len(proxy.token("a" * 500)))

    def test_a_control_sequence_does_not_survive_into_the_line(self):
        self.assertNotIn("\x1b", proxy.token("\x1b[2Jcleared"))
        self.assertNotIn("\n", proxy.token("one\ntwo"))


if __name__ == "__main__":
    unittest.main()
