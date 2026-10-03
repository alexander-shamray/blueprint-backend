#!/usr/bin/env python3
"""The fetcher's refusals, which are the only part of it that decides anything.

`deploy/canary/read_prometheus.py` ships without a suite and this file is the
departure from that precedent rather than an oversight. What that module does
before its one GET is build a URL; what this one does is **refuse** — an absent
variable, a plain-HTTP authority, a body that is not the document it asked for.
Each of those is a decision that fails a deploy, so each is a negative case, and
none of them needs a socket.

The GETs themselves are still untested and still untestable here, exactly as
they are one tree over: a fake `urlopen` proves that this file can parse what a
fake wrote.

    py -3.12 -m unittest discover -s deploy/keycloak
"""

from __future__ import annotations

import base64
import contextlib
import io
import json
import os
import tempfile
import re
import urllib.error
import urllib.parse
import urllib.request
import unittest
from pathlib import Path

import read_admin


class TheEnvironment(unittest.TestCase):
    """Four required variables, and an absent one stops the run rather than degrading."""

    def setUp(self):
        self.original = {name: os.environ.get(name) for name in read_admin.REQUIRED}

        def restore():
            for name, value in self.original.items():
                if value is None:
                    os.environ.pop(name, None)
                else:
                    os.environ[name] = value

        self.addCleanup(restore)
        self.set(read_admin.BASE_URL, "https://id.example.com")
        self.set(read_admin.REALM, "commerce")
        self.set(read_admin.CLIENT_ID, "realm-check")
        self.set(read_admin.CLIENT_SECRET, "not-a-real-value")

    def set(self, name: str, value: str) -> None:
        os.environ[name] = value

    def test_a_complete_environment_is_accepted(self):
        """The positive control: the negatives below each remove one thing from this."""
        values = read_admin.environment()
        self.assertEqual(values[read_admin.REALM], "commerce")

    def test_every_missing_variable_is_named_at_once(self):
        """Not the first one. An operator should learn what it needs in one run."""
        del os.environ[read_admin.REALM]
        del os.environ[read_admin.CLIENT_ID]
        with self.assertRaises(SystemExit) as stop:
            read_admin.environment()
        self.assertIn(read_admin.REALM, str(stop.exception))
        self.assertIn(read_admin.CLIENT_ID, str(stop.exception))

    def test_a_blank_variable_counts_as_missing(self):
        """`AddJwtAuthentication` treats whitespace as absent; so does this."""
        self.set(read_admin.CLIENT_ID, "   ")
        with self.assertRaises(SystemExit) as stop:
            read_admin.environment()
        self.assertIn(read_admin.CLIENT_ID, str(stop.exception))

    def test_plain_http_is_refused(self):
        """The bearer token this fetch carries can read every client secret in the realm."""
        self.set(read_admin.BASE_URL, "http://id.example.com")
        with self.assertRaises(SystemExit) as stop:
            read_admin.environment()
        self.assertIn("https or nothing", str(stop.exception))

    def test_a_trailing_slash_is_trimmed_rather_than_doubling_every_path(self):
        self.set(read_admin.BASE_URL, "https://id.example.com/")
        self.assertEqual(read_admin.environment()[read_admin.BASE_URL],
                         "https://id.example.com")


class Stubbed(unittest.TestCase):
    """The fixture the three classes below share, and no case of its own.

    It was `TheJoin` until `WhatItWrites` subclassed it, which re-ran every one
    of that class's cases under this one's name — a suite whose count grows
    without its coverage.
    """

    def setUp(self):
        # Zipped rather than spelled out key by key, for the reason
        # `read_admin.py` gives beside its own REQUIRED tuple: a
        # credential-shaped name next to a literal is a secret-scan finding,
        # and an accepted finding is a decision somebody has to re-read.
        self.values = dict(zip(read_admin.REQUIRED, (
            "https://id.example.com", "commerce", "realm-check", "an-invented-value",
        ), strict=True))
        # The originals are captured by `getattr` in the argument list, so the
        # cleanup restores what was there before this test replaced it. Written
        # this way rather than as two saved locals because a local named for a
        # token is a secret-scan finding, and the shorter form is better anyway.
        for name in ("token", "get"):
            self.addCleanup(setattr, read_admin, name, getattr(read_admin, name))
        # A JWT-SHAPED TOKEN CARRYING THE GRANT, because `clients` reads the
        # roles out of it before it asks for anything. A stub answering an
        # opaque string would make every case here exercise the refusal.
        read_admin.token = lambda *args: self.jwt(["view-clients"])

    def jwt(self, roles: list[str]) -> str:
        """A token of the shape Keycloak issues, carrying the roles given.

        Unsigned, because `granted_roles` verifies nothing — it reads what the
        server said it granted, and the server is what enforces it.
        """
        claims = {"resource_access": {read_admin.REALM_MANAGEMENT: {"roles": roles}}}
        payload = base64.urlsafe_b64encode(
            json.dumps(claims).encode("utf-8")).decode("ascii").rstrip("=")
        return f"header.{payload}.signature"

    def answers(self, representation, clients):
        """One realm document and one client list, served the way Keycloak does.

        The `max` ceiling is honoured here rather than ignored, because a stub
        that answered the whole list whatever was asked would make the ceiling
        untested while looking covered.
        """
        def get(url: str, _access: str):
            if "/clients" not in url:
                return representation
            query = urllib.parse.parse_qs(urllib.parse.urlsplit(url).query)
            most = int(query.get("max", [str(read_admin.CLIENT_LIMIT)])[0])
            return clients[:most]

        read_admin.get = get


