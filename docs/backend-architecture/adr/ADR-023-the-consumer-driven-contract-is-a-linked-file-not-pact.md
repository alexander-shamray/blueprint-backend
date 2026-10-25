# ADR-023 — The consumer-driven contract is a linked file, not Pact

**Decision.** [Appendix C](../appendix-c-delivery-plan.md)'s PR-26 is delivered as
one C# source file — `tests/Web.Bff.TestSupport/PricingContract.cs` — written by
the consumer, compiled into the consumer's suite, and **linked** into the
provider's exactly as `pricing.proto` is linked into `Web.Bff`. **Pact is not
adopted**: no package, no broker, no plugin, and no row in
[Appendix B](../appendix-b-licences.md).

It covers one relationship, which is the only contentious one this platform has:
[§9.7](../09-messaging.md)'s single synchronous hop, `Web.Bff → Catalog`. The file
holds six interactions and the consumer's per-reply tolerance;
`Web.Bff.Tests` drives each through the BFF's own screen against the stub, and
`Catalog.Api.Tests` verifies each against the real provider over
`ServiceFixture`.

**Why.** Three things, and the first alone is decisive.

**Pact's .NET binding cannot express gRPC.** PactNet 5.0.1 ships HTTP and
message pacts. Protobuf and gRPC are a *plugin* —
`pactflow/pact-protobuf-plugin` — and the .NET binding for the plugin
framework is `PactNet.Extensions.Grpc`,
pull request 548 against `pact-foundation/pact-net`, opened on 4 September 2025
and **still open**. So the mechanism Appendix C named cannot reach the
relationship Appendix C made it conditional on.

**The relationships Pact *could* express are not contentious.** The async
contracts travel as a shared assembly that both ends compile
([§4.3](../04-solution-structure.md)) and [§12.6](../12-test-strategy.md) already
round-trips every one of them through the bus serialiser; the gateway is a
reverse proxy with no semantic contract of its own
([§10.1](../10-api-gateway.md)); and the BFF's own HTTP API has no consumer inside
this repository. Adopting Pact for those would be adopting it where it has
nothing to catch.

**The route that does exist costs more than it buys.** Driving the plugin out of
band means `pact_verifier_cli` plus a platform-specific plugin binary installed
into `~/.pact/plugins`. Neither is a NuGet package, so
`Directory.Packages.props` cannot pin them and the licence gate — which reads
that file and Appendix B as text, ahead of the build
([§15.1](../15-cicd-deployment.md)) — would never see them. Pact's own
documentation also rules out `WebApplicationFactory` for provider verification,
because its Rust core makes real TCP calls, so Catalog's suite would need a
second hosting shape as well. All of that to express expectations the consumer
already states in C#.

> **What is taken is Pact's property, and only its machinery is declined.** The
> value of a pact is that **one artefact is authored by the consumer and
> verified against the provider** — not the broker, the wire format or the Rust
> core, which are how that property is shipped across a *repository* boundary.
> This is a monorepo, and the boundary Pact's machinery exists to cross is not
> there. `pricing.proto` already makes the argument one level down: the
> syntactic contract is one file with two generated halves, and this is the
> semantic contract shared the same way. The `.proto` is Catalog's because
> Catalog serves the RPC; this file is Web.Bff's because only a consumer can say
> what it needs.

**Consequences.**

**The contract is compiled rather than parsed, which moves a class of failure
from a run to a build.** An interaction naming a field the generated types do
not have is a compiler error in both suites. A pact file is data, and the same
mistake is a verification failure at best and a silently skipped expectation at
worst.

**It does not cross a repository boundary, and that is the whole of what is
given up.** Extract `Web.Bff` into its own repository and this file becomes
something that has to be published — at which point Pact, or a package, is the
answer after all. The decision is reversible and its trigger is nameable, which
is the most that can be asked of one taken against a plugin that may merge next
month.

**Only one relationship is covered**, and no gate says a second one is owed. The
platform has exactly one synchronous hop today by §9.7's design; if a second
appears, whether it earns a contract is the same conditional judgement Appendix
C already applies here.

**Both edges of the ceiling are pinned deliberately.** `GetPricesValidator`'s
`MaxProductIds` changing in *either* direction fails verification — one
interaction requires a basket at the ceiling to be served, the next requires one
past it to be refused. A provider free to change a number its consumer wrote
down has a contract nobody is holding; Pact pins an interaction the same way.

**The linked file is a build-time path into another suite's tree**, which is the
`.proto`'s cost paid a second time. It is cheaper here: no Dockerfile builds a
test project, so unlike a `ProjectReference` or a linked `.proto` there is no
`COPY` line to keep in step with it. What it does reach is `tools/new-service`,
which drops both the link and the verification suite — a contract copied to a
service no consumer calls is an expectation nobody holds.

> **The relationship was contentious, and it was measured rather than
> asserted.** `CheckoutEndpoints` compares a reply's currency to the request's
> with `OrdinalIgnoreCase`; `StubCatalog` echoed the *request's* spelling, so
> the comparison had never once been given two spellings to reconcile. Tightened
> to `Ordinal`, all 62 of `Web.Bff.Tests`' pre-PR tests that need no container
> still passed — while production would answer 500 on every lower-case currency
> a customer typed, because Catalog projects its own upper-cased column. **62 is
> the fast half and not the suite**: the four tests that want a Keycloak were
> not in the run, and nothing here claims they were. Three more divergences
> sat beside it: the stub filtered currency case-sensitively where Catalog does
> not, formatted amounts at the test's own scale rather than the column's
> `decimal(19,4)`, and enforced no ceiling at all. **A hand-written stub is a
> second, unverified specification**, and every one of those was it drifting.

**Amended by
[ADR-154](ADR-154-the-assistant-reads-whether-a-product-is-in-stock-through-an-inventory-grpc-hop-under-the-bffs-own-credentials-1452-amending-adr-151.md)**:
a second relationship is covered, `StockContract`, linked into
`Inventory.Api.Tests` as `PricingContract` is into Catalog's, so "only one" and
"exactly one hop" above describe the day this record was written. This record
is otherwise unchanged.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
