# ADR-086 — An unverified email is no contact

**Decision.** `KeycloakContactSource` answers `NoSuchCustomer` for a user whose
`emailVerified` is false, beside the three "does not exist" answers
[ADR-052](ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)
lists, and refuses a user with no boolean `emailVerified` by shape, as it does
one with no boolean `enabled`. A deployed realm therefore turns `verifyEmail`
on, or marks the addresses its own provisioning has proved as verified. It
amends ADR-052, which said the flag says nothing because the shipped realm has
`verifyEmail` off.
**Why.** The realm grants `manage-account` by default, so a customer, or anyone
holding their session, can change the account's email through Keycloak's
account console. With the flag unread, the worker took whatever address the
account held, cached it and sent every order, payment and shipping notice to
it: a stolen session redirected a customer's mail to a third party through the
shop's own relay. Keycloak clears `emailVerified` when a user changes their own
address, which a throwaway Keycloak 26.0 showed through its account API, so
reading the flag closes that path whatever the realm's settings. The shipped
realm keeps `verifyEmail` off and its seeded users verified, so nothing local
changes.
**Consequences.** A customer who changes their email gets no notice until the
new address is verified, which in a realm with `verifyEmail` off means until
an administrator marks it; each such row ends `no_such_customer`. A realm that
creates users without proving their address, by open registration with
verification off, sends nobody anything, which is the failure to look for when
every notice ends that way. Nothing checks a deployed realm's `verifyEmail`;
the obligation is stated here.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
