"""The game day's hands: Compose control, a token and an order, alert state, and the wait.

Stdlib only, on the terms of deploy/observability/check.py. Nothing here knows a runbook; a scenario
(scenarios/*.py) does, and game_day.py drives it. README.md owns what this proves and what it does not.
"""

from __future__ import annotations

import base64
import json
import os
import secrets
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable

ROOT = Path(__file__).resolve().parents[2]
COMPOSE_FILE = ROOT / "deploy" / "compose" / "docker-compose.yml"

# walk-an-order.sh's route, as its own defaults give it. test_harness.py reads that script and fails if any
# of the four stops agreeing, so this is a copy that is checked rather than a second opinion.
GATEWAY = os.environ.get("GATEWAY", "http://localhost:5000")
KEYCLOAK = os.environ.get("KEYCLOAK", "http://localhost:8080")
KEYCLOAK_ADMIN = ("admin", "admin")
PRODUCT = os.environ.get("PRODUCT", "5eed0000-0000-0000-0000-000000000003")
# 9090 is not published, so Prometheus is read through Grafana's datasource proxy, as compose.yml's smoke does.
GRAFANA = os.environ.get("GRAFANA", "http://localhost:3000")
PROMETHEUS_PROXY = "/api/datasources/proxy/uid/prometheus"

# The OpenTelemetry SDK's periodic export default (OTEL_METRIC_EXPORT_INTERVAL). Nothing under src/ or
# deploy/compose/ sets it, which test_harness.py asserts, so the constant is the platform's own value.
EXPORT_INTERVAL_SECONDS = 60
# Prometheus's default rule evaluation interval, which the bundled image's configuration leaves unset;
# README.md's deadline paragraph owns how that was read and what the first runs measured.
EVALUATION_INTERVAL_SECONDS = 60
# The bundled Prometheus reports an alert one evaluation after the one that first saw its expression true, with
# activeAt back-dated to the earlier one; README.md's deadline paragraph owns the measurements.
REPORT_LAG_SECONDS = EVALUATION_INTERVAL_SECONDS

POLL_SECONDS = 2.0

# The traffic loop's rate is the lowest that gives Latency's widest window a p99 over 100 observations, with the
# loop's two routes sent alternately; README.md's *The traffic loop* owns the argument and the 429 headroom.
LATENCY_WINDOW_SECONDS = 600
WINDOW_OBSERVATIONS = 100
TRAFFIC_ROUTES = 2
TRAFFIC_TICK_SECONDS = LATENCY_WINDOW_SECONDS / WINDOW_OBSERVATIONS / TRAFFIC_ROUTES
# A request a paused database holds runs on, so the loop bounds what it has out.
TRAFFIC_MAX_IN_FLIGHT = 40
# The access token lives five minutes; a new one is fetched well inside that.
TRAFFIC_LOGIN_REFRESH_SECONDS = 120


class GameDayError(Exception):
    """A step that did not do what its scenario said it would."""


class Timeout(GameDayError):
    """A convergence predicate that did not hold by its deadline, carrying the last thing it saw."""


@dataclass(frozen=True)
class Deadline:
    """How long a predicate may take: signal threshold, `for:`, export and evaluation, each a ceiling."""

    signal_seconds: int
    for_seconds: int = 0

    @property
    def seconds(self) -> int:
        return (self.signal_seconds + self.for_seconds + EXPORT_INTERVAL_SECONDS + EVALUATION_INTERVAL_SECONDS
                + REPORT_LAG_SECONDS)

    @property
    def derivation(self) -> str:
        return (f"{self.signal_seconds}s signal + {self.for_seconds}s for: + {EXPORT_INTERVAL_SECONDS}s export "
                f"+ {EVALUATION_INTERVAL_SECONDS}s evaluation + {REPORT_LAG_SECONDS}s reporting = {self.seconds}s")


