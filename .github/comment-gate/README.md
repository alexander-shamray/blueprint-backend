# The comment gate

**The claim: no comment a pull request adds names an issue, a delivery-plan
row, a reviewer or a history, stresses a word, sits in a comment block
longer than `BLOCK_LIMIT`, or is part of a C# `<remarks>` that cites no
section, ADR or `cref`; no pull request but a docs one or a sweep adds
more lines of C# comment than of C# code; no line it adds to a C# file
runs past `WIDTH_LIMIT` columns; and no prose line it adds to a Markdown
file runs past `PROSE_LIMIT`.**
[`docs/style-guide.md`](../../docs/style-guide.md)'s
*Comments* section is the rule, its *Wrapping* section owns the width of
code, and its *Prose conventions* section the width of prose; this gate is
their mechanical half.

## What it reads

- **The lines the pull request adds**, from `git diff -U0 -M` between the
  merge base and the head, with git's prefixes, rename detection and its
  limit, the diff algorithm, hunk context and a textual patch fixed on the
  command line so that no local configuration or attribute changes what it
  is handed. A renamed file is judged on its changed lines only. A diff that
  changes no file, a path git has to quote, or a header of a shape it does
  not know refuses the run with exit 2 rather than passing it.
- **Each changed file as the head commit holds it**, in the languages the
  rule names: `.cs`, `.py`, `.sh`, `.yml` and `.yaml`, the MSBuild files
  (`.csproj`, `.props`, `.targets`) and `.editorconfig`, for comments; and
  `.md`, for the width of its prose alone. Anything else — a Dockerfile, a
  Helm template, PowerShell — is not read. A file that is not UTF-8, or
  Python that does not parse, refuses the run.
- **Every comment token, and nothing inside another literal as one.** A
  trailing comment is judged as much as a whole-line one, so
  `Call(); // PR-1` fails, and a Python docstring is a comment. A string, a
  character literal, a C# directive's message, a shell heredoc or quoted
  word, a YAML quoted or block scalar and an MSBuild CDATA section are code,
  so a comment-shaped literal is not judged as a comment. The scaffold's
  templates are Python literals for that reason: the gate judges the service
  the scaffold renders when that service is committed, not the strings it
  renders from.
- **A workflow's `run:` block as the shell it is.** It is a YAML block scalar,
  and the runner executes it, so its comments are read with the shell reader
  after the block's indentation is removed. A workflow is a file under
  `.github/workflows/` or an `action.yml`; the block is read as POSIX shell
  whatever the step's `shell:` names, which the gate does not check. Any
  other block scalar — a path filter's embedded YAML and a `run:` key in any
  other YAML file included — stays a literal.

Each reader is a small lexer over the one language, stdlib only, on the
licence gate's terms: a gate that needs a `pip install` gets skipped.

## What it judges

**The patterns, on each added line's comment text**, markers stripped: an
issue or pull-request number (`#12`), a delivery-plan row (`PR-7`), a
reviewer (`Copilot`, `Grok`, `CodeQL`, `found in review`), history (`used
to`, `went stale`, `this comment said`) and emphasis (`**…**`, `<b>`). The
list is the gate's `PATTERNS`, which is its one copy. Under `.claude/` a
reviewer's name is allowed, because the harness's helpers are about the
reviewers and naming one there is a subject rather than a history.

**The block length.** A block is a run of lines holding nothing but comment;
a blank line or a line of code ends it, and a multi-line token — a
docstring, a `/* */` — counts every line it spans. An added comment line is
judged as its whole block in the file after the change, because one added
line can push an old block past the limit. A trailing comment is on a line
of code, so it is never part of a block.

**An uncited `<remarks>`.** A C# `<remarks>`, from the comment its
opening tag leads to its closing tag, or, when it has none, to the next
opening tag or the end of that comment block, whose comment text holds no
`§`, no Appendix E section such as `E.6.13`, no `ADR-` number and no
`cref=`, is a finding at its opening line when
any line of it is added. The pattern is the gate's `CITATION`.

**The width of a C# line.** An added line of a `.cs` file longer than
`WIDTH_LIMIT` characters, its line break and a file's byte-order mark not
counted, is a finding at that line, whatever it holds: code, a comment or
the inside of a literal. A file under a `Migrations/` directory or named
`*.Designer.cs` is generated, so the gate's `GENERATED` exempts it. The
suite also holds every tracked C# file to the width, so a line no diff added
— one the gate's base copy judged before the width was in it — fails the
suite rather than staying.

