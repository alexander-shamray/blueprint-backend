---
description: Append an architecture decision record as its own file under adr/, with its row in Appendix A
argument-hint: "<short title>  e.g. Cursor pagination for list endpoints"
allowed-tools: Read, Grep, Glob, Edit, Write, Bash(ls:*)
---

Add an ADR titled `$ARGUMENTS`: one new file under
`docs/backend-architecture/adr/` and one row in
`docs/backend-architecture/appendix-a-adrs.md`, which is the index and
carries no ADR body. Two agents each adding an ADR touch two new files and
one index row apiece, and the row is the only place they meet.

## Steps

1. **Find the next number.** `ls docs/backend-architecture/adr/` and take the
   highest `ADR-NNN` + 1, zero-padded to three digits. The index table must
   agree; where it does not, the files are right and the table is missing a
   row. **Never reuse or renumber.** If this decision reverses an existing
   ADR, write a new one and add a `**Supersedes.**` line; amend the old one
   with `**Superseded by ADR-NNN.**` rather than deleting it. Two branches
   that each take `highest + 1` collide only when the second merges, and
   `/check-links` check 5 is what catches the duplicate number; the later
   branch renumbers its own ADR, never the one already on `main`.
2. **Name the file** `ADR-0NN-<slug>.md`: the title lower-cased, punctuation
   dropped, spaces to hyphens — the slug the title's heading anchor would
   carry, so a reader who knows one knows the other.
3. **Write the file** in exactly this form:

   ```markdown
   # ADR-0NN — <Title>

   **Decision.** <What was decided. Imperative, specific, testable.>
   **Why.** <The forces. What breaks without it.>
   **Consequences.** <What this costs. The honest downside, the thing a future
   reader will resent. This is the part that earns the ADR its keep.>

   ---

   [Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
   ```

   No blank lines between the three bold-led sentences. Prose wrapped at 80
   columns. The footer is that line and nothing else: an ADR names no
   previous or next ADR, because appending one must not edit the one before
   it. A link to a chapter goes up one directory —
   `[§9.3](../09-messaging.md)` — and a link to another ADR names its file,
   `[ADR-021](ADR-021-saga-timeouts-are-scheduled-by-the-broker.md)`.
4. **Add the index row** at the end of Appendix A's table:
   `| **ADR-0NN** | [<Title>](adr/ADR-0NN-<slug>.md) |`. Nothing else in
   Appendix A changes.
5. **Cross-reference.** Link the chapter section the ADR governs and check
   whether that chapter should link back; a chapter cites the ADR as
   `[ADR-0NN](adr/ADR-0NN-<slug>.md)`.
6. **Check for conflict.** Grep the existing ADR files for the same subject.
   An ADR that contradicts an earlier one without superseding it is the exact
   defect `/validate-blueprint` exists to catch.
7. **Register any new dependency** the decision introduces in
   `appendix-b-licences.md` with its licence and role. Versions belong in
   `Directory.Packages.props`; state one in the register only where the version
   *is* the decision, as with MassTransit 8.x.

## Report

The number assigned, the file written, the sections linked, and anything you
found that the new ADR touches but does not yet reconcile.
