# The SQL style gate

**The claim: no tracked file outside the plans and this suite holds a SQL
join that [`docs/style-guide.md`](../../docs/style-guide.md)'s join rule
refuses.** The guide owns the rule, in *SQL style*'s paragraph opening "Every
join names its kind". This gate holds four of its parts: a join names its
kind, writes no `OUTER` and keeps its `ON` off the join's line, and the join's
`ON` line carries one predicate, its `AND` on a line of its own.

It reads the tracked tree rather than a diff, because a formatter never reads
inside a C# raw string literal, which is where nearly all of this SQL lives
(`.editorconfig`, the paragraph on the absent `[*.sql]` section). Nothing else
would catch a join already in the tree.

## What it reads

- Every file `git ls-files` lists, whatever its suffix, so a new kind of file
  holding SQL is read without this gate learning its name. A file holding a
  NUL byte is binary and is not read. A file listed but gone from the working
  tree is not read either.
- A Markdown file's lines inside its fences only, so prose that names a
  `JOIN` is not judged as SQL. Every other file is read line by line, its
  comments included.
- A join is `JOIN` in capitals followed by its source: a name, a bracketed
  name, a variable, a temp table, a parenthesised rowset or a C# interpolation
  hole. The word before it is its kind, and only `INNER`, `LEFT`, `RIGHT`,
  `FULL` and `CROSS` name one. `CROSS JOIN` and the `APPLY` forms take no `ON`
  and pass.
- An `ON` line packs a second predicate when an `AND` or `OR` sits outside
  every parenthesis and quoted string after the `ON`. Only the first `ON` line
  after a join that takes one is judged, however many lines its source wraps
  across.
- A run that reads no file fails, so a gate that read nothing never reports a
  clean tree.

## What it does not claim

- `docs/superpowers/` is not read. The plans keep the form they were written
  in and are never edited to match the code that followed. This suite is not
  read either, because its cases are the refused forms.
- Lower-case SQL is not read. The corpus writes its keywords in capitals, and
  reading `join` would judge every LINQ query expression as SQL.
- It reads one line at a time. A join whose source starts on its line and
  wraps onto the next is judged on the line holding `JOIN`. A `JOIN` ending
  its line, with its source wholly on the next, is not read as a join at all,
  and an `ON` reached by concatenating two C# strings is not seen as one line.
- SQL in a regular string, rather than a raw literal or a fence, is read as
  its physical line. A `\n` before a join's kind is read into the kind, which
  refuses a join that names one. A `\n` and a space before its `ON` put the
  `ON` on the join's line; a `\nON` with nothing between is not read as an
  `ON` at all and leaves the join waiting for the next `ON` line. An `ON`
  line opening with a quote is not an `ON` line either, so its second
  predicate goes unjudged, and the next `ON` line, even a `MERGE`'s, is judged
  as the join's. The tree holds none of these shapes.
- A join hint, `INNER HASH JOIN` or `LEFT LOOP JOIN`, is read as the kind,
  so the gate refuses it as a bare join. The tree holds no join hint.
- It holds the join rule and nothing else in *SQL style*. One predicate per
  line under `WHERE` or a `MERGE`'s `ON`, column aliases as assignments and
  the + 4 continuation indent stay review rules.

## How it runs

Its suite, then the gate, in the `pipeline-gate` job of
[`ci.yml`](../workflows/ci.yml). Locally:

```bash
py -3.12 -m unittest discover -s .github/sql-style-gate
py -3.12 .github/sql-style-gate/sql_style_gate.py
```

The suite's last class reads this repository's own tree. Every tracked text
file outside the plans and this suite that holds `JOIN` must be among those
the gate read, judged against a listing the test makes itself. Raw string
literals and blueprint fences must each yield a join. Those tests have the
surface as their subject, so a read that narrows fails there even while every
case above still passes.
