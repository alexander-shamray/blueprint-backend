# Runbook — cache hit ratio collapse

| | |
|---|---|
| Alert | `CacheHitRatioCollapse`, in `deploy/observability/alerts/awaiting-signal.yaml` — **not loaded yet** |
| Condition | hits / (hits + misses) < 50% over 10 minutes |
| Signal | **Owed — an instrument.** See the callout below |
| Owner | Platform ([§13.8](../backend-architecture/13-observability.md)) |

> **This alert cannot fire today, and what it is owed is the instrument.** It
> used to be owed two things; the second closed when §8.5's PR gave
> `AddRedisConnections` its first callers, and the remaining half is the one
> that was always the harder of the two.
>
> §13.2's `AddObservability` *does* register the
> `Microsoft.Extensions.Caching.Hybrid` meter. **The package publishes no
> meter.** `Microsoft.Extensions.Caching.Hybrid` 10.0.0 references
> `System.Diagnostics.Tracing` and not `System.Diagnostics.Metrics` — it
> reports through `HybridCacheEventSource` with `PollingCounter`, which is
> EventCounters — so that `AddMeter` line collects nothing and would still
> collect nothing with the cache in use. A registered meter with no publisher
> is exactly the trap §13.6 warns about, and the `AddMeter` line is what makes
> it look wired.
>
> The consumer half is closed as §15.4 measures it: Catalog and Ordering both
> call `AddRedisConnections` since §8.5's PR, so both hosts REGISTER the
> cache stack and the two Redis rows are required. **Registering is not
> constructing**, and an earlier revision of this callout said a `HybridCache`
> was constructed in each: DI builds a singleton when something resolves it,
> and nothing in `src/` injects one. So there are three states here, not two —
> no caller, a caller, and a cache anything actually reads — and the alert is
> waiting on none of them. A meter with no publisher reports nothing whichever
> state holds.
>
> So the signal needs an **instrument**: an EventCounters-to-OTel bridge, or a
> package version that publishes a meter. Taking the cache was necessary and
> was never sufficient, which two earlier drafts of this file each got wrong in
> a different direction.
>
> Everything below applies from the moment it arrives. Until then it is a
> procedure written ahead of its alert, which is what §13.9 asks for — and the
> Redis commands in it work today regardless, because they read the server
> rather than the metric.

## What it means

Redis lost its working set. Every miss becomes a database read, and **the
databases are sized for a warm cache**
([ADR-006](../backend-architecture/adr/ADR-006-redis-for-cache-and-coordination-never-as-a-store-of-record.md)) — so the immediate risk
is not the cache, it is the database behind it falling over under a load it was
never provisioned for.

Expect this alert to arrive with, or just before,
[`latency.md`](latency.md).

## Confirm it is coldness, not absence

Three different faults produce a low ratio and they need different responses.

**Name the instance and authenticate.** §8.1 runs **two** Redis deployments —
an `allkeys-lru` cache and a `noeviction` coordination instance — and every
connection carries a per-service ACL user, so an unqualified `deploy/redis`
with no credentials either reaches the wrong instance or answers `NOAUTH`.
Take an operational credential from the vault (§15.4), not the §14.1 Compose
default:

**The password has to be set *inside* the container.** `kubectl exec` does not
forward local environment variables, so exporting `REDISCLI_AUTH` in your own
shell leaves `redis-cli` unauthenticated and every command answers `NOAUTH` —
which reads as a broken procedure rather than a missing credential. Pipe it in
on stdin instead: it never reaches `argv`, and it never depends on forwarding
that does not happen.

```bash
ns=<namespace>
deploy=redis-cache          # the allkeys-lru instance, not coordination
operator=<operator-acl-user>

# From the vault (§15.4), never §14.1's Compose default. Read, not typed into a
# command, so it reaches neither the shell's history nor a process list.
read -rs -p 'operator password: ' OPERATOR_PASSWORD; echo
redis() {
    printf '%s' "$OPERATOR_PASSWORD" |
        kubectl -n "$ns" exec -i deploy/"$deploy" -- sh -c '
            REDISCLI_AUTH=$(cat); export REDISCLI_AUTH
            redis-cli --user "$0" --no-auth-warning "$@"' "$operator" "$@"
}

redis info stats       | grep -E 'keyspace|evicted'
redis info memory      | grep -E 'used_memory_human|maxmemory'
redis info replication
```

`REDISCLI_AUTH` inside the container keeps the password off that process list
too, which `redis-cli -a` does not.

- **Redis restarted or failed over.** Keyspace near zero, uptime low. The cache
  is cold and will warm; the job is to survive the warming.
- **Eviction under memory pressure.** `evicted_keys` climbing, `used_memory`
  near `maxmemory`. The working set no longer fits — this does not warm up, it
  keeps evicting.
- **Redis is unreachable.** Every read is a miss because nothing answers.
  HybridCache degrades to its L1 and then to the source, so the application
  keeps working and only the graph and the database load say so.

**§8.1's two-instance split matters here.** The cache instance is
`allkeys-lru`; the coordination instance is `noeviction` and holds distributed
locks. A hit-ratio collapse on the *cache* is a performance incident. The same
symptom on coordination means locks are being evicted, which is a correctness
incident and a different, worse conversation.

## Shed load while it warms

The databases are the thing at risk, so protect them first. In order of
preference:

1. **Tighten the edge.** §10.3's rate-limit policies are the one lever that
   works without a deploy and without touching the services. Reducing the
   permitted rate on the heaviest read routes buys the cache time to fill.
2. **Scale the read replicas** if the read path uses them.
3. **Scale the services out** — but only if the trace shows request queuing
   rather than database saturation. More replicas against a saturated database
   make it worse.

**Do not disable the cache to "reduce complexity" during the incident.** Every
read then goes to the database permanently rather than temporarily.

## Warming it

Usually nothing to do: normal traffic refills an LRU cache within minutes, and
§8.2's HybridCache stampede protection means a thousand concurrent misses on one
key produce one database read rather than a thousand.

Where a deliberate warm is wanted, drive the read endpoints for the hottest keys
rather than writing into Redis directly — a hand-populated entry with the wrong
key shape or no TTL is a bug that outlives the incident, and §8.1 enforces a
mandatory TTL **in code** precisely because entries without one accumulate.

## If it is eviction rather than restart

This is the case that does not resolve itself.

- Check what grew. A new cache consumer with a large value, or a TTL that was
  raised, changes the working-set size.
- Check the key namespaces. §8.1 partitions by
  `{service}:cache|lock|idem|denylist:`, and a service writing outside its
  prefix is both a bug and a capacity surprise. The list is the reserved set
  and not an inventory of what is live: **nothing writes `denylist:`**, which
  [ADR-033](../backend-architecture/adr/ADR-033-revocation-is-bounded-by-the-token-lifetime-and-no-denylist-exists.md)
  records as a decision rather than a gap, so an empty `denylist:` namespace is
  the healthy reading and a key found under it is the anomaly worth chasing.
- Raising `maxmemory` is a real fix if the working set genuinely grew. Raising
  it repeatedly is a cache being used as a database.

## Closing it

The ratio should climb back above 50% and keep climbing. Watch database load
come down with it — if the ratio recovers and the database stays hot, the load
was never cache-driven and the incident is somewhere else.
