# Actions cost — before and after

**What GitHub Actions costs this repository, measured over two 14-day
windows either side of the cost changes, and what the difference says about
the visibility and plan decision.**
[`tools/ci-cost/`](../tools/ci-cost/README.md) produces every measured figure
here but those a section names as read by hand, and owns how each is computed;
this file records what it reported, one dated section per window, and the
comparison once both exist. Every figure is a record of its day, per the
contract's §2, and none is kept current.

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
first change merged at 08:24 UTC on 2026-10-25, when #1463 landed #1435 and
#1437 together, so these are the 14 full UTC days before it and the baseline the
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
| #1461, `ci.yml`'s long jobs waiting on `format` and `workflow-lint` | Not isolable: it saves on a run that `format` or `workflow-lint` fails and on a branch run cancelled by a newer push before they finish, and the tool reads no job's conclusion to tell either apart | Only as the change in CI's minutes per run, 68.4 in A |

**The first baseline's conclusion stands on these numbers.** CI is 84% of
the bill; after #1435, the one change here that saves a measurable minute,
the remainder is ≈85,800 Linux-equivalent minutes per 14 days, about
ninety-two times the Free allowance a month, and only #1437, #1461 and #1438
reach it.

## Window B and the comparison — 2026-10-26 to 2026-11-08

Measured on 2026-11-09 against GitHub Free at `RATES` as read that day, which
are the rates read on 2026-10-24. #1461 merged through #1472 on 2026-10-25, so
Window B is the 14 full UTC days after it. Both windows were measured together
with the method's command, so the two columns are one read.

**Window A reproduced.** Its runs, jobs, minutes, list price and label events
equal the section above to the digit. PRs opened read 303 where that section
reads 298, and nothing here says why; the per-PR figures below use 303, so A's
list price per PR is $2.00 here and $2.03 there.

Events inside Window B or between the windows, none of them an optimisation:

- #1530, 2026-10-28: a rerun of `ci.yml` takes a concurrency group of its own.
  First attempts bill as before.
- #1876, 2026-11-08 16:24:59 UTC: `ci.yml`'s write grants split out of the jobs
  that run a pull request's code. `integration-report` billed 9 minutes and
  `publish-images` 18 before the window closed.
- `comment-gate` moved from a `ci.yml` job to its own workflow; Harness drift
  and Main red are new; Compose images ran 102 times against 8.
- The window was harness-heavy, and the path-filtered workflows show it: Broker
  permissions, Game day, Deploy, Observability and Helm ran about 40% to 50%
  less often. No outage or freeze is known inside it.

### The raw numbers

