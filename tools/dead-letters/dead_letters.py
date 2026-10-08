#!/usr/bin/env python3
"""List, inspect, replay or discard the messages MassTransit parks on `_error` and `_skipped` queues.

    py -3.12 tools/dead-letters/dead_letters.py {list,inspect,replay,discard} [QUEUE] [--json] ...
"""

from __future__ import annotations

import argparse
import base64
import datetime
import getpass
import http.client
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request
from typing import Callable, TextIO

SCHEMA = 1
OPERATOR = "dead-letter-operator"
DEFAULT_URL = "http://localhost:15672"
SUFFIXES = {"_error": "error", "_skipped": "skipped"}
DEFAULT_LIMIT = 100

# MassTransit's own transport headers, which docs/runbooks/error-queue.md reads off a faulted message.
FAULT_MESSAGE = "MT-Fault-Message"
FAULT_EXCEPTION = "MT-Fault-ExceptionType"
FAULT_INPUT = "MT-Fault-InputAddress"
REASON = "MT-Reason"

Send = Callable[[str, str, dict | None], object]


class Refused(Exception):
    """A request the tool will not carry out, with the reason an operator reads."""


class AnswerLost(Refused):
    """A request that may have reached the broker and acted, whose answer never came back."""


def http_transport(base_url: str, user: str, password: str, timeout: float = 30.0) -> Send:
    """The Management API over urllib; the credential travels in a header and never in argv."""
    if urllib.parse.urlsplit(base_url).scheme not in ("http", "https"):
        raise Refused(f"--url must be http or https, not {base_url!r}")
    token = base64.b64encode(f"{user}:{password}".encode("utf-8")).decode("ascii")

    def send(method: str, path: str, body: dict | None = None) -> object:
        data = None if body is None else json.dumps(body).encode("utf-8")
        request = urllib.request.Request(
            base_url.rstrip("/") + path,
            data=data,
            method=method,
            headers={"authorization": f"Basic {token}", "content-type": "application/json"})
        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                text = response.read().decode("utf-8")
        except urllib.error.HTTPError as error:
            detail = error.read().decode("utf-8", "replace").strip()
            raise Refused(f"{method} {path}: HTTP {error.code} {detail or error.reason}") from None
        except urllib.error.URLError as error:
            raise Refused(f"{method} {path}: {error.reason}") from None
        except (OSError, http.client.HTTPException) as error:
            # urllib wraps only the send; a timeout or reset awaiting or reading the answer arrives raw, after the
            # broker may have acted on the request.
            raise AnswerLost(f"{method} {path}: the answer was lost ({type(error).__name__}: {error})") from None
        return json.loads(text) if text else None

    return send


class Broker:
    """The four Management API calls this tool makes, and no others."""

    def __init__(self, send: Send, vhost: str) -> None:
        self.send = send
        self.vhost = vhost

    def _path(self, *parts: str) -> str:
        return "/api/" + "/".join(urllib.parse.quote(part, safe="") for part in parts)

    def queues(self) -> list[dict]:
        return list(self.send("GET", self._path("queues", self.vhost) + "?columns=name,messages", None) or [])

    def peek(self, queue: str, count: int) -> list[dict]:
        # ack_requeue_true puts each message back, redelivered-flagged (docs/runbooks/error-queue.md).
        body = {"count": count, "ackmode": "ack_requeue_true", "encoding": "base64"}
        return list(self.send("POST", self._path("queues", self.vhost, queue, "get"), body) or [])

    def take(self, queue: str) -> dict | None:
        # ack_requeue_false removes the message in the same call, so exactly one is taken at a time.
        body = {"count": 1, "ackmode": "ack_requeue_false", "encoding": "base64"}
        taken = self.send("POST", self._path("queues", self.vhost, queue, "get"), body) or []
        return taken[0] if taken else None

    def publish(self, exchange: str, message: dict) -> bool:
        body = {
            "properties": message.get("properties") or {},
            "routing_key": "",
            "payload": message.get("payload", ""),
            "payload_encoding": message.get("payload_encoding", "base64"),
        }
        answer = self.send("POST", self._path("exchanges", self.vhost, exchange, "publish"), body) or {}
        return bool(answer.get("routed"))


