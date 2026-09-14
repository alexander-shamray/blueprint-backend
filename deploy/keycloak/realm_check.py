#!/usr/bin/env python3
"""Every realm this platform is pointed at holds §11's token obligations.

§11.3 states the access-token lifetime, ADR-033 composes the revocation bound
out of it, and ADR-034 issues the browser no refresh token. All three are
realm settings, and every chart points at a deployed realm no file in this
repository holds — so the obligations are asserted against §14.1's Compose
export in CI and against a live realm at deploy time by one predicate, because
an export and the admin API's `RealmRepresentation` are the same document.
`read_admin.py` fetches; this file decides. The realm kind is an argument with
no default, because one obligation inverts on it: §11.2's password grant is on
in the Compose realm and off in a deployed one, and a check that guessed which
realm it was looking at would pass a production realm on the local realm's
terms. The lifetime is read out of `AuthenticationExtensions`'s
`AccessTokenLifetime` (ADR-040) rather than restated. Stdlib only, on the
licence gate's terms.

    py -3.12 deploy/keycloak/realm_check.py check --realm deploy/compose/keycloak/realm-export.json --kind local
    py -3.12 deploy/keycloak/realm_check.py inputs
"""

from __future__ import annotations

import argparse
import ipaddress
import json
import re
import sys
from pathlib import Path
from urllib.parse import urlsplit

ROOT = Path(__file__).resolve().parents[2]

LIFETIME_SOURCE = "src/BuildingBlocks/Common.Web/AuthenticationExtensions.cs"

# The default subject, and the only realm this repository owns. A constant
# rather than CI's argv, so that the reads-direction self-check below can see
# it; the deploy path always passes `--realm`, because the realm it checks is
# one nothing here holds a copy of.
COMPOSE_REALM = "deploy/compose/keycloak/realm-export.json"

# What this gate reads outside its own tree, declared beside the reads, and
# built from the constants above rather than restated: a list spelling those
# paths a second time would make the reads direction agree with its own copy.
# `deploy/helm` is read by the suite rather than by the gate, and is declared
# all the same, because a subject test that does not gate the files it
# validates is a subject test in name only.
CHART_VALUES = "deploy/helm"

# The deploy workflow is an input even though nothing here opens it at check
# time: `deploy.yml` is the caller that derives the authority, fetches the
# realm and judges it, and its rollout job runs on `workflow_dispatch` alone,
# so a change that deleted or reordered those steps would otherwise reach
# `main` with this gate skipped. A gate that cannot see its own invocation is
# one that stops being invoked quietly.
DEPLOY_WORKFLOW = ".github/workflows/deploy.yml"

# The canary plan is an input since ADR-043: `realm.yml`'s scheduled job loops
# over `canary.py workloads` to find every release whose realm it judges, so a
# change to the plan or to that subcommand changes the subject of this gate,
# and a subtree the triggers do not name reaches `main` with the gate skipped.
CANARY_PLAN = "deploy/canary"

SOURCE_INPUTS = [LIFETIME_SOURCE, COMPOSE_REALM, CHART_VALUES, DEPLOY_WORKFLOW, CANARY_PLAN]

WORKFLOW_PATH = ".github/workflows/realm.yml"

# The repository variable that opts the scheduled job in (ADR-043), declared
# here because the workflow's `if:` and the README both spell it and the suite
# reads this constant out of the workflow rather than trusting either. A
# repository variable rather than an Environment one, because a job-level `if`
# is evaluated before the job enters its Environment.
SCHEDULE_OPT_IN = "REALM_CHECK_SCHEDULED"

# This gate's own tree, subtracted from the reads direction: its own path
# appears in the docstring's invocation lines, and
# `check_workflow_covers_inputs` already adds it to what both triggers must
# cover.
OWN_TREE = "deploy/keycloak"

# A quoted path with at least one separator, and the repository root joined
# with a module constant. `check_source_inputs_covers_reads` needs both; the
# argument for two scans is there rather than here.
PATH_LITERAL = r'"((?:[A-Za-z0-9_.-]+/)+[A-Za-z0-9_.-]+)"'
# `root / CONSTANT` and `root / module.CONSTANT` alike, because the qualified
# form is how a sibling module names one of this file's paths.
ROOT_USE = r"(?:^|[^A-Za-z0-9_.])(?:ROOT|root)\s*/\s*(?:[a-z_][A-Za-z0-9_]*\.)?([A-Z_][A-Z0-9_]*)"

# The two triggers `realm.yml` must carry, named so a failure can say which.
TRIGGERS = ("pull_request", "push")

# `web-app` is §11.2's browser client, restated here rather than inferred from
# its flags: every flag that would identify it is one of the settings judged
# below, and a subject derived from the predicate passes vacuously the moment
# the predicate is what has gone wrong.
BROWSER_CLIENT = "web-app"

# `mobile-app` is the native client; no chapter fixes the name, so this is its
# first spelling. The same derivation trap applies as to BROWSER_CLIENT.
MOBILE_CLIENT = "mobile-app"

# The two entries Keycloak accepts in `webOrigins` that are not origins, named
# because the check below has to tell them apart rather than refuse both alike.
# `*` answers every page on the internet; `+` means the origins this client's
# own redirect URIs imply, which is a real setting on a client redirected to an
# http(s) URL and an empty one on a client redirected to a custom scheme.
ORIGIN_WILDCARD = "*"
ORIGIN_FROM_REDIRECTS = "+"

# The schemes a redirect URI has to be on for `+` to derive anything from it. A
# browser sends the origin of the page that made the request, and no page is
# served from `blueprint://`.
WEB_SCHEME_NAMES = ("http", "https")

# What a host may contain once a browser has finished with it, for the two
# schemes a browser normalises. A set of what is right rather than a list of
# wrong spellings, because a gate that enumerates what is wrong loses to a
# generator of wrong things — the argument `Gateway.Api` makes about origins.
# Deliberately narrower than WHATWG: it has to admit every host a realm will
# really name, and refusing anything else costs an operator one hand-edit,
# where admitting a spelling a browser rewrites costs a sign-in that fails
# with nothing saying why.
HOST_CHARACTERS = re.compile(r"[a-z0-9._-]+")

