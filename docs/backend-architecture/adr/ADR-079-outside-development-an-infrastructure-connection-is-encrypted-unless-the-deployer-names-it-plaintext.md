# ADR-079 — Outside Development, an infrastructure connection is encrypted unless the deployer names it plaintext

**Decision.** Outside Development, a host refuses to start on an
infrastructure connection that would cross the network unencrypted or
unverified: a `ConnectionStrings:RabbitMq` that is not `amqps://`, a
`RedisCache` or `RedisCoordination` string without `ssl=true`, and a SQL Server
string that sets `Encrypt` to `False` or `Optional` or trusts any server
certificate. A deployer who runs a hop in plaintext on purpose names that
connection under `Transport:Plaintext`, one entry per name.
`TransportSecurity.Refusal` in `Common.Infrastructure` holds the rule and names
a connection, never its value; each service's registration and the BFF's add
it as a start-up check that runs before any hosted service starts, each Redis
connection, which a hosted service's constructor can open before that check
runs, applies it before connecting, each migrator, which is never started,
calls it before it builds through its own Infrastructure's
`MigratorTransport`, the one path [§4.2](../04-solution-structure.md) leaves
it, and `TransportSecurityTests` holds every project under `src/` that reads a
connection string, the building blocks aside, to one of the two.
**Why.** A host's own HTTP hops are plain by
[§10.1](../10-api-gateway.md)'s decision because each is a Service name inside
the cluster, and a third party, the token endpoint and the SMTP relay already
refuse plaintext outside Development. The three infrastructure connections are
neither:
[ADR-065](ADR-065-every-workload-is-fenced-by-a-default-deny-networkpolicy.md)
states the database, Redis and the broker as peers outside the namespace,
possibly an `ipBlock` outside the cluster, and nothing in a string says which.
Their strings carry the per-service credential and were checked by nothing, so
an `amqp://` broker, a Compose-shaped Redis string or a copied
`TrustServerCertificate=True` reached a deployment unchanged and could send that
credential and every payload in the clear; no chapter had decided otherwise.
The deployer, who knows where each peer lives, is the one to say a plaintext
hop is in-cluster, so the host refuses until it is told. SqlClient encrypts and
validates by default, so only an explicit downgrade is refused there; the
broker client and StackExchange.Redis default to plaintext, so for those the
scheme or `ssl=true` is required.
**Consequences.** The charts set no `Transport:Plaintext`
([§15.4](../15-cicd-deployment.md)), so a cluster that reaches its broker, Redis
or SQL Server in plaintext, in-cluster behind
[ADR-065](ADR-065-every-workload-is-fenced-by-a-default-deny-networkpolicy.md)'s
NetworkPolicy say, gives those hops TLS or has its deployer set the key, which
the refusal names, on a pod through an `extraConfigMaps` entry. The migration
Job renders its connection string and nothing else
([§14.3](../14-local-development.md)), so a migrator's SQL hop in a cluster is
encrypted and verified, with no opt-out. The charts' broker fence defaults to
5671, the amqps port, so a deployer who names the broker plaintext also sets
`networkPolicy.broker.port` to the port it listens on. The opt-out is by name
and deliberate, and it is also the residual: a named connection crosses the
network in the clear, fenced only by ADR-065's policy where the CNI enforces it.
The integration suites' containers are such hops, and their job hosts, which run
as Production, name their SQL connections as a deployer would. The rule reads
connection strings, not the network, so a TLS connection to the wrong peer is
not its to catch; `tools/bff-replay` is an operator's tool, not a host, and
reads no environment name, so its six strings are its operator's to choose; and
Development keeps Compose's plaintext defaults
([§14.1](../14-local-development.md)).

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