def endpoint_of(queue: str) -> tuple[str, str]:
    """`ordering-commands_error` -> (`ordering-commands`, `error`); the runbooks' naming rule."""
    for suffix, kind in SUFFIXES.items():
        if queue.endswith(suffix) and len(queue) > len(suffix):
            return queue[: -len(suffix)], kind
    raise Refused(f"{queue}: not a dead-letter queue. This tool reads only queues ending "
                  f"{' or '.join(SUFFIXES)}, so it cannot drain an endpoint's live work")


def payload_bytes(message: dict) -> bytes:
    payload = message.get("payload", "")
    if message.get("payload_encoding") == "base64":
        return base64.b64decode(payload)
    return str(payload).encode("utf-8")


def headers_of(message: dict) -> dict:
    return (message.get("properties") or {}).get("headers") or {}


def message_id(message: dict) -> str | None:
    return (message.get("properties") or {}).get("message_id")


def input_endpoint(message: dict) -> str | None:
    """The queue MassTransit says the message faulted on, the last segment of its input address."""
    address = headers_of(message).get(FAULT_INPUT)
    if not isinstance(address, str) or not address:
        return None
    return urllib.parse.urlsplit(address).path.rstrip("/").rsplit("/", 1)[-1] or None


def describe(message: dict) -> dict:
    """One message as the JSON contract states it; the body's messageType is skipped-queue.md's diagnosis."""
    raw = payload_bytes(message)
    try:
        body = raw.decode("utf-8")
    except UnicodeDecodeError:
        body = None
    message_type = None
    if body is not None:
        try:
            envelope = json.loads(body)
        except ValueError:
            envelope = None
        if isinstance(envelope, dict):
            message_type = envelope.get("messageType")
    properties = message.get("properties") or {}
    headers = headers_of(message)
    return {
        "message_id": properties.get("message_id"),
        "correlation_id": properties.get("correlation_id"),
        "message_type": message_type,
        "fault_message": headers.get(FAULT_MESSAGE),
        "fault_exception_type": headers.get(FAULT_EXCEPTION),
        "input_address": headers.get(FAULT_INPUT),
        "reason": headers.get(REASON),
        "redelivered": bool(message.get("redelivered")),
        "properties": properties,
        "body": body,
        "body_base64": base64.b64encode(raw).decode("ascii"),
    }


# curl's escapes inside a quoted config value; the runbooks write a password's backslash and quote this way.
CURL_ESCAPES = {"\\": "\\", '"': '"', "t": "\t", "n": "\n", "r": "\r", "v": "\v"}


def curl_value(raw: str) -> str:
    """A config value as curl reads it: a quoted one unescaped up to its closing quote, a bare one to a space."""
    if not raw.startswith('"'):
        return raw.split(maxsplit=1)[0] if raw.strip() else ""
    out: list[str] = []
    chars = iter(raw[1:])
    for char in chars:
        if char == '"':
            break
        if char == "\\":
            escaped = next(chars, "")
            out.append(CURL_ESCAPES.get(escaped, escaped))
        else:
            out.append(char)
    return "".join(out)


def load_credential(path: str | None, environ: dict) -> tuple[str, str]:
    """The operator's credential, from a curl config or the environment, never from argv."""
    path = path or environ.get("DEAD_LETTERS_CREDENTIALS")
    if path:
        try:
            with open(path, encoding="utf-8") as file:
                lines = file.read().splitlines()
        except OSError as error:
            raise Refused(f"--credentials: {error.strerror}: {path}") from None
        for line in lines:
            key, _, value = line.strip().partition("=")
            if key.strip() == "user" and ":" in value:
                user, _, password = curl_value(value.strip()).partition(":")
                return user, password
        raise Refused(f"--credentials: {path} holds no `user = \"NAME:PASSWORD\"` line")
    password = environ.get("DEAD_LETTERS_PASSWORD")
    if not password:
        raise Refused("no credential: pass --credentials FILE or set DEAD_LETTERS_PASSWORD "
                      "(tools/dead-letters/README.md)")
    return environ.get("DEAD_LETTERS_USER") or OPERATOR, password


