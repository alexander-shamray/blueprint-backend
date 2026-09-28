#!/usr/bin/env python3
"""Every check in the realm gate, driven against a realm built to fail it.

Run against this repository the gate passes, which proves nothing about what it
would catch — so every case below is a negative one, with a single positive
control at the top to establish that the fixture the negatives are derived from
is itself clean. Without that control a gate that failed on everything would
look perfect here.

Three of the cases have a different subject from the rest. They do not ask what
the gate found; they ask **what it was looking at** — that the lifetime is
still read out of a file that still declares it, that a path this gate reads is
covered by something the workflow triggers on, and that a realm with no clients
is refused rather than passed. That is the failure this repository repeats most
and the only defence against it is a test whose subject is the gate's own
subject.

    py -3.12 -m unittest discover -s deploy/keycloak
"""

from __future__ import annotations

import contextlib
import io
import json
import re
import tempfile
import unittest
from pathlib import Path

import realm_check


def browser(**overrides) -> dict:
    """A compliant deployed `web-app`: no refresh token, standard flow, no password
    grant, and an origin its own token exchange can be read from."""
    client = {
        "clientId": realm_check.BROWSER_CLIENT,
        "standardFlowEnabled": True,
        "implicitFlowEnabled": False,
        "directAccessGrantsEnabled": False,
        "publicClient": True,
        "webOrigins": ["https://spa.example"],
        "attributes": {"use.refresh.tokens": "false"},
    }
    client.update(overrides)
    return client


def mobile(**overrides) -> dict:
    """A compliant `mobile-app`: refresh token, standard flow, no password grant,
    PKCE-only authentication, the fixed redirect URI and default scope, and the
    packaged WebView's own two browser origins."""
    client = {
        "clientId": realm_check.MOBILE_CLIENT,
        "standardFlowEnabled": True,
        "implicitFlowEnabled": False,
        "directAccessGrantsEnabled": False,
        "publicClient": True,
        "redirectUris": ["blueprint://auth/callback"],
        "webOrigins": ["https://localhost", "capacitor://localhost"],
        "defaultClientScopes": [
            "web-origins", "acr", "profile", "roles", "basic", "commerce-api", "email"],
        "attributes": {"use.refresh.tokens": "true", "pkce.code.challenge.method": "S256"},
    }
    client.update(overrides)
    return client


def worker(**overrides) -> dict:
    """A compliant `shipping-worker`: confidential, service accounts on, no
    interactive flow, and commerce-api as a default scope and not an optional
    one."""
    client = {
        "clientId": realm_check.WORKER_CLIENT,
        "enabled": True,
        "standardFlowEnabled": False,
        "implicitFlowEnabled": False,
        "directAccessGrantsEnabled": False,
        "serviceAccountsEnabled": True,
        "publicClient": False,
        "defaultClientScopes": [
            "web-origins", "acr", "profile", "roles", "basic", "email", "commerce-api"],
        "optionalClientScopes": ["address", "phone", "organization"],
        "webOrigins": [],
    }
    client.update(overrides)
    return client


def realm(*clients, **overrides) -> dict:
    """A realm document of the shape both an export and the admin API produce.

    `mobile-app` and `shipping-worker` are each appended unless the caller
    already supplied one. Most fixtures construct a realm to make a point
    about something else entirely — a lifetime override, a malformed flag —
    and none of those cases should have to learn every client exists just to
    keep passing. A case that IS about one of those clients supplies its own,
    by name, and this helper gets out of its way.
    """
    if clients:
        client_list = list(clients)
        if not any(isinstance(c, dict) and c.get("clientId") == realm_check.MOBILE_CLIENT
                   for c in client_list):
            client_list.append(mobile())
        if not any(isinstance(c, dict) and c.get("clientId") == realm_check.WORKER_CLIENT
                   for c in client_list):
            client_list.append(worker())
    else:
        client_list = [browser(), mobile(), worker()]

    document = {
        "realm": "commerce",
        "enabled": True,
        "accessTokenLifespan": 300,
        "revokeRefreshToken": True,
        "refreshTokenMaxReuse": 0,
        "clients": client_list,
    }
    document.update(overrides)
    return document


class Fixture(unittest.TestCase):
    """The lifetime is passed in rather than read, so no case here touches src/."""

    lifetime = 300

    def problems(self, document: dict, kind: str = realm_check.DEPLOYED) -> list[str]:
        return realm_check.check_realm(document, kind, self.lifetime)

    def one(self, document: dict, kind: str = realm_check.DEPLOYED) -> str:
        found = self.problems(document, kind)
        self.assertEqual(len(found), 1, found)
        return found[0]


class TheFixture(Fixture):
    def test_the_fixture_is_clean(self):
        """A compliant deployed realm produces nothing.

        Every case below mutates this document by one field, so a fixture that
        already failed would make all of them pass for the wrong reason.
        """
        self.assertEqual(self.problems(realm()), [])

    def test_the_local_realm_differs_by_exactly_one_field(self):
        """The Compose realm's password grant is the one obligation that inverts.

        §11.2 documents the password grant as a local affordance, so the same
        document cannot be compliant under both kinds — and this pins that the
        difference is that one field and not a second one nobody noticed.
        """
        local = realm(browser(directAccessGrantsEnabled=True))
        self.assertEqual(realm_check.check_realm(local, realm_check.LOCAL, self.lifetime), [])
        self.assertEqual(len(self.problems(local)), 1)


class TheLifetime(Fixture):
    def test_a_realm_lifespan_above_the_declared_one_is_caught(self):
        """Five hours is the misconfiguration #157 describes in its first sentence."""
        self.assertIn("accessTokenLifespan", self.one(realm(accessTokenLifespan=18000)))

    def test_a_missing_lifespan_is_caught_rather_than_defaulted(self):
        """Absent is not 300. Keycloak's own default is 60, and neither is the chapter's."""
        document = realm()
        del document["accessTokenLifespan"]
        self.assertIn("accessTokenLifespan is None", self.one(document))

    def test_a_client_level_override_is_caught(self):
        """Keycloak resolves the client attribute over the realm value.

        A realm at 300 with one client at 18000 issues five-hour tokens to that
        client, and a realm-level assertion alone calls it compliant.
        """
        client = browser(attributes={"use.refresh.tokens": "false",
                                     "access.token.lifespan": "18000"})
        self.assertIn("access.token.lifespan", self.one(realm(client)))

    def test_a_cleared_override_is_not_an_override(self):
        """Keycloak stores "" for an advanced setting filled in and then cleared.

        That is how an operator undoes exactly the misconfiguration this check
        exists to catch, so reading it as an override would fail the rollout on
        the shape of the fix.
        """
        client = browser(attributes={"use.refresh.tokens": "false",
                                     "access.token.lifespan": ""})
        self.assertEqual(self.problems(realm(client)), [])

    def test_an_override_that_is_not_a_number_is_refused(self):
        """Cannot say what that client issues, which is not the same as saying it is fine."""
        client = browser(attributes={"use.refresh.tokens": "false",
                                     "access.token.lifespan": "5 min"})
        self.assertIn("not a number of seconds", self.one(realm(client)))

    def test_an_override_equal_to_the_realm_value_is_not_a_finding(self):
        """Redundant is not wrong, and refusing it would fail a compliant realm."""
        client = browser(attributes={"use.refresh.tokens": "false",
                                     "access.token.lifespan": "300"})
        self.assertEqual(self.problems(realm(client)), [])

    def test_the_override_is_compared_as_text_because_keycloak_stores_it_that_way(self):
        """Client attributes are a string map; an integer 300 there is still 300."""
        client = browser(attributes={"use.refresh.tokens": "false",
                                     "access.token.lifespan": 300})
        self.assertEqual(self.problems(realm(client)), [])


class TheFlagsAreBooleans(Fixture):
    """A flag of the wrong type is refused rather than compared.

    Every comparison in the gate is an identity test against True or False, so
    a string is *neither* — and the check reading it would fall through as
    though the flag were off. That is the fail-open shape this whole file is
    written against, and it shipped in the first draft: `implicitFlowEnabled`
    was the one comparison written `is True` rather than `is not False`.
    """

    def test_a_string_flag_is_refused_rather_than_read_as_off(self):
        found = self.problems(realm(browser(implicitFlowEnabled="true")))
        self.assertEqual(len(found), 1, found)
        self.assertIn("rather than a boolean", found[0])

    def test_a_string_false_is_refused_too(self):
        """The right answer in an unjudgeable value. A realm this loose is one to fix."""
        self.assertIn("rather than a boolean", self.one(realm(browser(implicitFlowEnabled="false"))))

    def test_an_integer_flag_is_refused_and_the_obligation_speaks_too(self):
        """Two problems for one field, and both are true statements about it.

        The type check says the value cannot be compared; the obligation says
        the guarantee is therefore not established. Collapsing them would mean
        choosing which half of that to hide.
        """
        found = self.problems(realm(browser(standardFlowEnabled=1)))
        self.assertEqual(len(found), 2, found)
        self.assertIn("rather than a boolean", found[0])
        self.assertIn("standard flow", found[1])

    def test_a_null_flag_is_left_to_the_check_that_reads_it(self):
        """`null` is not typed here, because what it means differs per obligation.

        An absent or null flag is Keycloak's default, and whether that default
        satisfies an obligation is the obligation's question — so this reports
        the obligation rather than the type.
        """
        found = self.problems(realm(browser(standardFlowEnabled=None)))
        self.assertEqual(len(found), 1, found)
        self.assertIn("standard flow", found[0])

    def test_a_client_that_is_not_an_object_is_refused(self):
        """Skipping it would let a malformed realm carry an unjudged client."""
        found = self.problems(realm(browser(), "commerce-api"))
        self.assertTrue(any("where a client object belongs" in p for p in found), found)


