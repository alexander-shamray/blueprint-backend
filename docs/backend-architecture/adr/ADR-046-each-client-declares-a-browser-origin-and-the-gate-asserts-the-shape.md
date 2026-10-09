# ADR-046 — Each client declares a browser origin, and the gate asserts the shape

**Decision.** Every client the realm gate names declares a non-empty
`webOrigins`, and `mobile-app`'s holds the packaged app's own browser origins.
`realm_check.py`'s `check_web_origins` asserts the **obligation and not the
values**: the field is present, it is a non-empty array, `*` is refused, `+`
is refused on a client whose `redirectUris` imply no browser origin, and every
other entry must equal the canonical origin a browser would send — a scheme, a
host, and a port only where the scheme does not imply one. What those origins
*are* is not asserted, because the file that decides them is not in this
repository. The check judges `web-app` on the same terms as `mobile-app`, and
Keycloak's own built-in clients on neither.

**Why.** `redirectUris` and `webOrigins` are two fields describing two
different hops, and treating the first as though it covered the second is how
the native client shipped unable to sign anybody in. The authorization request
leaves the app for the system browser and returns through
`blueprint://auth/callback`, which `redirectUris` correctly names. The **token
exchange is a second request, made by the app's own page** — from the
WebView's own loopback origin, which differs by platform and is the Compose
export's to state — straight to Keycloak, and `webOrigins` is the only thing
that decides whether the script that made it may read the answer. `mobile-app`
declared none, so Keycloak granted none.

**Nothing in the system could report it, which is what makes it a gate's job
rather than a test's.** The request carries
`application/x-www-form-urlencoded` and, on a public client, no `Authorization`
header — both CORS-safelisted, so there is no preflight to fail. The request
goes out, Keycloak validates the code and mints a token, and the browser
discards the response because no `Access-Control-Allow-Origin` came back with
it. The authorization code is spent, `fetch` rejects with a bare `TypeError`,
and the server logs a success. It was found by reading the realm against the
client, in the sibling repository's
[issue #6](https://github.com/alexander-shamray/blueprint-frontend/issues/6);
its own unit suite could not have found it, because those tests inject a
`fetch` mock and the one thing a mock cannot do is refuse to hand over a real
response.

**Correcting the export alone would have fixed the one realm this repository
owns and left every other realm broken in exactly the same silence.** This
repository provisions no realm and holds only §14.1's Compose export;
[ADR-042](ADR-042-the-deployed-realm-is-checked-at-deploy-time.md) and
[ADR-043](ADR-043-the-deployed-realm-is-checked-between-rollouts.md) point the
same predicate at whichever realm a deployment uses, at a rollout and on a
schedule. So the obligation goes in the predicate, and the export is then one
realm that satisfies it rather than the whole of the fix — the same reasoning
[ADR-044](ADR-044-the-native-client-holds-a-refresh-token-and-the-realm-rotates-it.md)
gives for every other setting on this client.

**The values stay out of the predicate because this repository cannot see
them.** A packaged app's browser origin is whatever `capacitor.config.ts` says —
`androidScheme` and `iosScheme` — and that file is in `blueprint-frontend`,
which this repository neither owns nor reads. Either setting moving moves the
origin, and nothing here would know. A gate pinning the literal pair would
therefore fail a realm that had been *corrected* rather than one that had
drifted, and the correction would arrive as a red gate in this repository for
a change made in another. What can be judged without owning that file is the
shape, and all three ways this fails silently are shape: nothing granted,
everything
granted, and an entry no browser will ever send.

**`+` is judged against the client rather than refused outright**, because it
means opposite things on the two clients here. Keycloak reads it as the origins
the client's own `redirectUris` imply, which on `web-app` resolves to real
origins and on `mobile-app` — whose only redirect is a custom scheme — resolves
to none. Banning it would fail a deployed `web-app` that was configured
correctly; accepting it everywhere would accept, on the native client, a value
that grants exactly as much as the empty array this record exists to refuse and
reads in a console as though the question had been answered.

**The canonical-origin equality is `Gateway.Api`'s, brought over
deliberately.** That guard decides the same question by one equality against a
canonical form rather than by enumerating the ways a string can fail to be an
origin, and its own comment in `Program.cs` argues why — the ways a string can
*be* an origin are finite and the ways it can fail are not. Keycloak compares
the `Origin` header as text, so the reasoning transfers intact. The one
difference is that a custom scheme passes: `WithOrigins` feeds ASP.NET Core's
matcher, so the gateway refuses anything but http(s) at startup, where a realm
entry is a string Keycloak compares and the iOS WebView's scheme is one it
will be handed.

**Consequences.**

- **This grants CORS to a named origin on a secretless public client, and the
  widening is smaller than it reads.** CORS decides whether a *script* may read
  a response, never whether a request may be made — the token endpoint still
  demands an authorization code and its PKCE verifier, neither of which an
  attacker's page has. The Android loopback origin is shared by every
  Capacitor app on a device, which sounds like the objection and is not one:
  another app on that device can already make the identical request from
  native code, where CORS has never applied. What the grant adds is that a
  page at that origin may read the answer, and on a packaged device the page
  there is this app's own.
- **The gate cannot tell a right origin from a wrong one, and says so.** A
  realm naming an origin the app does not actually send passes this check and
  fails on the device exactly as before. Nothing this gate can read closes
  that gap: only a packaged build reaching the stack establishes that the
  origin in the realm is the origin the app sends, and that round trip belongs
  to `blueprint-frontend`. This record asserts the shape and does not pretend
  to substitute for it.
- **The iOS origin cannot be given to the gateway, and this record does not
  take that decision.** `Gateway.Api` refuses a `Cors:Origins` entry whose
  scheme is not http(s), at startup and on purpose. So a packaged iOS build
  calling the **gateway** from its WebView needs either that guard relaxed —
  which pairs a custom scheme with `AllowCredentials()` and is not an obvious
  yes — or `iosScheme: 'https'` in the sibling repository, so that both
  platforms present the same http(s) loopback origin. **This record covers the
  realm hop only**, and reading it as having settled the gateway hop is the
  mistake the two hops' similarity invites.
- **`web-app` is now judged on an obligation it already satisfied**, which is
  the point rather than an accident: the obligation belongs to the token
  exchange and not to a platform, and a console click clearing that field would
  have broken the browser client the same silent way.
- **The projection grew a field**, so `judged` and everything downstream of it
  — including `read_admin.py`'s deploy-time fetch — now carry `webOrigins`. A
  realm edited to clear it is a red gate rather than a sign-in that stops
  working, which is the whole trade this record buys.
- **§11.2's native-client note is amended** to state the obligation and cite
  this record for it; the literal origins live in the Compose export and are
  restated nowhere.

**Amended by [ADR-089](ADR-089-the-browser-origin-obligation-binds-the-clients-that-exchange-a-code-from-a-page.md)**,
which binds the origin obligation to `web-app` and `mobile-app`, the clients
that exchange a code from a page, and not to every client the gate names.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