def wait_until(predicate: Callable[[], tuple[bool, str]], deadline_seconds: float, what: str, *,
               clock: Callable[[], float] = time.monotonic, sleep: Callable[[float], None] = time.sleep,
               poll: float = POLL_SECONDS) -> float:
    """Poll until the predicate holds, and return the seconds it took. Never a bare sleep.

    The predicate is asked once more at the deadline, so a condition that became true during the last sleep is
    seen. The timeout names the last observation, because "did not happen" is the least useful finding.
    """
    started = clock()
    while True:
        ok, observed = predicate()
        elapsed = clock() - started
        if ok:
            return elapsed
        if elapsed >= deadline_seconds:
            raise Timeout(f"{what}: not within {deadline_seconds:.0f}s; last saw {observed}")
        sleep(min(poll, deadline_seconds - elapsed))


class Compose:
    """docker compose against deploy/compose/, with the runner injected so a test sees the argv."""

    def __init__(self, run: Callable[..., subprocess.CompletedProcess] = subprocess.run,
                 compose_file: Path = COMPOSE_FILE) -> None:
        self._run = run
        self._base = ["docker", "compose", "-f", str(compose_file)]

    def _call(self, *args: str, check: bool = True, stdin: str | None = None) -> str:
        # encoding is explicit: text=True alone decodes with the Windows ANSI page and drops what it cannot read.
        result = self._run(
            [*self._base, *args], input=stdin, capture_output=True, text=True, encoding="utf-8",
            errors="replace", check=False)
        if check and result.returncode != 0:
            # sqlcmd reports a T-SQL error on stdout, so both streams are the message.
            said = (result.stderr.strip() + " " + result.stdout.strip()).strip()
            raise GameDayError(f"docker compose {' '.join(args)} exited {result.returncode}: {said[:600]}")
        return result.stdout + (result.stderr if not check else "")

    def stop(self, service: str) -> None:
        self._call("stop", service)

    def start(self, service: str) -> None:
        self._call("start", service)

    def restart(self, service: str) -> None:
        self._call("restart", service)

    def pause(self, service: str) -> None:
        self._call("pause", service)

    def unpause(self, service: str) -> None:
        # Unpausing a container that is not paused is an error to compose; restore must be safe to run twice.
        self._call("unpause", service, check=False)

    def running(self, service: str) -> bool:
        return bool(self._call("ps", "--status", "running", "-q", service).strip())

    def logs(self, service: str, since: str = "10m") -> str:
        return self._call("logs", "--no-color", "--since", since, service, check=False)

    def exec_sql(self, sql: str, database: str = "Ordering") -> str:
        """Run T-SQL as sa through the container's own sqlcmd, and return its text with the padding trimmed."""
        # The password is the container's own environment's, as its healthcheck reads it, so it is on no argv;
        # -b makes a T-SQL error an exit code, and -I turns QUOTED_IDENTIFIER on, which sqlcmd leaves off and
        # which the outbox's filtered indexes refuse a write without (SSMS and the application default to on).
        script = ('/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b -I '
                  f'-d {database} -W -s"|" -i /dev/stdin')
        return self._call("exec", "-T", "sql", "bash", "-c", script, stdin=sql).strip()

    def exec_redis(self, service: str, *args: str) -> str:
        return self._call("exec", "-T", service, "redis-cli", *args).strip()

    def rabbitmqctl(self, *args: str, stdin: str | None = None) -> str:
        """The broker through its container, which needs no account (deploy/compose/README.md)."""
        return self._call("exec", "-T", "rabbitmq", "rabbitmqctl", *args, stdin=stdin).strip()


Http = Callable[[str, str, dict[str, str], bytes | None], tuple[int, str]]


def _request(method: str, url: str, headers: dict[str, str], body: bytes | None, timeout: float) -> tuple[int, str]:
    request = urllib.request.Request(url, data=body, headers=headers, method=method)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.status, response.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode("utf-8", errors="replace")
    except (urllib.error.URLError, TimeoutError, ConnectionError) as error:
        return 0, str(error)


