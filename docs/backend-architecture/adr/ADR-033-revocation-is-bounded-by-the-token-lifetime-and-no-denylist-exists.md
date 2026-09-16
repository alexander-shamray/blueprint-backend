# ADR-033 — Revocation is bounded by the token lifetime, and no denylist exists

**Decision.** This platform accepts a **bounded revocation window** of **330
seconds** — the access-token lifetime of 300, stated normatively in
[§11.3](../11-identity-authorization.md), plus the 30-second `ClockSkew` a
lifetime check adds to `exp`. Both settings are part of the bound, and only
one of them is the realm's. There is no token denylist and no
introspection call: a service validates signature, issuer, audience and
lifetime locally and consults nothing else. [ADR-006](ADR-006-redis-for-cache-and-coordination-never-as-a-store-of-record.md)'s
listing of "the token denylist" among Redis's contents is withdrawn. The
`{service}:denylist:` keyspace keeps its row in [§8.1](../08-caching-redis.md)'s
table as a **reservation**, on the terms `{service}:ratelimit:` already has, and
`RedisKeys` spells no member for it.

**Why.** The claim had no consumer and read as a control. `RedisKeys.Denylist`
existed, §8.1 gave the keyspace the strictest eviction policy in the platform,
and ADR-006 recorded the decision — while §11.3's own prose already admitted
that observing revocation "needs introspection or a deny list, neither of which
this platform has". Three sites implied a mechanism the fourth denied, and a
reviewer asking "is revocation handled" met a yes.

Building the consumer instead was considered and refused on four measurements,
not on preference. **Two of the four hosts have no Redis at all** — the gateway
and the BFF each carry one project reference and neither calls
`AddRedisConnections` — and the gateway is the edge every external request
enters. A `JwtBearerEvents` handler resolves per request, so `ValidateOnBuild`
cannot see the gap and the gateway would throw on its first authenticated
request; making the lookup optional instead would mean the edge silently never
checks, which is the fail-open shape §12's gate-coverage rule refuses.
**The keyspace is per-service and the ACL enforces it**: `RedisKeys` prefixes
with `ApplicationName` verbatim and §8.1 provisions `~{ApplicationName}:*` from
the same value, so one revocation is N writes against a host inventory nothing
enumerates. **There is no producer**, and no chapter specifies one — the realm's
`web-bff` client sets `backchannel.logout.session.required` with no
`backchannelLogoutUrl`, so a consumer shipped alone is a check that always
misses. And a mechanism no ADR decided is a design change to raise rather than
take.

**Consequences.** Logging a user out, disabling an account, or responding to a
stolen token at Keycloak has no effect on an already-issued access token for up
to 330 seconds. That is now a recorded number rather than a realm default
nobody chose, and `RealmImportTests` pins the lifetime half of it — the
chapter's figure and the realm's are two statements about one fact, and the
test is what keeps them from drifting apart. **Shortening the window is a realm
edit, a chapter edit and possibly a `ClockSkew` edit**, because the bound is a
sum: 300 from the realm and 30 from `TokenValidationParameters`, and cutting
the lifetime alone leaves the skew where it is.

> **The two halves of that number are enforced in different places, and only
> one of them travels.** `ClockSkew` is `Common.Web`'s, set in code and pinned
> by `JwtAuthenticationTests`, so every host that composes
> `AddCommonWebDefaults` carries the 30 wherever it is deployed. The lifetime
> is the realm's, and the realm this repository owns is
> `deploy/compose/keycloak/realm-export.json` — [§14.1](../14-local-development.md)'s
> Compose realm, which is what `RealmImportTests` reads. Every chart points at
> `https://id.example.com/realms/commerce`, an externally provisioned realm
> this repository holds no configuration for and runs no deploy-time check
> against, so a deployed realm can issue five-hour access tokens while every
> sentence here still reads 300 seconds.
>
> **So the bound is half-guaranteed rather than local**, and saying "both
> halves are local" — as an earlier draft of this callout did — contradicts
> the Decision above, which is careful to say only one of the two settings is
> the realm's.
>
> **That division is the one §15.4 already draws for every Secret**, and the
> realm half is stated rather than closed for the same reason: the charts
> create no Secrets and provision no realm, so the platform's identity provider
> is somebody's operational input and not this repository's artefact. What this
> repository can honestly claim is the *shape* — the settings that must hold,
> the number they must hold at, and a test proving the one realm it owns holds
> them. **A deployed realm owes `accessTokenLifespan` 300 and no client-level
> `access.token.lifespan` override**; it owes no `ClockSkew`, which is not a
> realm setting at all and is why telling an operator to configure one would
> send them looking for something that does not exist.
> [#157](https://github.com/alexander-shamray/blueprint-backend/issues/157)
> carries that gap and the three shapes a fix could take, because a gap this
> record merely described would be the TODO nothing re-checks.

**This is the whole of the revocation story**, so a future denylist
is a decision that supersedes this record rather than a gap someone may quietly
fill: it would owe a producer, a consumer reachable from every host including
the two with no Redis today, and a fan-out rule across per-service keyspaces.

> **The callout above is amended by
> [ADR-040](ADR-040-no-host-accepts-a-token-with-more-life-left-than-the-revocation-bound.md),
> and nothing here has been edited.** The decision this record took — a bounded
> revocation window of 330 seconds, no denylist and no introspection call — is
> untouched and still binding, and so is the arithmetic behind it.
>
> **What moved is where the number is held to.** This record says a deployed
> realm can issue five-hour access tokens while every sentence here still reads
> 300, that the realm half is stated rather than closed, and that the bound is
> therefore half-guaranteed. **All three of those remain true**, and ADR-040
> does not discharge the obligation this callout states: every host now refuses
> a token carrying more than the 330 seconds above, whatever realm issued it,
> which bounds what an unchecked realm can cost without checking it. A
> five-hour token is refused for most of its life and admitted in its last
> window, so the realm still owes `accessTokenLifespan` 300 and no client-level
> override, and nothing here reads a deployed realm to find out. **The
> `ClockSkew` sentence stands exactly as written**, and is worth keeping where
> an operator reaching for a realm setting will read it.

> **The realm half of this callout is now *observed* — by
> [ADR-042](ADR-042-the-deployed-realm-is-checked-at-deploy-time.md) — and
> nothing here has been edited.** Observed and not owned, which is why the
> amendment above can go on saying the realm half is stated rather than closed
> and the bound half-guaranteed: both remain true of what this repository
> *provisions*. The decision this record took — a bounded
> revocation window of 330 seconds, no denylist and no introspection call — is
> untouched and still binding, and so is the arithmetic and the `ClockSkew`
> sentence the amendment above already declined to move.
>
> **What moved is the sentence saying nothing here reads a deployed realm.**
> `deploy/keycloak/realm_check.py` reads one, from `deploy.yml`'s rollout job,
> before the first step that touches a cluster — so a realm issuing five-hour
> access tokens now fails the deploy rather than being deployed onto. The two
> sentences this record is careful about stay careful: only one of the two
> settings in the bound is the realm's, and the realm this repository *owns* is
> still the Compose one. What has changed is not ownership but observation, and
> those were being run together — "holds no configuration for" and "runs no
> deploy-time check against" were one clause here, and only the second half of
> it has been paid.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