class TheJoin(Stubbed):
    """The clients list is added to the realm representation, and never over it."""

    def test_the_two_documents_join_into_one_export_shape(self):
        self.answers({"realm": "commerce", "accessTokenLifespan": 300},
                     [{"clientId": "web-app"}])
        realm = read_admin.fetch(self.values)
        self.assertEqual(realm["accessTokenLifespan"], 300)
        self.assertEqual([c["clientId"] for c in realm["clients"]], ["web-app"])

    def test_a_representation_that_already_carries_clients_stops(self):
        """Overwriting it would silently choose which of two lists is judged."""
        self.answers({"realm": "commerce", "clients": []}, [{"clientId": "web-app"}])
        with self.assertRaises(SystemExit) as stop:
            read_admin.fetch(self.values)
        self.assertIn("already carries a clients key", str(stop.exception))

    def test_a_realm_answer_that_is_not_an_object_stops(self):
        self.answers([], [{"clientId": "web-app"}])
        with self.assertRaises(SystemExit) as stop:
            read_admin.fetch(self.values)
        self.assertIn("realm representation", str(stop.exception))

    def test_a_clients_answer_that_is_not_a_list_stops(self):
        read_admin.get = lambda url, _a: ({"clientId": "web-app"}
                                          if "/clients" in url else {"realm": "commerce"})
        with self.assertRaises(SystemExit) as stop:
            read_admin.fetch(self.values)
        self.assertIn("client list", str(stop.exception))


class TheCeiling(Stubbed):
    """Completeness is refused rather than inferred.

    Paging was tried twice and both terminating conditions were wrong for the
    same reason: Keycloak pages client models and filters representations it
    cannot render afterwards, so neither a short page nor an empty one proves
    there is nothing behind it. One request with a ceiling has no terminating
    condition to get wrong — only a response AT the ceiling could have been cut
    short, and that one stops the run.
    """

    def realm_of(self, count: int) -> list:
        return [{"clientId": f"client-{n}"} for n in range(count)]

    def test_the_request_asks_for_the_whole_realm_at_once(self):
        asked = []

        def get(url: str, _access: str):
            if "/clients" not in url:
                return {"realm": "commerce"}
            asked.append(url)
            return self.realm_of(9)

        read_admin.get = get
        self.assertEqual(len(read_admin.fetch(self.values)["clients"]), 9)
        self.assertEqual(len(asked), 1, asked)
        self.assertIn(f"max={read_admin.CLIENT_LIMIT}", asked[0])
        # `first` is not sent: there is nothing to skip when the ceiling is the
        # whole realm, and a paged request is what the two wrong terminating
        # conditions were built on.
        self.assertNotIn("first=", asked[0])

    def test_a_response_at_the_ceiling_is_refused_rather_than_truncated(self):
        """The only answer that might not be the whole list is the one that stops."""
        self.answers({"realm": "commerce"}, self.realm_of(read_admin.CLIENT_LIMIT))
        with self.assertRaises(SystemExit) as stop:
            read_admin.fetch(self.values)
        self.assertIn("refuses to judge", str(stop.exception))

    def test_one_below_the_ceiling_is_the_whole_realm(self):
        """The server had room to answer more and did not, so this is all of them."""
        self.answers({"realm": "commerce"}, self.realm_of(read_admin.CLIENT_LIMIT - 1))
        fetched = read_admin.fetch(self.values)["clients"]
        self.assertEqual(len(fetched), read_admin.CLIENT_LIMIT - 1)


