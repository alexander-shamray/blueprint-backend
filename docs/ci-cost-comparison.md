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
| Changes measured | the issues tagged as the effort's optimizations: #1435 edit-target-guard's Windows and macOS legs, #1436 `realm.yml`'s schedule, #1437 the `labeled`/`unlabeled` triggers, #1438 a self-hosted runner, #1461 `ci.yml`'s sub-minute checks gating its long jobs |
| Left out of Window B | #1436, closed as not planned: a scheduled `realm.yml` run bills nothing while `REALM_CHECK_SCHEDULED` is unset, so the change saves no minute; #1438, whose adopt-or-not decision is priced from this comparison's scenarios, so Window B cannot wait for it |
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

## Window A — 2026-10-10 to 2026-10-23, the first baseline

Superseded by the next section, which measures the 14 days the method names;
this one stays as the record of its day. Measured on 2026-10-24 against GitHub
Free at the rates read that day, before any of the four changes had a pull
request. The issue's own wording asks for the 14 days before the first change
merges; these are the 14 days before the effort started, so should the first
merge land later, the command re-measures the 14 days before it and this section
is superseded by a dated one. No outage or freeze is known inside the window.
GHCR could not be read: the session's token is refused `users/{owner}`, so the
version counts were not read, and the tool reads them only for a window ending
now, so Window A's cannot be recovered; storage is owed by the billing usage
report.

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
| #1437, no full CI run on `labeled`/`unlabeled` | Not isolable: a run records `pull_request` without its action. CI ran ≈4.2 times per PR opened, push runs included, and the label events, issues included, were 1,100, so the bound is wide | Only as the change in CI runs per PR between the windows |
| #1438, a self-hosted runner | Whatever share of CI's 87,058 it moves; its own rate is `RATES["self-hosted"]` | Yes, as the self-hosted row |

**CI is 84% of the bill, and the two changes this window can measure,
#1435 and #1436, reach about 15% of it.** After them the remainder is ≈87,800
Linux-equivalent minutes per 14 days, over ninety times the Free allowance
a month, so #1439 is decided by CI's Linux minutes — which only #1437 and
#1438 reach — and not by the OS legs or the schedule. Copilot's runs are
the second-largest line and none of the four changes touches them.

## Window A — 2026-10-11 to 2026-10-24, at the first merge

Measured on 2026-10-25 against GitHub Free at `RATES` as read on 2026-10-24. The
first change merged at 08:24 UTC that day, when #1463 landed #1435 and #1437
together, so these are the 14 full UTC days before it and the baseline the
comparison reads. Thirteen of the days are the first baseline's, which is why
its minutes and price agree with that one's to within 3%. No outage or freeze is
known inside the window. GHCR was refused again, this time for want of
`read:packages`; storage is still owed by the billing usage report.

| | A |
|---|---|
| Runs | 8,371 |
| Runs on `pull_request` | 6,165 |
| Runs on `push` | 927 |
| Runs on `schedule` | 84 |
| Runs on `workflow_dispatch` | 2 |
| Runs on other events (`dynamic`: Copilot and Dependabot) | 1,193 |
| Jobs, every attempt | 52,702 |
| Billed minutes, Linux | 85,767 |
| Billed minutes, Windows | 1,222 |
| Billed minutes, macOS | 1,263 |
| Linux-equivalent minutes | 100,855 |
| Linux-equivalent minutes, per 30 days | 216,117 |
| Share of the Free allowance, per 30 days | 10,806% |
| List price if private | $605.13 |
| List price if private, per 30 days | $1,296.70 |
| Over the Free allowance, per 30 days | $1,284.70 |
| PRs opened | 298 |
| Commits pushed that ran a workflow | 280 |
| Label events, issues and PRs | 1,025 |
| List price per run | $0.07 |
| List price per PR | $2.03 |

| Workflow | Runs | Linux-equivalent minutes |
|---|---|---|
| CI | 1,245 | 85,216 |
| Copilot's `PR #n` runs, 286 workflows | 874 | 6,128 |
| Push on main | 280 | 2,555 |
| Closure gate | 1,130 | 1,128 |
| Compose | 138 | 1,035 |
| Locality gate | 1,130 | 1,004 |
| Broker permissions | 542 | 550 |
| Commit gate | 600 | 543 |
| Game day | 526 | 527 |
| Deploy | 516 | 514 |
| Dependabot updates and the rest, 37 workflows | 99 | 440 |
| Observability | 425 | 425 |
| Helm | 404 | 404 |
| Merge notification | 235 | 232 |
| Realm | 227 | 154 |

### What this baseline says about each change

| | Its share of A, in Linux-equivalent minutes | Isolable from the run data |
|---|---|---|
| #1435, edit-target-guard's Windows and macOS legs | ≈15,100 (15%) | Yes, as the Windows and macOS rows |
| #1436, `realm.yml`'s schedule | 154 (0.2%), all of it the path-filtered `check` job the change would not touch | Left out of Window B |
| #1437, no full CI run on `labeled`/`unlabeled` | Not isolable, for the first baseline's reason; CI ran ≈4.2 times per PR opened against 1,025 label events | Only as the change in CI runs per PR |
| #1438, a self-hosted runner | Whatever share of CI's 85,216 it moves | Left out of Window B |
| #1461, `ci.yml`'s long jobs waiting on `format` and `workflow-lint` | Not isolable: it saves only on a run that `format` or `workflow-lint` fails, and the tool reads no job's conclusion | Only as the change in CI's minutes per run, 68.4 in A |

**The first baseline's conclusion stands on these numbers.** CI is 84% of
the bill; after #1435 and #1436 the remainder is ≈85,600 Linux-equivalent
minutes per 14 days, about ninety-two times the Free allowance a month, and
only #1437, #1461 and #1438 reach it.

## Window B and the comparison

Not yet measured. #1435 and #1437 merged on 2026-10-25, which fixes Window
A's end; #1461 was still open that day, so Window B starts on the first
full UTC day after it merges, or after it is closed and named above as left
out. Written when Window B ends: the raw numbers, each
optimization's effect from its workflow's row where one can be isolated, the
residual cost, a projection for low, normal and high usage, and the
recommendation for #1439.
