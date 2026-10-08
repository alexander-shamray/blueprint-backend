#!/usr/bin/env python3
"""Fetch a deployed realm through Keycloak's admin API and write it out.

The one file in `deploy/keycloak` that talks to anything. It is deliberately
thin and deliberately separate: `realm_check.py` decides, this fetches, and
keeping the network on the other side of a file boundary is what lets the
decision have a suite at all — `deploy/canary`'s split, adopted rather than
re-invented.

**What it writes is a realm export, not a new format.** Keycloak's
`GET /admin/realms/{realm}` answers a `RealmRepresentation` and
`GET /admin/realms/{realm}/clients` answers the `ClientRepresentation` list
that a full export carries under `clients`; joining the two produces the same
document shape `deploy/compose/keycloak/realm-export.json` holds. That is why
one predicate can judge both, and it is the whole reason this file exists in
this form rather than as a second checker.

**It never interprets.** A missing key stays missing and a client this
repository has never heard of is written out unchanged, because the only thing
that may decide whether a realm is compliant is the file that has a suite.

**Credentials fail closed.** Every one of the four environment variables is
required and an absent one stops the run naming it. There is no "check what we
can": a realm-obligation check that quietly degrades to checking nothing is the
fail-open shape §12's gate-coverage rule refuses, and it would report a pass on
the exact realm this gate was filed for.

**Plain HTTP is refused.** The admin API carries a bearer token that can read
every client secret in the realm, and §11.2 already refuses metadata over plain
HTTP outside Development. There is no development affordance here because there
is no local subject: the Compose realm is checked from its file.

Stdlib `urllib`, so `deploy/keycloak` adds no dependency and no
licence-register entry. Two GETs and a form POST is not a client library.

    py -3.12 deploy/keycloak/read_admin.py --out realm.json
"""

from __future__ import annotations

import argparse
import base64
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

# The names of the four environment variables this reads, declared in the file
# that DECIDES rather than here — `deploy/canary`'s direction, where
# `read_prometheus.py` imports from `canary.py` and never the reverse. Two of
# the four are what `realm_check.py authority` writes into `$GITHUB_ENV`, and a
# writer and a reader spelling a variable separately agree right up until one
# of them is edited.
import realm_check
from realm_check import ENVIRONMENT as REQUIRED

# The first of the four is the SERVER ROOT and not the realm's issuer URL — the
# admin endpoints sit beside `/realms`, not under it — which is why
# `realm_check.py authority` splits an authority rather than passing it whole.
BASE_URL, REALM, CLIENT_ID, CLIENT_SECRET = REQUIRED

TIMEOUT_SECONDS = 30

# ONE REQUEST WITH A CEILING, AND A REFUSAL AT THE CEILING. Paging was tried
# twice here and both terminating conditions were wrong for the same reason:
# Keycloak pages client MODELS and filters representations it cannot render
# afterwards, so neither a short page nor an empty one proves there is nothing
# behind it. There is no clients/count endpoint to appeal to, and the export
# that would be authoritative needs rights this credential deliberately does
# not hold.
#
# So completeness is not inferred at all. `max` is asked for far above any
# realm this platform will have, and a response AT the ceiling is refused
# rather than truncated — the one case where the answer might not be the whole
# list is the one case this stops on. `first` is not sent: there is nothing to
# skip when the ceiling is the whole realm.
CLIENT_LIMIT = 10000

# Without view-clients Keycloak filters the client list silently (ADR-042), so
# it is required; every realm-management role held must also be a read role
# below, because a grant wider than a read is one a leaked secret hands over
# whole (docs/secrets.md). view-realm composes nothing, as the suite asserts.
REALM_MANAGEMENT = "realm-management"
COMPLETENESS_ROLES = ("view-clients",)
PERMITTED_ROLES = ("view-clients", "query-clients", "view-realm")


def environment() -> dict[str, str]:
    """The four required values, or a stop naming every one that is missing.

    Every one, not the first. An operator wiring this into an environment for
    the first time should learn what it needs in one run rather than four.
    """
    missing = [name for name in REQUIRED if not os.environ.get(name, "").strip()]
    if missing:
        raise SystemExit(
            "read_admin: the realm check needs " + ", ".join(missing) +
            ". These are deploy-time credentials for a Keycloak service "
            "account with realm-read rights; docs/secrets.md carries how they "
            "are provisioned and rotated. Refusing to run rather than "
            "reporting a realm nobody read.")

    values = {name: os.environ[name].strip() for name in REQUIRED}
    base = values[BASE_URL].rstrip("/")
    if not base.startswith("https://"):
        raise SystemExit(
            f"read_admin: {BASE_URL} is {base!r}. The admin API carries a "
            "bearer token that can read every client secret in the realm, so "
            "it is https or nothing.")
    values[BASE_URL] = base
    return values