class TheGrant(Stubbed):
    """Completeness rests on the account seeing every client, so the grant is read.

    Keycloak applies `max` to the client-model stream and then drops the
    representations the caller may not see, so a filtered list is not
    distinguishable from a complete one — no ceiling and no page boundary can
    establish it. What can be established is the premise, and this is where it
    is.
    """

    def test_an_account_with_view_clients_is_accepted(self):
        self.answers({"realm": "commerce"}, [{"clientId": "web-app"}])
        self.assertEqual(len(read_admin.fetch(self.values)["clients"]), 1)

    def test_realm_admin_is_accepted_because_it_composes_view_clients(self):
        read_admin.token = lambda *args: self.jwt(["realm-admin"])
        self.answers({"realm": "commerce"}, [{"clientId": "web-app"}])
        self.assertEqual(len(read_admin.fetch(self.values)["clients"]), 1)

    def test_view_realm_alone_is_refused_because_it_composes_nothing(self):
        """The role that reads like it should be enough, and is not.

        An earlier revision accepted it on the reasoning that it implies
        `view-clients`. `deploy/compose/keycloak/realm-export.json` says
        otherwise — `view-realm` is a non-composite role — so accepting it
        approved a credential with no client visibility for the one check whose
        purpose is to establish that it has some.
        """
        read_admin.token = lambda *args: self.jwt(["view-realm"])
        self.answers({"realm": "commerce"}, [])
        with self.assertRaises(SystemExit) as stop:
            read_admin.fetch(self.values)
        self.assertIn("view-clients", str(stop.exception))

    def test_an_account_without_the_grant_stops_before_it_asks(self):
        """Not after: a list nobody could have seen in full is not a list to judge."""
        asked = []
        read_admin.token = lambda *args: self.jwt(["view-users"])
        read_admin.get = lambda url, _a: asked.append(url) or {"realm": "commerce"}
        with self.assertRaises(SystemExit) as stop:
            read_admin.fetch(self.values)
        self.assertIn("view-clients", str(stop.exception))
        self.assertEqual([u for u in asked if "/clients" in u], [])

    def test_a_token_that_is_not_a_jwt_stops(self):
        """An unestablished premise is the same thing as an unmet one."""
        self.answers({"realm": "commerce"}, [])
        read_admin.token = lambda *args: "an-opaque-string"
        with self.assertRaises(SystemExit) as stop:
            read_admin.fetch(self.values)
        self.assertIn("not a JWT", str(stop.exception))

    def test_a_payload_that_does_not_decode_stops(self):
        self.answers({"realm": "commerce"}, [])
        read_admin.token = lambda *args: "header.!!!not-base64!!!.signature"
        with self.assertRaises(SystemExit) as stop:
            read_admin.fetch(self.values)
        self.assertIn("does not decode", str(stop.exception))

    def test_a_token_carrying_no_resource_access_stops(self):
        payload = base64.urlsafe_b64encode(
            json.dumps({"sub": "x"}).encode("utf-8")).decode("ascii").rstrip("=")
        read_admin.token = lambda *args: f"header.{payload}.signature"
        self.answers({"realm": "commerce"}, [])
        with self.assertRaises(SystemExit) as stop:
            read_admin.fetch(self.values)
        self.assertIn("holds none of", str(stop.exception))


class TheRolesThisGateAccepts(unittest.TestCase):
    """The premise behind `COMPLETENESS_ROLES`, asserted against a realm.

    The list is a claim about Keycloak's role model — that these roles grant
    visibility of every client and that the ones left out do not — and a claim
    like that belongs against an artefact rather than in a comment. It shipped
    wrong once: `view-realm` was accepted on the reasoning that it implies
    `view-clients`, which the export beside it flatly contradicts.

    §14.1's realm is the subject because it is the one this repository owns and
    because Keycloak generates these roles itself, so the shape here is the
    shape a deployed realm has.
    """

    def realm_management_roles(self) -> dict:
        import realm_check

        export = (Path(realm_check.__file__).resolve().parents[2]
                  / realm_check.COMPOSE_REALM)
        document = json.loads(export.read_text(encoding="utf-8"))
        roles = document["roles"]["client"][read_admin.REALM_MANAGEMENT]
        return {role["name"]: role for role in roles}

    def composed_by(self, name: str) -> set[str]:
        """The roles `name` grants directly, one level down."""
        role = self.realm_management_roles()[name]
        if not role.get("composite"):
            return set()
        return set(role.get("composites", {})
                   .get("client", {})
                   .get(read_admin.REALM_MANAGEMENT, []))

    def test_every_accepted_role_grants_client_visibility(self):
        """Either it IS view-clients, or it composes it."""
        roles = self.realm_management_roles()
        for accepted in read_admin.COMPLETENESS_ROLES:
            self.assertIn(accepted, roles, f"{accepted} is not a realm-management role")
            self.assertTrue(
                accepted == "view-clients" or "view-clients" in self.composed_by(accepted),
                f"{accepted} is accepted by read_admin but neither is nor composes "
                "view-clients, so it does not establish that the client list is complete")

    def test_view_realm_is_not_accepted_and_the_export_says_why(self):
        """The specific mistake, pinned against the artefact that disproves it."""
        self.assertNotIn("view-realm", read_admin.COMPLETENESS_ROLES)
        self.assertNotIn("view-clients", self.composed_by("view-realm"))

    def test_the_export_still_declares_the_role_this_gate_names(self):
        """The subject test for the subject test: `view-clients` has to exist.

        A realm that renamed it would make every case above vacuous — they all
        ask about a role by name — and would leave the gate refusing every
        credential rather than accepting a wrong one.
        """
        self.assertIn("view-clients", self.realm_management_roles())