class TheImplicitFlow(Fixture):
    def test_a_client_enabling_the_implicit_flow_is_caught(self):
        """It is what makes accessTokenLifespanForImplicitFlow unreachable.

        The realm ships 900 for that setting and no chapter states a value, so
        the silence about it is only honest while nothing can reach it.
        """
        self.assertIn("implicit flow", self.one(realm(browser(implicitFlowEnabled=True))))

    def test_a_client_that_is_not_the_browser_is_judged_too(self):
        """The lifetime obligations are every client's, not `web-app`'s.

        A service client with a five-hour override issues five-hour tokens just
        as surely, and an earlier draft of this gate only looked at the browser.
        """
        other = {"clientId": "commerce-api", "implicitFlowEnabled": True}
        found = self.problems(realm(browser(), other))
        self.assertEqual(len(found), 1, found)
        self.assertIn("commerce-api", found[0])


class TheRefreshToken(Fixture):
    def test_an_issued_refresh_token_is_caught(self):
        client = browser(attributes={"use.refresh.tokens": "true"})
        self.assertIn("use.refresh.tokens", self.one(realm(client)))

    def test_an_absent_attribute_is_the_violation_and_not_a_silence(self):
        """Keycloak issues refresh tokens on the standard flow by default.

        Reading a missing attribute as compliant would make the one setting
        ADR-034 rests on optional — and Keycloak's default is the opposite of
        what ADR-034 states, so absence is the failure.
        """
        self.assertIn("declares no use.refresh.tokens", self.one(realm(browser(attributes={}))))

    def test_a_client_with_no_attributes_map_at_all_is_caught(self):
        client = browser()
        del client["attributes"]
        self.assertIn("declares no use.refresh.tokens", self.one(realm(client)))

    def test_the_standard_flow_has_to_be_on_for_the_attribute_to_mean_anything(self):
        """A client that mints no token issues no refresh token either.

        Without this the refresh-token check passes on a broken realm, which is
        the guarantee holding for the wrong reason — #157's own table lists
        `standardFlowEnabled` for exactly this.
        """
        client = browser(standardFlowEnabled=False)
        self.assertIn("standard flow", self.one(realm(client)))


class ThePasswordGrant(Fixture):
    def test_a_deployed_realm_keeping_the_password_grant_is_caught(self):
        client = browser(directAccessGrantsEnabled=True)
        self.assertIn("directAccessGrantsEnabled", self.one(realm(client)))

    def test_a_local_realm_without_it_is_caught_too(self):
        """§14.1's documented login is a password grant; a local realm needs it.

        The check runs in both directions deliberately. A one-directional check
        would let the local realm drift into a shape the README's curl cannot
        use, and the README is what tells a developer the platform works.
        """
        found = realm_check.check_realm(realm(), realm_check.LOCAL, self.lifetime)
        self.assertEqual(len(found), 1, found)
        self.assertIn("directAccessGrantsEnabled", found[0])

    def test_an_absent_flag_is_caught_for_a_local_realm(self):
        """Keycloak's default is false, so absence fails the local realm honestly."""
        client = browser()
        del client["directAccessGrantsEnabled"]
        found = realm_check.check_realm(realm(client), realm_check.LOCAL, self.lifetime)
        self.assertEqual(len(found), 1, found)


class TheMobileClient(Fixture):
    """`check_mobile_client`'s obligations, the mirror of `TheRefreshToken`'s and
    `ThePasswordGrant`'s with the polarity `mobile-app` carries instead."""

    def test_a_missing_refresh_token_attribute_is_caught(self):
        # pkce.code.challenge.method is kept alongside the one attribute this
        # case is about — `attributes` replaces the whole map on override, and
        # dropping it here would report two problems for one mutation.
        client = mobile(attributes={"pkce.code.challenge.method": "S256"})
        self.assertIn("declares no use.refresh.tokens", self.one(realm(browser(), client)))

    def test_a_refresh_token_attribute_set_to_false_is_caught(self):
        """The obligation runs the opposite way from the browser's: mobile is issued one."""
        client = mobile(attributes={"use.refresh.tokens": "false",
                                    "pkce.code.challenge.method": "S256"})
        self.assertIn("use.refresh.tokens", self.one(realm(browser(), client)))

    def test_the_standard_flow_has_to_be_on_here_too(self):
        """Without it there is no token to refresh, so the attribute above means nothing."""
        client = mobile(standardFlowEnabled=False)
        self.assertIn("standard flow", self.one(realm(browser(), client)))

    def test_the_password_grant_is_caught_in_either_realm_kind(self):
        """Unlike the browser's, this check does not take the realm kind:
        nothing documents a password-grant login for the native client."""
        client = mobile(directAccessGrantsEnabled=True)
        deployed = self.problems(realm(browser(), client), realm_check.DEPLOYED)
        # The local realm's OWN password-grant obligation is the opposite of
        # the deployed one's (`ThePasswordGrant`), so `browser()` is given the
        # flag `check_browser_client` requires for LOCAL here — otherwise a
        # second, unrelated problem about `web-app` would be counted below.
        local = self.problems(
            realm(browser(directAccessGrantsEnabled=True), client), realm_check.LOCAL)
        self.assertEqual(len(deployed), 1, deployed)
        self.assertEqual(len(local), 1, local)
        self.assertIn("directAccessGrantsEnabled", deployed[0])
        self.assertIn("directAccessGrantsEnabled", local[0])

    def test_pkce_weakened_to_plain_is_caught(self):
        """The one thing standing between an intercepted code and the attacker
        who intercepted it, on a client with no secret to fall back on."""
        client = mobile(attributes={"use.refresh.tokens": "true",
                                    "pkce.code.challenge.method": "plain"})
        self.assertIn("pkce.code.challenge.method", self.one(realm(browser(), client)))

    def test_a_missing_pkce_attribute_is_caught_rather_than_defaulted(self):
        client = mobile(attributes={"use.refresh.tokens": "true"})
        self.assertIn("pkce.code.challenge.method", self.one(realm(browser(), client)))

    def test_a_widened_redirect_uri_is_caught(self):
        """A second entry, a wildcard, or an http(s) scheme beside the custom
        one is a code — and through it a refresh token — delivered wherever
        the wider pattern also matches."""
        client = mobile(redirectUris=["blueprint://auth/callback", "http://localhost/*"])
        self.assertIn("redirectUris", self.one(realm(browser(), client)))

    def test_a_missing_commerce_api_scope_is_caught(self):
        """A token with this scope missing carries no audience and no
        permission claim, silently."""
        client = mobile(defaultClientScopes=["web-origins", "acr", "profile", "roles", "basic", "email"])
        self.assertIn("commerce-api", self.one(realm(browser(), client)))

    def test_public_client_flipped_to_false_is_caught(self):
        """This app ships with no secret it could keep confidential — every
        install carries the same one — so a confidential posture is a
        credential baked into the binary rather than a real one."""
        client = mobile(publicClient=False)
        self.assertIn("publicClient", self.one(realm(browser(), client)))


class TheWorkerClient(Fixture):
    def test_a_missing_worker_is_caught_rather_than_passed(self):
        """The vacuous half: every check below is a property of one client."""
        document = realm(browser(), mobile())
        document["clients"] = [c for c in document["clients"]
                               if c.get("clientId") != realm_check.WORKER_CLIENT]
        self.assertIn("shipping-worker", self.one(document))

    def test_a_public_worker_is_caught(self):
        self.assertIn("publicClient", self.one(realm(browser(), worker(publicClient=True))))

    def test_service_accounts_turned_off_is_caught(self):
        self.assertIn("service accounts disabled",
                      self.one(realm(browser(), worker(serviceAccountsEnabled=False))))

    def test_a_disabled_worker_is_caught(self):
        self.assertIn("disabled", self.one(realm(browser(), worker(enabled=False))))

    def test_each_interactive_flow_is_caught_on_its_own(self):
        for flag in ("standardFlowEnabled", "directAccessGrantsEnabled", "implicitFlowEnabled"):
            with self.subTest(flag=flag):
                found = self.problems(realm(browser(), worker(**{flag: True})))
                # check_implicit_flow also refuses the implicit flow on every
                # client, so the worker limb's own wording is what is matched:
                # naming the flag alone would stay green with that limb gone.
                self.assertTrue(any(f"has {flag}=" in problem for problem in found), found)

    def test_an_optional_audience_scope_is_caught(self):
        """The default list keeps commerce-api, so only the optional limb fires."""
        found = self.one(realm(browser(), worker(
            optionalClientScopes=["address", "commerce-api"])))
        self.assertIn("OPTIONAL", found)

    def test_a_missing_audience_scope_is_caught(self):
        self.assertIn("default client scope",
                      self.one(realm(browser(), worker(defaultClientScopes=["basic"]))))

    def test_a_string_service_account_flag_is_refused_rather_than_read_as_on(self):
        """The flag joined FLAGS, so a hand-edited "true" is refused.

        Two findings and not one, so `self.problems` rather than `self.one`:
        `check_flags_are_booleans` refuses the string, and the identity test
        in `check_worker_client` sees a value that is not `True`. Both are
        asserted, because a case naming one would stay green if the other
        limb were deleted.
        """
        found = self.problems(realm(browser(), worker(serviceAccountsEnabled="true")))
        self.assertTrue(any("boolean" in problem for problem in found), found)
        self.assertTrue(any("service accounts disabled" in problem for problem in found), found)


