# ADR-133 — The domain-name gate matches service names only, and a feature written in other nouns is caught by review, not the gate (#1095), adding to ADR-096

**Decision.** [ADR-096](ADR-096-a-chapter-names-a-reference-service-only-as-evidence.md)
part 5's gate keeps its one match: a service's directory name under
`src/Services/`, as a whole word. It does not also match a closed list of
feature nouns held in Appendix E, and it does not read a chapter heading as
naming a feature. A chapter block that specifies a reference feature in other
nouns (buyer, delivery address, device, preference) is not the gate's to
refuse. Two things stand in for it. First, the cut #1085 made: a feature's
contract, states and fields live in Appendix E (E.6.10 to E.6.14 for the
buyer's order read, the product image upload, the saved delivery address, the
device registration and the notification preferences), and the chapter states
the rule and cites the section. Second, the review of a chapter PR asks the
question the gate cannot, whether a new section specifies a feature the
reference domain owns, and moves it to Appendix E if so. ADR-096 carries a
note pointing here, so that a green gate is not read as more than it checks.
**Why.** The nouns are ordinary words in the chapters. Counted on `21d346b0`
over the 16 scanned files, as blocks (a paragraph, a table row or a heading)
that hold the word and lack the marker: `buyer` 14 of 22, `device` 20 of 25,
`preference` 18 of 24, `customer` 52 of 68, `delivery address` 2 of 6, and
`order` 212 of 267, where the product image upload's noun, `product image`,
matches none. A gate on any of them would refuse most of the prose it reads,
which is a gate to be switched off, and the ones it should refuse (a feature
section) are no easier to tell from the ones it should not than the evidence
judgement ADR-096 already leaves to the marker. A narrower list, of nouns that
only the features use, is not stable: it is a second roster that a feature
added later joins only when someone remembers to add it, which is the drift
the gate exists to remove. Reading it from Appendix E would follow a fork's
replacement of the appendix, but it would only be as complete as the section
headings are, and a heading is the thing the next feature section is free to
word differently.
**Consequences.** The next feature section can repeat the drift and pass every
gate; the defence is a reviewer who asks, and #1085's cut shows what that
costs when nobody did, chapter sections moved after the fact. The gate's
suite is unchanged, because its subject, the scanned set, is unchanged. If a
second feature section lands in a chapter despite this, the decision is
reopened by a new ADR that supersedes this one, with that PR as its evidence,
and a list of nouns is then the first thing to try.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
