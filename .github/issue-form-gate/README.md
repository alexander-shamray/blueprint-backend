# The issue-form gate

**The claim: every issue form under `.github/ISSUE_TEMPLATE/` offers exactly
the vocabularies its owners hold, every choice it offers is a label in this
repository, and a blank issue is switched off for a filer below write
access.** Its owners are
[`docs/change-locality.md`](../../docs/change-locality.md) §6's *issue
vocabulary* bullet for the kind, and
[`gh-issue-create.sh`](../../.claude/scripts/gh-issue-create.sh)'s closed
`case` arms for the severity and the area.

## What it reads

- `config.yml`, which must set `blank_issues_enabled: false`: with a blank
  issue on offer, a filer can skip the form and every field it asks for.
- Every other entry in the directory, which must be a `.yml` or `.yaml`
  form: GitHub offers a Markdown template beside the form, with none of its
  fields, so one fails the gate. Each form must carry a `kind`, a
  `severity` and an `area` dropdown and no other dropdown, so every
  dropdown option is a label name. An empty directory fails, and so
  does a form with a top-level `labels:` key, which would attach its labels
  to every issue the form opens whatever the answers said.
- The form's kind against the kinds §6's bullet lists after `kind is`, its
  severity against the helper's `case "$severity"` arm, and its area against
  the words of the helper's `case "$area"` arm, where `code,test` is two
  words because the helper files it as two labels; as sets, with a repeated
  option refused.
- The helper's `case "$kind"` arm, every word of which must be one of §6's
  kinds: the helper filing under a kind the form does not offer is the drift
  this gate exists for, seen from the other side.
- Every dropdown option against the names `gh label list --json name`
  prints, on stdin. An empty or malformed list fails, so a gate that read no
  label never passes.

The form is read with a line reader that accepts the shape `finding.yml`
keeps and refuses what it cannot place, because there is no stdlib YAML
parser and a gate needing a `pip install` is a gate that gets skipped: an
options list in flow style, an option that is not a bare label name, a
dropdown with no id or no options, or the same dropdown twice.

## What it does not claim

- An answer attaches no label: a form can attach only fixed ones, which the
  gate refuses. The answers sit in the issue body under the dropdowns'
  headings, and whoever triages copies them onto the issue; the gate holds
  that each answer has a label to copy onto.
- It does not read the text fields, the label colours or descriptions,
  or whether GitHub accepts the form: GitHub validates the schema when it
  renders the form, and a broken one shows on the *New issue* page.
- It does not hold which areas the form lets a filer combine: the form's
  dropdown is a multiple choice, and the helper's pairs are its own.
- A filer with write access still sees GitHub's *Blank issue*, marked
  *Maintainers only*: the setting binds the read and triage roles, and
  nothing in a repository's files can switch it off for the rest.

## How it runs

Its suite, then the gate, in the fast job of [`ci.yml`](../workflows/ci.yml),
which reads the labels with the job's token. Locally:

```bash
py -3.12 -m unittest discover -s .github/issue-form-gate
gh label list --limit 1000 --json name | py -3.12 .github/issue-form-gate/issue_form_gate.py
```