# A redirect URI's scheme, when it has one at all: Keycloak resolves a
# relative redirect against the client's `rootUrl` before taking an origin
# from it, so `/*` on a client with a `rootUrl` is an ordinary configuration
# whose origin this gate cannot see. Matching the scheme rather than the
# prefix is what lets `check_web_origins` tell "provably derives nothing" from
# "cannot tell", and refuse only the first.
REDIRECT_SCHEME = re.compile(r"^([A-Za-z][A-Za-z0-9+.\-]*):")

# Dropped from the canonical origin when the scheme implies them, because a
# browser drops them: `https://id.example.com:443` is never what arrives in an
# `Origin` header, so a realm entry spelling it matches nothing. Keycloak
# compares that header as text and has no opinion about a custom scheme's
# port, so only the two web schemes have a default to drop.
DEFAULT_PORTS = {"http": 80, "https": 443}

# A realm representation carries every confidential client's secret, and this
# gate needs none of them: the admin API answers `secret` for each such client
# and §14.1's own export carries one, so the credential is removed at the door
# rather than avoided by every message downstream. Redacting at load makes the
# property structural rather than a rule every future author must remember.
CREDENTIAL_KEYS = frozenset({
    "secret", "password", "value", "privateKey", "publicKey", "certificate",
    "bindCredential", "clientSecret", "adminPassword",
})
REDACTED = "<redacted by realm_check>"

# Exactly what the checks read, and the gate judges nothing else. Redacting
# the credential keys leaves the object carrying every other field of a realm,
# with the property depending on a deny-list staying complete; `judged` builds
# a new document out of the fields below instead, so what the checks hold has
# no credential in it and a Keycloak version that adds a secret-bearing field
# changes nothing here. `webOrigins` is the one field whose absence is the
# defect rather than a widening: Keycloak grants no origin by default, and a
# page whose token exchange gets no `Access-Control-Allow-Origin` discards
# the token unread.
REALM_FIELDS = ("accessTokenLifespan", "revokeRefreshToken", "refreshTokenMaxReuse")
CLIENT_FIELDS = ("clientId", "standardFlowEnabled", "implicitFlowEnabled",
                 "directAccessGrantsEnabled", "publicClient", "redirectUris",
                 "defaultClientScopes", "webOrigins")
CLIENT_ATTRIBUTES = ("use.refresh.tokens", "access.token.lifespan",
                     "pkce.code.challenge.method")

LOCAL = "local"
DEPLOYED = "deployed"
KINDS = (LOCAL, DEPLOYED)

# The chart value that decides which realm every host validates against, and
# the two environment variables `read_admin.py` reads. `authority` derives the
# second pair from the first so the two cannot name different realms: a
# rollout that checked realm A and installed realm B would pass this gate and
# leave the workload pointed at the realm it was filed about.
AUTHORITY_VALUE = ("identity", "authority")
REALMS_SEGMENT = "/realms/"

# What Helm's `strvals` parser reads as structure rather than as text. The
# deploy step passes the derived authority as `--set-string
# identity.authority=<value>`, and `strvals` splits on a comma whatever the
# shell quoting is, so `https://host/realms/x,image.registry=attacker.example`
# is two assignments, one of which nobody checked. Refusing them here is what
# makes the deploy step's `--set-string` safe, as `canary.py validate-tag`
# does for the tag.
STRVALS_METACHARACTERS = ",\\{}[]="

# The environment `read_admin.py` reads, declared here and imported there —
# `deploy/canary`'s direction, where `read_prometheus.py` imports from
# `canary.py` and never the reverse — because the ones `authority` writes
# have to be one statement with the ones the fetcher reads. Named in a tuple
# and unpacked, because a credential-shaped constant assigned a literal is the
# shape §15.1's secret scan exists to catch, and these hold names, never
# values.
ENVIRONMENT = (
    "KEYCLOAK_BASE_URL",
    "KEYCLOAK_REALM",
    "KEYCLOAK_CHECK_CLIENT_ID",
    "KEYCLOAK_CHECK_CLIENT_SECRET",
)
BASE_URL_VARIABLE, REALM_VARIABLE = ENVIRONMENT[0], ENVIRONMENT[1]

# Every flag this gate reads, and it reads no other. Keycloak serialises these
# as JSON booleans in both an export and an admin-API answer, so anything else
# is a hand-edited realm — and `check_flags_are_booleans` refuses one rather
# than comparing it. The comparisons below are all identity tests against
# `True` or `False`, which means a string `"true"` would be neither enabled nor
# disabled but *unjudged*, and an unjudged flag is a pass.
FLAGS = (
    "implicitFlowEnabled",
    "standardFlowEnabled",
    "directAccessGrantsEnabled",
)


def read_access_token_lifetime(root: Path = ROOT) -> int:
    """The 300, taken out of `AuthenticationExtensions` rather than written here.

    Raises rather than defaulting. A gate that cannot find the number it is
    checking against has to say so — substituting 300 would make the read
    decorative, which is the shape ADR-033 was written to withdraw.
    """
    source = root / LIFETIME_SOURCE
    try:
        text = source.read_text(encoding="utf-8")
    except OSError as error:
        raise SystemExit(f"realm-gate: {LIFETIME_SOURCE} is not readable: {error}") from error

    # Comments are stripped first and the member is anchored: the file's doc
    # comments name `AccessTokenLifetime`, so a prose mention plus a
    # reformatted declaration would otherwise leave exactly one match, in the
    # comment, and this gate would assert a number the platform does not hold.
    code = "\n".join(
        line for line in text.splitlines() if not line.lstrip().startswith("//"))
    matches = re.findall(
        r"readonly\s+TimeSpan\s+AccessTokenLifetime\s*=\s*"
        r"TimeSpan\.FromSeconds\(\s*(\d+)\s*\)", code)
    if len(matches) != 1:
        raise SystemExit(
            f"realm-gate: {LIFETIME_SOURCE} declares AccessTokenLifetime "
            f"{len(matches)} time(s), expected exactly one. The lifetime a realm "
            "owes is read from that declaration, so this gate cannot say what "
            "the realm owes and must not report a pass.")
    return int(matches[0])