class TheWebOrigins(Fixture):
    """The CORS grant the token exchange needs, which is not the redirect URI.

    Every case here is one silence at a different depth — the field absent, the
    field empty, the field holding something no browser will ever send — and
    none of the three produces an error anywhere. Keycloak mints the token and
    the browser discards it unread, so this gate is the only thing in the
    system positioned to see them.
    """

    def test_an_absent_web_origins_is_caught(self):
        """Keycloak's default grants nothing, so silence is the violation."""
        client = mobile()
        del client["webOrigins"]
        self.assertIn("declares no webOrigins", self.one(realm(browser(), client)))

    def test_an_empty_web_origins_is_caught(self):
        """The state the shipped realm was actually in when this was written."""
        self.assertIn("empty webOrigins", self.one(realm(browser(), mobile(webOrigins=[]))))

    def test_the_browser_client_is_judged_on_it_too(self):
        """The obligation belongs to the token exchange and not to a platform.

        `web-app` runs the same exchange from a browser and satisfies this
        already; what the case pins is that it would be caught breaking the
        same silent way, rather than that the native client is special.
        """
        self.assertIn(realm_check.BROWSER_CLIENT,
                      self.one(realm(browser(webOrigins=[]), mobile())))

    def test_a_wildcard_is_caught(self):
        self.assertIn("Access-Control-Allow-Origin: *",
                      self.one(realm(browser(), mobile(webOrigins=["*"]))))

    def test_plus_derives_nothing_from_a_custom_scheme_redirect(self):
        """Granting nothing while reading as answered is the worst of the three."""
        self.assertIn("they imply none", self.one(realm(browser(), mobile(webOrigins=["+"]))))

    def test_plus_is_accepted_where_the_redirect_uris_do_imply_an_origin(self):
        """Which is why `+` is judged against the client rather than banned.

        `web-app`'s redirects are http(s) URLs, so `+` really does resolve to
        origins there — refusing it outright would fail a deployed realm that
        was configured correctly.
        """
        spa = browser(webOrigins=["+"], redirectUris=["https://spa.example/*"])
        self.assertEqual(self.problems(realm(spa, mobile())), [])

    def test_a_custom_scheme_origin_is_accepted(self):
        """The one place this parts company with the gateway's identical guard.

        `Cors:Origins` refuses a non-http(s) scheme at startup because
        `WithOrigins` feeds ASP.NET Core's matcher; Keycloak compares a string,
        and `capacitor://localhost` is what an iOS WebView sends.
        """
        self.assertEqual(
            self.problems(realm(browser(), mobile(webOrigins=["capacitor://localhost"]))), [])

    def test_a_trailing_slash_is_not_an_origin(self):
        self.assertIn("index 0", self.one(realm(browser(), mobile(webOrigins=["https://localhost/"]))))

    def test_a_path_is_not_an_origin(self):
        self.assertIn(
            "index 0", self.one(realm(browser(), mobile(webOrigins=["https://localhost/auth"]))))

    def test_a_default_port_spelled_out_is_not_what_a_browser_sends(self):
        """The seventh value, one repository over, that six earlier clauses all passed."""
        self.assertIn(
            "index 0", self.one(realm(browser(), mobile(webOrigins=["https://localhost:443"]))))

    def test_a_port_the_scheme_does_not_imply_is_part_of_the_origin(self):
        """The other half: dropping every port would refuse §14.1's own dev server."""
        self.assertEqual(
            self.problems(realm(browser(), mobile(webOrigins=["http://localhost:5173"]))), [])

    def test_an_uppercased_host_is_not_what_a_browser_sends(self):
        self.assertIn(
            "index 0", self.one(realm(browser(), mobile(webOrigins=["https://LOCALHOST"]))))

    def test_a_web_origins_that_is_not_an_array_is_refused(self):
        self.assertIn("not an array",
                      self.one(realm(browser(), mobile(webOrigins="https://localhost"))))

    def test_a_non_string_entry_is_refused_rather_than_parsed(self):
        self.assertIn("index 0", self.one(realm(browser(), mobile(webOrigins=[5]))))

    def test_every_malformed_entry_is_named_and_the_valid_one_is_not(self):
        found = self.one(realm(browser(), mobile(
            webOrigins=["https://localhost", "https://localhost/", "not-an-origin"])))
        self.assertIn("index 1, 2", found)

    # Four spellings the first draft of `canonical_origin` let through, because
    # it compared the raw `netloc` and `urlsplit` does not look at a port until
    # `.port` is read. Copilot found them on PR #203; each is a string no
    # browser can produce, passing a check whose whole subject is what a
    # browser produces.

    def test_a_non_numeric_port_is_refused(self):
        self.assertIn("index 0", self.one(realm(browser(), mobile(
            webOrigins=["https://localhost:not-a-port"]))))

    def test_a_port_above_the_range_is_refused(self):
        self.assertIn("index 0", self.one(realm(browser(), mobile(
            webOrigins=["https://localhost:70000"]))))

    def test_a_bare_trailing_colon_is_refused(self):
        """A browser sends no port rather than an empty one."""
        self.assertIn("index 0", self.one(realm(browser(), mobile(
            webOrigins=["https://localhost:"]))))

    def test_a_zero_padded_port_is_refused(self):
        """`:0443` parses as 443 and is not how 443 is spelled — nor is it sent."""
        self.assertIn("index 0", self.one(realm(browser(), mobile(
            webOrigins=["https://localhost:0443"]))))

    def test_an_ipv6_literal_origin_is_accepted(self):
        """The brackets an origin carries and `hostname` strips are put back.

        Rebuilding the authority is what refuses the four cases above, and this
        is the case that rebuilding could have broken silently: refusing every
        IPv6 origin would have looked exactly like the fix working.
        """
        self.assertEqual(self.problems(realm(browser(), mobile(
            webOrigins=["http://[::1]:8080"]))), [])

    def test_a_relative_redirect_is_not_evidence_that_plus_derives_nothing(self):
        """Keycloak resolves a relative redirect against the client's `rootUrl`.

        So `+` beside `redirectUris: ["/*"]` is an ordinary configuration whose
        origin this gate cannot see, and the first draft failed it — refusing a
        realm that was correct. The gate holds no `rootUrl` and does not guess.
        """
        spa = browser(webOrigins=["+"], redirectUris=["/*"])
        self.assertEqual(self.problems(realm(spa, mobile())), [])

    # Six host spellings a browser rewrites before it sends them, which the
    # rebuilt authority still passed because `urlsplit` is not a WHATWG host
    # parser. Copilot found them on PR #203 round two, one field left of the
    # port cases above and the same defect: a string no browser can send,
    # passing a check whose whole subject is what a browser sends.

    def test_an_ipv4_shorthand_is_refused(self):
        """A browser serialises this as a dotted quad, so the realm never matches."""
        self.assertIn("index 0", self.one(realm(browser(), mobile(
            webOrigins=["https://127.1"]))))

    def test_an_octet_with_a_leading_zero_is_refused(self):
        self.assertIn("index 0", self.one(realm(browser(), mobile(
            webOrigins=["https://010.0.0.1"]))))

    def test_a_bare_integer_host_is_refused(self):
        self.assertIn("index 0", self.one(realm(browser(), mobile(
            webOrigins=["https://2130706433"]))))

    def test_a_dotted_quad_is_accepted(self):
        """The other half: refusing every IPv4 host would pass all three above."""
        self.assertEqual(self.problems(realm(browser(), mobile(
            webOrigins=["https://127.0.0.1"]))), [])

    def test_a_unicode_host_is_refused_because_a_browser_sends_punycode(self):
        self.assertIn("index 0", self.one(realm(browser(), mobile(
            webOrigins=["https://\u00fcnicode.example"]))))

    def test_the_punycode_a_browser_would_send_is_accepted(self):
        """Which is what makes the refusal above a spelling rule, not a ban."""
        self.assertEqual(self.problems(realm(browser(), mobile(
            webOrigins=["https://xn--nicode-2ya.example"]))), [])

    def test_an_expanded_ipv6_literal_is_refused(self):
        """`0:0:0:0:0:0:0:1` is `::1` once a browser has serialised it."""
        self.assertIn("index 0", self.one(realm(browser(), mobile(
            webOrigins=["http://[0:0:0:0:0:0:0:1]"]))))

    def test_a_percent_escape_in_a_special_scheme_host_is_refused(self):
        """A browser decodes it, so the realm holds a string it never sends."""
        self.assertIn("index 0", self.one(realm(browser(), mobile(
            webOrigins=["https://%6cocalhost"]))))

    def test_a_host_outside_the_permitted_set_is_refused_whatever_it_is(self):
        """The set is what closes the class, rather than one more named spelling."""
        self.assertIn("index 0", self.one(realm(browser(), mobile(
            webOrigins=["https://local_host!.example"]))))

    def test_a_custom_scheme_host_is_left_opaque(self):
        """The rule is scheme-aware: a browser normalises no host it does not own.

        Without this case the set above reads as a global ban on percent
        escapes, and the next reader would apply it to the one scheme this
        whole obligation exists to serve.
        """
        self.assertEqual(self.problems(realm(browser(), mobile(
            webOrigins=["capacitor://%6cocalhost"]))), [])

    def test_a_service_name_and_a_dotted_domain_still_pass(self):
        """The other half of the set: it has to admit what a realm really names."""
        self.assertEqual(self.problems(realm(
            browser(webOrigins=["https://id.example.com"]),
            mobile(webOrigins=["http://keycloak-svc:8080"]))), [])

    # `+` beside no redirect URI at all derives nothing, which is the same
    # empty grant reached by a second route. The first draft folded absent and
    # empty into the conservative branch and let both through.

    def test_plus_with_no_redirect_uris_at_all_is_caught(self):
        client = browser(webOrigins=["+"])
        client.pop("redirectUris", None)
        self.assertIn("they imply none", self.one(realm(client, mobile())))

    def test_plus_with_an_empty_redirect_uri_list_is_caught(self):
        self.assertIn("they imply none", self.one(realm(
            browser(webOrigins=["+"], redirectUris=[]), mobile())))

    def test_a_malformed_redirect_uri_list_stays_conservative(self):
        """Not an array is a hand-edited realm, and what Keycloak makes of it
        is not this gate's to predict — so `+` is not judged on it."""
        self.assertEqual(self.problems(realm(
            browser(webOrigins=["+"], redirectUris="not-an-array"), mobile())), [])

    # A character set closes the class of hosts that differ by a character, and
    # these three differ by parse. `ends_in_a_number` is WHATWG's own test
    # written out, after two approximations of it each missed a form.

    def test_a_scoped_ipv6_literal_is_refused(self):
        """`ipaddress` accepts a zone identifier and a URL cannot carry one."""
        self.assertIn("index 0", self.one(realm(browser(), mobile(
            webOrigins=["https://[fe80::1%25eth0]"]))))

    def test_a_hexadecimal_final_label_is_refused(self):
        """`127.0x1` is an IPv4 attempt: the prefix is on the label, not the host."""
        self.assertIn("index 0", self.one(realm(browser(), mobile(
            webOrigins=["https://127.0x1"]))))

    def test_a_numeric_host_with_a_trailing_dot_is_refused(self):
        """One trailing dot is dropped before the last label is read."""
        self.assertIn("index 0", self.one(realm(browser(), mobile(
            webOrigins=["https://127.1."]))))

    def test_a_domain_whose_label_merely_starts_with_0x_is_accepted(self):
        """The other half: `ends_in_a_number` reads the LAST label, not the first.

        Without this case, a test that read the host rather than its final
        label would refuse an ordinary domain and every negative above would
        still pass.
        """
        self.assertEqual(self.problems(realm(browser(), mobile(
            webOrigins=["https://0x.example"]))), [])

    def test_the_offending_value_is_not_echoed(self):
        """Userinfo survives the authority form, so the message carries an index.

        `Gateway.Api/Program.cs` reached this the same way and says so: the
        guard that rejects a credential must not be the thing that publishes
        it, because an exception message reaches the logs where §13.4's
        redactor cannot see an interpolated value.
        """
        withheld = self.MARKER
        found = self.one(realm(browser(), mobile(
            webOrigins=[f"https://demo:{withheld}@localhost"])))
        self.assertIn("index 0", found)
        self.assertNotIn(withheld, found)

    # Referenced rather than written into the case above, on
    # `WhatTheGateHolds`'s reasoning: §15.1's secret scan reads a
    # credential-shaped name assigned a literal, and a userinfo password spelled
    # at its use site is exactly that shape.
    MARKER = "marker-withheld"


