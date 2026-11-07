---
description: Scaffold a blueprint chapter and rewire the index and neighbouring footers
argument-hint: "<number> <title>  e.g. 16 Data migration"
allowed-tools: Read, Grep, Glob, Write, Edit, Bash(ls:*)
---

Add a chapter to `docs/backend-architecture/` from `$ARGUMENTS`: its first word
is the number and the rest is the title, so an unquoted title stays whole.

## Steps

1. **Check the number is free.** If it collides with an existing chapter,
   stop and ask whether to insert (renumbering everything after) or append.
   Renumbering touches every `§n` reference in the blueprint — never do it
   without confirmation.
2. **Create the file** as `NN-kebab-title.md`, zero-padded, matching the
   existing naming (`09-messaging.md`, `15-cicd-deployment.md`).
3. **Write the skeleton** in house style — see `docs/style-guide.md`:

   ```markdown
   # <N>. <Title>

   ## <N>.1 <First section>

   <Prose wrapped at 80 columns. British spelling. State the decision, then
   why, then what it costs.>

   ---

   [← §<N-1> <Prev title>](<prev-file>.md) · [Index](README.md) · [§<N+1> <Next title>](<next-file>.md)
   ```

4. **Update the index** — add a row to the chapter table in
   `docs/backend-architecture/README.md`, in reading order.
5. **Rewire both neighbours' footers** so the chain is unbroken in both
   directions. If the new chapter goes at the end of the numbered chapters, the
   previous chapter's `→` and Appendix A's `←` both change.
6. **Report** the files touched and run the footer/index checks from
   `/check-links` on the affected files.

## Constraints

- No emoji, no admonition syntax, no `--` where an em dash belongs.
- Do not invent content. Write the heading structure and leave clearly marked
  placeholders unless the user gave you the substance.
- If the chapter will reference a package not yet in
  `appendix-b-licences.md`, say so — the register has to keep up.