def redact(node: object) -> object:
    """The same document with every credential-shaped value replaced.

    Recursive, because a realm nests them — a client's `secret`, a user's
    `credentials[].value`, an LDAP component's `bindCredential` — and the key
    decides, not the value, so nothing here guesses what a secret looks like.
    `value` is a generic name and is redacted wherever it appears, which is the
    right way round for a document this gate only reads flags out of.
    """
    if isinstance(node, dict):
        return {key: (REDACTED if key in CREDENTIAL_KEYS else redact(value))
                for key, value in node.items()}
    if isinstance(node, list):
        return [redact(item) for item in node]
    return node


def judged(document: dict) -> dict:
    """A realm reduced to the fields this gate reads, and nothing else.

    Absence is preserved rather than defaulted, because half the checks turn on
    it — an absent `use.refresh.tokens` is ADR-034 violated. A client that is
    not an object survives as itself, because refusing one is
    `check_flags_are_booleans`'s job.
    """
    realm = {key: document[key] for key in REALM_FIELDS if key in document}

    clients: list = []
    for client in document.get("clients", []) if isinstance(document.get("clients"), list) else []:
        if not isinstance(client, dict):
            clients.append(client)
            continue
        narrowed = {key: client[key] for key in CLIENT_FIELDS if key in client}
        attributes = client.get("attributes")
        if isinstance(attributes, dict):
            narrowed["attributes"] = {
                key: attributes[key] for key in CLIENT_ATTRIBUTES if key in attributes}
        elif "attributes" in client:
            narrowed["attributes"] = attributes
        clients.append(narrowed)

    if "clients" in document:
        realm["clients"] = clients
    return realm


def authority_of(values: dict) -> str:
    """`identity.authority` out of a release's own values, or a stop.

    The subject is `helm get values <release> -o json`, which answers what the
    running release was installed with — and what `-f stable-values.yaml`
    reinstalls two steps later. So the realm this gate reads is the realm the
    rollout is about to point every host at, by construction rather than by two
    variables somebody keeps in step.
    """
    node: object = values
    for key in AUTHORITY_VALUE:
        if not isinstance(node, dict) or key not in node:
            path = ".".join(AUTHORITY_VALUE)
            raise SystemExit(
                f"realm-gate: the release's values carry no {path}. Every chart "
                "requires it (§15.4), so a release without one is a release "
                "this gate cannot check rather than one that passes.")
        node = node[key]

    if not isinstance(node, str) or not node.strip():
        raise SystemExit(
            f"realm-gate: identity.authority is {node!r}, which names no realm.")
    return node.strip()


def split_authority(authority: str) -> tuple[str, str]:
    """An OIDC authority into the server root and the realm name.

    Keycloak's admin endpoints sit beside `/realms` rather than under it, so
    the two halves are what `read_admin.py` needs and neither is the authority.
    It refuses rather than guesses, and each refusal names the realm this gate
    would otherwise read wrongly.
    """
    # Whitespace anywhere is refused, and a newline is the reason: the two
    # results become `NAME=value` lines appended to `$GITHUB_ENV`, where a
    # newline ends one assignment and starts another, and the value comes from
    # `helm get values` — whoever can edit a release must not be able to set
    # this job's environment.
    if any(character.isspace() for character in authority):
        raise SystemExit(
            f"realm-gate: identity.authority is {authority!r}, which contains "
            "whitespace. This value becomes a NAME=value line in $GITHUB_ENV, "
            "where a newline starts a second assignment.")

    if not authority.startswith("https://"):
        raise SystemExit(
            f"realm-gate: identity.authority is {authority!r}. The admin API "
            "carries a bearer token that can read every client secret in the "
            "realm, so it is https or nothing.")
    if "?" in authority or "#" in authority:
        raise SystemExit(
            f"realm-gate: identity.authority is {authority!r}. An admin path "
            "appended to a URL carrying a query or a fragment lands inside it.")

    found = sorted(set(authority) & set(STRVALS_METACHARACTERS))
    if found:
        raise SystemExit(
            f"realm-gate: identity.authority is {authority!r}, which contains "
            f"{''.join(found)!r}. Helm's strvals parser reads those as "
            "structure, and this value is passed to `--set-string` — a comma "
            "would make one assignment into two, and the second would be an "
            "override nobody checked.")

    root, separator, realm = authority.rstrip("/").partition(REALMS_SEGMENT)
    if not separator or not realm:
        raise SystemExit(
            f"realm-gate: identity.authority is {authority!r}, which has no "
            f"{REALMS_SEGMENT}<name> segment. Keycloak's admin endpoints sit "
            "beside that segment, so this gate cannot say where to read.")
    if "/" in realm:
        raise SystemExit(
            f"realm-gate: the realm in {authority!r} is {realm!r}, which is not "
            "a single path segment.")
    return root, realm


def clients_of(realm: dict) -> list[dict]:
    """The realm's clients, or an empty list when the key is absent or wrong.

    Absent and empty are the same answer to every caller here — there is
    nothing to judge — and `check_realm` refuses that answer outright.
    """
    clients = realm.get("clients")
    return clients if isinstance(clients, list) else []


def check_realm(realm: dict, kind: str, lifetime: int) -> list[str]:
    """The obligations of §11.3, ADR-033 and ADR-034, against one realm document.

    Every check that follows names a client or a realm key, so the first thing
    established is that there are clients to name. A realm document with no
    `clients` array satisfies "no client overrides the lifetime" and "no client
    enables the implicit flow" perfectly, and answering that with a pass is the
    vacuous-gate failure this repository repeats most.
    """
    if kind not in KINDS:
        return [f"the realm kind {kind!r} is not one of {', '.join(KINDS)}"]

    problems: list[str] = []
    clients = clients_of(realm)
    if not clients:
        return [
            "the realm document carries no clients array, so every per-client "
            "obligation below would pass without judging anything. This is a "
            "malformed or truncated realm, not a compliant one"
        ]

    named = [c for c in clients if isinstance(c, dict) and c.get("clientId") == BROWSER_CLIENT]
    if len(named) != 1:
        problems.append(
            f"the realm declares the browser client {BROWSER_CLIENT!r} "
            f"{len(named)} time(s), expected exactly one. ADR-034's "
            "refresh-token obligation is a property of that client and cannot "
            "be checked without it")

    mobile = [c for c in clients if isinstance(c, dict) and c.get("clientId") == MOBILE_CLIENT]
    if len(mobile) != 1:
        problems.append(
            f"the realm declares the native client {MOBILE_CLIENT!r} "
            f"{len(mobile)} time(s), expected exactly one. Its refresh-token "
            "obligation is a property of that client and cannot be checked "
            "without it")

    problems += check_flags_are_booleans(clients)
    problems += check_lifetime(realm, clients, lifetime)
    problems += check_implicit_flow(clients)
    if named:
        problems += check_browser_client(named[0], kind)
        problems += check_web_origins(named[0], BROWSER_CLIENT)
    if mobile:
        problems += check_mobile_client(mobile[0])
        problems += check_web_origins(mobile[0], MOBILE_CLIENT)
        problems += check_refresh_token_rotation(realm)
    return problems


