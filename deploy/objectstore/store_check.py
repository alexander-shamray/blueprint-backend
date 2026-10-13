#!/usr/bin/env python3
"""Ask a deployed object store whether it refuses the two image credential pairs a delete and a list.

§10.8 states that neither pair can list a bucket or delete; Compose proves it of the local store and this
proves it of a deployed one (ADR-110): `py -3.12 deploy/objectstore/store_check.py check`."""

from __future__ import annotations

import argparse
import datetime
import hashlib
import hmac
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid

ENDPOINT, BUCKET, REGION = "OBJECT_STORE_ENDPOINT", "OBJECT_STORE_BUCKET", "OBJECT_STORE_REGION"

# How the bucket is named in a request: in the path, or as a label of the host
# for a store reachable only that way (ADR-114). Unset reads as the first.
ADDRESSING = "OBJECT_STORE_ADDRESSING"
ADDRESSINGS = ("path", "virtual")

# The URL a green run requests, for a monitor outside GitHub to hear (ADR-114).
# A bearer token in its path, so it is a secret and never in a message.
HEARTBEAT = "OBJECT_STORE_HEARTBEAT_URL"

# One variable per half of each pair, named for the host that holds the pair
# (§10.8): the BFF's writes `uploads/`, Catalog's reads it and writes `products/`.
PAIRS = {
    "bff": ("OBJECT_STORE_BFF_ACCESS_KEY", "OBJECT_STORE_BFF_SECRET_KEY"),
    "catalog": ("OBJECT_STORE_CATALOG_ACCESS_KEY", "OBJECT_STORE_CATALOG_SECRET_KEY"),
}

REQUIRED = (ENDPOINT, BUCKET, REGION) + tuple(name for pair in PAIRS.values() for name in pair)

# Both prefixes a pair may write, because a delete refused under one and
# allowed under the other is the policy a copy-paste produces.
PREFIXES = ("uploads/", "products/")

TIMEOUT_SECONDS = 30

# The status and the code a refusal carries. A pair the store does not know
# answers 403 as well, with another code, and counting it as a refusal would
# pass a rotated secret as a policy that holds.
REFUSED = 403
REFUSAL_CODE = "AccessDenied"

# The suite points the check at a server on this host; nothing else is plain.
LOOPBACK = ("127.0.0.1", "localhost", "::1")

EMPTY_SHA256 = hashlib.sha256(b"").hexdigest()


class NoRedirect(urllib.request.HTTPRedirectHandler):
    """urllib re-sends a request's headers to a redirect's target, so a store, or
    anything answering for it, could be handed the signed request elsewhere. A
    redirect is an answer to judge, as read_admin.py treats one."""

    def redirect_request(self, *args, **kwargs):
        return None


OPENER = urllib.request.build_opener(NoRedirect)


class Refusal(Exception):
    """The store answered something that is not a verdict, or the check cannot ask."""


def _hmac(key: bytes, message: str) -> bytes:
    return hmac.new(key, message.encode("utf-8"), hashlib.sha256).digest()


def signing_key(secret: str, date: str, region: str, service: str = "s3") -> bytes:
    """SigV4's derived key."""
    key = _hmac(("AWS4" + secret).encode("utf-8"), date)
    for part in (region, service, "aws4_request"):
        key = _hmac(key, part)
    return key


def sign(
    method: str, url: str, access_key: str, secret_key: str, region: str, now: datetime.datetime
) -> dict[str, str]:
    """The headers of a SigV4-signed, bodiless request."""
    parts = urllib.parse.urlsplit(url)
    stamp = now.strftime("%Y%m%dT%H%M%SZ")
    date = stamp[:8]
    headers = {"host": parts.netloc, "x-amz-content-sha256": EMPTY_SHA256, "x-amz-date": stamp}
    names = ";".join(sorted(headers))
    query = "&".join(
        f"{urllib.parse.quote(k, safe='-_.~')}={urllib.parse.quote(v, safe='-_.~')}"
        for k, v in sorted(urllib.parse.parse_qsl(parts.query, keep_blank_values=True))
    )
    canonical = "\n".join(
        [method, urllib.parse.quote(parts.path or "/", safe="/-_.~"), query]
        + [f"{k}:{headers[k]}" for k in sorted(headers)]
        + ["", names, EMPTY_SHA256]
    )
    scope = f"{date}/{region}/s3/aws4_request"
    to_sign = "\n".join(["AWS4-HMAC-SHA256", stamp, scope, hashlib.sha256(canonical.encode("utf-8")).hexdigest()])
    signature = hmac.new(signing_key(secret_key, date, region), to_sign.encode("utf-8"), hashlib.sha256).hexdigest()
    headers["Authorization"] = (
        f"AWS4-HMAC-SHA256 Credential={access_key}/{scope}, SignedHeaders={names}, Signature={signature}"
    )
    return {("Host" if k == "host" else k): v for k, v in headers.items()}


