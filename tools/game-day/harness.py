"""The game day's hands: Compose control, a token and an order, alert state, and the wait.

Stdlib only, on the terms of deploy/observability/check.py. Nothing here knows a runbook; a scenario
(scenarios/*.py) does, and game_day.py drives it. README.md owns what this proves and what it does not.
"""

from __future__ import annotations

import json
import os
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

ROOT = Path(__file__).resolve().parents[2]
COMPOSE_FILE = ROOT / "deploy" / "compose" / "docker-compose.yml"

# walk-an-order.sh's route, as its own defaults give it. test_harness.py reads that script and fails if any
# of the four stops agreeing, so this is a copy that is checked rather than a second opinion.
GATEWAY = os.environ.get("GATEWAY", "http://localhost:5000")
KEYCLOAK = os.environ.get("KEYCLOAK", "http://localhost:8080")
PRODUCT = os.environ.get("PRODUCT", "5eed0000-0000-0000-0000-000000000003")
# 9090 is not published, so Prometheus is read through Grafana's datasource proxy, as compose.yml's smoke does.
GRAFANA = os.environ.get("GRAFANA", "http://localhost:3000")
PROMETHEUS_PROXY = "/api/datasources/proxy/uid/prometheus"

# The OpenTelemetry SDK's periodic export default (OTEL_METRIC_EXPORT_INTERVAL). Nothing under src/ or
# deploy/compose/ sets it, which test_harness.py asserts, so the constant is the platform's own value.
EXPORT_INTERVAL_SECONDS = 60
# Prometheus's default rule evaluation interval. The bundled LGTM image's configuration is not in this
# repository, so this is its documented default and the first live run is what measures it (README.md).
EVALUATION_INTERVAL_SECONDS = 60

POLL_SECONDS = 2.0


class GameDayError(Exception):
    """A step that did not do what its scenario said it would."""


class Timeout(GameDayError):
    """A convergence predicate that did not hold by its deadline, carrying the last thing it saw."""


@dataclass(frozen=True)
class Deadline:
    """How long a predicate may take, and the sum that says so, as tests/.../Journey/Deadlines.cs does.

    The rule's own threshold is the time the signal needs to cross it (an age gauge needs that many seconds to
    read above the line); `for:` is the rule's wait; the export interval is the longest a gauge goes unpublished;
    the evaluation interval is the longest a published value goes unjudged. Each is a ceiling, so the sum is.
    """

    signal_seconds: int
    for_seconds: int = 0

    @property
    def seconds(self) -> int:
        return self.signal_seconds + self.for_seconds + EXPORT_INTERVAL_SECONDS + EVALUATION_INTERVAL_SECONDS

    @property
    def derivation(self) -> str:
        return (f"{self.signal_seconds}s signal + {self.for_seconds}s for: + {EXPORT_INTERVAL_SECONDS}s export "
                f"+ {EVALUATION_INTERVAL_SECONDS}s evaluation = {self.seconds}s")


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


Http = Callable[[str, str, dict[str, str], bytes | None], tuple[int, str]]


def http(method: str, url: str, headers: dict[str, str], body: bytes | None) -> tuple[int, str]:
    request = urllib.request.Request(url, data=body, headers=headers, method=method)
    try:
        with urllib.request.urlopen(request, timeout=15) as response:
            return response.status, response.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode("utf-8", errors="replace")
    except (urllib.error.URLError, TimeoutError, ConnectionError) as error:
        return 0, str(error)


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

    def loaded(self, name: str) -> bool:
        """Whether the rule is loaded at all, which `inactive` cannot say: a rule nobody loaded is also quiet."""
        groups = self._get("/api/v1/rules")["data"]["groups"]
        return any(rule["name"] == name for group in groups for rule in group["rules"])


class Logs:
    """What a service logged, read from Loki through Grafana's proxy.

    The hosts log through one OpenTelemetry provider and nothing else (§13.4), so a container's stdout is empty
    and this is the only place a log line exists. The runbooks' `kubectl logs` steps are translated to it.
    """

    def __init__(self, send: Http = http, base: str = GRAFANA, clock: Callable[[], float] = time.time) -> None:
        self._send = send
        self._base = base
        self._clock = clock

    def search(self, service_name: str, pattern: str, since_seconds: int = 900, limit: int = 20) -> list[str]:
        """The lines of `service_name` matching `pattern` (a RE2 regex) in the last `since_seconds`, newest first."""
        now = int(self._clock() * 1e9)
        query = urllib.parse.urlencode({
            "query": '{service_name="' + service_name + '"} |~ "' + pattern.replace("\\", "\\\\") + '"',
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


@dataclass
class World:
    compose: Compose
    alerts: Alerts
    orders: Orders
    logs: Logs
    say: Callable[[str], None] = print
    caused_at: float = 0.0

    def since_cause(self) -> int:
        """Seconds since the runner began the cause, rounded up, so a first step reads this run's lines and no
        earlier run's: Loki keeps what a previous scenario logged, and a line from it would pass for this one."""
        return int(time.time() - self.caused_at) + 5