def check_flags_are_booleans(clients: list[dict]) -> list[str]:
    """A flag that is not a boolean is refused rather than compared.

    Absent is allowed here and judged where it matters — an absent flag is
    Keycloak's default and every check below decides for itself whether that
    default satisfies the obligation. What this refuses is a present value of
    the wrong type, because every comparison in this file is an identity test:
    `"true"` is neither `True` nor `False`, so it would fall through
    `check_implicit_flow` as though the flow were off.
    """
    problems: list[str] = []
    for client in clients:
        if not isinstance(client, dict):
            problems.append(
                f"the clients array holds a {type(client).__name__} where a "
                "client object belongs, so every obligation below would skip it")
            continue
        for flag in FLAGS:
            # `null` is not a wrong type here, it is an unstated one, and what
            # an unstated flag means differs per obligation, so it is left to
            # the check that reads it.
            if flag in client and client[flag] is not None and not isinstance(client[flag], bool):
                problems.append(
                    f"client {client.get('clientId')!r} sets {flag} to a "
                    f"{type(client[flag]).__name__} rather than a boolean. "
                    "Every check here compares against true or false, so a "
                    "value of any other type would be neither and would pass "
                    "unjudged")
    return problems


def check_lifetime(realm: dict, clients: list[dict], lifetime: int) -> list[str]:
    """The realm's lifetime is the chapter's, and no client overrides it.

    Two settings, because Keycloak resolves the client attribute over the realm
    value: a realm at 300 with one client at 18000 issues five-hour tokens to
    that client, and the realm-level assertion alone would call it compliant.
    """
    problems: list[str] = []
    declared = realm.get("accessTokenLifespan")
    if declared != lifetime:
        problems.append(
            f"accessTokenLifespan is {declared!r}, and {LIFETIME_SOURCE} "
            f"declares {lifetime}. ADR-033's revocation bound is that number "
            "plus the 30-second ClockSkew, so a realm that disagrees widens a "
            "window no chapter re-states")

    for client in clients:
        if not isinstance(client, dict):
            continue
        attributes = client.get("attributes")
        if not isinstance(attributes, dict):
            continue
        override = attributes.get("access.token.lifespan")
        if override is None or not str(override).strip():
            # A blank string is not an override: Keycloak stores "" for an
            # advanced setting filled in and then cleared in the console, which
            # is the ordinary way an operator undoes the mistake this check
            # exists to catch.
            continue

        seconds = str(override).strip()
        if not seconds.lstrip("-").isdigit():
            # The value is named, not echoed: `access.token.lifespan` is a
            # token-shaped name, so a message quoting whatever a realm put
            # there is a credential-shaped read reaching a log.
            problems.append(
                f"client {client.get('clientId')!r} sets "
                "access.token.lifespan to something that is not a number of "
                "seconds. This gate cannot say what lifetime that client "
                "issues, which is not the same as saying it is the realm's")
        # An override equal to the realm value is not a finding. It is
        # redundant rather than wrong, and failing it would make this gate
        # refuse a realm that holds the obligation it exists to enforce.
        elif int(seconds) != lifetime:
            # The parsed number, not the source text: a count of seconds cannot
            # carry anything else.
            problems.append(
                f"client {client.get('clientId')!r} sets "
                f"access.token.lifespan to {int(seconds)} seconds, overriding "
                f"the realm's {lifetime}. A client-level lifespan is the "
                "misconfiguration this gate was filed for")
    return problems


def check_implicit_flow(clients: list[dict]) -> list[str]:
    """No client enables the implicit flow, which is what makes the other lifespan moot.

    `accessTokenLifespanForImplicitFlow` is 900 in the shipped realm and is not
    asserted anywhere, because nothing can reach it. That is only true while no
    client enables the flow, so this is the check that keeps the silence about
    the other setting honest rather than a gap.
    """
    problems: list[str] = []
    for client in clients:
        if isinstance(client, dict) and client.get("implicitFlowEnabled") is True:
            problems.append(
                f"client {client.get('clientId')!r} enables the implicit flow. "
                "accessTokenLifespanForImplicitFlow then governs its tokens, "
                "and no chapter states a value for it")
    return problems


def check_browser_client(client: dict, kind: str) -> list[str]:
    """ADR-034's refresh-token rule, and §11.2's password grant.

    The refresh-token attribute is checked for presence and not only for value.
    Keycloak's default is to issue refresh tokens on the standard flow, so an
    absent `use.refresh.tokens` is the violation spelled as a silence — reading
    a missing attribute as compliant would make the one setting ADR-034 rests
    on optional.
    """
    problems: list[str] = []
    attributes = client.get("attributes")
    attributes = attributes if isinstance(attributes, dict) else {}

    refresh = attributes.get("use.refresh.tokens")
    if refresh is None:
        problems.append(
            f"client {BROWSER_CLIENT!r} declares no use.refresh.tokens "
            "attribute. Keycloak issues refresh tokens on the standard flow by "
            "default, so the absence is ADR-034 violated and not unspecified")
    elif str(refresh).lower() != "false":
        problems.append(
            f"client {BROWSER_CLIENT!r} sets use.refresh.tokens to something "
            "other than \"false\". ADR-034 gives the browser an access token "
            "and no refresh token")

    # The positive half. Without it the attribute above holds for the wrong
    # reason: a client with no standard flow issues no refresh token because it
    # issues nothing at all, and the check would pass on a broken realm.
    if client.get("standardFlowEnabled") is not True:
        problems.append(
            f"client {BROWSER_CLIENT!r} does not enable the standard flow. "
            "The refresh-token obligation above then holds because the client "
            "mints no token at all, which is not the guarantee ADR-034 states")

    grants = client.get("directAccessGrantsEnabled")
    if kind == DEPLOYED and grants is not False:
        problems.append(
            f"client {BROWSER_CLIENT!r} has directAccessGrantsEnabled="
            f"{grants!r}. Section 11.2 documents the password grant as a local "
            "affordance and says a deployed realm turns it off")
    if kind == LOCAL and grants is not True:
        problems.append(
            f"client {BROWSER_CLIENT!r} has directAccessGrantsEnabled="
            f"{grants!r}. Section 14.1's documented login is a password grant, "
            "so the local realm needs it and the README's curl would not work")
    return problems


