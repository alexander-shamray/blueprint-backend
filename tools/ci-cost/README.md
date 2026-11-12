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
share in a second table, then `MAIN_WORKFLOW`'s jobs and each window's days.
Days are whole UTC days, both ends inclusive.

| | |
|---|---|
| Billed minutes | each job's `completed_at - started_at`, rounded up to the whole minute, every attempt of a run included (`filter=all`); a skipped job bills none |
| Runner OS | from the job's labels; `self-hosted` wins over the OS it runs |
| List price | `RATES` in the script, read from GitHub's runner pricing page on the date `RATES_READ` names; a public repository pays none of it on standard runners |
| Linux-equivalent minutes | the list price divided by the Linux rate: how much of a plan's included minutes the usage spends, since the allowance is valued at that rate |
| Per 30 days | the window's figure scaled from its own length |
| `MAIN_WORKFLOW` | the workflow also broken down by job, by run conclusion and by `pull_request` runs per head commit, which is what tells the changes to it apart; matrix legs of one job are summed, and a job's runs are those it billed a minute in, each counted once however many legs or attempts |
| A window's days | Linux minutes and Linux-equivalent minutes, each job on the UTC day it started |
| GHCR | container versions per package, and the bytes of every blob their manifests name, each counted once however many images share it, read through the registry API anonymously or with `GH_TOKEN`; a manifest it could not read makes the figure a floor, and says so. Read only for a window ending yesterday or later, since the API keeps no history of a level |
| Actions storage | live artefacts and active caches, on the same condition |

Three limits to know before reading a figure. **The runs endpoint answers at
most 1,000 results to one query**, so the script halves a span until each query
fits under it. **Pages are requested by number**, never through the `Link`
header, because the proxy cloud sessions run behind refuses the numeric-id URLs
it carries. **A 14-day window takes more requests than a user token's 5,000 an
hour**, so a call refused for a rate limit waits for the hour's reset, or a
minute for a secondary limit, and is retried; a dropped connection or a 5xx is
retried three times before it counts as refused, and a full page still
answering 5xx is read again in smaller pages. **A row arriving between two pages
moves the rest down and repeats one**, so a listing drops a repeated id. A PRs,
label-events, packages or storage read the token is refused — the packages
listing without `read:packages`, typically — is printed under the table rather
than failing the run; a refused runs or jobs read ends it, because no figure
stands without them.

## The latency report

The cost report says what the account pays; `--latency` says how long a pull
request waits, which is what a speed change is measured by. It runs before
and after each one, over the same window:

```bash
py -3.12 tools/ci-cost/ci_cost.py --latency                         # origin's last 30 full days
py -3.12 tools/ci-cost/ci_cost.py --latency --repo owner/a --repo owner/b --window A=2026-10-13:2026-11-12
py -3.12 tools/ci-cost/ci_cost.py --latency --day 2026-11-10 --cap 20 --cap 40 --cap 60 --json
```

A `--window`, or the default, prints four tables per repository and window;
`--day` adds the day's two, and alone prints only them. Each duration is a
nearest-rank p50 / p90, so every figure is one that was measured.

| | |
|---|---|
| Weekly run time | `updated_at - run_started_at` of each successful run, by the Monday, in UTC, of its creation; what showed the 2026-11-08 regression |
| Critical path | of the newest `SAMPLE` successful runs of each workflow: run start to its first job, first job to last end, the longest job, and the span beyond the longest job (the serial chain), from the latest attempt's jobs |
| Queue wait | per job, `created_at` to `started_at`, matrix legs' waits pooled under the job's name, over the same runs |
| Slowest steps | per job, the `STEPS` steps with the highest p90, skipped steps left out |
| A day (`--day`) | every job of the runs created that UTC day in every `--repo`: jobs per hour, the peak running in one minute (a job counts in each minute it touches), how many were running when a job that waited over `QUEUED_AFTER` seconds was queued, and the day replayed under each `--cap` (`CAPS` by default) |

The replay runs the day's jobs first come first served under the cap, each as
long as it ran, so its waits are the cap's alone: the as-run row also holds
the time a runner takes to start, and the two are compared by their gap, never
as equals. A job of an older run re-run that day is not in the day.

A window's runs are listed **per workflow**
(`actions/workflows/<id>/runs`), which groups them by workflow without a
second pass and lets a workflow with no run be skipped; each listing is
halved by `runs_between` under the 1,000-result cap like the cost report's,
and shares its paging and retries. A sampled run costs one request, so
`--latency` takes about `SAMPLE` requests a workflow plus a day's jobs.

## The prune

`ghcr_prune.py` deletes what
[ADR-199](../../docs/backend-architecture/adr/ADR-199-a-scheduled-prune-keeps-what-a-rollout-named-1900-amending-adr-071-and-adr-134.md)
does not keep, from the packages
[ADR-200](../../docs/backend-architecture/adr/ADR-200-the-prune-takes-its-packages-from-the-image-matrix-1915-amending-adr-199.md)
names, and those two ADRs own the rule and its limits, with
[ADR-201](../../docs/backend-architecture/adr/ADR-201-the-rollout-tags-the-digest-it-verified-so-the-prune-keeps-what-a-release-pins-1915-amending-adr-109-and-adr-199.md)'s
tag on what a rollout verified. Without `--delete` it
only reports, so this reads the registry and changes nothing:

```bash
py -3.12 tools/ci-cost/ghcr_prune.py
```

A run takes minutes, since it lists every version and reads the manifests of
the tagged ones it keeps.

Stdlib only, and the suite answers from a fake API:

```bash
cd tools/ci-cost && py -3.12 -m unittest
```