def http(method: str, url: str, headers: dict[str, str], body: bytes | None) -> tuple[int, str]:
    return _request(method, url, headers, body, 15)


# Longer than a SQL command's 30 seconds, so a request held by a paused database ends as the server's answer and not
# as the loop hanging up, which would leave the server's own duration to the disconnect.
TRAFFIC_TIMEOUT_SECONDS = 45


def patient_http(method: str, url: str, headers: dict[str, str], body: bytes | None) -> tuple[int, str]:
    return _request(method, url, headers, body, TRAFFIC_TIMEOUT_SECONDS)


class Alerts:
    """Alert state as Prometheus reports it, read through Grafana's datasource proxy."""

    def __init__(self, send: Http = http, base: str = GRAFANA) -> None:
        self._send = send
        self._base = base

    def _get(self, path: str) -> dict:
        status, text = self._send("GET", f"{self._base}{PROMETHEUS_PROXY}{path}", {}, None)
        if status != 200:
            raise GameDayError(f"Prometheus via Grafana: GET {path} answered {status}: {text[:200]}")
        return json.loads(text)

    def state(self, name: str) -> str:
        """`firing`, `pending`, or `inactive` when Prometheus lists no active alert of that name."""
        alerts = self._get("/api/v1/alerts")["data"]["alerts"]
        states = {alert["state"] for alert in alerts if alert["labels"].get("alertname") == name}
        for state in ("firing", "pending"):
            if state in states:
                return state
        return "inactive"

    def query(self, expression: str) -> list[dict]:
        """An instant PromQL query's result vector, for a runbook step that is a query."""
        found = self._get("/api/v1/query?" + urllib.parse.urlencode({"query": expression}))["data"]
        return found["result"]

    def loaded(self, name: str) -> bool:
        """Whether the rule is loaded at all, which `inactive` cannot say: a rule nobody loaded is also quiet."""
        groups = self._get("/api/v1/rules")["data"]["groups"]
        return any(rule["name"] == name for group in groups for rule in group["rules"])

    def labels(self, name: str) -> list[dict[str, str]]:
        """The labels of each active alert of that name, for a runbook that reads one (`queue`) to find its subject."""
        alerts = self._get("/api/v1/alerts")["data"]["alerts"]
        return [alert["labels"] for alert in alerts if alert["labels"].get("alertname") == name]


def regex_literal(text: str) -> str:
    """`text` as a RE2 pattern that matches itself: Loki's regexes are RE2, which rejects an escaped space."""
    return "".join("\\" + char if char in "\\.^$*+?()[]{}|" else char for char in text)


class Logs:
    """What a service logged, read from Loki through Grafana's proxy.

    The hosts log through one OpenTelemetry provider and nothing else (§13.4), so a container's stdout is empty
    and this is the only place a log line exists. The runbooks' `kubectl logs` steps are translated to it.
    """

    def __init__(self, send: Http = http, base: str = GRAFANA, clock: Callable[[], float] = time.time) -> None:
        self._send = send
        self._base = base
        self._clock = clock

    def search(self, service_name: str, pattern: str, since_seconds: int = 900, limit: int = 20,
               exception: str | None = None) -> list[str]:
        """The lines of `service_name` matching `pattern` (a RE2 regex) in the last `since_seconds`, newest first.

        `exception` also matches the entry's exception message, which the hosts log as structured metadata and
        never in the line itself, so a runbook's "with the refusal as its exception" is read through it.
        """
        now = int(self._clock() * 1e9)
        selector = '{service_name="' + service_name + '"} |~ "' + pattern.replace("\\", "\\\\") + '"'
        if exception is not None:
            selector += ' | exception_message =~ ".*' + exception.replace("\\", "\\\\") + '.*"'
        query = urllib.parse.urlencode({
            "query": selector,
            "start": now - since_seconds * 10**9, "end": now, "limit": limit, "direction": "backward"})
        status, text = self._send(
            "GET", f"{self._base}/api/datasources/proxy/uid/loki/loki/api/v1/query_range?{query}", {}, None)
        if status != 200:
            raise GameDayError(f"Loki via Grafana: query answered {status}: {text[:200]}")
        return [line for stream in json.loads(text)["data"]["result"] for _, line in stream["values"]]


