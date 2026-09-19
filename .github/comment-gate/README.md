# The comment gate

**The claim: no comment a pull request adds names an issue, a delivery-plan
row, a reviewer or a history, stresses a word, or sits in a comment block
over ten lines.** [`docs/style-guide.md`](../../docs/style-guide.md)'s
*Comments* section is the rule; this gate is its mechanical half.

## What it reads

- **The lines the pull request adds**, from `git diff -U0 -M` between the
  merge base and the head, with git's prefixes, rename detection and hunk
  context fixed on the command line so that no local configuration changes
  what it is handed.
  A renamed file is judged on its changed lines only. A diff that changes no
  file, a path git has to quote, or a header of a shape it does not know
  refuses the run with exit 2 rather than passing it.
- **Each changed file as the head commit holds it**, in the languages the
  rule names: `.cs`, `.py`, `.sh`, `.yml` and `.yaml`, the MSBuild files
  (`.csproj`, `.props`, `.targets`) and `.editorconfig`. Anything else — the
  Markdown, a Dockerfile, a Helm template, PowerShell — is
  not read. A file that is not UTF-8, or Python that does not parse, refuses
  the run.
- **Every comment token, and nothing inside another literal.** A trailing
  comment is judged as much as a whole-line one, so `Call(); // PR-1` fails,
  and a Python docstring is a comment. A string, a character literal, a
  C# directive's message, a shell heredoc or quoted word, a YAML quoted or
  block scalar and an MSBuild CDATA section are code, so a comment-shaped
  literal is not judged. The scaffold's templates are Python literals for
  that reason: the gate judges the service the scaffold renders when that
  service is committed, not the strings it renders from.
- **A workflow's `run:` block as the shell it is.** It is a YAML block scalar,
  and the runner executes it, so its comments are read with the shell reader
  after the block's indentation is removed. Any other block scalar — a path
  filter's embedded YAML included — stays a literal.

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
line can push an old block past ten. A trailing comment is on a line of code,
so it is never part of a block.

**Added lines only.** The corpus is brought under the rule by the sweeps in
[`docs/churn-plan.md`](../../docs/churn-plan.md), not by this gate refusing
every pull request until they land. Editing a line inside an old block that
already breaks the rule fails, which is the point: the change that touches
it is the one that cuts it.

**What it leaves to the reviewer**, as the guide says: whether a comment
says why, cites its owner rather than copying the argument, or counts
things that live elsewhere. Some pattern words have innocent uses — "the key
used to sign" — and the gate fails them anyway; the answer is to say it
another way.

The suite pairs each reader's comments with the literals beside them that it
must not judge, each pattern with its innocent neighbour, and runs the whole
gate over a throwaway git repository, so the diff half is observed rather
than assumed. It also reads every tracked file in a language the gate reads,
so a file the lexers cannot parse fails the suite before it fails a pull
request.

## How it runs

The `comment-gate` job in [`ci.yml`](../workflows/ci.yml). The suite runs the
head copy on every run; on a pull request, **the gate that judges is read out
of the base commit**, on the closure gate's design, so a pull request cannot
loosen the rule it is judged by. A base with no gate directory is judged by
the head copy under a warning, as the locality gate's bootstrap is, because
the pull request that lands the gate has no base copy. `docs/testing.md` has
the local invocation.