def check_mobile_client(client: dict) -> list[str]:
    """The native client's whole shape, not only the refresh-token half of it.

    `web-app` runs in a browser, where a refresh token is reachable by any
    script on the origin — ADR-034's reason to withhold one. `mobile-app` is a
    platform-installed app with no equivalent surface, so the realm issues it
    a refresh token and what has to hold instead is rotation
    (`check_refresh_token_rotation`). The password-grant check takes no realm
    kind, because nothing documents a password-grant login for the native
    client in either realm — §14.1's curl recipe is `web-app`'s alone. On a
    secretless public client a wrong answer to any of the remaining checks is
    a live exploit rather than a preference, and each message says which.
    """
    problems: list[str] = []
    attributes = client.get("attributes")
    attributes = attributes if isinstance(attributes, dict) else {}

    refresh = attributes.get("use.refresh.tokens")
    if refresh is None:
        problems.append(
            f"client {MOBILE_CLIENT!r} declares no use.refresh.tokens "
            "attribute. Keycloak's default already issues one on the standard "
            "flow, so the realm happens to comply today — but an unstated "
            "attribute is the same silence ADR-034 refuses for the browser, "
            "read the other way round, and a future Keycloak default is not "
            "this gate's to trust")
    elif str(refresh).lower() != "true":
        problems.append(
            f"client {MOBILE_CLIENT!r} sets use.refresh.tokens to something "
            "other than \"true\". The native client has no way to obtain a "
            "fresh access token without one once the short-lived one expires")

    # The positive half, on check_browser_client's own reasoning: without the
    # standard flow there is no authorization-code exchange and therefore no
    # refresh token either, whatever the attribute above says.
    if client.get("standardFlowEnabled") is not True:
        problems.append(
            f"client {MOBILE_CLIENT!r} does not enable the standard flow. "
            "The refresh-token obligation above then holds because the "
            "client mints no token at all, which is not the guarantee this "
            "route was added for")

    grants = client.get("directAccessGrantsEnabled")
    if grants is not False:
        problems.append(
            f"client {MOBILE_CLIENT!r} has directAccessGrantsEnabled="
            f"{grants!r}. The native client authenticates through the "
            "authorization-code flow with PKCE; nothing documents a password "
            "grant for it, in either realm kind")

    method = attributes.get("pkce.code.challenge.method")
    if method != "S256":
        problems.append(
            f"client {MOBILE_CLIENT!r} sets pkce.code.challenge.method to "
            f"{method!r}, not \"S256\". This client holds no secret, so PKCE "
            "is the only thing standing between an intercepted authorization "
            "code and the attacker who intercepted it — 'plain' or absent "
            "both leave that door open")

    if client.get("publicClient") is not True:
        problems.append(
            f"client {MOBILE_CLIENT!r} has publicClient="
            f"{client.get('publicClient')!r}. It ships with no secret it "
            "could keep confidential — every install of the app carries the "
            "same one — so a client-secret posture is a credential baked "
            "into the binary rather than a real one")

    redirects = client.get("redirectUris")
    if redirects != ["blueprint://auth/callback"]:
        problems.append(
            f"client {MOBILE_CLIENT!r} has redirectUris={redirects!r}, not "
            "exactly [\"blueprint://auth/callback\"]. Widening this — a second "
            "entry, a wildcard, an http(s) scheme beside the custom one — is "
            "a code, and through it a refresh token, delivered wherever the "
            "wider pattern also matches")

    scopes = client.get("defaultClientScopes")
    if not isinstance(scopes, list) or "commerce-api" not in scopes:
        problems.append(
            f"client {MOBILE_CLIENT!r} does not hold commerce-api as a "
            "default client scope. A client-issued token with this scope "
            "missing carries no audience and no permission claim, and every "
            "request it makes is refused with nothing in this file's own "
            "checks to say why")
    return problems


def ends_in_a_number(host: str) -> bool:
    """WHATWG's test for whether a host is parsed as IPv4 rather than a domain.

    Written out rather than approximated, because `127.0x1` puts the hex
    prefix on the last label and `127.1.` leaves the last label empty, and
    both are rewritten by a browser. Drop one trailing empty label, then the
    host ends in a number if the last label is all digits or a `0x` prefix
    followed by hex digits or nothing; the caller then requires the dotted
    quad `ipaddress` prints.
    """
    labels = host.split(".")
    if labels and labels[-1] == "":
        labels = labels[:-1]
    if not labels:
        return False

    last = labels[-1].lower()
    if last.isdigit():
        return True
    return last.startswith("0x") and all(c in "0123456789abcdef" for c in last[2:])