class Orders:
    """One order through the gateway as `demo`, on walk-an-order.sh's route."""

    def __init__(self, send: Http = http) -> None:
        self._send = send

    def token(self) -> str:
        form = urllib.parse.urlencode({
            "grant_type": "password", "client_id": "web-app", "username": "demo", "password": "demo"}).encode()
        status, text = self._send(
            "POST", f"{KEYCLOAK}/realms/commerce/protocol/openid-connect/token",
            {"Content-Type": "application/x-www-form-urlencoded"}, form)
        if status != 200:
            raise GameDayError(f"no token from {KEYCLOAK}: {status} {text[:200]}")
        return json.loads(text)["access_token"]

    def place(self) -> str:
        body = json.dumps({
            "commandId": str(uuid.uuid4()),
            "items": [{"productId": PRODUCT, "quantity": 1}],
            "shippingAddress": {"line1": "1 Test Street", "city": "Almaty", "postalCode": "050000", "country": "KZ"},
            "currency": "EUR"}).encode()
        status, text = self._send(
            "POST", f"{GATEWAY}/api/v1/orders",
            {"Authorization": f"Bearer {self.token()}", "Content-Type": "application/json"}, body)
        # The answer is a bare JSON string, the order's id; anything else is a refusal.
        if status not in (200, 201, 202) or not text.startswith('"'):
            raise GameDayError(f"the order was refused: {status} {text[:200]}")
        return json.loads(text)


class Traffic:
    """A bounded request generator through the gateway, so a rule over request counts has a window to read.
    Each tick alternates an anonymous catalog read and an authenticated cancel of an order that does not exist:
    neither writes, and both reach a database. Each request runs on its own thread, so a database that holds
    them does not slow the loop. It ends at `stop()` or at its bound, whichever is first."""

    def __init__(self, send: Http = patient_http, token: Callable[[], str] | None = None,
                 clock: Callable[[], float] = time.monotonic, tick: float = TRAFFIC_TICK_SECONDS,
                 base: str = GATEWAY) -> None:
        self._send = send
        self._token = token
        self._clock = clock
        self._tick = tick
        self._base = base
        self._halt = threading.Event()
        self._lock = threading.Lock()
        self._token_lock = threading.Lock()
        self._thread: threading.Thread | None = None
        self._workers: list[threading.Thread] = []
        self._bearer = ""
        self._bearer_at = float("-inf")
        self._sent = 0
        self._skipped = 0
        self._statuses: dict[int, int] = {}

    @property
    def running(self) -> bool:
        return self._thread is not None and self._thread.is_alive()

    def start(self, max_seconds: float) -> None:
        if self.running:
            raise GameDayError("the traffic loop is already running")
        self._halt.clear()
        with self._lock:
            self._sent, self._skipped, self._statuses = 0, 0, {}
        self._thread = threading.Thread(target=self._loop, args=(max_seconds,), name="game-day-traffic", daemon=True)
        self._thread.start()

    def stop(self) -> None:
        """End the loop and wait for the requests it still has out, none of which outlives the client timeout."""
        self._halt.set()
        if self._thread is not None:
            self._thread.join()
        for worker in self._workers:
            worker.join(TRAFFIC_TIMEOUT_SECONDS + 5)
        self._workers = []

    def counts(self) -> dict[str, object]:
        with self._lock:
            return {"sent": self._sent, "skipped": self._skipped, "statuses": dict(sorted(self._statuses.items()))}

    def _loop(self, max_seconds: float) -> None:
        started = self._clock()
        sent = 0
        while not self._halt.is_set() and self._clock() - started < max_seconds:
            self._workers = [worker for worker in self._workers if worker.is_alive()]
            if len(self._workers) >= TRAFFIC_MAX_IN_FLIGHT:
                with self._lock:
                    self._skipped += 1
            else:
                worker = threading.Thread(target=self._one, args=(sent % TRAFFIC_ROUTES,), daemon=True)
                self._workers.append(worker)
                worker.start()
            sent += 1
            self._halt.wait(self._tick)

    def _headers(self) -> dict[str, str]:
        with self._token_lock:
            now = self._clock()
            if self._token is not None and now - self._bearer_at >= TRAFFIC_LOGIN_REFRESH_SECONDS:
                try:
                    self._bearer = self._token()
                    self._bearer_at = now
                except GameDayError:
                    # An old token is good for minutes, and a loop that dies on one failed fetch ends the window it
                    # exists to fill; the 401s a lapsed one earns are in the counts.
                    pass
            return {"Authorization": f"Bearer {self._bearer}", "Content-Type": "application/json"}

    def _one(self, route: int) -> None:
        if route == 0:
            status, _ = self._send("GET", f"{self._base}/api/v1/catalog/products?limit=1", {}, None)
        else:
            body = json.dumps({"reason": "customer_request"}).encode()
            status, _ = self._send(
                "POST", f"{self._base}/api/v1/orders/{uuid.uuid4()}/cancel", self._headers(), body)
        with self._lock:
            self._sent += 1
            self._statuses[status] = self._statuses.get(status, 0) + 1