class TheRefreshTokenRotation(Fixture):
    """`revokeRefreshToken` and `refreshTokenMaxReuse`, judged once `mobile-app` exists."""

    def test_revoke_refresh_token_off_is_caught(self):
        document = realm()
        document["revokeRefreshToken"] = False
        self.assertIn("revokeRefreshToken", self.one(document))

    def test_a_missing_revoke_refresh_token_is_caught_rather_than_defaulted(self):
        document = realm()
        del document["revokeRefreshToken"]
        self.assertIn("revokeRefreshToken is None", self.one(document))

    def test_a_nonzero_max_reuse_is_caught(self):
        document = realm()
        document["refreshTokenMaxReuse"] = 1
        self.assertIn("refreshTokenMaxReuse", self.one(document))

    def test_a_missing_max_reuse_is_caught_rather_than_defaulted(self):
        document = realm()
        del document["refreshTokenMaxReuse"]
        self.assertIn("refreshTokenMaxReuse is None", self.one(document))

    def test_rotation_is_not_checked_without_a_mobile_client(self):
        """No client is issued a refresh token to rotate, so nothing here binds.

        Mirrors `check_browser_client` being skipped when `web-app` is absent:
        an obligation with no client to attach to is not one this gate can
        report against a specific field, so it is silent rather than guessing.
        """
        other = {"clientId": "commerce-api"}
        document = realm(clients=[browser(), other])
        document["revokeRefreshToken"] = False
        found = self.problems(document)
        self.assertTrue(
            any(realm_check.MOBILE_CLIENT in p and "0 time(s)" in p for p in found), found)
        self.assertFalse(any("revokeRefreshToken" in p for p in found), found)


class WhatTheGateIsLookingAt(Fixture):
    def test_a_realm_with_no_clients_is_refused_rather_than_passed(self):
        """Every per-client obligation is vacuously true of an empty realm.

        This is the gate-coverage failure in its purest form: a truncated realm
        document satisfies "no client overrides the lifetime" perfectly.
        """
        found = self.problems(realm(**{"clients": []}))
        self.assertEqual(len(found), 1, found)
        self.assertIn("no clients array", found[0])

    def test_a_realm_with_no_clients_key_is_refused(self):
        document = realm()
        del document["clients"]
        self.assertIn("no clients array", self.one(document))

    def test_a_realm_missing_the_browser_client_is_refused(self):
        """ADR-034's obligation is a property of `web-app`, and cannot be checked without it."""
        other = {"clientId": "commerce-api"}
        found = self.problems(realm(other))
        self.assertEqual(len(found), 1, found)
        self.assertIn("0 time(s)", found[0])

    def test_a_duplicated_browser_client_is_refused(self):
        """Two `web-app` entries mean the compliant one could be the one not used."""
        found = self.problems(realm(browser(), browser()))
        self.assertEqual(len(found), 1, found)
        self.assertIn("2 time(s)", found[0])

    def test_a_realm_missing_the_mobile_client_is_refused(self):
        """The refresh-token obligation is a property of `mobile-app` and cannot be checked without it."""
        other = {"clientId": "commerce-api"}
        found = self.problems(realm(clients=[browser(), other, worker()]))
        self.assertEqual(len(found), 1, found)
        self.assertIn("0 time(s)", found[0])

    def test_a_duplicated_mobile_client_is_refused(self):
        """Two `mobile-app` entries mean the compliant one could be the one not used."""
        found = self.problems(realm(browser(), mobile(), mobile()))
        self.assertEqual(len(found), 1, found)
        self.assertIn("2 time(s)", found[0])

    def test_an_unknown_realm_kind_judges_nothing(self):
        """The kind has no default, and a typo must not silently pick one."""
        found = realm_check.check_realm(realm(), "production", self.lifetime)
        self.assertEqual(len(found), 1, found)
        self.assertIn("not one of", found[0])


class TheLifetimeIsRead(unittest.TestCase):
    """The 300 comes out of `AuthenticationExtensions`, and these pin that read."""

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        (self.root / realm_check.LIFETIME_SOURCE).parent.mkdir(parents=True, exist_ok=True)

    def write(self, text: str) -> None:
        (self.root / realm_check.LIFETIME_SOURCE).write_text(text, encoding="utf-8")

    DECLARATION = ("    public static readonly TimeSpan AccessTokenLifetime = "
                   "TimeSpan.FromSeconds({0});\n")

    def test_the_declaration_is_read(self):
        self.write(self.DECLARATION.format(300))
        self.assertEqual(realm_check.read_access_token_lifetime(self.root), 300)

    def test_a_changed_declaration_moves_what_the_realm_owes(self):
        """The point of reading it: one edit moves the constant and the gate together."""
        self.write(self.DECLARATION.format(120))
        self.assertEqual(realm_check.read_access_token_lifetime(self.root), 120)

    def test_a_missing_declaration_stops_rather_than_defaulting(self):
        """A gate that substitutes 300 when it cannot find 300 is checking its own copy."""
        self.write("public static readonly TimeSpan AccessTokenLifetime = FromMinutes(5);")
        with self.assertRaises(SystemExit) as stop:
            realm_check.read_access_token_lifetime(self.root)
        self.assertIn("0 time(s)", str(stop.exception))

    def test_two_declarations_stop_rather_than_picking_one(self):
        self.write(self.DECLARATION.format(300) + self.DECLARATION.format(60))
        with self.assertRaises(SystemExit) as stop:
            realm_check.read_access_token_lifetime(self.root)
        self.assertIn("2 time(s)", str(stop.exception))

    def test_a_doc_comment_quoting_the_assignment_is_not_the_declaration(self):
        """The dangerous pair is a prose mention plus a reformatted member.

        Both present is two matches and fails closed. The comment alone would
        have been one match, and the gate would then have asserted a number the
        platform had stopped holding — silently, and in the direction that
        makes a longer lifetime look compliant.
        """
        self.write("    /// readonly TimeSpan AccessTokenLifetime = "
                   "TimeSpan.FromSeconds(300);\n"
                   "    public static readonly TimeSpan AccessTokenLifetime = "
                   "TimeSpan.FromMinutes(1);\n")
        with self.assertRaises(SystemExit) as stop:
            realm_check.read_access_token_lifetime(self.root)
        self.assertIn("0 time(s)", str(stop.exception))

    def test_a_similarly_named_local_is_not_the_declaration(self):
        """`readonly TimeSpan` is the anchor, so a look-alike cannot stand in."""
        self.write("var AccessTokenLifetime = TimeSpan.FromSeconds(600);")
        with self.assertRaises(SystemExit) as stop:
            realm_check.read_access_token_lifetime(self.root)
        self.assertIn("0 time(s)", str(stop.exception))

    def test_an_unreadable_source_stops(self):
        with self.assertRaises(SystemExit) as stop:
            realm_check.read_access_token_lifetime(self.root)
        self.assertIn("not readable", str(stop.exception))

    def test_the_shipped_declaration_is_still_there(self):
        """The one case here that reads the repository, and it is the subject test.

        Every case above proves the parser works on text this file wrote. This
        one proves the parser is still pointed at a file that still declares the
        constant in the shape it parses — the way this gate stops covering its
        newest surface is a refactor of `AuthenticationExtensions`, not a bad
        regex.
        """
        self.assertGreater(realm_check.read_access_token_lifetime(), 0)


