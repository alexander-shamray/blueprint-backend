# Actions cost — before and after

**What GitHub Actions costs this repository, measured over two 14-day
windows either side of the cost changes, and what the difference says about
the visibility and plan decision.**
[`tools/ci-cost/`](../tools/ci-cost/README.md) produces every figure here
and owns how each is computed; this file records what it reported, one
dated section per window, and the comparison once both exist. Every figure
is a record of its day, per the contract's §2, and none is kept current.

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

## Window A — 2026-10-10 to 2026-10-23, the baseline

Measured on 2026-10-24 against GitHub Free at the rates read that day,
before any of the four changes had a pull request. The issue's own wording
asks for the 14 days before the first change merges; these are the 14 days
before the effort started, so should the first merge land later, the
command re-measures the 14 days before it and this section is superseded by
a dated one. No outage or freeze is known inside the window. GHCR could not
be read: the session's token is refused `users/{owner}`, which the packages
listing needs, so the end-of-window storage is owed by a run with
`read:packages`.

| | A |
|---|---|
| Runs | 8,649 |
| Runs on `pull_request` | 6,321 |
| Runs on `push` | 1,008 |
| Runs on `schedule` | 81 |
| Runs on `workflow_dispatch` | 2 |
| Runs on other events (`dynamic`: Copilot and Dependabot) | 1,237 |
| Jobs, every attempt | 53,972 |
| Billed minutes, Linux | 87,937 |
| Billed minutes, Windows | 1,257 |
| Billed minutes, macOS | 1,297 |
| Linux-equivalent minutes | 103,434 |
| Linux-equivalent minutes, per 30 days | 221,645 |
| Share of the Free allowance, per 30 days | 11,082% |
| List price if private | $620.61 |
| List price if private, per 30 days | $1,329.87 |
| Over the Free allowance, per 30 days | $1,317.87 |
| PRs opened | 304 |
| Commits pushed that ran a workflow | 297 |
| Label events, issues and PRs | 1,100 |
| List price per run | $0.07 |
| List price per PR | $2.04 |

| Workflow | Runs | Linux-equivalent minutes |
|---|---|---|
| CI | 1,264 | 87,058 |
| Copilot's `PR #n` runs, 302 workflows | 909 | 6,348 |
| Push on main | 297 | 2,693 |
| Compose | 175 | 1,279 |
| Closure gate | 1,157 | 1,155 |
| Locality gate | 1,157 | 1,042 |
| Broker permissions | 580 | 588 |
| Game day | 557 | 558 |
| Deploy | 553 | 551 |
| Commit gate | 555 | 508 |
| Observability | 472 | 472 |
| Helm | 435 | 435 |
| Dependabot updates and the rest | 76 | 359 |
| Merge notification | 228 | 225 |
| Realm | 234 | 163 |

### What the baseline says about each change

| | Its share of A, in Linux-equivalent minutes | Isolable from the run data |
|---|---|---|
| #1435, edit-target-guard's Windows and macOS legs | ≈15,500 (15%): every Windows and macOS minute is that job's, and a macOS minute costs over ten Linux ones | Yes, as the Windows and macOS rows |
| #1436, `realm.yml` hourly to six-hourly | 163 (0.2%) | Yes, as the Realm row |
| #1437, no full CI run on `labeled`/`unlabeled` | Not isolable: a run records `pull_request` without its action. CI ran ≈3.2 times per PR and the label events, issues included, were 1,100, so the bound is wide | Only as the change in CI runs per PR between the windows |
| #1438, a self-hosted runner | Whatever share of CI's 87,058 it moves; its own rate is `RATES["self-hosted"]` | Yes, as the self-hosted row |

**CI is 84% of the bill, and the two changes that can be isolated reach
about a sixth of it.** After #1435 and #1436 the remainder is ≈87,700
Linux-equivalent minutes per 14 days, over ninety times the Free allowance
a month, so #1439 is decided by CI's Linux minutes — which only #1437 and
#1438 reach — and not by the OS legs or the schedule. Copilot's runs are
the second-largest line and none of the four changes touches them.

## Window B and the comparison

Not yet measured. Written when Window B ends: the raw numbers, each
optimization's effect from its workflow's row where one can be isolated, the
residual cost, a projection for low, normal and high usage, and the
recommendation for #1439.