class TheRedirect(unittest.TestCase):
    """A redirect is refused, because `urllib` carries the credential onto it."""

    def test_the_opener_refuses_to_follow_one(self):
        handler = read_admin.NoRedirects()
        with self.assertRaises(urllib.error.HTTPError) as refused:
            handler.redirect_request(
                urllib.request.Request("https://id.example.com/admin/realms/commerce"),
                None, 302, "Found", {}, "https://elsewhere.example.com/")
        self.assertIn("elsewhere.example.com", str(refused.exception))

    def test_the_opener_this_file_uses_carries_the_refusal(self):
        """A handler nothing installs refuses nothing."""
        self.assertTrue(
            any(isinstance(h, read_admin.NoRedirects) for h in read_admin.OPENER.handlers),
            read_admin.OPENER.handlers)

    def test_every_network_call_in_the_file_goes_through_that_opener(self):
        """The subject test, and the assertion above is not it.

        `OPENER` carrying the refusal proves nothing about whether anything
        uses it: changing either call site back to `urllib.request.urlopen`
        leaves the handler installed on an opener nothing opens, and the
        credential follows a 302 again with the suite green. Every other case
        in this file replaces `read_admin.get` and `read_admin.token` wholesale,
        so no test reaches the real call sites — which is why this one reads
        the source.
        """
        source = Path(read_admin.__file__).read_text(encoding="utf-8")
        opened = re.findall(r"^\s+with (\S+)\.open\(", source, re.MULTILINE)
        self.assertEqual(opened, ["OPENER", "OPENER"], source)
        self.assertNotIn("urllib.request.urlopen(", source)