class Realm:
    """The commerce realm's service-account grants, read and changed through Keycloak's admin API.

    The admin login is the bootstrap one infrastructure.yml sets (§14.1's local-development exception), which
    test_refused_reads.py reads from that file. A scenario that revokes a grant restores it and reads it back here.
    """

    def __init__(self, send: Http = http, base: str = KEYCLOAK) -> None:
        self._send = send
        self._base = base

    def _token(self) -> str:
        form = urllib.parse.urlencode({
            "grant_type": "password", "client_id": "admin-cli", "username": KEYCLOAK_ADMIN[0],
            "password": KEYCLOAK_ADMIN[1]}).encode()
        status, text = self._send(
            "POST", f"{self._base}/realms/master/protocol/openid-connect/token",
            {"Content-Type": "application/x-www-form-urlencoded"}, form)
        if status != 200:
            raise GameDayError(f"no admin token from {self._base}: {status} {text[:200]}")
        return json.loads(text)["access_token"]

    def _admin(self, method: str, path: str, body: object = None, ok: tuple[int, ...] = (200, 204)):
        # A token per call: the admin one lives for a minute, and a restore may run half an hour after a cause.
        headers = {"Authorization": f"Bearer {self._token()}"}
        data = None
        if body is not None:
            headers["Content-Type"] = "application/json"
            data = json.dumps(body).encode()
        status, text = self._send(method, f"{self._base}/admin/realms/commerce{path}", headers, data)
        if status not in ok:
            raise GameDayError(f"Keycloak admin: {method} {path} answered {status}: {text[:200]}")
        return json.loads(text) if text.strip() else None

    def _ids(self, account: str, client: str) -> tuple[str, str]:
        users = self._admin("GET", "/users?" + urllib.parse.urlencode({"username": account, "exact": "true"}))
        clients = self._admin("GET", "/clients?" + urllib.parse.urlencode({"clientId": client}))
        if not users or not clients:
            raise GameDayError(f"Keycloak has no user {account} or no client {client} in the commerce realm")
        return users[0]["id"], clients[0]["id"]

    def roles(self, account: str, client: str) -> list[str]:
        """The names of `client`'s roles that `account` holds directly."""
        user, container = self._ids(account, client)
        return sorted(role["name"] for role in self._admin("GET", f"/users/{user}/role-mappings/clients/{container}"))

    def _role(self, container: str, role: str) -> dict:
        return self._admin("GET", f"/clients/{container}/roles/{urllib.parse.quote(role, safe='')}")

    def revoke(self, account: str, client: str, role: str) -> None:
        user, container = self._ids(account, client)
        self._admin("DELETE", f"/users/{user}/role-mappings/clients/{container}", [self._role(container, role)])

    def grant(self, account: str, client: str, role: str) -> None:
        user, container = self._ids(account, client)
        self._admin("POST", f"/users/{user}/role-mappings/clients/{container}", [self._role(container, role)])