class Run:
    """One invocation's broker, output streams and audit trail."""

    def __init__(self, broker: Broker, args: argparse.Namespace, broker_user: str, out: TextIO, err: TextIO,
                 clock: Callable[[], datetime.datetime], who: str) -> None:
        self.broker = broker
        self.args = args
        self.broker_user = broker_user
        self.out = out
        self.err = err
        self.clock = clock
        self.who = who

    def audit(self, queue: str, message: dict, action: str, destination: str | None, reason: str | None) -> None:
        line = json.dumps({
            "ts": self.clock().isoformat(timespec="seconds"),
            "operator": self.who,
            "broker_user": self.broker_user,
            "vhost": self.broker.vhost,
            "queue": queue,
            "message_id": message_id(message),
            "action": action,
            "destination": destination,
            "reason": reason,
        })
        print(line, file=self.err)
        if self.args.audit_log:
            with open(self.args.audit_log, "a", encoding="utf-8") as log:
                log.write(line + "\n")

    def writable(self, path: str) -> None:
        """Refuse before anything moves, rather than holding a taken message nothing can write down."""
        try:
            os.close(os.open(path, os.O_WRONLY | os.O_CREAT | os.O_APPEND, 0o600))
        except OSError as error:
            raise Refused(f"{path}: {error.strerror}") from None

    def record(self, queue: str, message: dict) -> None:
        # Written and synced before the message goes anywhere, so a run that dies after the take still has it.
        line = json.dumps({"ts": self.clock().isoformat(timespec="seconds"), "queue": queue, "message": message})
        descriptor = os.open(self.args.record, os.O_WRONLY | os.O_CREAT | os.O_APPEND, 0o600)
        with os.fdopen(descriptor, "a", encoding="utf-8") as file:
            file.write(line + "\n")
            file.flush()
            os.fsync(file.fileno())

    def emit(self, document: dict, lines: list[str]) -> None:
        if self.args.json:
            print(json.dumps(document, indent=2), file=self.out)
        else:
            for line in lines:
                print(line, file=self.out)


def command_list(run: Run) -> int:
    queues = []
    for queue in sorted(run.broker.queues(), key=lambda q: q.get("name", "")):
        name = queue.get("name", "")
        for suffix, kind in SUFFIXES.items():
            if name.endswith(suffix) and len(name) > len(suffix):
                queues.append({"name": name, "kind": kind, "endpoint": name[: -len(suffix)],
                               "messages": int(queue.get("messages") or 0)})
    lines = [f"{q['messages']:>8}  {q['kind']:<8} {q['name']}" for q in queues] or ["no dead-letter queues"]
    run.emit({"schema": SCHEMA, "command": "list", "vhost": run.broker.vhost, "queues": queues}, lines)
    return 0


def command_inspect(run: Run) -> int:
    queue = run.args.queue
    endpoint, kind = endpoint_of(queue)
    messages = [describe(m) for m in run.broker.peek(queue, run.args.limit)]
    lines = []
    for index, m in enumerate(messages, 1):
        lines.append(f"[{index}] message_id={m['message_id']} correlation_id={m['correlation_id']}")
        for label in ("message_type", "fault_message", "fault_exception_type", "input_address", "reason"):
            if m[label]:
                lines.append(f"    {label}: {m[label]}")
        lines.append(f"    body: {m['body'] if m['body'] is not None else '(binary, see --json body_base64)'}")
    lines.append(f"{len(messages)} message(s) read from {queue} and requeued, now flagged redelivered")
    run.emit({"schema": SCHEMA, "command": "inspect", "vhost": run.broker.vhost, "queue": queue,
              "kind": kind, "endpoint": endpoint, "messages": messages}, lines)
    return 0


