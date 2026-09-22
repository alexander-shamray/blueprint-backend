# ADR-053 — A jurisdiction is a value the deployment is given

**Decision.** This is the record
[§11.7](../11-identity-authorization.md)'s compliance row asks for, written
before Shipping and Notifications put personal data in new places and send
the first messages a regulator would read. The platform is built for the
United Kingdom and Kazakhstan with the third country left unnamed on
purpose — a decision about where it runs and none about the domain, which
the READMEs still call illustrative — and four rules hold it open:

1. **No type, no template and no branch names a country.** What a
   jurisdiction varies is handed to a deployment as configuration: the
   languages a customer message must be rendered in — a **set**, because
   Kazakhstan's is two — the time zone its dates are rendered in, and each
   retention window. A service that reads any of them binds one options
   class, validated at start and **refused rather than clamped**, as
   `RetentionPolicy` is. A template missing for a required language fails
   the host at start, not the send at night. `Address` is the precedent and
   already argues it: it checks presence and the shape of an ISO 3166-1
   alpha-2 code, and refuses to learn a postcode format or to ask
   `RegionInfo`.
2. **A made-up jurisdiction proves it.** A test deployment whose values are
   invented — an address country of `ZZ`, which `Address` already
   constructs, its own language set, zone and windows — is configuration,
   and the suite of each service that binds the options passes under it
   with no line of code changed. The third country is then
   a values file and a resource folder.
3. **Personal data stays in the deployment that collected it.** Residency
   is met by **a deployment per jurisdiction** — its own databases, realm,
   broker and log store — which database-per-service and per-environment
   chart values already permit. §11.7's `TenantId` is **not** that seam:
   tenancy shares a store, and residency forbids exactly that. So no
   synchronous read and no message crosses a deployment, and every third
   party personal data is shown to — a carrier, a mail relay — is a chart
   value and never a constant, named in its service's design as a processor
   with the country it runs in.
4. **What was said to a customer is kept as evidence, not as a copy.** A
   message template is versioned as [§9.2](../09-messaging.md) versions a
   contract — a new version beside the old, both in use while a consumer of
   the old exists — and the record of a send holds the event's id, the
   template's key, version and language, the outcome and the times, the
   customer's id rather than the mailbox, and never the body. On erasure
   the id is replaced as §11.7 replaces an order's, and the row, then
   holding nothing personal, lives for the statutory window; §11.7's rule
   that a notification log row is deleted outright is amended to this.
   Every template is a **service message** about an order the customer
   placed; one line of promotion moves it into a class that needs a consent
   this platform records nowhere, so a template that is not about the
   customer's order is a new ADR before it is a pull request.

**Card data never arrives, and that is now a decision rather than an
accident.** `AuthorisationRequest` carries an order, a payer, an amount and
a currency, and Payments keeps a provider's reference, so PCI DSS scope is
the payment provider's. A contract, a column or a log line that would hold
a card number, an expiry or a verification code is a new ADR before it is a
pull request.

**Why.** §11.7 says of regulated data "decide before handling … not after",
and no record did. The cost of deciding late is specific here: a country
encoded in a type is found by the first customer it refuses, a template
folder missing a language is found by a regulator, and a store shared across
a border cannot be unshared by configuration.

What the two countries ask, as far as an engineer can say it. **Every cell
is a claim for counsel to confirm and none was read from this repository**;
the rules above exist so that a confirmed answer changes a value and not the
code:

| | United Kingdom | Kazakhstan |
|---|---|---|
| Where data lives | UK GDPR restricts transfers out, and Kazakhstan has no adequacy regulation | Law No. 94-V has citizens' data stored on servers in the country |
| Lawful basis | Contract covers fulfilment and the service messages about it | Consent-centred, and the record of the consent is the evidence |
| Messages | PECR: a service message needs no consent, marketing does | Consumer information is owed in Kazakh and in Russian |
| The confirmation | Confirmation on a durable medium — the email is the compliance | A fiscal receipt through a fiscal data operator |
| Records kept | VAT records, six years | Tax records, five years |
| A breach | The ICO within 72 hours | The authorised body within one business day |
| Time | Two offsets a year | One zone, and the law that made it one is recent — tzdata is data, and it moves |

Rule 4 dissolves a pull that would otherwise be argued in every review:
personal data wants a short window and evidence of a durable-medium notice
wants a long one. A row with nothing personal in it may live for the long
one.

**Consequences.** **Four things an adopter must bring are named rather than
built**: a tax model — no order, line or contract carries a rate, an
inclusive flag or a breakdown, so no VAT receipt can be rendered from what
the platform holds; a consent record — the realm ships with registration
off and its terms action disabled; a fiscal-receipt integration; and the
residency decision itself, since `deploy/helm` charts no stateful workload
and the region of every store is whoever runs it.

**`Money` is the standing counter-example.** Each service's `Money.Of`
rounds every currency to the same fixed exponent, right for the pound and
the tenge and wrong for a currency with none or three, and Payments names
that exponent `PaymentAmounts.MinorUnitPlaces` and scales by it the integer
the provider is sent — so "open to a third country" is false in two
constructors and on one wire. A minor-unit table keyed by ISO 4217 code is
owed as the one source of all three; until it exists nothing new copies
the literal.

**The log store is where rule 3 runs out.** [§13.4](../13-observability.md)
sends a customer's id to the log, which
[ADR-035](ADR-035-an-integration-event-carries-identifiers-not-personal-data.md) already calls
personal data, §11.7's erasure reaches no log, and nothing under
`deploy/observability` states a retention. A stated lifetime for the log
store is owed, and so is the procedure for a personal-data incident — the
one event here with a statutory clock on it. No alert fires for it, and
[§13.9](../13-observability.md)'s gate pairs every runbook with one, so
the procedure is owed beside the extension's Privacy service and not under
`docs/runbooks/`.

**The realm offers no language today.** It maps a `locale` attribute and
ships with internationalisation off and no supported locale, and a consumer
holds no token to read a claim from, so
[ADR-052](ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)'s
contact row is the only source a message has for one. A customer with no
language is sent the deployment's whole required set in one message, never
a default that turns out to be the build agent's.

**A suite proves the seam and never the compliance.** `ZZ`, a Kazakh-script
round trip through every store and protocol a name or address crosses, and
a date rendered at the edge of a day in the deployment's zone are the three
tests this record owes; none of them says a regulator is satisfied.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
