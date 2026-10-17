# The domain-name gate

**The claim: a chapter names a reference service only in a block that carries
[ADR-096](../../docs/backend-architecture/adr/ADR-096-a-chapter-names-a-reference-service-only-as-evidence.md)'s
marker.** The gate fails a block in a scanned file that names a service
without it, naming the file, the block's first line and the names, so that a
paragraph added tomorrow is refused until it is evidence and says so.

| Subcommand | Refuses | Reads |
|---|---|---|
| `check` | a block that holds a service's name as a whole word, in its capitalised directory spelling, and lacks the marker; and an empty reader, which is a finding and not a pass | every scanned file's blocks, and the directories under `src/Services/` |
| `files` | nothing | prints the scanned set, one path a line |

**The marker is the ADR's, and this gate does not restate it.** It checks the
two halves the ADR names, a link whose target begins
`appendix-e-reference-domain.md#` and the phrase "in the reference domain",
both inside one block; a quotation marker or a line break does not split the
phrase. It cannot judge whether a block is evidence, only that it says so.

**A block is a paragraph, a table row or a heading.** A blank line ends a
paragraph, a table row is judged alone, and a code fence belongs to the
paragraph before it, blank lines inside the fence included.

**The names are read, never typed.** A service is a directory under
`src/Services/`, so one added by `tools/new-service` is covered the day it
lands and a removed one leaves the set.

**It sees a service's name and nothing else.** A block that specifies a
reference feature in other nouns (buyer, device, preference) passes, because
those are ordinary words in the chapters; [ADR-133](../../docs/backend-architecture/adr/ADR-133-the-domain-name-gate-matches-service-names-only-and-a-feature-written-in-other-nouns-is-caught-by-review-not-the-gate-1095-adding-to-adr-096.md)
records why the gate does not match them, and that review is the defence.

**The scanned set is every `*.md` under `docs/backend-architecture/` that a
closed list does not name**: `adr/` and Appendices A to E, spelled out in the
gate. A file not on the list is scanned, so a new chapter, and an Appendix F,
are covered by default.

## How it runs

Its suite, then `check`, in the fast job of [`ci.yml`](../workflows/ci.yml).
The suite's subject is the scanned set and not only the hits: a chapter added
to a fixture tree is scanned and a bare name in it fails, the exempt list is
asserted equal to the files above so that a new appendix is not exempt by
omission, every chapter on disk is asserted scanned, and a marked block
passes. Each refusal has a positive control beside it, and each was proved
by a mutation that makes it fail.