def command_move(run: Run, verb: str) -> int:
    """Replay or discard: dry run unless --execute, and one message taken at a time when it runs."""
    args = run.args
    queue = args.queue
    endpoint, kind = endpoint_of(queue)
    if not args.all and not args.message_id:
        raise Refused(f"{verb}: name the messages with --message-id ID (repeatable) or take them all with --all")
    if args.execute and not args.record:
        raise Refused(f"{verb} --execute needs --record FILE: each message is written there before it "
                      f"leaves {queue}, which is the record the runbooks require before a discard")
    if args.execute:
        run.writable(args.record)
    wanted = None if args.all else set(args.message_id)
    destination = endpoint if verb == "replay" else None
    snapshot = run.broker.peek(queue, args.limit)
    actions: list[dict] = []

    def act(message: dict, action: str, reason: str | None = None, to: str | None = destination) -> None:
        actions.append({"message_id": message_id(message), "action": action, "destination": to,
                        "reason": reason})

    if not args.execute:
        for message in snapshot:
            if wanted is None or message_id(message) in wanted:
                reason = refusal(message, endpoint) if verb == "replay" else None
                act(message, "would-refuse" if reason else f"would-{verb}", reason)
        found = {message_id(m) for m in snapshot}
        return finish(run, verb, queue, kind, endpoint, actions, sorted((wanted or set()) - found), dry_run=True)

    pending = set(wanted or ())
    returned: set[str] = set()
    for _ in range(len(snapshot)):
        if wanted is not None and not pending:
            break
        try:
            message = run.broker.take(queue)
        except AnswerLost as error:
            reason = f"{error}; the take may have removed a message that is now in neither {queue} nor --record"
            actions.append({"message_id": None, "action": "failed", "destination": None, "reason": reason})
            run.audit(queue, {}, "failed", None, reason)
            break
        except Refused as error:
            actions.append({"message_id": None, "action": "failed", "destination": None, "reason": str(error)})
            run.audit(queue, {}, "failed", None, str(error))
            break
        if message is None:
            break
        try:
            run.record(queue, message)
        except OSError as error:
            put_back(run, queue, message, "failed", f"--record could not be written: {error}", actions,
                     recorded=False)
            break
        identity = message_id(message)
        if wanted is not None and identity not in wanted:
            put_back(run, queue, message, "returned", "not selected", actions)
            if identity is not None and identity in returned:
                break
            returned.add(identity)
            continue
        pending.discard(identity)
        if verb == "discard":
            act(message, "discarded")
            run.audit(queue, message, "discarded", None, None)
            continue
        reason = refusal(message, endpoint)
        if reason:
            put_back(run, queue, message, "refused", reason, actions)
            continue
        try:
            routed = run.broker.publish(endpoint, message)
        except AnswerLost as error:
            # Returned as well, it could be on both; the record holds it either way, so the run stops and says so.
            reason = f"{error}; the replay may have reached {endpoint}, so it is not returned, and --record holds it"
            act(message, "failed", reason)
            run.audit(queue, message, "failed", endpoint, reason)
            break
        except Refused as error:
            routed, reason = False, str(error)
        if routed:
            act(message, "replayed")
            run.audit(queue, message, "replayed", endpoint, None)
            continue
        put_back(run, queue, message, "failed", reason or f"the broker routed it nowhere from `{endpoint}`", actions)
        break
    return finish(run, verb, queue, kind, endpoint, actions, sorted(pending), dry_run=False)


def refusal(message: dict, endpoint: str) -> str | None:
    """Why a replay to `endpoint` would land somewhere MassTransit says the message did not come from."""
    named = input_endpoint(message)
    if named and named != endpoint:
        return f"{FAULT_INPUT} names `{named}`, not `{endpoint}`; replay it there by hand"
    return None


def put_back(run: Run, queue: str, message: dict, action: str, reason: str, actions: list[dict],
             recorded: bool = True) -> None:
    """Return a taken message to the queue it came from, unchanged, at its tail."""
    held = "the --record file holds it" if recorded else "--record does not hold it, so it is written whole to stderr"
    try:
        returned: bool | None = run.broker.publish(queue, message)
    except AnswerLost as error:
        returned, reason = None, f"{reason}; returning it lost its answer ({error}), so it may be back on {queue}, " \
                                 f"and {held}"
    except Refused as error:
        returned, reason = False, f"{reason}; returning it failed too: {error}"
    if returned is False:
        reason = f"{reason}; it is not on {queue} any more and {held}"
    if returned is not True and not recorded:
        # The last copy this run holds; prefixed, so no reader of the audit lines takes it for one.
        print("unrecorded " + json.dumps({"queue": queue, "message": message}), file=run.err)
    actions.append({"message_id": message_id(message), "action": action, "destination": queue, "reason": reason})
    run.audit(queue, message, action, queue, reason)