def canonical_origin(text: object) -> str | None:
    """The origin a browser would send for this text, or `None` if it is not one.

    One equality against the canonical form rather than a list of ways a
    string can fail to be an origin, for the reason `Gateway.Api/Program.cs`
    gives: the ways a string can be an origin are finite and the ways it can
    fail are not, and Keycloak compares the `Origin` header as text, so an
    entry that is not character-for-character what a browser sends matches
    nothing. The authority is rebuilt from `hostname` and `port` rather than
    compared as it arrived, because surviving a round trip through the parser
    is what "already canonical" means. A custom scheme passes, unlike in the
    gateway, because a realm entry is a string Keycloak compares and a
    packaged WebView really does present one. The stdlib has no WHATWG host
    parser and this gate may not add a dependency, so what cannot be
    canonicalised here is refused: an IP literal must already be the form
    `ipaddress` prints, a non-ASCII host is refused because the punycode a
    browser sends is the spelling the realm needs, and on a special scheme the
    host must be drawn from `HOST_CHARACTERS`, while whether a host is an IPv4
    attempt is `ends_in_a_number`'s algorithm rather than an alphabet.
    """
    if not isinstance(text, str):
        return None
    try:
        parts = urlsplit(text)
        port = parts.port
    except ValueError:
        return None
    if not parts.scheme or parts.username is not None:
        return None

    host = parts.hostname
    if not host:
        return None

    # A browser sends punycode, never the Unicode form, so the Unicode form is
    # a spelling the realm can never match. `hostname` has already lowercased.
    if not host.isascii():
        return None

    scheme = parts.scheme.lower()

    if ":" in host:
        # An IPv6 literal, which an origin brackets and `hostname` does not.
        try:
            address = ipaddress.IPv6Address(host)
        except ValueError:
            return None
        # `ipaddress` accepts a zone identifier and a URL may not carry one, so
        # a scoped address round-trips through this function unchanged while
        # being an origin no browser can send.
        if address.scope_id is not None or str(address) != host:
            return None
    elif scheme in WEB_SCHEME_NAMES and not HOST_CHARACTERS.fullmatch(host):
        # A browser normalises the host of a special scheme before sending it —
        # percent escapes are decoded, among other things — so anything outside
        # the set above is a spelling that arrives in an `Origin` header as
        # something else. A custom scheme's host is opaque and is left alone.
        return None

    if ":" not in host and ends_in_a_number(host):
        # A host WHATWG parses as IPv4 is serialised as a dotted quad, so one
        # reaching here has to be that quad already. `ipaddress` refuses
        # shorthand, leading zeros, hex octets, a trailing dot and bare
        # integers, which is exactly the set a browser would rewrite.
        try:
            if str(ipaddress.IPv4Address(host)) != host:
                return None
        except ValueError:
            return None

    authority = f"[{host}]" if ":" in host else host
    if port is not None and port != DEFAULT_PORTS.get(scheme):
        authority = f"{authority}:{port}"

    canonical = f"{scheme}://{authority}"
    return canonical if canonical == text else None


def redirects_cannot_imply_an_origin(client: dict) -> bool:
    """True only where `+` provably derives nothing from this client.

    Asked in the direction that fails silent rather than loud: a relative
    redirect like `/*` is resolved against the client's `rootUrl` before an
    origin is taken from it, and this gate does not hold `rootUrl`, so a URI
    with no scheme is not evidence of anything and returns False. What remains
    provable is a client every one of whose redirect URIs is absolute and on a
    scheme no page is served from, where `+` resolves to nothing while reading
    in a console as though the question had been answered. A redirect of
    `https://` with no host is knowingly let through, because mirroring
    Keycloak's resolution means reimplementing one this repository does not
    own.
    """
    redirects = client.get("redirectUris")
    # Absent and empty are the answer, not "cannot tell": Keycloak drops `+`
    # and derives from what is left, and what is left is nothing.
    if redirects is None or (isinstance(redirects, list) and not redirects):
        return True
    # Malformed stays conservative: a `redirectUris` that is not an array is a
    # hand-edited realm, and what Keycloak would make of it is not this gate's
    # to predict.
    if not isinstance(redirects, list):
        return False
    for uri in redirects:
        if not isinstance(uri, str):
            return False
        scheme = REDIRECT_SCHEME.match(uri)
        if scheme is None or scheme.group(1).lower() in WEB_SCHEME_NAMES:
            return False
    return True


def check_web_origins(client: dict, client_id: str) -> list[str]:
    """The origin the client's own page sends, which is not its redirect URI.

    `redirectUris` is where Keycloak sends the authorization code;
    `webOrigins` decides whose script Keycloak will let read a token response,
    and the exchange is a second request the page makes itself. The failure
    produces no error anywhere: `application/x-www-form-urlencoded` is
    CORS-safelisted and a public client sends no `Authorization` header, so
    there is no preflight to fail — Keycloak mints a token, the browser
    discards the response, and the authorization code is spent. The obligation
    is asserted and the values are not, because what a packaged app's browser
    origin actually is comes out of `capacitor.config.ts` in a repository this
    one neither owns nor reads; what this gate can see is the shape, which is
    where the silent failures live. Both named clients are judged, because the
    obligation belongs to the token exchange and not to a platform; Keycloak's
    own built-in clients ship with no origins and need none.
    """
    problems: list[str] = []
    origins = client.get("webOrigins")

    if origins is None:
        return [
            f"client {client_id!r} declares no webOrigins. Keycloak answers a "
            "token request with no Access-Control-Allow-Origin unless the "
            "client grants one, and a browser discards a response it may not "
            "read — so the exchange succeeds, the authorization code is spent, "
            "and the sign-in fails with nothing on either side saying why"
        ]
    if not isinstance(origins, list):
        return [
            f"client {client_id!r} has a webOrigins that is not an array. "
            "Keycloak serialises this field as one in an export and in an "
            "admin-API answer alike, so anything else is a hand-edited realm "
            "rather than a configured one"
        ]
    if not origins:
        return [
            f"client {client_id!r} declares an empty webOrigins, which grants "
            "CORS to nothing at all. That is the absent field above written "
            "out, and it is worse for reading in a console as though the "
            "question had been answered"
        ]

    if ORIGIN_WILDCARD in origins:
        problems.append(
            f"client {client_id!r} declares {ORIGIN_WILDCARD!r} as a web "
            "origin, which answers every page on the internet with "
            "Access-Control-Allow-Origin: *. The token endpoint still demands "
            "an authorization code and its PKCE verifier, so this is a "
            "widening rather than an exploit — but it is one no client here "
            "needs, and naming the origins costs a line")

    if ORIGIN_FROM_REDIRECTS in origins and redirects_cannot_imply_an_origin(client):
        problems.append(
            f"client {client_id!r} declares {ORIGIN_FROM_REDIRECTS!r} as a "
            "browser origin, which means the origins its own redirectUris "
            "imply — and every one of them is absolute and on a scheme no page "
            "is served from, so they imply none. At runtime that is an empty "
            "webOrigins; in a console it reads as a setting")

    malformed = [index for index, origin in enumerate(origins)
                 if origin not in (ORIGIN_WILDCARD, ORIGIN_FROM_REDIRECTS)
                 and canonical_origin(origin) is None]
    if malformed:
        problems.append(
            f"client {client_id!r} has a webOrigins entry that is not a "
            f"browser origin at index {', '.join(str(i) for i in malformed)}. "
            "One is a scheme, a host, and a port only when it is not the "
            "scheme's default — exactly as a browser serialises it — because "
            "Keycloak compares the Origin header as text and anything else "
            "matches nothing. The value is deliberately not echoed: userinfo "
            "survives the authority form, and a guard that rejects a "
            "credential must not be the thing that publishes it")
    return problems


