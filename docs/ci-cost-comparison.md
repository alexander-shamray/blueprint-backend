# Actions cost — before and after

**What GitHub Actions costs this repository, measured over two 14-day
windows either side of the cost changes, and what the difference says about
the visibility and plan decision.** [`tools/ci-cost/`](../tools/ci-cost/README.md)
produces every figure here and owns how each is computed; this file records
what it reported, one dated section per window, and the comparison once both
exist. Every figure is a record of its day, per the contract's §2, and none
is kept current.

## Method

| | |
|---|---|
| Window A | the 14 full UTC days before the first cost change merges |
| Window B | the 14 full UTC days starting once the last one has merged; a change deliberately left out is named here with its reason |
| Changes measured | the issues tagged as the effort's optimizations: #1435 edit-target-guard's Windows and macOS legs, #1436 `realm.yml`'s schedule, #1437 the `labeled`/`unlabeled` triggers, #1438 a self-hosted runner |
| Decision informed | #1439, repository visibility and the GitHub plan |
| Command | `py -3.12 tools/ci-cost/ci_cost.py --window A=<first>:<last> --window B=<first>:<last>` |

**The repository is public, and a public repository pays nothing for
standard runners.** Every price below is therefore the list price the same
usage would cost were the repository private — the figure #1439 needs —
not an amount anyone was billed. The rates and the date they were read are
`RATES` and `RATES_READ` in the script; the plan priced against is GitHub
Free unless a section says otherwise.

**Activity differs between windows**, so each window reports its cost per
run and per PR beside its total, and the comparison reads those ratios
before the totals. An outage, a release freeze or a burst of automated PRs
inside a window is named in its section, since the API cannot tell one from
ordinary use.

## Window A — the baseline

Measurement in progress; this section is replaced by the tool's output and
its reading.

## Window B and the comparison

Not yet measured. Written when Window B ends: the raw numbers, each
optimization's effect from its workflow's row where one can be isolated, the
residual cost, a projection for low, normal and high usage, and the
recommendation for #1439.