class NoRedirects(urllib.request.HTTPRedirectHandler):
    """A redirect is refused rather than followed, because the header travels.

    `urllib` copies a request's headers onto the redirected request and strips
    only the content ones, so an `Authorization` header follows a 302 to
    **any** host — including one outside the realm's origin, and the `https`
    check in `environment` says nothing about where a redirect leads. The token
    this fetch carries can read every client secret in the realm, so it is the
    one header that must not travel.

    Refusing rather than re-signing per origin is the smaller decision: the
    admin API of the realm a rollout is about to install is not a place a
    redirect is expected, and a redirect that *is* expected is a change to
    where this reads, which belongs to whoever makes it.
    """

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise urllib.error.HTTPError(
            req.full_url, code,
            f"refusing to follow a {code} redirect to {newurl} — the admin "
            "credential would travel with it", headers, fp)


# One opener for every request this file makes, so no call site can forget.
OPENER = urllib.request.build_opener(NoRedirects)


def token(base_url: str, realm: str, client_id: str, client_secret: str) -> str:
    """A client-credentials access token for the service account.

    The token endpoint is the realm's own, so the credential is a client of the
    realm being checked. That is deliberate: a cross-realm admin account would
    hold rights over realms this check has no business reading.
    """
    # `safe=""`, because `quote` leaves `/` alone by default and `/` is the
    # only character that changes which realm this reads. A KEYCLOAK_REALM of
    # `commerce/../master` would otherwise normalise to a different realm and
    # this run would report that one holding section 11's obligations.
    url = (f"{base_url}/realms/{urllib.parse.quote(realm, safe='')}"
           "/protocol/openid-connect/token")
    form = urllib.parse.urlencode({
        "grant_type": "client_credentials",
        "client_id": client_id,
        "client_secret": client_secret,
    }).encode("ascii")

    request = urllib.request.Request(url, data=form, method="POST")
    request.add_header("Content-Type", "application/x-www-form-urlencoded")
    with OPENER.open(request, timeout=TIMEOUT_SECONDS) as response:  # noqa: S310
        body = json.loads(response.read().decode("utf-8"))

    access = body.get("access_token")
    if not isinstance(access, str) or not access:
        # The body is NOT printed. A failed token response can echo the request
        # form back, and this one carries a client secret.
        raise SystemExit(
            "read_admin: the token endpoint returned no access_token. The "
            "response body is withheld because it can contain the credential "
            "that was sent.")
    return access


def granted_roles(access_token: str) -> set[str]:
    """The realm-management roles this token actually carries.

    The payload is decoded and NOT verified, which is safe because nothing is
    authorised on it: the server issued this token and the server is what
    enforces the roles. What it is read for is the opposite of a trust
    decision — to find out whether this account can see the whole realm, and
    to stop if it cannot.

    A token that is not a JWT stops the run rather than being waved through:
    the premise this file's completeness rests on would then be unestablished,
    which is the same thing as unmet.
    """
    parts = access_token.split(".")
    if len(parts) != 3:
        raise SystemExit(
            "read_admin: the token endpoint answered something that is not a "
            "JWT, so the roles this account holds cannot be read — and the "
            "client list's completeness rests on them.")

    payload = parts[1]
    payload += "=" * (-len(payload) % 4)
    try:
        claims = json.loads(base64.urlsafe_b64decode(payload).decode("utf-8"))
    except (ValueError, UnicodeDecodeError) as error:
        raise SystemExit(
            f"read_admin: the token's payload does not decode: {error}") from error

    access = claims.get("resource_access", {})
    management = access.get(REALM_MANAGEMENT, {}) if isinstance(access, dict) else {}
    roles = management.get("roles", []) if isinstance(management, dict) else []
    return {role for role in roles if isinstance(role, str)}


def get(url: str, access_token: str) -> object:
    request = urllib.request.Request(url, method="GET")
    request.add_header("Authorization", f"Bearer {access_token}")
    request.add_header("Accept", "application/json")
    with OPENER.open(request, timeout=TIMEOUT_SECONDS) as response:  # noqa: S310
        return json.loads(response.read().decode("utf-8"))