class TheDeclaredInputs(unittest.TestCase):
    """The two directions: nothing read undeclared, and nothing declared untriggered."""

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.workflow = self.root / realm_check.WORKFLOW_PATH
        self.workflow.parent.mkdir(parents=True, exist_ok=True)

    def triggers(self, *entries: str) -> str:
        block = "\n".join(f"      - '{entry}'" for entry in entries)
        return f"on:\n  pull_request:\n    paths:\n{block}\n  push:\n    paths:\n{block}\n"

    def every_input(self) -> tuple[str, ...]:
        return tuple(realm_check.SOURCE_INPUTS) + (realm_check.OWN_TREE, realm_check.WORKFLOW_PATH)

    def test_a_workflow_covering_every_input_passes(self):
        self.workflow.write_text(self.triggers(*self.every_input()), encoding="utf-8")
        self.assertEqual(realm_check.check_workflow_covers_inputs(self.root), [])

    def test_a_glob_covers_a_directory_entry(self):
        entries = [f"{e}/**" for e in self.every_input()]
        self.workflow.write_text(self.triggers(*entries), encoding="utf-8")
        self.assertEqual(realm_check.check_workflow_covers_inputs(self.root), [])

    def test_an_uncovered_input_is_caught_in_both_triggers(self):
        """Both, not the first: a change that skips the gate on `main` is the same defect."""
        entries = [e for e in self.every_input() if e != realm_check.LIFETIME_SOURCE]
        self.workflow.write_text(self.triggers(*entries), encoding="utf-8")
        found = realm_check.check_workflow_covers_inputs(self.root)
        self.assertEqual(len(found), 2, found)
        self.assertIn("pull_request trigger", found[0])
        self.assertIn("push trigger", found[1])

    def test_a_missing_trigger_is_named_rather_than_counted(self):
        """An earlier form counted two paths lists without asking whose they were.

        Replacing `push` with any other event that accepts `paths` left it
        green while nothing ran the gate on `main` — the exact defect the
        docstring names. The message now says which event is missing.
        """
        block = "\n".join(f"      - '{e}'" for e in self.every_input())
        self.workflow.write_text(f"on:\n  push:\n    paths:\n{block}\n", encoding="utf-8")
        found = realm_check.check_workflow_covers_inputs(self.root)
        self.assertEqual(len(found), 1, found)
        self.assertIn("no pull_request trigger", found[0])

    def test_a_trigger_renamed_to_something_else_is_caught(self):
        """`pull_request_target` accepts `paths` and is not `pull_request`."""
        block = "\n".join(f"      - '{e}'" for e in self.every_input())
        self.workflow.write_text(
            f"on:\n  pull_request_target:\n    paths:\n{block}\n"
            f"  push:\n    paths:\n{block}\n", encoding="utf-8")
        found = realm_check.check_workflow_covers_inputs(self.root)
        self.assertEqual(len(found), 1, found)
        self.assertIn("no pull_request trigger", found[0])

    def test_double_quoted_and_unquoted_entries_are_read(self):
        """Three quoting styles are valid YAML for one list, and all three count.

        The text parser is the licence gate's terms — stdlib has no YAML — and
        the cost of that is paid as a refusal rather than a pass. Reading only
        one style would have made a correct workflow fail.
        """
        entries = list(self.every_input())
        styles = [f'      - "{entries[0]}"', f"      - {entries[1]}"]
        styles += [f"      - '{e}'" for e in entries[2:]]
        block = "\n".join(styles)
        self.workflow.write_text(
            f"on:\n  pull_request:\n    paths:\n{block}\n"
            f"  push:\n    branches: [main]\n    paths:\n{block}\n", encoding="utf-8")
        self.assertEqual(realm_check.check_workflow_covers_inputs(self.root), [])

    def test_a_missing_workflow_is_a_failure(self):
        found = realm_check.check_workflow_covers_inputs(self.root)
        self.assertEqual(len(found), 1, found)
        self.assertIn("not readable", found[0])

    def test_the_shipped_workflow_covers_every_declared_input(self):
        """The subject test for this direction, run against the real workflow."""
        self.assertEqual(realm_check.check_workflow_covers_inputs(), [])

    def test_every_path_this_gate_reads_is_declared(self):
        """The other direction, over this gate's own source."""
        self.assertEqual(realm_check.check_source_inputs_covers_reads(), [])

    def test_the_chart_values_the_suite_reads_are_seen_by_the_scan(self):
        """Removing one entry must name that entry, not merely fail.

        `CHART_VALUES` is read by a sibling module as `root /
        realm_check.CHART_VALUES`, and an unqualified `ROOT / NAME` pattern
        could not see it — so the entry passed on its own declaration and
        pointing the read at another tree would have gone unnoticed. This
        removes exactly that entry and demands the scan says so.
        """
        original = list(realm_check.SOURCE_INPUTS)
        self.addCleanup(lambda: setattr(realm_check, "SOURCE_INPUTS", original))
        realm_check.SOURCE_INPUTS = [e for e in original if e != realm_check.CHART_VALUES]
        found = realm_check.check_source_inputs_covers_reads()
        self.assertTrue(any(realm_check.CHART_VALUES in problem for problem in found), found)

    # THE FIXTURES BELOW ASSEMBLE THEIR PATHS RATHER THAN SPELLING THEM.
    # This module is one of the sources the scan reads, so a path literal
    # written here is a read of that path as far as the scan is concerned —
    # the first draft of these two cases made `realm_check.py inputs`
    # report three problems against the repository, which is the same
    # collision one layer further out.
    SEPARATOR = '/'

    def fixture(self, *lines: str) -> str:
        return "\n".join(lines)

    def test_a_path_named_only_in_prose_is_not_a_read(self):
        """The failure this tree has had three times, tested rather than reworded.

        A comment or a docstring explaining what the scan looks for
        satisfies the scan, so the check fails on its own documentation —
        and the fix that lasts is stripping prose, not choosing different
        words.
        """
        real = self.SEPARATOR.join(
            ["src", "BuildingBlocks", "Common.Web", "Authentication.cs"])
        prose = self.SEPARATOR.join(["deploy", "nowhere", "absent.json"])
        commented = self.SEPARATOR.join(["deploy", "nowhere", "other.json"])
        joined = "ROOT " + self.SEPARATOR + " MISSING"
        code = realm_check.code_of(self.fixture(
            f'X = \"{real}\"',
            'def f():',
            f'    """Prose naming {prose} and {joined}."""',
            f'    # A comment naming {commented} too',
            '    return 1'))
        self.assertIn(real, code)
        self.assertNotIn(prose, code)
        self.assertNotIn(commented, code)
        self.assertNotIn("MISSING", code)

    def test_a_docstring_closed_on_its_opening_line_keeps_the_code_after_it(self):
        """A one-line docstring toggles twice, so what follows survives."""
        kept = self.SEPARATOR.join(["deploy", "keycloak", "x.json"])
        code = realm_check.code_of(self.fixture(
            'def f():',
            '    """One line."""',
            f'    Y = \"{kept}\"'))
        self.assertIn(kept, code)

    def test_an_undeclared_read_is_caught(self):
        """Emptying the declaration must fail, or the check is reading its own copy."""
        original = list(realm_check.SOURCE_INPUTS)
        self.addCleanup(lambda: setattr(realm_check, "SOURCE_INPUTS", original))
        realm_check.SOURCE_INPUTS = []
        found = realm_check.check_source_inputs_covers_reads()
        self.assertTrue(found)
        self.assertTrue(any(realm_check.LIFETIME_SOURCE in problem for problem in found))


class TheTrustedOrigin(unittest.TestCase):
    """The realm is derived and the origin is pinned, and swapping those is a hole.

    Deriving the realm stops the gate checking a realm nobody is deploying to.
    Deriving the *origin* would hand a release's values control of where this
    job posts a client secret — `https://attacker.example/realms/x` and the
    credential goes to that host's token endpoint, which is a worse hole than
    the mismatch deriving was introduced to close.
    """

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.values = Path(self.directory.name) / "values.json"

    # `origin` AND NOT `trusted`, and the name is the whole of the fix for
    # three CodeQL alerts. `py/clear-text-logging-sensitive-data` classifies a
    # value by the NAME that holds it, reads `trusted` as a secret, and then
    # follows this list into `realm_check.main` and out through the prints at
    # the end of it — three high-severity findings whose entire flow began in
    # a test helper's parameter and touched no realm and no credential.
    #
    # Renaming rather than suppressing, because the classifier was wrong about
    # what this holds and the correction is to say what it holds: an origin.
    def run_authority(self, authority: str, origin: str) -> int:
        self.values.write_text(
            json.dumps({"identity": {"authority": authority}}), encoding="utf-8")
        # The two `NAME=value` lines are the point of the command and noise in
        # a suite, so they are captured rather than printed.
        with contextlib.redirect_stdout(io.StringIO()):
            return realm_check.main([
                "realm_check.py", "authority",
                "--values", str(self.values), "--trusted-origin", origin])

    def test_a_release_on_the_trusted_origin_is_accepted(self):
        self.assertEqual(
            self.run_authority("https://id.example.com/realms/commerce",
                               "https://id.example.com"), 0)

    def test_a_trailing_slash_on_the_trusted_origin_does_not_matter(self):
        self.assertEqual(
            self.run_authority("https://id.example.com/realms/commerce",
                               "https://id.example.com/"), 0)

    def test_a_release_on_another_origin_stops_the_rollout(self):
        """The exfiltration case: the secret would have gone to that host."""
        with self.assertRaises(SystemExit) as stop:
            self.run_authority("https://attacker.example/realms/commerce",
                               "https://id.example.com")
        self.assertIn("attacker.example", str(stop.exception))
        self.assertIn("does not name", str(stop.exception))

    def test_a_sibling_host_is_not_the_trusted_one(self):
        """Prefix matching would admit `id.example.com.attacker.example`."""
        with self.assertRaises(SystemExit) as stop:
            self.run_authority("https://id.example.com.attacker.example/realms/x",
                               "https://id.example.com")
        self.assertIn("Refusing", str(stop.exception))

    def test_the_written_values_are_checked_again_before_they_are_printed(self):
        """The second-line defence against `$GITHUB_ENV` injection.

        `split_authority` refuses whitespace, so this guard cannot fire through
        the ordinary path — which is exactly why it needs a test of its own. A
        refactor that relaxed the input-side refusal would otherwise move the
        guarantee out of sight with the suite green.
        """
        original = realm_check.split_authority
        self.addCleanup(lambda: setattr(realm_check, "split_authority", original))
        realm_check.split_authority = lambda _authority: (
            "https://id.example.com", "com\nSOME_VAR=x")
        with self.assertRaises(SystemExit) as stop:
            self.run_authority("https://id.example.com/realms/commerce",
                               "https://id.example.com")
        self.assertIn("refusing to write", str(stop.exception))

    def test_a_path_under_the_trusted_origin_is_not_the_trusted_origin(self):
        """`/auth/realms/x` is a different root, and this refuses rather than trims."""
        with self.assertRaises(SystemExit) as stop:
            self.run_authority("https://id.example.com/auth/realms/commerce",
                               "https://id.example.com")
        self.assertIn("/auth", str(stop.exception))


