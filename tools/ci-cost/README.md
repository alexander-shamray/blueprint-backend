# The Actions cost measurement

[`docs/ci-cost-comparison.md`](../../docs/ci-cost-comparison.md) is the
comparison this directory measures for; this file records what a developer
needs at the keyboard.

```bash
py -3.12 tools/ci-cost/ci_cost.py                                   # the last 14 full days, as window A
py -3.12 tools/ci-cost/ci_cost.py --window A=2026-10-10:2026-10-23 --window B=2026-11-10:2026-11-23
py -3.12 tools/ci-cost/ci_cost.py --plan Pro --json                 # priced against another allowance
py -3.12 tools/ci-cost/ci_cost.py --repo owner/name                 # another repository than origin's
```

It reads the Actions REST API through `gh api`, so it works against a public
or a private repository with whatever token `gh` holds, and prints one
column per window: runs per trigger, jobs, billed minutes per runner OS, the
list price, PRs opened, commits pushed and label events, and each workflow's
share in a second table. Days are whole UTC days, both ends inclusive.

| | |
|---|---|
| Billed minutes | each job's `completed_at - started_at`, rounded up to the whole minute, every attempt of a run included (`filter=all`); a skipped job bills none |
| Runner OS | from the job's labels; `self-hosted` wins over the OS it runs |
| List price | `RATES` in the script, read from GitHub's runner pricing page on the date `RATES_READ` names; a public repository pays none of it on standard runners |
| Linux-equivalent minutes | the list price divided by the Linux rate: how much of a plan's included minutes the usage spends, since the allowance is valued at that rate |
| Per 30 days | the window's figure scaled from its own length |
| GHCR | container versions per package, read only for a window ending yesterday or later, since the API keeps no history of a level; the REST API reports no size, which only the billing usage report holds |

Two limits to know before reading a figure. **The runs endpoint answers at
most 1,000 results to one query**, so the script halves a span until each
query fits under it. **Pages are requested by number**, never through the
`Link` header, because the proxy cloud sessions run behind refuses the
numeric-id URLs it carries. A read the token is refused — the packages
listing without `read:packages`, typically — is printed under the table
rather than failing the run.

Stdlib only, and the suite answers from a fake API:

```bash
cd tools/ci-cost && py -3.12 -m unittest
```