def check_refresh_token_rotation(realm: dict) -> list[str]:
    """Realm-wide rotation, which is what makes a stolen refresh token cost something.

    `revokeRefreshToken` and `refreshTokenMaxReuse` are realm settings with no
    per-client override, so they are checked once here rather than in
    `check_mobile_client`. Realm-wide is not a second exposure for anything
    that is actually exposed by it: ADR-034 leaves `web-app` no refresh token,
    `web-bff` runs no authorization-code flow and its client-credentials
    tokens come with none, and Keycloak's built-in consoles tolerate rotation.
    Called only once a mobile client has been found, on the reasoning
    `check_browser_client` is guarded by, because a rotation setting is only
    an obligation once a client exists for it to bind.
    """
    problems: list[str] = []

    revoke = realm.get("revokeRefreshToken")
    if revoke is not True:
        problems.append(
            f"revokeRefreshToken is {revoke!r}, and {MOBILE_CLIENT!r} is "
            "issued a refresh token. Without rotation, a refresh token copied "
            "once keeps minting access tokens for as long as the session "
            "lasts, whatever ADR-033's access-token bound says about the "
            "token it mints")

    reuse = realm.get("refreshTokenMaxReuse")
    if reuse != 0:
        problems.append(
            f"refreshTokenMaxReuse is {reuse!r}, not 0. A nonzero value lets "
            "a refresh token already rotated out of use be replayed that "
            "many more times, which is exactly the reuse window rotation "
            "exists to close")
    return problems


def code_of(source: str) -> str:
    """The source with its comments and its string paragraphs removed.

    A matcher in this tree matches the prose about the matcher whenever it is
    handed a file, and rewording lasts until the next sentence, so the scans
    are given code. Inline spans are removed first, because a span that opens
    and closes on one line leaves an even delimiter count that a toggle alone
    would keep. A regex rather than an `ast` walk, because what has to
    disappear is every string written as prose, and `ast` finds only the ones
    in a docstring position.
    """
    inline = re.compile(r'"""[\s\S]*?"""|\'\'\'[\s\S]*?\'\'\'')
    kept: list[str] = []
    inside = False
    for raw in source.splitlines():
        line = raw if inside else inline.sub("", raw)
        delimiters = line.count('"""') + line.count("'''")
        if inside:
            if delimiters % 2:
                inside = False
            continue
        if delimiters % 2:
            inside = True
            continue
        if line.lstrip().startswith("#"):
            continue
        kept.append(line)
    return "\n".join(kept)


def check_source_inputs_covers_reads() -> list[str]:
    """Every path this tree reads is covered by a SOURCE_INPUTS entry.

    The tree and not this file, because the suite reads a chart value this
    module never opens; grepping the sources, because the list and the reads
    drift the moment a check grows a second input. This direction establishes
    that nothing is read undeclared and says nothing about a declared entry no
    constant spells — `check_workflow_covers_inputs` is where a declared entry
    earns its keep. `WORKFLOW_PATH` is subtracted rather than matched: the
    trigger check adds it to what the workflow must cover, so declaring it
    here would make the workflow require itself twice.
    """
    source = "\n".join(
        module.read_text(encoding="utf-8")
        for module in sorted(Path(__file__).resolve().parent.glob("*.py")))

    # Two scans, because the literal scan requires a separator and cannot see
    # a read of `global.json` at the repository root, and dropping the
    # separator would match `access.token.lifespan`; the second scan looks at
    # how a path is used instead. Both read the stripped copy, because a
    # self-check that fails on its own explanation is one somebody deletes.
    code = code_of(source)

    quoted = set(re.findall(PATH_LITERAL, code))
    # A dot is an ordinary character in a path segment, because `Common.Web`
    # is a directory; `..` is subtracted because a relative link is not a
    # read.
    quoted = {r for r in quoted if ".." not in r.split("/")}
    # A path literal is one whose first segment exists at the repository root:
    # without that, the media types `read_admin.py` sends would be read as
    # reads, and a path nothing could open is not a read.
    quoted = {r for r in quoted if (ROOT / r.split("/")[0]).exists()}

    problems: list[str] = []
    used = set()
    for name in re.findall(ROOT_USE, code):
        value = globals().get(name)
        if isinstance(value, str):
            used.add(value)
        else:
            problems.append(
                f"this gate reads ROOT / {name}, which is not a module-level "
                "string constant, so the reads-direction check cannot say what "
                "path it is")

    reads = {r for r in quoted | used
             if r != WORKFLOW_PATH and r != OWN_TREE and not r.startswith(f"{OWN_TREE}/")}
    if not reads:
        return problems + [
            "the self-check found no path literal and no root-joined constant "
            "anywhere in deploy/keycloak, so it is the scan that is broken "
            "rather than the list that is complete"
        ]

    for read in sorted(reads):
        if not any(read == entry or read.startswith(f"{entry}/") for entry in SOURCE_INPUTS):
            problems.append(
                f"{read} is read by this gate and covered by no SOURCE_INPUTS "
                f"entry, so {WORKFLOW_PATH} will not run on a change to it")
    return problems