def ask(method: str, url: str, access_key: str, secret_key: str, region: str) -> tuple[int, str]:
    """The status and body of one request; an HTTP error status is an answer, a network failure is not."""
    now = datetime.datetime.now(datetime.timezone.utc)
    request = urllib.request.Request(url, method=method, headers=sign(method, url, access_key, secret_key, region, now))
    try:
        with OPENER.open(request, timeout=TIMEOUT_SECONDS) as response:
            return response.status, response.read(65536).decode("utf-8", "replace")
    except urllib.error.HTTPError as error:
        return error.code, error.read(65536).decode("utf-8", "replace")
    except (urllib.error.URLError, TimeoutError, OSError) as error:
        path = urllib.parse.urlsplit(url).path
        raise Refusal(f"{method} {path}: the store could not be reached ({type(error).__name__})") from None


def code_of(body: str) -> str:
    match = re.search(r"<Code>([^<]+)</Code>", body)
    return match.group(1) if match else ""


def probes(endpoint: str, bucket: str, addressing: str = "path") -> list[tuple[str, str, str]]:
    """(label, method, url) for each request a pair must be refused.

    The delete names a key nothing holds, so a store that wrongly allows it
    removes nothing: authorization is decided before existence."""
    name = urllib.parse.quote(bucket, safe="")
    if addressing == "virtual":
        root = urllib.parse.urlsplit(endpoint)
        base, listing = f"{root.scheme}://{name}.{root.netloc}", "/?list-type=2&max-keys=1"
    else:
        base = f"{endpoint.rstrip('/')}/{name}"
        listing = "?list-type=2&max-keys=1"
    absent = f"check-{uuid.uuid4().hex}"
    found = [(f"delete under {prefix}", "DELETE", f"{base}/{prefix}{absent}") for prefix in PREFIXES]
    found.append(("list the bucket", "GET", f"{base}{listing}"))
    return found


def judge(label: str, status: int, body: str) -> str | None:
    """None when refused, otherwise what was found."""
    if status == REFUSED and code_of(body) == REFUSAL_CODE:
        return None
    if status == REFUSED:
        return f"{label}: refused with {code_of(body) or 'no code'}, which is not a policy refusing it ({REFUSAL_CODE})"
    return f"{label}: answered {status}, so the pair is not refused it"


def require_https(name: str, value: str, reason: str) -> None:
    url = urllib.parse.urlsplit(value)
    if url.scheme != "https" and not (url.scheme == "http" and url.hostname in LOOPBACK):
        raise Refusal(f"{name} must be an https URL, or http on a loopback host: {reason}")


def heartbeat(env: dict[str, str]) -> None:
    """Request the monitor's URL once. The message never carries the URL, whose path is its token."""
    if not env.get(HEARTBEAT):
        raise Refusal(f"not configured: {HEARTBEAT}")
    require_https(HEARTBEAT, env[HEARTBEAT], "its path is a token")
    try:
        with OPENER.open(urllib.request.Request(env[HEARTBEAT], method="GET"), timeout=TIMEOUT_SECONDS) as response:
            status = response.status
    except urllib.error.HTTPError as error:
        status = error.code
    except (urllib.error.URLError, TimeoutError, OSError) as error:
        raise Refusal(f"the heartbeat could not be sent ({type(error).__name__})") from None
    if not 200 <= status < 300:
        raise Refusal(f"the heartbeat was answered {status}")


def check(env: dict[str, str]) -> list[str]:
    """Every violation found; an empty list is a store that holds §10.8's sentence."""
    missing = [name for name in REQUIRED if not env.get(name)]
    if missing:
        raise Refusal("not configured: " + ", ".join(missing))
    require_https(ENDPOINT, env[ENDPOINT], "the pairs are sent to it")
    addressing = env.get(ADDRESSING) or ADDRESSINGS[0]
    if addressing not in ADDRESSINGS:
        raise Refusal(f"{ADDRESSING} must be one of {', '.join(ADDRESSINGS)} when set")
    violations = []
    for host, (access, secret) in PAIRS.items():
        for label, method, url in probes(env[ENDPOINT], env[BUCKET], addressing):
            status, body = ask(method, url, env[access], env[secret], env[REGION])
            found = judge(label, status, body)
            if found:
                violations.append(f"{host} pair, {found}")
    return violations


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("check", "heartbeat"))
    command = parser.parse_args(argv).command
    try:
        if command == "heartbeat":
            heartbeat(dict(os.environ))
            print("The heartbeat was sent.")
            return 0
        violations = check(dict(os.environ))
    except Refusal as error:
        print(f"::error::{error}")
        return 2
    for line in violations:
        print(f"::error::{line}")
    if violations:
        return 1
    print("Neither credential pair can delete or list the bucket.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