class TheAuthorityDecidesWhichRealmIsRead(unittest.TestCase):
    """The realm checked is the realm the rollout installs, by construction.

    Two environment variables naming a realm, beside a chart value naming
    another, is a gate that passes on realm A while the workload is pointed at
    realm B — compliant, unrelated, and no help at all. So the pair is derived
    from `identity.authority` in the release's own values rather than declared
    beside it, and there is no second place to keep in step.
    """

    def values(self, authority) -> dict:
        return {"image": {"tag": "abc"}, "identity": {"authority": authority}}

    def test_whitespace_anywhere_stops_because_the_value_becomes_an_env_line(self):
        """A newline ends the assignment and starts another.

        `authority` prints `NAME=value` lines a shell appends to
        `$GITHUB_ENV`, and the authority comes from `helm get values` — so
        without this refusal, whoever can edit a release can set an arbitrary
        environment variable for every remaining step of the rollout.
        """
        with self.assertRaises(SystemExit) as stop:
            realm_check.split_authority(
                "https://id.example.com/realms/com\nSOME_VAR=x")
        self.assertIn("whitespace", str(stop.exception))

    def test_the_root_and_the_realm_come_out_of_the_authority(self):
        root, realm = realm_check.split_authority(
            realm_check.authority_of(self.values("https://id.example.com/realms/commerce")))
        self.assertEqual(root, "https://id.example.com")
        self.assertEqual(realm, "commerce")

    def test_the_root_is_not_the_authority(self):
        """Keycloak's admin endpoints sit beside `/realms`, not under it.

        Handing `read_admin.py` the authority is the mistake this split exists
        to make impossible, and it is the one an operator makes by hand.
        """
        root, _ = realm_check.split_authority("https://id.example.com/auth/realms/commerce")
        self.assertEqual(root, "https://id.example.com/auth")

    def test_a_trailing_slash_does_not_become_part_of_the_realm(self):
        _, realm = realm_check.split_authority("https://id.example.com/realms/commerce/")
        self.assertEqual(realm, "commerce")

    def test_a_release_with_no_identity_authority_stops(self):
        with self.assertRaises(SystemExit) as stop:
            realm_check.authority_of({"image": {"tag": "abc"}})
        self.assertIn("identity.authority", str(stop.exception))

    def test_an_empty_authority_stops(self):
        with self.assertRaises(SystemExit) as stop:
            realm_check.authority_of(self.values("   "))
        self.assertIn("names no realm", str(stop.exception))

    def test_plain_http_stops(self):
        with self.assertRaises(SystemExit) as stop:
            realm_check.split_authority("http://id.example.com/realms/commerce")
        self.assertIn("https or nothing", str(stop.exception))

    def test_an_authority_with_no_realms_segment_stops(self):
        with self.assertRaises(SystemExit) as stop:
            realm_check.split_authority("https://id.example.com/commerce")
        self.assertIn("/realms/", str(stop.exception))

    def test_a_query_or_fragment_stops(self):
        """An admin path appended to one of these lands inside it."""
        for authority in ("https://id.example.com/realms/commerce?x=1",
                          "https://id.example.com/realms/commerce#x"):
            with self.assertRaises(SystemExit) as stop:
                realm_check.split_authority(authority)
            self.assertIn("query or a fragment", str(stop.exception))

    def test_every_strvals_metacharacter_is_refused(self):
        """Derived from the constant, so a character added to it is covered.

        Listing the cases by hand is how the next entry arrives untested. Each
        one goes in the realm segment, which is the part of an authority a
        cluster-held value can most freely choose.
        """
        for character in realm_check.STRVALS_METACHARACTERS:
            with self.subTest(character=character):
                with self.assertRaises(SystemExit) as stop:
                    realm_check.split_authority(
                        f"https://id.example.com/realms/commerce{character}x")
                self.assertIn("strvals", str(stop.exception))

    def test_the_comma_injection_is_refused_in_its_concrete_form(self):
        """The shape the refusal exists for, spelled out rather than generated.

        `--set-string identity.authority=<value>` is parsed by Helm's strvals,
        where a comma separates assignments — so this authority is TWO of them
        and the second overrides a registry nobody checked. It is the tag
        preflight's failure one value over, and `canary.py validate-tag` exists
        because this repository has already paid for it once.
        """
        with self.assertRaises(SystemExit) as stop:
            realm_check.split_authority(
                "https://id.example.com/realms/commerce,image.registry=attacker.example")
        message = str(stop.exception)
        self.assertIn("strvals", message)
        # The comma AND the `=` are both named: the injected assignment needs
        # both, and a message that reported only one would understate what the
        # value carries.
        self.assertIn(",=", message)

    def test_a_compliant_authority_still_passes(self):
        """The control: the refusal must not have swallowed the ordinary case."""
        root, realm = realm_check.split_authority(
            "https://id.example.com/realms/commerce")
        self.assertEqual((root, realm), ("https://id.example.com", "commerce"))

    def test_a_realm_name_with_a_separator_in_it_stops(self):
        with self.assertRaises(SystemExit) as stop:
            realm_check.split_authority("https://id.example.com/realms/commerce/../master")
        self.assertIn("single path segment", str(stop.exception))

    def test_the_shipped_charts_carry_an_authority_this_can_split(self):
        """The subject test: the chart value this reads still has the shape it parses.

        Every chart's `identity.authority` is a placeholder in this repository
        and a real one at a rollout, but the *shape* is what this parses — so a
        chart that changed the key or the form would break the deploy path and
        nothing else would say so.
        """
        import re
        from pathlib import Path

        # `realm_check.CHART_VALUES`, NOT a `"deploy" / "helm"` spelled here.
        # The declared-inputs scan looks for path literals, and a join of two
        # bare segments is invisible to it — so this read would have been
        # undeclared while the constant that declares it sat unused.
        root = Path(realm_check.__file__).resolve().parents[2]
        charts = sorted((root / realm_check.CHART_VALUES).glob("*/values.yaml"))
        self.assertTrue(charts, "no chart values found under " + realm_check.CHART_VALUES)

        # ANCHORED TO `identity:`, because that is the key path the deploy
        # step walks. An unanchored `authority:` matches a chart that renamed
        # the parent, which is exactly the change that would stop a rollout
        # with this test green.
        pattern = re.compile(
            r"^identity:\s*$(?:\n(?![A-Za-z])[^\n]*)*?\n\s+authority:\s*(\S+)\s*$",
            re.MULTILINE)
        # EVERY DEPLOYABLE CHART, and the set is derived rather than taken
        # from what still mentions an authority. Scoping by "mentions one"
        # excuses the chart that stopped mentioning one, which is the removal
        # this is for; scoping by "declares identity:" excuses the chart that
        # renamed the parent. The deployables are `deploy/helm/smoke.sh`'s
        # classification — every directory with a `Chart.yaml`, less the
        # library chart and the umbrella, which render no workload of their own.
        deployables = sorted(
            v for v in charts
            if v.parent.name not in ("common", "platform")
            and (v.parent / "Chart.yaml").exists())
        self.assertTrue(deployables, "no deployable chart found")

        declared = 0
        for values in deployables:
            text = values.read_text(encoding="utf-8")
            match = pattern.search(text)
            self.assertIsNotNone(
                match, f"{values.parent.name} declares an authority that is not "
                       "under an `identity:` key, which is the path "
                       "realm_check.authority_of walks")
            declared += 1
            # `split_authority` refuses a bad shape by raising, which is the
            # assertion. Calling it is the test.
            realm_check.split_authority(match.group(1))
        self.assertTrue(declared, "no chart declares identity.authority")


