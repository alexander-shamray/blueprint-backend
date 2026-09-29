# ADR-055 — An outbound hop registers beside its layer, and the host calls it

**Decision.** §4.2's one registration method per layer covers what that layer
holds for itself. An outbound hop is a client to a peer (§9.7) or to a third
party. In a service's Infrastructure it registers in a method of its own
beside the layer's, named for the hop: `AddPaymentProvider`,
`AddCarrierGateway`, `AddDeliveryAddressSource`. The host's `Program.cs` calls
it after the layer's. A host with no layers of its own, such as `Web.Bff`,
registers its hop in `Program.cs` directly. The host's own
client-credential bindings (§11.5) stay in `Program.cs`: the handler, the
token client, and any decoration of the token cache, such as Shipping's
`GrantCheckedTokenCache`. `Program.cs` does nothing else with Infrastructure.
**Why.** Each hop reads values that belong to the host and refuses to start
without them: a base address checked at registration, or a scheme rule that
needs the host's environment, which the layer's method is not given. §9.7
already makes a peer call the caller's `Program.cs`'s to register, so that a
host holding client credentials is a host that calls a peer; a third party's
hop has the same shape for the same reason. Folding each hop into its layer's
one method would hand that method the environment and every host's
credential decision.
**Consequences.** A host's `Program.cs` lists the hops it makes, so which
hosts call out is read from one file per host. The cost is that the rule's
shape is held by review and by nothing else: §4.2's architecture tests read
references, not registrations, so a second method that is not a hop would
pass them.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