@dataclass(frozen=True)
class Grant:
    """The one role a worker's service account holds, which a scenario takes away and must give back."""

    account: str
    client: str
    role: str

    def take(self, realm: Realm) -> None:
        realm.revoke(self.account, self.client, self.role)

    def give_back(self, realm: Realm) -> None:
        """Restore the grant, then read the account back: the realm export gives it this role and no other."""
        realm.grant(self.account, self.client, self.role)
        held = realm.roles(self.account, self.client)
        if held != [self.role]:
            raise GameDayError(f"{self.account} holds {held} of {self.client} after the restore, not [{self.role!r}]")


# The Management API under the tool's own variable, so one setting points both at a broker published elsewhere.
# Compose publishes 15672, which is why the runbooks' `kubectl port-forward` has nothing to translate to here.
MANAGEMENT = os.environ.get("DEAD_LETTERS_URL", "http://localhost:15672")
DEAD_LETTERS = ROOT / "tools" / "dead-letters" / "dead_letters.py"
OPERATOR = "dead-letter-operator"
# MassTransit's default envelope, which this platform keeps (skipped-queue.md says why the type is in the body).
MASSTRANSIT_JSON = "application/vnd.masstransit+json"


class Broker:
    """The broker as the queue runbooks reach it: as dead-letter-operator, through the Management API and the tool.

    The account ships with no password on Compose (deploy/compose/README.md), so `open` sets one for the run and
    `close` clears it, the tool README's local route. The password is on no argv: rabbitmqctl reads it from stdin
    and the tool from its environment, as the runbooks' `read -s` gives it.
    """

    def __init__(self, compose: Compose, send: Http = http, base: str = MANAGEMENT,
                 run: Callable[..., subprocess.CompletedProcess] = subprocess.run, python: str = sys.executable,
                 secret: Callable[[], str] = lambda: secrets.token_urlsafe(24)) -> None:
        self._compose = compose
        self._send = send
        self._base = base.rstrip("/")
        self._run = run
        self._python = python
        self._secret = secret
        self._password = ""

    def open(self) -> None:
        self._password = self._secret()
        self._compose.rabbitmqctl("change_password", OPERATOR, stdin=self._password)

    def close(self) -> None:
        """Clear the password, which the shipped definitions leave empty; safe to run twice."""
        self._password = ""
        self._compose.rabbitmqctl("clear_password", OPERATOR)

    def publish(self, exchange: str, message_type: str, message: dict) -> str:
        """Publish one message in MassTransit's envelope to an endpoint's exchange, and return its MessageId."""
        message_id, correlation_id = str(uuid.uuid4()), str(uuid.uuid4())
        envelope = {
            "messageId": message_id, "correlationId": correlation_id, "conversationId": str(uuid.uuid4()),
            "sourceAddress": "rabbitmq://rabbitmq/game-day", "destinationAddress": f"rabbitmq://rabbitmq/{exchange}",
            "messageType": [message_type], "message": message, "headers": {},
            "sentTime": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
        body = json.dumps({
            "properties": {"content_type": MASSTRANSIT_JSON, "message_id": message_id,
                           "correlation_id": correlation_id, "delivery_mode": 2},
            "routing_key": "", "payload": json.dumps(envelope), "payload_encoding": "string"}).encode()
        credential = base64.b64encode(f"{OPERATOR}:{self._password}".encode()).decode()
        status, text = self._send(
            "POST", f"{self._base}/api/exchanges/%2F/{urllib.parse.quote(exchange, safe='')}/publish",
            {"Authorization": f"Basic {credential}", "Content-Type": "application/json"}, body)
        if status != 200 or not json.loads(text).get("routed"):
            raise GameDayError(f"the publish to {exchange} as {OPERATOR} was not routed: {status} {text[:200]}")
        return message_id

    def tool(self, *args: str) -> tuple[int, dict]:
        """dead_letters.py as the runbooks run it, with `--json`: its exit code and its document."""
        env = {key: value for key, value in os.environ.items()
               if key not in ("DEAD_LETTERS_CREDENTIALS", "DEAD_LETTERS_USER")}
        env.update(DEAD_LETTERS_PASSWORD=self._password, DEAD_LETTERS_URL=self._base)
        result = self._run([self._python, str(DEAD_LETTERS), *args, "--json"], capture_output=True, text=True,
                           encoding="utf-8", errors="replace", env=env, check=False)
        try:
            return result.returncode, json.loads(result.stdout)
        except ValueError:
            raise GameDayError(f"dead_letters.py {' '.join(args)} exited {result.returncode} with no JSON: "
                               f"{result.stderr.strip()[:300]}") from None

    def discard(self, queue: str, message_ids: list[str]) -> list[str]:
        """The runbooks' Discard, by id with a record; returns what it discarded. An id already gone is not an error,
        so a restore that runs twice finds nothing to do the second time."""
        if not message_ids:
            return []
        handle, record = tempfile.mkstemp(prefix="game-day-", suffix=".jsonl")
        os.close(handle)
        try:
            selection = [part for message_id in message_ids for part in ("--message-id", message_id)]
            code, document = self.tool("discard", queue, *selection, "--execute", "--record", record)
        finally:
            # The record is the tool's last copy of a message, and these are the run's own synthetic ones.
            os.remove(record)
        if "error" in document:
            raise GameDayError(f"dead_letters.py discard {queue} was refused: {document['error']}")
        discarded = [action["message_id"] for action in document["actions"] if action["action"] == "discarded"]
        failed = [action for action in document["actions"] if action["action"] in ("failed", "refused")]
        if failed or set(message_ids) - set(discarded) - set(document["not_found"]):
            raise GameDayError(f"dead_letters.py discard {queue} exited {code}: {document['actions']}")
        return discarded

    def queues(self) -> dict[str, tuple[int, int]]:
        """Each queue's depth and consumer count, through the container rather than any account."""
        text = self._compose.rabbitmqctl("list_queues", "name", "messages", "consumers", "--quiet",
                                         "--no-table-headers")
        found: dict[str, tuple[int, int]] = {}
        for line in text.splitlines():
            parts = line.split()
            if len(parts) == 3 and parts[1].isdigit() and parts[2].isdigit():
                found[parts[0]] = (int(parts[1]), int(parts[2]))
        return found

    def purge(self, queue: str) -> None:
        self._compose.rabbitmqctl("purge_queue", queue)


@dataclass
class World:
    compose: Compose
    alerts: Alerts
    orders: Orders
    logs: Logs
    say: Callable[[str], None] = print
    caused_at: float = 0.0
    realm: Realm | None = None
    traffic: Traffic = field(default_factory=Traffic)
    broker: Broker | None = None

    def since_cause(self) -> int:
        """Seconds since the runner began the cause, rounded up, so a first step reads this run's lines and no
        earlier run's: Loki keeps what a previous scenario logged, and a line from it would pass for this one."""
        return int(time.time() - self.caused_at) + 5
