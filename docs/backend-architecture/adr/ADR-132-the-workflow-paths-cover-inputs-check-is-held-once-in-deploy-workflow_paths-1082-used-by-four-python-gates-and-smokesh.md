# ADR-132 — The workflow-paths-cover-inputs check is held once in deploy/workflow_paths (#1082), used by four Python gates and smoke.sh

**Decision.** `deploy/workflow_paths/workflow_paths.py` holds the reader of a
workflow trigger's `paths:` filter, the test of whether one glob covers an
input, and the two checks built on them: both triggers cover every declared
input, and every path a gate reads is declared. `check_permissions.py`,
`check.py`, `realm_check.py` and `canary.py` import it, and `smoke.sh` calls it
through its command line. A gate keeps its own `SOURCE_INPUTS` and its own scan
of what it reads, adds `workflow_paths.HOME` to the list, and every workflow
that runs such a gate filters on `deploy/workflow_paths/**`. Its suite runs in
`ci.yml`'s `pipeline-gate` job, and its subject is the workflow files
themselves, counted without it.
**Why.** The rule was written five times and the copies had drifted: five
readers in three forms, one of which ran the last trigger's "body" to the end
of the file, and one of which counted every `paths:` list without asking whose
it was; and two glob tests, one of which accepted only an exact entry or
`entry/**`. A form one copy misreads is
fixed in that gate and stays in the other four, which is the gate that silently
stops covering its surface that `CLAUDE.md` names this repository's
most-repeated failure.
**Consequences.** Four gates now fail together when the module is wrong, and
the module's own filter entry in each workflow is what makes a change to it run
them. The reader is text rather than YAML, so a form it does not recognise is
refused, not passed. `canary.py` read the two path lists by count and now reads
them by trigger name, as the other gates did. A glob that names an ancestor of
an input now covers it in `realm_check.py` and `canary.py`, as it already did in
the other two. Each gate still spells its own reads scan, because what counts
as a read differs per gate and a shared scan would be a shared guess.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