def clients(base: str, realm: str, access_token: str) -> list:
    """Every client, in one request, or a refusal.

    Reading part of a realm and calling it the realm is how a client past the
    boundary enables the implicit flow, or overrides the token lifetime,
    without ever reaching `check_realm` — and with `web-app` in what was read,
    the rollout passes on a realm nobody looked at the end of.

    **Completeness is refused rather than inferred.** A response holding
    exactly `CLIENT_LIMIT` clients is the only one that could have been cut
    short, and it stops the run; anything below the ceiling is the whole list,
    because the server had room to answer more and did not.
    """
    # THE GRANT FIRST, BECAUSE THE ANSWER'S COMPLETENESS DEPENDS ON IT. A
    # filtered client list is not distinguishable from a complete one, so the
    # only thing that can be checked is the premise.
    held = granted_roles(access_token)
    if not held & set(COMPLETENESS_ROLES):
        raise SystemExit(
            "read_admin: this account holds none of "
            f"{', '.join(COMPLETENESS_ROLES)} on {REALM_MANAGEMENT}, so "
            "Keycloak will silently drop the clients it may not view and a "
            "short list would look exactly like a complete one. `view-realm` "
            "is NOT enough and does not compose `view-clients`; "
            "docs/secrets.md carries what the account needs.")

    wider = sorted(held - set(PERMITTED_ROLES))
    if wider:
        raise SystemExit(
            f"read_admin: this account holds {', '.join(wider)} on "
            f"{REALM_MANAGEMENT}, beyond the read roles "
            f"{', '.join(PERMITTED_ROLES)}. Its secret is exercised from a CI "
            "runner, so a grant wider than a read is one a leak hands "
            "over whole; docs/secrets.md carries what the account holds.")

    query = urllib.parse.urlencode({"max": CLIENT_LIMIT})
    answer = get(f"{base}/admin/realms/{realm}/clients?{query}", access_token)
    if not isinstance(answer, list):
        raise SystemExit("read_admin: the admin API did not answer a client list.")

    if len(answer) >= CLIENT_LIMIT:
        raise SystemExit(
            f"read_admin: the realm answered {len(answer)} clients against a "
            f"ceiling of {CLIENT_LIMIT}, so this may not be all of them. A "
            "realm that large is one this gate refuses to judge rather than "
            "truncate — every per-client obligation is satisfied by the "
            "clients nobody fetched.")
    return answer


def fetch(values: dict[str, str]) -> dict:
    """The realm representation with its clients joined in, exactly as an export holds them."""
    base = values[BASE_URL]
    realm = urllib.parse.quote(values[REALM], safe="")
    access = token(base, values[REALM], values[CLIENT_ID], values[CLIENT_SECRET])

    representation = get(f"{base}/admin/realms/{realm}", access)
    if not isinstance(representation, dict):
        raise SystemExit("read_admin: the admin API did not answer a realm representation.")

    every_client = clients(base, realm, access)

    # The realm representation carries no `clients` key of its own, so this
    # adds rather than overwrites. Asserting that keeps a future Keycloak
    # answering a partial list from being silently replaced by a full one, or
    # the reverse.
    if "clients" in representation:
        raise SystemExit(
            "read_admin: the realm representation already carries a clients "
            "key. Joining the client list would overwrite it, and which of the "
            "two the checker should judge is a decision this file must not "
            "take on its own.")
    representation["clients"] = every_client

    # PROJECTED BEFORE IT IS WRITTEN, not only before it is judged. Redacting
    # here was the first answer and it was the weaker one: it still wrote the
    # whole admin representation to a file on a CI runner, so any
    # secret-bearing field Keycloak adds that `CREDENTIAL_KEYS` does not list
    # would land in `$RUNNER_TEMP` for a later step to read or an operator to
    # print. The narrowing has to happen on the way OUT of the fetch, not on
    # the way in to the judgement.
    #
    # Redaction still runs first, and the order is deliberate: a credential
    # under one of the six projected keys — a realm that put a secret in a
    # client attribute — is removed before the projection can carry it through.
    # `load_realm` does both again, because either file can be handed a
    # document the other never touched.
    return realm_check.judged(realm_check.redact(representation))


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", required=True, type=Path,
                        help="where to write the realm document realm_check.py will read")
    args = parser.parse_args(argv[1:])

    values = environment()
    try:
        realm = fetch(values)
    except (OSError, json.JSONDecodeError, UnicodeDecodeError) as error:
        # A realm that cannot be read is not a realm that passed. This is the
        # canary's rule for an unreachable Prometheus, one artefact over.
        #
        # WIDER THAN `URLError`, and the width is the point. `urllib` wraps
        # only connection-phase failures; a reset or a timeout during
        # `response.read()` raises the bare `OSError`, and a proxy answering a
        # 200 with an HTML error page raises out of `json.loads`. Both are the
        # commonest way this step fails and both used to escape as a
        # traceback — loud, and wrong about what happened. `URLError` is an
        # `OSError`, so this still covers it.
        #
        # THE ERROR IS PRINTED AND THE VALUES ARE NOT. Only the base URL is
        # named, on `token`'s rule: a failed exchange can echo the request form
        # back, and that form carries the client secret.
        raise SystemExit(
            f"read_admin: {values[BASE_URL]} could not be read: "
            f"{type(error).__name__}: {error}. A rollout that cannot see its "
            "own realm has to stop rather than guess.") from error

    args.out.write_text(json.dumps(realm, indent=2), encoding="utf-8")
    print(f"read_admin: wrote {values[REALM]} with "
          f"{len(realm.get('clients', []))} client(s) to {args.out}.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