def check_workflow_covers_inputs(root: Path = ROOT) -> list[str]:
    """Both of the workflow's triggers cover every declared input.

    Both, and each named: a merged change that skips the gate on `main` is the
    same defect one branch later, and anchoring each `paths:` block to its
    event is what lets a failure name the event instead of a position. Text
    rather than YAML, on the licence gate's terms; the cost is that only the
    quoting styles below are recognised, and it is paid as a refusal rather
    than a pass.
    """
    workflow = root / WORKFLOW_PATH
    try:
        text = workflow.read_text(encoding="utf-8")
    except OSError as error:
        return [f"{WORKFLOW_PATH} is not readable: {error}"]

    problems = []
    for event in TRIGGERS:
        block = re.search(
            rf"^  {event}:\s*\n(?:(?!^  \S).)*?^    paths:\s*\n((?:^ *-[^\n]*\n)+)",
            text, re.MULTILINE | re.DOTALL)
        if block is None:
            problems.append(
                f"{WORKFLOW_PATH} has no {event} trigger with a paths list this "
                "check can read. That is not the same as saying its inputs are "
                "covered, so it is reported rather than skipped")
            continue

        patterns = re.findall(r"-\s*['\"]?([^'\"\s#]+)['\"]?", block.group(1))
        for entry in SOURCE_INPUTS + [OWN_TREE, WORKFLOW_PATH]:
            if not any(p == entry or p == f"{entry}/**" for p in patterns):
                problems.append(
                    f"{WORKFLOW_PATH}'s {event} trigger does not cover {entry}, "
                    "so a change to it would not run this gate")
    return problems


def load_realm(path: Path) -> dict:
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except OSError as error:
        raise SystemExit(f"realm-gate: {path} is not readable: {error}") from error
    except json.JSONDecodeError as error:
        raise SystemExit(f"realm-gate: {path} is not JSON: {error}") from error
    if not isinstance(document, dict):
        raise SystemExit(f"realm-gate: {path} is not a realm representation")

    # Projected before anything else holds it: every caller reaches a `print`
    # eventually, so the narrowing is here rather than at each of them.
    return judged(redact(document))


def fail(problems: list[str], subject: str) -> int:
    if not problems:
        return 0
    print(f"realm-gate: {len(problems)} problem(s) with {subject}:\n", file=sys.stderr)
    for problem in problems:
        print(f"  - {problem}", file=sys.stderr)
    return 1


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Check a Keycloak realm against section 11's obligations.")
    commands = parser.add_subparsers(dest="command", required=True)

    check = commands.add_parser("check", help="one realm document against the obligations")
    check.add_argument("--realm", type=Path, default=ROOT / COMPOSE_REALM,
                       help="a Keycloak realm export, or the file read_admin.py wrote; "
                            f"defaults to {COMPOSE_REALM}, the one realm this repository owns")
    check.add_argument("--kind", required=True, choices=KINDS,
                       help="which realm this is; it has no default because one obligation inverts on it")

    commands.add_parser("inputs", help="this gate's reads against its workflow's triggers")

    authority = commands.add_parser(
        "authority", help="the realm to read, derived from the release's own values")
    authority.add_argument("--values", required=True, type=Path,
                           help="`helm get values <release> -o json` for the release being rolled")
    authority.add_argument("--trusted-origin", required=True,
                           help="the identity provider this deployment is willing to authenticate "
                                "to; the release's authority must name it")

    args = parser.parse_args(argv[1:])

    if args.command == "authority":
        try:
            document = json.loads(args.values.read_text(encoding="utf-8"))
        except OSError as error:
            raise SystemExit(f"realm-gate: {args.values} is not readable: {error}") from error
        except json.JSONDecodeError as error:
            raise SystemExit(f"realm-gate: {args.values} is not JSON: {error}") from error
        if not isinstance(document, dict):
            raise SystemExit(f"realm-gate: {args.values} is not a values document")

        root, realm = split_authority(authority_of(document))

        # The origin is pinned and the realm is derived: deriving the realm
        # stops the gate checking a realm nobody is deploying to, and deriving
        # the origin would hand the release's values control of where this job
        # posts a client secret. Both sides are trimmed the same way, or
        # `https://host//realms/x` matches nothing an operator would type.
        # `expected` and not `trusted`, because
        # `py/clear-text-logging-sensitive-data` classifies by the name holding
        # a value and reads `trusted` as a secret.
        expected = args.trusted_origin.strip().rstrip("/")
        if root.rstrip("/") != expected:
            raise SystemExit(
                f"realm-gate: the release is running with an authority on "
                f"{root!r}, and this deployment names {expected!r}. Refusing "
                "to authenticate to an identity provider the deploy "
                "environment does not name.")

        # Repeated on the output, because what must never contain a newline is
        # what is written, and a refusal relaxed on the input side would move
        # that guarantee somewhere this line cannot see.
        for value in (root, realm):
            if any(character.isspace() for character in value):
                raise SystemExit(
                    f"realm-gate: refusing to write {value!r} into the "
                    "environment: whitespace in a NAME=value line starts a "
                    "second assignment.")

        # Two `NAME=value` lines, for `>> $GITHUB_ENV`. Nothing else goes to
        # stdout on this path, because anything else would be read as one.
        # The base URL is written even though it equals the trusted origin the
        # caller already holds: `read_admin.py` requires all four names in the
        # environment, and writing the one this file verified is what makes the
        # verification load-bearing rather than advisory.
        print(f"{BASE_URL_VARIABLE}={root}")
        print(f"{REALM_VARIABLE}={realm}")
        return 0

    if args.command == "inputs":
        problems = check_source_inputs_covers_reads() + check_workflow_covers_inputs()
        if code := fail(problems, "this gate's declared inputs"):
            return code
        print(f"realm-gate: {len(SOURCE_INPUTS)} declared input(s), all read and all triggered.")
        return 0

    lifetime = read_access_token_lifetime()
    realm = load_realm(args.realm)
    problems = check_realm(realm, args.kind, lifetime)
    if code := fail(problems, f"the {args.kind} realm in {args.realm}"):
        return code
    print(f"realm-gate: the {args.kind} realm in {args.realm} holds all "
          f"{len(clients_of(realm))} client(s) to a {lifetime}-second lifetime "
          "and the browser to no refresh token.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