def finish(run: Run, verb: str, queue: str, kind: str, endpoint: str, actions: list[dict],
           not_found: list[str], dry_run: bool) -> int:
    lines = []
    for a in actions:
        target = f" -> {a['destination']}" if a["destination"] else ""
        note = f" ({a['reason']})" if a["reason"] else ""
        lines.append(f"{a['action']:<14} {a['message_id']}{target}{note}")
    lines += [f"not found      {identity}" for identity in not_found]
    if dry_run:
        lines.append(f"dry run: nothing left {queue}. Pass --execute --record FILE to {verb}; the read "
                     f"requeued what it saw, flagged redelivered")
    failed = any(a["action"] in ("failed", "refused") for a in actions) or bool(not_found)
    run.emit({"schema": SCHEMA, "command": verb, "vhost": run.broker.vhost, "queue": queue, "kind": kind,
              "endpoint": endpoint, "dry_run": dry_run, "actions": actions, "not_found": not_found}, lines)
    return 1 if failed else 0


def parser() -> argparse.ArgumentParser:
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--url", help=f"the Management API (default $DEAD_LETTERS_URL or {DEFAULT_URL})")
    common.add_argument("--vhost", default="/")
    common.add_argument("--credentials", help="a curl config holding `user = \"NAME:PASSWORD\"`")
    common.add_argument("--audit-log", help="append each audit line to this file as well as stderr")
    common.add_argument("--json", action="store_true", help="one JSON document on stdout (schema in README.md)")

    root = argparse.ArgumentParser(prog="dead_letters.py", description=__doc__.splitlines()[0])
    commands = root.add_subparsers(dest="command", required=True)
    commands.add_parser("list", parents=[common], help="dead-letter queues and their depth")
    inspect = commands.add_parser("inspect", parents=[common], help="read messages and requeue them")
    inspect.add_argument("queue")
    inspect.add_argument("--limit", type=int, default=DEFAULT_LIMIT)
    for verb in ("replay", "discard"):
        move = commands.add_parser(verb, parents=[common], help=f"{verb} messages; a dry run without --execute")
        move.add_argument("queue")
        selection = move.add_mutually_exclusive_group()
        selection.add_argument("--message-id", action="append", default=[])
        selection.add_argument("--all", action="store_true")
        move.add_argument("--limit", type=int, default=DEFAULT_LIMIT)
        move.add_argument("--execute", action="store_true", help="carry it out; without it nothing moves")
        move.add_argument("--record", help="append each taken message here before it is moved or dropped")
    return root


def main(argv: list[str] | None = None, *, environ: dict | None = None, send: Send | None = None,
         out: TextIO | None = None, err: TextIO | None = None,
         clock: Callable[[], datetime.datetime] | None = None, who: str | None = None) -> int:
    args = parser().parse_args(argv)
    environ = os.environ if environ is None else environ
    out = out or sys.stdout
    err = err or sys.stderr
    try:
        if getattr(args, "limit", 1) < 1:
            raise Refused("--limit must be at least 1")
        user, password = load_credential(args.credentials, environ)
        url = args.url or environ.get("DEAD_LETTERS_URL") or DEFAULT_URL
        broker = Broker(send or http_transport(url, user, password), args.vhost)
        run = Run(broker, args, user, out, err, clock or (lambda: datetime.datetime.now(datetime.timezone.utc)),
                  who or operator_name())
        if args.audit_log:
            run.writable(args.audit_log)
        if args.command == "list":
            return command_list(run)
        if args.command == "inspect":
            return command_inspect(run)
        return command_move(run, args.command)
    except Refused as error:
        if args.json:
            print(json.dumps({"schema": SCHEMA, "command": args.command, "error": str(error)}), file=out)
        print(f"dead_letters: {error}", file=err)
        return 2


def operator_name() -> str:
    try:
        return getpass.getuser()
    except (OSError, ImportError, KeyError):
        return "unknown"


if __name__ == "__main__":
    sys.exit(main())
