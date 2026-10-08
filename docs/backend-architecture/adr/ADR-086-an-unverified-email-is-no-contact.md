# ADR-086 — An unverified email is no contact

**Decision.** `KeycloakContactSource` answers `NoSuchCustomer` for a user whose
`emailVerified` is false, beside the three "does not exist" answers
[ADR-052](ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)
lists, and refuses a user with no boolean `emailVerified` by shape, as it does
one with no boolean `enabled`. What that stops depends on the realm. With
`verifyEmail` off, as the shipped realm has it, a changed address is never used
until an administrator marks it verified. With it on, Keycloak mails a link to
the new address and whoever controls that address can verify it, a session
thief included, so the flag delays such a redirect rather than preventing it; a
deployment that must prevent it makes the email attribute editable by
administrators only, in its user profile. A deployment whose users' addresses
are proved some other way marks them verified. It amends ADR-052, which said
the flag says nothing because the shipped realm has `verifyEmail` off.
**Why.** The realm grants `manage-account` by default, so a customer, or anyone
holding their session, can change the account's email through Keycloak's
account console. With the flag unread, the worker took whatever address the
account held, cached it and sent every order, payment and shipping notice to
it: a stolen session redirected a customer's mail to a third party through the
shop's own relay, silently. Keycloak clears `emailVerified` when a user changes
their own address, which a throwaway Keycloak 26.0 showed through its account
API, so such a change leaves an address the worker does not use until it is
verified again. The shipped realm keeps `verifyEmail` off and its seeded users
verified, so nothing local changes.
**Consequences.** Once the stored contact stops being served, after
`ContactOptions.Freshness` or, while Keycloak cannot answer,
`ContactOptions.StaleCeiling` (ADR-052), a customer who changed their email
gets no notice until the new address is verified, and in a realm with
`verifyEmail` off that means until an administrator marks it; each such row
ends `no_such_customer`. A realm that creates users without proving their
address, by open registration with verification off, sends nobody anything,
which is the failure to look for when every notice ends that way. Nothing
checks a deployed realm's settings; the choice is stated here.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
