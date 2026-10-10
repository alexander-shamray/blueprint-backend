"""docs/runbooks/error-rate.md. Alerts: ErrorRateGateway then ErrorRateService, each `for: 5m`, under the traffic loop.
Cause:      `stop ordering-api` for ErrorRateGateway; then `stop sql` for ErrorRateService, which the gateway reports too.
First step: the runbook's first PromQL, which says whether the 5xx is the edge's or a service's, read as written.
Restore:    start ordering-api, then sql once it accepts a login; each waits for its alert to resolve.
"""

from __future__ import annotations

from types import SimpleNamespace

import harness

RUNBOOK = "error-rate.md"
ALERT = "ErrorRateGateway"
GATEWAY = "Gateway.Api"
# The rules are `> 0.01` over a 5-minute `rate`, `for: 5m`. The ratio crosses 1% within the first export of failures:
# half the loop's requests fail, and the window holds about 100.
FOR_SECONDS = 300
DEADLINE = harness.Deadline(signal_seconds=0, for_seconds=FOR_SECONDS)
# The 5xx leave a 5-minute window five minutes after the last, and the loop keeps the window populated while they do.
SETTLE = harness.Deadline(signal_seconds=300)
# The loop is bounded: warm-up, the cause's longest wait, the settle, and a margin.
TRAFFIC_SECONDS = 300 + DEADLINE.seconds + SETTLE.seconds + 300
# The window must be populated before the cause, or a quiet start says nothing: two exports and an evaluation.
WARM_SECONDS = 2 * harness.EXPORT_INTERVAL_SECONDS + harness.EVALUATION_INTERVAL_SECONDS + 60
SQL_BACK_SECONDS = 180

# The runbook's query, verbatim apart from its layout.
FIRST_QUERY = ('sum by (service_name, http_route) ('
               'rate(http_server_request_duration_seconds_count{http_response_status_code=~"5.."}[5m]))')


def _served(service: str, negate: bool = False) -> str:
    match = "!=" if negate else "="
    selector = f'service_name{match}"{service}"'
    return f"sum by (service_name) (rate(http_server_request_duration_seconds_count{{{selector}}}[5m]))"


def _warm(world: harness.World, negate: bool) -> None:
    """Start the loop and wait until the denominator the rule divides by has a series for what is about to fail."""
    world.traffic.start(TRAFFIC_SECONDS)
    harness.wait_until(
        lambda: _read(world, _served(GATEWAY, negate)),
        WARM_SECONDS, "the loop's requests reaching Prometheus before the cause")


def _read(world: harness.World, query: str) -> tuple[bool, str]:
    rows = [row for row in world.alerts.query(query) if float(row["value"][1]) > 0]
    return bool(rows), f"{len(rows)} series with requests"


def cause(world: harness.World) -> None:
    _warm(world, negate=False)
    world.compose.stop("ordering-api")
    world.say("ordering-api stopped behind the gateway; the loop's cancels now fail at the edge")


def _failing(world: harness.World) -> list[dict]:
    """The runbook's query, keeping the series that still count a 5xx: a route that failed earlier reads 0 and is no
    evidence of this cause, so it would otherwise stand as the one the reader is told about."""
    rows = [row for row in world.alerts.query(FIRST_QUERY) if float(row["value"][1]) > 0]
    return sorted(rows, key=lambda row: -float(row["value"][1]))


def first_step(world: harness.World) -> tuple[bool, str]:
    rows = _failing(world)
    edge = [row for row in rows if row["metric"].get("service_name") == GATEWAY]
    behind = sorted({row["metric"].get("service_name") for row in rows} - {GATEWAY})
    if not edge:
        return False, f"the runbook's query reads no 5xx for {GATEWAY}: {rows}"
    if world.alerts.state("ErrorRateService") != "inactive":
        return False, "ErrorRateService fires beside the gateway's, so the runbook's edge-or-service split is blurred"
    route = edge[0]["metric"].get("http_route", "?")
    note = f"; services behind it with 5xx: {', '.join(behind)}" if behind else "; no service behind it reports one"
    return True, f"{GATEWAY} 5xx on {route} at {float(edge[0]['value'][1]):.3f}/s{note}"


def restore(world: harness.World) -> None:
    world.compose.start("ordering-api")


def settled(world: harness.World) -> tuple[bool, str]:
    state = world.alerts.state(ALERT)
    return state == "inactive", f"{ALERT} is {state}"


def _service_cause(world: harness.World) -> None:
    _warm(world, negate=True)
    world.compose.stop("sql")
    world.say("sql stopped under the services; every request that reaches a database now fails")


def _service_first_step(world: harness.World) -> tuple[bool, str]:
    rows = _failing(world)
    services = sorted({row["metric"].get("service_name") for row in rows} - {GATEWAY, None})
    if not services:
        return False, f"the runbook's query reads 5xx for no service behind the gateway: {rows}"
    edge = world.alerts.state(ALERT)
    # The runbook: gateway and a backend both alerting means work the backend first; the edge reports its downstream.
    return True, (f"5xx from {', '.join(services)}; ErrorRateGateway is {edge}, which the runbook reads as the edge "
                  f"reporting them")


def _service_restore(world: harness.World) -> None:
    world.compose.start("sql")
    harness.wait_until(_sql_answers(world), SQL_BACK_SECONDS, "sql accepting a login after the start")


def _sql_answers(world: harness.World):
    def ask() -> tuple[bool, str]:
        try:
            return "1" in world.compose.exec_sql("SELECT 1;", "master"), "sqlcmd answered"
        except harness.GameDayError as error:
            return False, str(error)[:200]
    return ask


def _service_settled(world: harness.World) -> tuple[bool, str]:
    # The gateway fired on the services' 500s too, and the next scenario cannot start while it does.
    states = {name: world.alerts.state(name) for name in ("ErrorRateService", "ErrorRateGateway")}
    return all(state == "inactive" for state in states.values()), ", ".join(f"{k} is {v}" for k, v in states.items())


NEXT = SimpleNamespace(
    RUNBOOK=RUNBOOK, ALERT="ErrorRateService", DEADLINE=DEADLINE, SETTLE=SETTLE, cause=_service_cause,
    first_step=_service_first_step, restore=_service_restore, settled=_service_settled)