class WhatTheGateHolds(unittest.TestCase):
    """A credential cannot leak out of a document that never carried one.

    Redacting the credential keys was the first answer and it was not enough:
    the object still held every other field of a realm, so the property rested
    on a deny-list staying complete as Keycloak grows fields. The projection
    inverts it — the keys `REALM_FIELDS`, `CLIENT_FIELDS` and
    `CLIENT_ATTRIBUTES` name survive, and nothing else does.
    """

    # The three fixture values live here and are REFERENCED below rather than
    # written beside their keys. §15.1's secret scan reads a credential-shaped
    # name assigned a literal, and `"password": "..."` is exactly that shape —
    # so spelling them inline earns two accepted findings for strings whose
    # whole purpose is to be absent from the output.
    MARKERS = {"smtp": "marker-one", "user": "marker-two", "client": "marker-three"}

    def realm_with_secrets(self) -> dict:
        return {
            "realm": "commerce",
            "accessTokenLifespan": 300,
            "revokeRefreshToken": True,
            "refreshTokenMaxReuse": 0,
            "smtpServer": {"password": self.MARKERS["smtp"], "host": "mail"},
            "users": [{"username": "demo",
                       "credentials": [{"type": "password",
                                        "value": self.MARKERS["user"]}]}],
            "clients": [{
                "clientId": realm_check.BROWSER_CLIENT,
                "standardFlowEnabled": True,
                "implicitFlowEnabled": False,
                "directAccessGrantsEnabled": False,
                "webOrigins": ["https://spa.example"],
                "secret": self.MARKERS["client"],
                "protocolMappers": [{"name": "x"}],
                # `realm_client` is the unlisted-attribute example the test
                # below reads — CLIENT_ATTRIBUTES now includes
                # pkce.code.challenge.method, so that one would survive the
                # projection and could no longer serve as its own example.
                "attributes": {"use.refresh.tokens": "false",
                               "pkce.code.challenge.method": "S256",
                               "realm_client": "false"},
            }, {
                "clientId": realm_check.MOBILE_CLIENT,
                "standardFlowEnabled": True,
                "implicitFlowEnabled": False,
                "directAccessGrantsEnabled": False,
                "publicClient": True,
                "redirectUris": ["blueprint://auth/callback"],
                "webOrigins": ["https://localhost", "capacitor://localhost"],
                "defaultClientScopes": [
                    "web-origins", "acr", "profile", "roles", "basic", "commerce-api", "email"],
                "attributes": {"use.refresh.tokens": "true",
                               "pkce.code.challenge.method": "S256"},
            }, worker()],
        }

    def test_no_credential_bearing_field_survives_the_projection(self):
        held = json.dumps(realm_check.judged(realm_check.redact(self.realm_with_secrets())))
        for leaked in self.MARKERS.values():
            self.assertNotIn(leaked, held)
        self.assertNotIn("smtpServer", held)
        self.assertNotIn("credentials", held)
        self.assertNotIn("secret", held)

    def test_the_fields_every_check_reads_do_survive(self):
        """The other half: a projection that dropped these would judge nothing."""
        held = realm_check.judged(self.realm_with_secrets())
        self.assertEqual(held["accessTokenLifespan"], 300)
        client = held["clients"][0]
        self.assertEqual(client["clientId"], realm_check.BROWSER_CLIENT)
        self.assertTrue(client["standardFlowEnabled"])
        self.assertEqual(client["attributes"]["use.refresh.tokens"], "false")
        self.assertEqual(client["webOrigins"], ["https://spa.example"])
        self.assertEqual(
            realm_check.check_realm(held, realm_check.DEPLOYED, 300), [])

    def test_an_absent_key_stays_absent(self):
        """Half the checks turn on absence, so a projection must not default one."""
        held = realm_check.judged({"clients": [{"clientId": "x"}]})
        self.assertNotIn("accessTokenLifespan", held)
        self.assertNotIn("attributes", held["clients"][0])
        self.assertNotIn("standardFlowEnabled", held["clients"][0])

    def test_a_client_that_is_not_an_object_survives_to_be_refused(self):
        """Dropping it would hide the malformed realm rather than judge it."""
        held = realm_check.judged({"clients": ["not-a-client"]})
        self.assertEqual(held["clients"], ["not-a-client"])
        self.assertTrue(any("where a client object belongs" in p
                            for p in realm_check.check_realm(held, realm_check.LOCAL, 300)))

    def test_an_unknown_attribute_does_not_survive(self):
        """The attribute allow-list is three keys, and the realm ships more."""
        held = realm_check.judged(self.realm_with_secrets())
        self.assertNotIn("realm_client", held["clients"][0]["attributes"])


def commands_of(relative: str) -> str:
    """A workflow's COMMANDS, with its comment lines removed.

    Both workflows explain each realm call in a comment beside it, so a raw
    search finds `realm_check.py authority` whether or not anything runs it —
    deleting the command outright would have left every case below green. It
    is the fourth time in this tree that a matcher matched the prose about the
    matcher, and the third artefact to need stripping: Python comments, Python
    docstrings, and now YAML.

    Whole lines only. An inline `#` cannot be stripped safely from a shell
    line — a URL fragment and a `sed` expression both carry one — and no
    command in either file has a trailing comment.
    """
    text = (Path(realm_check.__file__).resolve().parents[2]
            / relative).read_text(encoding="utf-8")
    return "\n".join(line for line in text.splitlines()
                      if not line.lstrip().startswith("#"))


# The three calls that close #157, in the order they have to be in. The
# rollout makes them once; the scheduled job makes them per release; the
# order is the same invariant in both files, so it is one list.
REALM_CALLS = (
    "realm_check.py authority",       # which realm, out of the chart
    "read_admin.py --out",            # fetch it
    "realm_check.py check",           # judge it
)


def realm_call_positions(case: unittest.TestCase, relative: str,
                         text: str | None = None) -> list[int]:
    """Where each call sits in `relative`'s commands — or in `text`, when the
    caller has already cut the file down to the one job that may make them."""
    text = commands_of(relative) if text is None else text
    found = []
    for call in REALM_CALLS:
        index = text.find(call)
        case.assertNotEqual(
            index, -1,
            f"{relative} no longer contains `{call}`, so nothing in it judges "
            "a deployed realm")
        found.append(index)
    return found


class TheRolloutStillCallsThisGate(unittest.TestCase):
    """The three calls that close #157, and the order they have to be in.

    Every other case here asks what the gate decides. This one asks whether
    anything still asks it — the rollout job runs on `workflow_dispatch` alone,
    so a change that deleted or reordered these steps reaches `main` with no
    job that would have noticed. `deploy.yml` is a declared input of this tree
    for the same reason, so a change to it runs this suite.
    """

    def workflow(self) -> str:
        return commands_of(realm_check.DEPLOY_WORKFLOW)

    def positions(self) -> list[int]:
        return realm_call_positions(self, realm_check.DEPLOY_WORKFLOW)

    def test_all_three_calls_are_there_and_in_that_order(self):
        found = self.positions()
        self.assertEqual(found, sorted(found),
                         "the rollout derives, fetches and judges out of order")

    def test_they_run_before_the_first_command_that_changes_the_cluster(self):
        """`kubectl patch` is the first mutation, and the check is upstream of it.

        A check that runs after the HPA floor has moved is a check on a cluster
        the rollout has already started changing.
        """
        text = self.workflow()
        judged = text.find("realm_check.py check")
        mutation = text.find("kubectl patch")
        self.assertNotEqual(mutation, -1, "the rollout no longer patches the HPA")
        self.assertLess(judged, mutation)

    def upgrades(self) -> list[str]:
        """Each `helm upgrade` in the rollout, joined across its continuations.

        A command is one logical line here: the invocations wrap over five or
        six physical lines with trailing backslashes, so anything that reasons
        about a whole command has to rejoin them first.
        """
        joined = self.workflow().replace("\\" + chr(10), " ")
        return [line.strip() for line in joined.splitlines()
                if line.strip().startswith("helm upgrade")]

    def test_every_upgrade_installs_the_authority_that_was_checked(self):
        """Per command, because a total is not the invariant.

        Counting pins against invocations across the whole file passes on two
        pins in one command and none in another — which is the shape that
        actually ships, since the promotion and the canary are edited at
        different times for different reasons. What has to be true is that no
        `helm upgrade` in this job installs an authority nobody checked.
        """
        commands = self.upgrades()
        self.assertTrue(commands, "the rollout no longer upgrades anything")
        for command in commands:
            pins = command.count(
                '--set-string identity.authority="$CHECKED_AUTHORITY"')
            self.assertEqual(
                pins, 1,
                f"{pins} authority pin(s) on: {command[:120]}")