**The width of a Markdown prose line.** An added line of a `.md` file longer
than `PROSE_LIMIT` characters, its line break not counted, is a finding at
that line, unless the guide exempts it: a table row, an HTML line, a heading,
a reference definition, a line inside a fenced code block, or one of a file's
leading front matter. A link's target and a bare address are measured as one
character, since the guide lets them run long and not the prose around them.
A fence ends on the character that opened it, at least as long, and one never
closed is a finding where it opens whenever a line at or after it is
added. A line inside a blockquote is tested for a fence and for the
exemptions after its quote markers, and its width is still measured whole, so
a quoted fence and a quoted table row are exempt as unquoted ones are. A fence
opened inside a quote closes only at its own depth and ends when a line drops
below it. Front matter is a
leading block with no blank line before its closing rule. A file under
`docs/superpowers/` is exempt, since a spec is never edited to match the code
that followed it.
Markdown has no comments, so the check sits beside the comment rule and
reads the same added lines. The suite does not hold the tracked files to it:
about 212 old lines are over, and rewrapping them is a separate mechanical
commit that this gate does not wait for.

**Added lines only.** The corpus is brought under the rule by the sweeps in
[`docs/churn-plan.md`](../../docs/churn-plan.md) and
[`docs/churn-plan-2.md`](../../docs/churn-plan-2.md), not by this gate
refusing every pull request until they land. Editing a line inside an old
block that already breaks the rule fails, which is the point: the change
that touches it is the one that cuts it.

**What it leaves to the reviewer**, as the guide says: whether a comment
says why, cites its owner rather than copying the argument, or counts
things that live elsewhere. Some pattern words have innocent uses — "the key
used to sign" — and the gate fails them anyway; the answer is to say it
another way.

The suite pairs each reader's comments with the literals beside them that it
must not judge, each pattern with its innocent neighbour, and runs the whole
gate over a throwaway git repository, so the diff half is observed rather
than assumed. It also reads every tracked file in a language the gate reads
for comments, so a file the lexers cannot parse fails the suite before it
fails a pull request; `.md` is read in diff mode only.

## What it reports, and the ratio it refuses

**Beside its findings, the gate prints what the added lines are made of**:
for C#, and for Python and shell together, the added lines that hold only
comment against the added lines that hold code, blank lines in neither. The
languages are the gate's `REPORTED`. The scripts' line fails nothing.

**The C# line fails a pull request whose added comment lines outnumber its
added code lines**, as one finding naming both counts; equal counts pass.
Two pull requests are excepted, and the run prints the exception it took:
one whose title is of the `docs` type, as the gate's `DOCS_TITLE` reads it,
because rewording a comment adds comment and no code; and one labelled
`comment-sweep`, the gate's `SWEEP_LABEL`, because a sweep rewrites comments
shorter, so what it adds is comment. CI hands the gate the title in
`PR_TITLE` and the labels, a JSON list of names, in `PR_LABELS`, as the
event that started the run carried them. The gate's workflow takes
`labeled` and `unlabeled` among its `pull_request` types, so a label added
starts a run that reads it; re-running the failed job would not, as it
replays the old event. A title
changed counts from the next push. A run without either, as a local one is,
takes no exception, and a `PR_LABELS` that is not a list of names refuses
the run.

**`--tree` judges every line of the tracked files under the paths it is
given**, as the working tree holds them, instead of a diff, and prints the
same two lines for the whole tree, failing neither. It is how a sweep shows
that a tree is under the rule; CI does not run it.

## How it runs

The `comment-gate` job in
[`comment-gate.yml`](../workflows/comment-gate.yml), a workflow of its own
because a label has to start a run of this gate, and in `ci.yml` it would
start a build and every suite as well. The suite runs the head copy on
every run; on a pull request, **the gate that judges is read out
of the base commit**, on the closure gate's design, so a pull request cannot
loosen the rule it is judged by. A base with no gate directory is judged by
the head copy under a warning, as the locality gate's bootstrap is, because
the pull request that lands the gate has no base copy. `docs/testing.md` has
the local invocation.
