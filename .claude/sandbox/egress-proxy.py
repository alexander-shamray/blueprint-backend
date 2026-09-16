#!/usr/bin/env python3
"""The reviewer's only way out: a CONNECT-only forward proxy with a host allow-list.

The reviewer container sits on a Docker network created with --internal, which
has no route to anything but the containers on it. This process is the one
container on that network which is also on a network with egress, and it
forwards exactly one thing: a TLS tunnel (HTTP CONNECT) to port 443 of a host
on the allow-list. Everything else is answered with 403 and closed.

Stdlib only, so it runs in the reviewer image itself (which already carries
python3 for the licence gate) and adds no second image, no second pin and no
second digest to track.

What it deliberately does not do:

- No plain-HTTP forwarding (GET/POST through the proxy). grok speaks TLS to
  api.x.ai; an http:// URL through here is refused, not fetched, so nothing
  leaves in the clear.
- No port other than 443. An allow-listed host on another port is still a
  refusal.
- No TLS interception. The tunnel is opaque, which is the point: the proxy
  cannot read the credential it carries, and neither can a log line.
- No logging of anything the reviewer chose beyond a sanitised host token.
  The request target is reviewer-controlled text, and a log is read by a
  person; it is reduced to a hostname alphabet and truncated before it is
  printed, the same reduction grok-review.sh applies to the verdict fields.

Configuration is by environment, read once at start:

    EGRESS_ALLOW   comma-separated hostnames, default "api.x.ai,auth.x.ai"
    EGRESS_PORT    listening port, default 8888
"""

import os
import re
import select
import socket
import socketserver
import sys

ALLOWED = frozenset(
    h.strip().lower()
    for h in os.environ.get("EGRESS_ALLOW", "api.x.ai,auth.x.ai").split(",")
    if h.strip()
)
PORT = int(os.environ.get("EGRESS_PORT", "8888"))
UPSTREAM_PORT = 443
CONNECT_TIMEOUT = 10
IDLE_TIMEOUT = 300
SAFE = re.compile(r"[^A-Za-z0-9. -]")


def token(value):
    """Reduce reviewer-supplied text to a hostname alphabet, truncated."""
    return SAFE.sub("", value)[:80].strip() or "-"


def log(verdict, host):
    print(f"{verdict} {token(host)}", flush=True)


class Tunnel(socketserver.StreamRequestHandler):
    timeout = CONNECT_TIMEOUT

    def refuse(self, status, host):
        log("deny", host)
        self.wfile.write(
            f"HTTP/1.1 {status}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n".encode("ascii")
        )

    def handle(self):
        try:
            request_line = self.rfile.readline(8192)
            # Drain the headers; nothing in them is used. A CONNECT carries no body.
            while True:
                header = self.rfile.readline(8192)
                if header in (b"", b"\r\n", b"\n"):
                    break
        except (OSError, ValueError):
            return
        parts = request_line.decode("latin-1", "replace").split()
        if len(parts) != 3 or parts[0] != "CONNECT":
            # Not a tunnel: a plain-HTTP request through the proxy, or noise.
            # Logged with its method so a refused `GET http://…` is legible as
            # one rather than as a hostname with the scheme squashed into it.
            method = token(parts[0]) if parts else "-"
            self.refuse("403 Forbidden", f"{method} {parts[1] if len(parts) > 1 else ''}")
            return
        host, _, port = parts[1].rpartition(":")
        host = host.strip("[]").lower()
        if host not in ALLOWED or port != str(UPSTREAM_PORT):
            self.refuse("403 Forbidden", host)
            return
        try:
            upstream = socket.create_connection((host, UPSTREAM_PORT), timeout=CONNECT_TIMEOUT)
        except OSError:
            self.refuse("502 Bad Gateway", host)
            return
        log("allow", host)
        self.wfile.write(b"HTTP/1.1 200 Connection established\r\n\r\n")
        self.relay(self.connection, upstream)

    @staticmethod
    def relay(client, upstream):
        client.settimeout(None)
        upstream.settimeout(None)
        sockets = [client, upstream]
        try:
            while True:
                readable, _, _ = select.select(sockets, [], [], IDLE_TIMEOUT)
                if not readable:
                    return
                for source in readable:
                    data = source.recv(65536)
                    if not data:
                        return
                    (upstream if source is client else client).sendall(data)
        except OSError:
            return
        finally:
            for s in sockets:
                try:
                    s.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass
                s.close()


class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


if __name__ == "__main__":
    log("listen", f"{PORT}")
    print("allow-list " + " ".join(sorted(ALLOWED)), flush=True)
    with Server(("0.0.0.0", PORT), Tunnel) as server:
        try:
            server.serve_forever()
        except KeyboardInterrupt:
            sys.exit(0)