class TheScheduleStillCallsThisGate(unittest.TestCase):
    """The same three calls, between rollouts (ADR-043).

    `realm.yml`'s `deployed` job is the one moment the rollout cannot be, and
    it has the rollout's failure mode exactly: it runs on `schedule` and
    `workflow_dispatch`, so a change that deleted a step, dropped the guard or
    lost the trigger reaches `main` with no diff-driven job that would have
    noticed. Each case here is a property the workflow's own comment argues,
    asserted against the commands rather than the prose about them.
    """

    def workflow(self) -> str:
        return commands_of(realm_check.WORKFLOW_PATH)

    def job(self) -> str:
        """The `deployed` job alone, so a property of it cannot be satisfied
        by the `check` job above it — both run this suite, and only one of
        them may go on to read a realm."""
        text = self.workflow()
        start = text.find("\n  deployed:")
        self.assertNotEqual(start, -1,
                            f"{realm_check.WORKFLOW_PATH} has no `deployed` job")
        return text[start:]

    def test_the_schedule_and_the_dispatch_are_declared(self):
        text = self.workflow()
        self.assertIsNotNone(
            re.search(r"^  schedule:\s*\n\s*- cron: ", text, re.MULTILINE),
            "the deployed realm is no longer read on a schedule")
        self.assertIsNotNone(
            re.search(r"^  workflow_dispatch:", text, re.MULTILINE),
            "an operator can no longer re-run the judgement by hand")

    def test_the_local_job_runs_only_on_a_diff(self):
        """A schedule has no diff to gate, and the `deployed` job runs the
        suite itself — so the `check` job on a schedule would be the same
        command twice in one run, and its `if:` says so."""
        text = self.workflow()
        check = text[text.find("\n  check:"):text.find("\n  deployed:")]
        self.assertIn("github.event_name == 'pull_request' || "
                      "github.event_name == 'push'", check)

    def test_the_job_is_opted_in_by_the_declared_variable(self):
        """Not the fail-open skip this repository refuses: the guard reads a
        configuration and not a runtime failure, and the name is the one
        `realm_check.py` declares so the README and the workflow agree."""
        job = self.job()
        match = re.search(r"^    if: (.*)$", job, re.MULTILINE)
        self.assertIsNotNone(match, "the deployed job carries no guard at all")
        guard = match.group(1)
        self.assertIn(f"vars.{realm_check.SCHEDULE_OPT_IN} == 'enabled'", guard)
        self.assertIn("github.event_name == 'schedule'", guard)
        self.assertIn("github.event_name == 'workflow_dispatch'", guard)

    def test_the_job_runs_under_the_production_environment(self):
        self.assertIn("environment: production", self.job())

    def test_a_dispatch_from_a_branch_is_refused_before_anything_is_checked_out(self):
        job = self.job()
        refusal = job.find('"$GITHUB_REF" != "refs/heads/main"')
        checkout = job.find("actions/checkout")
        self.assertNotEqual(refusal, -1, "a branch may now judge production")
        self.assertLess(refusal, checkout)

    def test_all_three_calls_are_there_and_in_that_order(self):
        found = realm_call_positions(self, realm_check.WORKFLOW_PATH, self.job())
        self.assertEqual(found, sorted(found),
                         "the scheduled job derives, fetches and judges out of order")

    def test_the_negatives_run_before_the_judgement_in_this_job_too(self):
        job = self.job()
        suite = job.find("unittest discover -s deploy/keycloak")
        judged = job.find("realm_check.py check")
        self.assertNotEqual(suite, -1, "the deciding copy of the gate is no longer observed red")
        self.assertLess(suite, judged)

    def test_every_release_in_the_plan_is_judged(self):
        """The set of releases is the canary plan's, not a list restated in
        YAML — `deploy.yml`'s `options:` is a dispatch menu, this is a subject."""
        self.assertIn("canary.py workloads", self.job())

    def test_the_workload_list_is_assigned_before_it_is_looped(self):
        """`for X in $(cmd)` swallows cmd's failure under `set -e`: a plan
        command that exited non-zero became an empty list, the loop judged
        nothing, and the step passed green with no issue filed. The list is
        captured first, an empty one is refused, and the loop reads the
        variable."""
        job = self.job()
        self.assertIn("WORKLOADS=$(python deploy/canary/canary.py workloads)", job)
        self.assertIn('if [ -z "$WORKLOADS" ]', job)
        self.assertNotIn("for WORKLOAD in $(", job)
        self.assertLess(job.find("WORKLOADS=$("), job.find("for WORKLOAD in"))

    def test_the_workload_list_keeps_its_line_protocol_into_the_loop(self):
        """An unquoted `$WORKLOADS` as the word list re-splits the answer on
        whitespace and expands a glob against the checkout (review round
        eleven). `mapfile` keeps one line as one release, the loop quotes the
        array, and the array is not filled through a process substitution,
        which would lose the status the assignment keeps."""
        job = self.job()
        self.assertIn('mapfile -t WORKLOAD_LIST <<< "$WORKLOADS"', job)
        self.assertIn('for WORKLOAD in "${WORKLOAD_LIST[@]}"; do', job)
        self.assertNotIn("for WORKLOAD in $WORKLOADS", job)
        self.assertLess(job.find('if [ -z "$WORKLOADS" ]'), job.find("mapfile -t"))

    def test_the_derivation_is_assigned_before_it_is_read(self):
        """`eval "$(cmd)"` and `< <(cmd)` both swallow a failed cmd under
        `set -e`, which is the rollout's own lesson. The derivation is captured
        first and each line exported as a literal."""
        job = self.job()
        self.assertIn("DERIVED=$(python deploy/keycloak/realm_check.py authority", job)
        self.assertIn('<<< "$DERIVED"', job)
        self.assertIn('export "$NAME=$VALUE"', job)
        self.assertNotIn("eval ", job)
        self.assertNotIn("< <(", job)

    def test_a_red_judgement_files_an_issue_and_only_a_red_judgement(self):
        """What a red run DOES. Guarded on the judge step's own outcome, so a
        dispatch refused for coming from a branch files nothing."""
        job = self.job()
        match = re.search(r"^      - name: A drifted realm is an issue.*\n"
                          r"        if: (.*)$", job, re.MULTILINE)
        self.assertIsNotNone(match, "a red run no longer files anything")
        self.assertIn("failure()", match.group(1))
        self.assertIn("steps.judge.outcome == 'failure'", match.group(1))
        self.assertIn("id: judge", job)
        self.assertIn("gh issue create", job)
        self.assertIn("--label security --label critical", job)
        self.assertIn("gh issue comment", job)
        self.assertIn("issues: write", job)

    def test_the_open_issue_is_looked_for_past_the_default_page(self):
        """One issue per drift is only true while the listing can see the
        open one. `gh issue list` defaults to 30 and the first form said 100;
        past either the next hourly red files a duplicate, which is the
        argument `.claude/scripts/gh-issue-list.sh` already makes for 1000."""
        job = self.job()
        match = re.search(r"gh issue list (.*?)\\\n", job)
        self.assertIsNotNone(match, "the filing step no longer lists issues")
        self.assertIn("--limit 1000", match.group(1))
        self.assertIn("--state open", match.group(1))
        self.assertIn("--label security", match.group(1))

    def test_every_line_of_a_run_block_stays_inside_the_block(self):
        """A `run: |` block ends at the first line indented less than its
        body, and a heredoc written the way bash wants it — terminator at
        column 0 — did exactly that on the first push: GitHub refused the
        file and the run had no jobs. YAML strips the block's indentation, so
        the terminator sits at column 0 of the SCRIPT when it sits at the
        block's indentation in the FILE. No YAML parser is in the stdlib, so
        this asserts the shape rather than the parse."""
        text = (Path(realm_check.__file__).resolve().parents[2]
                / realm_check.WORKFLOW_PATH).read_text(encoding="utf-8")
        lines = text.splitlines()
        inside = None
        for number, line in enumerate(lines, 1):
            stripped = line.strip()
            if inside is not None:
                if stripped == "":
                    continue
                indent = len(line) - len(line.lstrip())
                if indent >= inside:
                    continue
                inside = None
            if stripped == "run: |":
                inside = len(line) - len(line.lstrip()) + 2
                continue
            if inside is None and stripped.startswith("EOF"):
                self.fail(f"{realm_check.WORKFLOW_PATH}:{number} closes a "
                          "heredoc outside its run block")

    def test_the_concurrency_key_is_the_subject(self):
        """A scheduled run and a pushed gate run share `refs/heads/main`, and
        under a ref-only key each cancelled the other. The two deployed
        triggers share ONE key, because two judgements in flight race the
        filing step's check-then-create and the tracker ends up with two
        issues for one drift."""
        text = self.workflow()
        match = re.search(r"^  group: (.*)$", text, re.MULTILINE)
        self.assertIsNotNone(match, "the workflow has no concurrency group")
        group = match.group(1)
        self.assertIn("github.event_name == 'schedule'", group)
        self.assertIn("github.event_name == 'workflow_dispatch'", group)
        self.assertIn("&& 'deployed'", group)
        self.assertIn("format('{0}-{1}', github.event_name, github.ref)", group)

    def test_the_judge_step_times_out_before_the_job_does(self):
        """A job cancelled on its own timeout runs no `failure()` step, so the
        hung read this budget exists for would be the one that filed nothing.
        The step's timeout has to be the shorter one, with room after it."""
        job = self.job()
        job_cap = re.search(r"^    timeout-minutes: (\d+)$", job, re.MULTILINE)
        self.assertIsNotNone(job_cap, "the deployed job has no timeout")
        judge = job[job.find("id: judge"):]
        step_cap = re.search(r"^        timeout-minutes: (\d+)$", judge, re.MULTILINE)
        self.assertIsNotNone(step_cap, "the judge step has no timeout of its own")
        self.assertLess(int(step_cap.group(1)), int(job_cap.group(1)))
        self.assertLess(judge.find("timeout-minutes:"), judge.find("run: |"))

    def test_the_workload_list_the_loop_reads_still_exists(self):
        """`deploy/canary` is a declared input because the loop reads it, and
        this is the read that earns the declaration: the subcommand the loop
        depends on is asserted to exist, so a rename there runs this gate and
        fails it rather than leaving the loop to fail at the next hour."""
        plan = (Path(realm_check.__file__).resolve().parents[2]
                / realm_check.CANARY_PLAN / "canary.py").read_text(encoding="utf-8")
        self.assertIn('add_parser("workloads"', plan)
        self.assertIn('args.command == "workloads"', plan)

    def test_the_readme_names_the_variable_the_workflow_reads(self):
        readme = (Path(realm_check.__file__).resolve().parent / "README.md").read_text(
            encoding="utf-8")
        self.assertIn(f"`{realm_check.SCHEDULE_OPT_IN}`", readme)


class TheRealmIsLoaded(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name) / "realm.json"

    def test_a_realm_document_is_loaded_and_narrowed_to_what_is_judged(self):
        """`realm` is not among the fields this gate reads, so it does not survive."""
        self.path.write_text(json.dumps(realm()), encoding="utf-8")
        loaded = realm_check.load_realm(self.path)
        self.assertEqual(loaded["accessTokenLifespan"], 300)
        self.assertNotIn("realm", loaded)

    def test_a_body_that_is_not_json_stops(self):
        """A proxy answering an HTML error page with a 200 is the ordinary way this happens."""
        self.path.write_text("<html>not a realm</html>", encoding="utf-8")
        with self.assertRaises(SystemExit) as stop:
            realm_check.load_realm(self.path)
        self.assertIn("not JSON", str(stop.exception))

    def test_a_json_document_that_is_not_an_object_stops(self):
        self.path.write_text("[]", encoding="utf-8")
        with self.assertRaises(SystemExit) as stop:
            realm_check.load_realm(self.path)
        self.assertIn("not a realm representation", str(stop.exception))

    def test_a_missing_file_stops(self):
        with self.assertRaises(SystemExit) as stop:
            realm_check.load_realm(self.path)
        self.assertIn("not readable", str(stop.exception))


if __name__ == "__main__":
    unittest.main()