| | A | B | Change |
|---|---|---|---|
| Runs | 8,371 | 8,131 | −2.9% |
| Runs on `pull_request` | 6,165 | 5,905 | −4.2% |
| Runs on `push` | 927 | 563 | −39.3% |
| Runs on `schedule` | 84 | 71 | −15.5% |
| Runs on `workflow_dispatch` | 2 | 0 | |
| Runs on other events (`dynamic`; in B also Main red's 622 `workflow_run`) | 1,193 | 1,592 | +33.4% |
| Jobs, every attempt | 52,702 | 41,231 | −21.8% |
| Billed minutes, Linux | 85,767 | 66,088 | −22.9% |
| Billed minutes, Windows | 1,222 | 316 | −74.1% |
| Billed minutes, macOS | 1,263 | 319 | −74.7% |
| Linux-equivalent minutes | 100,855 | 69,911 | −30.7% |
| Linux-equivalent minutes, per 30 days | 216,117 | 149,809 | |
| Share of the Free allowance, per 30 days | 10,806% | 7,490% | |
| List price if private | $605.13 | $419.47 | −30.7% |
| List price if private, per 30 days | $1,296.70 | $898.86 | |
| Over the Free allowance, per 30 days | $1,284.70 | $886.86 | |
| PRs opened | 303 | 263 | −13.2% |
| Commits pushed that ran a workflow | 280 | 190 | −32.1% |
| Label events, issues and PRs | 1,025 | 530 | −48.3% |
| CI's runs on `pull_request` | 963 | 766 | −20.5% |
| CI's `pull_request` runs per head commit | 1.06 | 1.00 | |
| CI's runs ending `success`, and their minutes | 706, 60,823 | 475, 40,747 | |
| CI's runs ending `cancelled`, and their minutes | 462, 19,738 | 410, 11,197 | |
| CI's runs ending `failure`, and their minutes | 77, 4,655 | 70, 4,352 | |

| Workflow | A runs | A minutes | B runs | B minutes |
|---|---|---|---|---|
| CI | 1,245 | 85,216 | 955 | 56,295 |
| Copilot's `PR #n` runs (286 workflows in A, 251 in B) | 874 | 6,128 | 761 | 5,547 |
| Push on main | 280 | 2,555 | 189 | 1,807 |
| Closure gate | 1,130 | 1,128 | 880 | 881 |
| Locality gate | 1,130 | 1,004 | 880 | 832 |
| Comment gate | 0 | 0 | 822 | 822 |
| Compose | 138 | 1,035 | 117 | 795 |
| Commit gate | 600 | 543 | 766 | 717 |
| Game day | 526 | 527 | 327 | 328 |
| Deploy | 516 | 514 | 293 | 293 |
| Harness drift | 0 | 0 | 287 | 287 |
| Broker permissions | 542 | 550 | 262 | 262 |
| Observability | 425 | 425 | 258 | 258 |
| Helm | 404 | 404 | 238 | 238 |
| Dependabot updates and the rest | 91 | 432 | 37 | 229 |
| Merge notification | 235 | 232 | 262 | 190 |
| Compose images | 8 | 8 | 102 | 105 |
| Realm | 227 | 154 | 73 | 16 |
| Main red | 0 | 0 | 622 | 9 |

Minutes are Linux-equivalent, in this table and the next.

| CI's job | A minutes | B minutes | A per CI run | B per CI run |
|---|---|---|---|---|
| `integration` | 30,619 | 22,335 | 24.59 | 23.39 |
| `edit-target-guard`, Windows and macOS | 15,088 | 3,823 | 12.12 | 4.00 |
| `images` | 14,547 | 10,283 | 11.68 | 10.77 |
| `build` | 5,069 | 3,570 | 4.07 | 3.74 |
| `scaffold-build` | 2,942 | 1,656 | 2.36 | 1.73 |
| `review-helpers` | 2,472 | 2,393 | 1.99 | 2.51 |
| `commits` | 1,705 | 2,257 | 1.37 | 2.36 |
| `comment-gate` | 1,231 | 0 | 0.99 | 0 |
| `packages` | 1 | 532 | 0 | 0.56 |
| The other jobs | 11,542 | 9,446 | 9.27 | 9.89 |

### What the ratios say

**The bill fell 30.7%, and most of that is less activity.** Which ratio reads as
a saving depends on what one unit of work is taken to be:

| Per | A | B | Change |
|---|---|---|---|
| CI run | 81.0 minutes | 73.2 minutes | −9.6% |
| PR opened | $2.00 | $1.59 | −20.1% |
| Head commit on a PR | $0.67 | $0.55 | −18.1% |
| Commit reaching `main` | $2.16 | $2.21 | +2.2% |
| Run of any workflow | $0.072 | $0.052 | −28.6%, flattered by Main red's 622 near-free runs |

**The change reaching `main` cost no less.** Each one took more CI: 3.4 CI runs
on a pull request per commit reaching `main` in A, 4.0 in B, and 263 PRs opened
for 190 commits landed against 303 for 280. Review loops and harness syncs
closed unmerged are the likely reason, which the run data cannot separate, so
this document claims a saving per CI run and per PR, not per change delivered.

### Each change's effect

Per 14 days, at Window B's activity, in Linux-equivalent minutes and list price:

| Change | Effect | At list price | How it was read |
|---|---|---|---|
| #1435, `edit-target-guard`'s Windows and macOS legs | **−7,750**, measured | −$46.50 | The job is exactly those two legs, in both windows: 12.12 minutes per CI run became 4.00, over B's 955 runs. It still ran on about one run in four, because the harness files its path filter watches were much of what the window changed |
| #1437, no CI run on `labeled` or `unlabeled` | **At most −2,700**, bounded | At most −$16.20 | A label's run repeats its head commit. A had 58 runs over its 905 heads, 6.4%, and B none over its 766, so the trigger is gone. 49 runs at B's 54.9 Linux minutes is a ceiling: a re-push or a reopen also repeats a head, and a label's run could be cancelled early |
| #1461, `build`, `integration` and `scaffold-build` waiting on the sub-minute checks | **About −2,100, or −2,900 with `images` behind them**, not isolated | −$12.60 to −$17.40 | Those jobs' minutes per CI run, which also move with what a window changes. No saving shows on failing runs, 60.5 minutes each in A and 62.2 in B, and a cancelled run fell from 42.7 to 27.3, of which the guard's legs explain up to 8.1 |
| #1436, `realm.yml`'s schedule | None: left out | | Realm's fall from 154 to 16 is not this change, which never landed |
| #1438, a self-hosted runner | None: left out | | Priced under *The whole private option* |

**Together the three save about 9,800 to 13,400 minutes per 14 days, $59 to $80
at list price: 12% to 16% of what Window B would have cost without them.** The
rest of the 30,944-minute fall is activity. A successful CI run cost 86.2
minutes in A and 85.8 in B although it lost about eight minutes of the guard's
legs, so its Linux jobs grew by about that much, and the comparison cannot hold
that constant.

### The residual cost

**149,809 Linux-equivalent minutes per 30 days, $898.86 at list price, about 75
times the Free allowance.** CI is 80.5% of it, and `integration` and `images`
are 47% of the whole on their own. Copilot's runs are 7.9%, and no change
reached them. No further job's trigger is a comparable lever: what is left is
the suite itself, and how many times a pull request runs it, since 43% of CI's
runs ended cancelled and spent 16% of the window's minutes doing so.

### Usage scenarios

Per 30 days. Normal is Window B's rate; High is 1.5 times it, Window A's rate of
commits reaching `main`; Low is a quarter of it, about B's quietest day,
2026-11-04, kept up for a month. Low and High are assumptions scaled from B, not
measurements. The days here and below are a one-off sum over the tool's job
reads, which the tool does not print.

| | Low | Normal | High |
|---|---|---|---|
| PRs opened | 141 | 564 | 845 |
| All on GitHub's runners: minutes | 37,452 | 149,809 | 224,714 |
| All on GitHub's runners: list price | $224.71 | $898.86 | $1,348.29 |
| Heavy jobs self-hosted: minutes left on GitHub's runners | 16,420 | 65,681 | 98,522 |
| Heavy jobs self-hosted: list price | $98.52 | $394.09 | $591.13 |
| All Linux self-hosted: minutes left on GitHub's runners | 2,048 | 8,192 | 12,288 |
| All Linux self-hosted: list price | $12.29 | $49.15 | $73.73 |

The heavy jobs are `integration`, `images`, `build`, `scaffold-build`, Compose,
Game day and Deploy, 56% of the bill. All Linux leaves only the Windows and
macOS legs. Moving the heavy jobs alone still leaves about 33 times the Free
allowance at Normal.

**The runner has to hold the load.** B's 66,088 Linux minutes are 3.3 jobs
running without a pause for the whole 14 days, and its busiest day averaged 5.8.
Hetzner's AX42, 8 cores and 64 GB, was €79 a month and its AX102, 16 cores and
128 GB, €159, both excluding VAT, on the date below; upkeep and the fallback for
a required check when the runner is offline are unpriced, and #1438 owns them.
Self-hosted minutes bill nothing on the date below. GitHub announced a $0.002
charge per minute for March 2026 and postponed it; its pricing page names none,
and it would add about $283 a month to All Linux at Normal.

### The whole private option

Prices read on 2026-11-09, for one user and one active committer: `main`'s only
human author in Window B, the bots aside. Each figure is a month, and a
machine's price comes on top of the two self-hosted columns.

| | Keeps | Plan and add-ons | All hosted, Low / Normal / High | Heavy jobs self-hosted, Normal | All Linux self-hosted, Normal |
|---|---|---|---|---|---|
| Public, Free | Everything | $0 | $0 / $0 / $0 | Refused: #1438 bars a runner from a public repository | Refused |
| Private, Enterprise Cloud organisation with Code Security | Attestations, `production` reviewers, CodeQL | $21 + $30 = $51 | $51.00 / $649.86 / $1,099.29 | $145.09 | $51.00 |
| Private, Pro on the personal account | None of the three: supersedes ADR-109 and ADR-134 | $4 | $210.71 / $884.86 / $1,334.29 | $380.09 | $35.15 |
| Private, Free | None of the three, nor protected branches, so `main`'s required checks | $0 | $212.71 / $886.86 / $1,336.29 | $382.09 | $37.15 |

Each plan's included usage is taken off the minutes first: $300 on Enterprise
Cloud, $18 on Pro and $12 on Free. An organisation on Team is $4 a user and
includes $18, but keeps none of the three without Code Security, and even then
neither attestations nor the reviewers. Secret Protection, for secret scanning
and push protection on a private repository, is $19 more per active committer on
any row that wants it. Keeping `blueprint-backend` private and its siblings
public changes none of these figures, since the minutes and the safeguards are
all here.

**GHCR storage bills nothing today, public or private.** At the end of Window B
the repository held 14,170 container versions in 18 public packages, as the tool
reads them, holding at least 47.1 GB: a one-off walk of each version's manifests
through the registry API, each blob counted once, with 73 reads timing out, so
the figure is a floor. GitHub's page says Container registry storage and
bandwidth are currently free, with a month's notice of any change. Were that to
end at the calculator's $0.25 per GB, this level would cost about $11.65 a month
on Free, $11.27 on Pro and nothing within Enterprise Cloud's 50 GB. The cost of
a private package is the one #1439 names: an anonymous pull stops working for
the fork and any adopter.

**Actions storage is too small to change the table.** A read by hand on
2026-11-09 found 18,919 live artifacts holding 3.96 GB, which at $0.25 per GB is
under $1 a month over Free's or Pro's allowance and nothing on Enterprise
Cloud's. The 7.56 GB of caches sit inside the 10 GB each plan includes per
repository.

Read on 2026-11-09:

- [Runner
  pricing](https://docs.github.com/en/billing/reference/actions-runner-pricing):
  `RATES`, unchanged, and no self-hosted charge.
- [Actions
  billing](https://docs.github.com/en/billing/concepts/product-billing/github-actions):
  included usage per plan; self-hosted usage free.
- [GitHub's pricing](https://github.com/pricing): Enterprise $21 and Team $4 a
  user. It lists no Pro, whose $4 is from [the plans
  FAQ](https://docs.github.com/en/get-started/learning-about-github/faq-about-changes-to-githubs-plans)
  and is the one price here no page confirmed on the day.
- [Security plans](https://github.com/security/plans): Code Security $30 and
  Secret Protection $19 per active committer.
- [Packages
  billing](https://docs.github.com/en/billing/concepts/product-billing/github-packages)
  and [the calculator](https://github.com/pricing/calculator): the Container
  registry's terms and $0.25 per GB.
-
  [Environments](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments):
  required reviewers on Free, Pro and Team are for public repositories only.
- [Hetzner's AX
  servers](https://www.hetzner.com/dedicated-rootserver/matrix-ax/).

Today's reading of the attestation docs names no plan, so the Enterprise Cloud
requirement rests on the check #1439 records from 2026-10-26. The billing usage
report was not read, since the token lacks the `user` scope, so no figure here
has been compared with one.

### The recommendation

**#1439: stay public.** Every private option that keeps the safeguards costs at
least $51 a month plus a machine large enough for all of CI, and that needs a
runner, an organisation and a required-check fallback none of which exists; left
on GitHub's runners it costs about $650 a month at Normal. Pro looks cheap only
with every Linux job self-hosted, at $35.15 plus the machine, and gives up the
signed-image rollout, the `production` reviewers and CodeQL for it. Public keeps
all of that for nothing. Going private would also not recall the one fork's
copy. This is a recommendation: #1439 says the decision is the owner's, and it
stays open for them.

**#1438: do not adopt while the repository is public**, since its own boundary
bars a runner there and public minutes bill nothing, so there is nothing to
save. If the owner takes #1439 private, adopt it for all Linux jobs and not only
the heavy ones: only that brings the minutes inside Enterprise Cloud's included
$300 at every scenario.