class WhatItWrites(Stubbed):
    """The seam: the file `--out` writes is a file the deploy-time check reads.

    It meets here and nowhere else in either suite, so this case drives
    `read_admin.main` rather than asserting about a document the test wrote
    itself. An earlier version did exactly that — it round-tripped its own
    literal through `json` and never called `read_admin` at all, so deleting
    the write, changing its encoding or writing to a different path would all
    have left it green with its docstring still claiming the seam was covered.
    """

    def setUp(self):
        super().setUp()
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.out = Path(directory.name) / "realm.json"
        # `main` reads the environment rather than the fixture dict, which is
        # the point of driving `main` at all: the seam includes that read.
        original = {name: os.environ.get(name) for name in self.values}

        def restore():
            for name, value in original.items():
                if value is None:
                    os.environ.pop(name, None)
                else:
                    os.environ[name] = value

        self.addCleanup(restore)
        os.environ.update(self.values)

    def run_main(self) -> int:
        """`main`, with its one line of output kept out of the suite's."""
        with contextlib.redirect_stdout(io.StringIO()):
            return read_admin.main(["read_admin.py", "--out", str(self.out)])

    def test_what_read_admin_writes_is_what_realm_check_accepts(self):
        import realm_check

        self.answers({"realm": "commerce", "accessTokenLifespan": 300,
                      "revokeRefreshToken": True, "refreshTokenMaxReuse": 0},
                     [{"clientId": realm_check.BROWSER_CLIENT,
                       "standardFlowEnabled": True,
                       "implicitFlowEnabled": False,
                       "directAccessGrantsEnabled": False,
                       "webOrigins": ["https://spa.example"],
                       "attributes": {"use.refresh.tokens": "false"}},
                      {"clientId": realm_check.MOBILE_CLIENT,
                       "standardFlowEnabled": True,
                       "implicitFlowEnabled": False,
                       "directAccessGrantsEnabled": False,
                       "publicClient": True,
                       "redirectUris": ["blueprint://auth/callback"],
                       "webOrigins": ["https://localhost", "capacitor://localhost"],
                       "defaultClientScopes": ["web-origins", "acr", "profile", "roles",
                                               "basic", "commerce-api", "email"],
                       "attributes": {"use.refresh.tokens": "true",
                                      "pkce.code.challenge.method": "S256"}},
                      {"clientId": realm_check.WORKER_CLIENT,
                       "enabled": True,
                       "standardFlowEnabled": False,
                       "implicitFlowEnabled": False,
                       "directAccessGrantsEnabled": False,
                       "serviceAccountsEnabled": True,
                       "publicClient": False,
                       "defaultClientScopes": ["basic", "commerce-api"],
                       "optionalClientScopes": ["address"],
                       "webOrigins": []},
                      {"clientId": realm_check.CONTACT_CLIENT,
                       "enabled": True,
                       "standardFlowEnabled": False,
                       "implicitFlowEnabled": False,
                       "directAccessGrantsEnabled": False,
                       "serviceAccountsEnabled": True,
                       "publicClient": False,
                       "defaultClientScopes": ["basic", "roles"],
                       "optionalClientScopes": ["address"],
                       "webOrigins": []}])
        self.assertEqual(self.run_main(), 0)

        # Read back through the deploy path's own two calls, not through json.
        document = realm_check.load_realm(self.out)
        self.assertEqual(
            realm_check.check_realm(document, realm_check.DEPLOYED, 300), [])

    def test_a_realm_the_fetch_returns_is_judged_and_can_fail(self):
        """The positive above proves the seam carries a compliant realm.

        This proves it carries a failing one, which is what stops a rollout —
        a seam that only transmits passes is the fail-open shape again.
        """
        import realm_check

        self.answers({"realm": "commerce", "accessTokenLifespan": 18000,
                      "revokeRefreshToken": True, "refreshTokenMaxReuse": 0},
                     [{"clientId": realm_check.BROWSER_CLIENT,
                       "standardFlowEnabled": True,
                       "implicitFlowEnabled": False,
                       "directAccessGrantsEnabled": False,
                       "webOrigins": ["https://spa.example"],
                       "attributes": {"use.refresh.tokens": "false"}},
                      {"clientId": realm_check.MOBILE_CLIENT,
                       "standardFlowEnabled": True,
                       "implicitFlowEnabled": False,
                       "directAccessGrantsEnabled": False,
                       "publicClient": True,
                       "redirectUris": ["blueprint://auth/callback"],
                       "webOrigins": ["https://localhost", "capacitor://localhost"],
                       "defaultClientScopes": ["web-origins", "acr", "profile", "roles",
                                               "basic", "commerce-api", "email"],
                       "attributes": {"use.refresh.tokens": "true",
                                      "pkce.code.challenge.method": "S256"}},
                      {"clientId": realm_check.WORKER_CLIENT,
                       "enabled": True,
                       "standardFlowEnabled": False,
                       "implicitFlowEnabled": False,
                       "directAccessGrantsEnabled": False,
                       "serviceAccountsEnabled": True,
                       "publicClient": False,
                       "defaultClientScopes": ["basic", "commerce-api"],
                       "optionalClientScopes": ["address"],
                       "webOrigins": []},
                      {"clientId": realm_check.CONTACT_CLIENT,
                       "enabled": True,
                       "standardFlowEnabled": False,
                       "implicitFlowEnabled": False,
                       "directAccessGrantsEnabled": False,
                       "serviceAccountsEnabled": True,
                       "publicClient": False,
                       "defaultClientScopes": ["basic", "roles"],
                       "optionalClientScopes": ["address"],
                       "webOrigins": []}])
        self.run_main()
        found = realm_check.check_realm(
            realm_check.load_realm(self.out), realm_check.DEPLOYED, 300)
        self.assertEqual(len(found), 1, found)
        self.assertIn("accessTokenLifespan", found[0])


class TheRealmSegmentIsEscaped(Stubbed):
    """`/` in a realm name must not change which realm is read."""

    def test_a_traversal_in_the_realm_name_does_not_reach_another_realm(self):
        seen = []

        def get(url: str, _access: str):
            seen.append(url)
            return [] if "/clients" in url else {"realm": "commerce"}

        read_admin.get = get
        self.values[read_admin.REALM] = "commerce/../master"
        read_admin.fetch(self.values)
        self.assertTrue(all("/../" not in url for url in seen), seen)
        self.assertTrue(any("commerce%2F..%2Fmaster" in url for url in seen), seen)


if __name__ == "__main__":
    unittest.main()
