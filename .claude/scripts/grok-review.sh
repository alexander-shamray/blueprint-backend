#!/usr/bin/env bash
# Run Grok's /review-branch inside a container, so the external reviewer gets
# the repository and nothing else.
#
# A worktree would isolate only the reviewer's edits: the process would still
# hold this host's filesystem, network and credentials, and a deny list bounds
# the session, never the subprocess it starts. The boundary is
# .claude/sandbox/Dockerfile — no gh token, no SSH keys, no host filesystem
# beyond the clone below, non-root inside — so bypassPermissions is passed and
# its blast radius is the container.
#
# Egress is confined to api.x.ai and auth.x.ai, and it takes two containers,
# because Docker alone offers all or none. The reviewer runs on a network
# created with --internal, which Docker gives no gateway: a member routes to
# the other members and to nothing else, and the embedded resolver answers
# SERVFAIL for any name outside it. The one member with a second leg on the
# bridge is egress-proxy.py, a CONNECT-only tunnel with a host allow-list,
# which the reviewer reaches through HTTPS_PROXY.
#
# The credential half is narrowed, not closed. The OAuth fallback below copies
# ~/.grok/auth.json in, and it carries a refresh-token-bearing session for the
# x.ai account that anything inside can read; confined egress means it can be
# sent only to the hosts the session is for, and the session's own blast
# radius against x.ai remains. XAI_API_KEY is the documented posture —
# scoped, revocable, and no file crosses — and the ordering below tries it
# first.
#
# This helper owns the ledger slot it spends: it takes the slot, resolves the
# pull request from the branch it is about to clone, and posts the reservation
# itself immediately before the model call it accounts for, so invoking a
# review and spending a slot are one operation no ordering mistake can
# separate. .claude/settings.json denies the `reserve` and `release` spellings
# to the session that invokes it.
set -euo pipefail

# The ceiling is read out of the ledger rather than restated, so the bound has
# one literal. An unreadable ceiling refuses every slot rather than admitting
# all of them, because a cap must fail closed.
ledger="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/grok-ledger.sh"
ceiling=$(sed -n 's/^CEILING=\([1-9][0-9]*\)$/\1/p' "$ledger" | head -1)
[[ "$ceiling" =~ ^[1-9][0-9]*$ ]] ||
  { echo "could not read CEILING from $ledger; refusing to reserve a check" >&2; exit 2; }

# The slot this review spends and which kind of check it is. Validated to the
# ledger's own vocabulary rather than passed through, because a slot outside
# 1..$ceiling is a claim about a cap that does not exist.
#
# The PR is not an argument: a caller-supplied number — a typo, or an
# instruction substituting another open pull request — would post the
# reservation there while this branch is cloned and reviewed. It is resolved
# below from the branch instead, so the slot and the review are one subject.
[ "$#" -eq 2 ] ||
  { echo "usage: grok-review.sh <slot 1-$ceiling> <full|recheck>" >&2; exit 2; }
slot="$1"
mode="$2"
[[ "$slot" =~ ^[1-9][0-9]*$ ]] && [ "$slot" -le "$ceiling" ] ||
  { echo "slot must be 1..$ceiling — the ceiling grok-ledger.sh declares: $slot" >&2; exit 2; }
case "$mode" in
  full|recheck) ;;
  *) echo "mode must be full or recheck: $mode" >&2; exit 2 ;;
esac

# Two patterns, declared together and away from the code that applies them, so
# each has one declaration for the suite beside this file to read by name.

# What a usage limit looks like. The preflight below skips such a round rather
# than failing it, and an exhausted prepaid balance answers `API error (status
# 402 Payment Required): Grok Build usage balance exhausted`, which is not
# `429`, not `quota` and not `(no|any) credits`.
#
# A status code needs a status context and a boundary on both sides. The
# pattern is matched against the whole text of a probe run, and a false
# positive is the expensive direction: it reports a working reviewer as out of
# limits and skips every round silently. A word-bounded `402` still matches
# `"input_tokens": 402`, so the number must follow `status` or `code` within a
# few non-digits, and `([^0-9]|$)` stops `status 4021` matching. The prose
# alternatives catch the same responses; the codes stay because a provider
# rewords its prose and keeps its codes.
limit_re='rate.?limit|quota|usage limit|usage balance|balance exhausted|too many requests|(no|any) credits|402 payment required|(status|code)[^0-9]{0,3}(402|429)([^0-9]|$)'

# What a finished turn looks like, parsed rather than matched. A reviewer that
# exhausts its output or turn budget (`max_tokens`, `max_turn_requests`) exits
# 0, writes JSON and leaves no suggestions.md, an absence that reads as a clean
# verdict — so the root `stopReason` must be exactly this value. A regex cannot
# tell a root field from a nested one, nor establish that the output is JSON,
# so the check below asks jq. The value is pinned the way the client version is
# in .claude/sandbox/Dockerfile: a grok bump re-verifies it.
stop_ok=end_turn

# Docker on Windows wants a Windows path in --volume; elsewhere the path is
# already right. cygpath exists only under MSYS/Git Bash, which is the tell.
host_path() {
  if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi
}

branch=$(git branch --show-current)
[ -n "$branch" ] || { echo "not on a branch" >&2; exit 2; }
# The pull request the ledger row lands on, resolved from the branch this
# script is about to clone, so the two cannot be different subjects.
#
# `--head` filters on the branch name alone and matches across forks, so a
# fork's open pull request on the same branch name is a candidate, and would
# take the reservation while this branch is reviewed. So the head repository
# must be this checkout's; `gh repo view` reads the checkout, so both sides of
# the comparison are properties of the filesystem.
repo=$(gh repo view --json nameWithOwner --jq .nameWithOwner) ||
  { echo "cannot resolve this checkout's repository" >&2; exit 2; }
# Blank counts as missing, and `||` does not see it: `gh` printing an empty
# string exits 0, and the comparison below would then match every row whose
# head repository is absent, as a deleted fork's is, admitting a stranger's
# pull request precisely when its owner cannot be established.
[ -n "$repo" ] ||
  { echo "this checkout's repository resolved to nothing" >&2; exit 2; }
# Tab-separated and filtered in awk rather than inside --jq, because gh's --jq
# takes no --arg: embedding "$repo" in the jq program would put a shell value
# into a program text, which is the shape this directory exists to avoid.
pr=$(gh pr list --head "$branch" --state open --json number,headRepository \
       --jq '.[] | "\(.headRepository.nameWithOwner // "")\t\(.number)"' |
     awk -F'\t' -v r="$repo" '$1 == r { print $2 }') ||
  { echo "cannot ask GitHub which pull request $branch has" >&2; exit 2; }
# Exactly one, and anything else is refused rather than guessed past: none
# means no ledger to write to and so no cap to enforce, and more than one is
# ambiguous.
[ "$(grep -c . <<<"$pr")" -eq 1 ] ||
  { echo "expected exactly one open pull request for $branch in $repo, found: ${pr:-none}" >&2; exit 2; }
# suggestions.md is the one file allowed to differ — it is the review's own
# working state. Anything else, tracked or untracked, means the reviewer would
# read a state the PR does not carry: the clone below holds only commits.
status=$(git status --porcelain)
[ -z "$(grep -v '^?? suggestions.md$' <<<"$status" || true)" ] ||
  { echo "tree has uncommitted changes; commit before the review, or the reviewer reads a state the PR does not carry" >&2; exit 3; }
# The daemon, not just the CLI: `command -v docker` passes on a machine whose
# Docker Desktop is installed and stopped, and the build then fails with
# Docker's own generic status instead of exit 7.
docker info >/dev/null 2>&1 ||
  { echo "docker is required and its daemon must be running: the reviewer runs in a container (.claude/sandbox/Dockerfile)" >&2; exit 7; }
# jq, because the verdict below is a JSON document and the question asked of it
# — what is the root stopReason — is not one a regex can answer. Probed here
# rather than discovered at the end of a review, on the same argument as the
# daemon check above: a missing tool should cost a second, not a round.
command -v jq >/dev/null 2>&1 ||
  { echo "jq is required: the reviewer's verdict is JSON and its root stopReason must be parsed rather than matched" >&2; exit 14; }

sandbox=$(cd "$(dirname "${BASH_SOURCE[0]}")/../sandbox" && pwd)
work=$(mktemp -d "${TMPDIR:-/tmp}/grok-review-XXXXXX")
result=$(mktemp "${TMPDIR:-/tmp}/grok-review-result-XXXXXX")
auth=$(mktemp -d "${TMPDIR:-/tmp}/grok-review-auth-XXXXXX")
# The proxy and its network are torn down here too, in that order: a network
# with a member still attached refuses to be removed, and a proxy left running
# is a container with egress that nothing is watching. Both names are assigned
# after the build, so the guard is on the variable and not on docker's answer.
net=""
proxy=""
cleanup() {
  [ -z "$proxy" ] || docker rm --force "$proxy" >/dev/null 2>&1 || true
  [ -z "$net" ] || docker network rm "$net" >/dev/null 2>&1 || true
  rm -rf "$work" "$auth" 2>/dev/null || true
  rm -f "$result" 2>/dev/null || true
}
trap cleanup EXIT
chmod 700 "$auth"

# A clone, not a worktree. A worktree's .git is a file pointing back into this
# checkout, and that path is precisely what the container must not mount — git
# inside would resolve nothing. A clone carries its own .git and its own
# origin/* refs, which is all /review-branch reads.
git clone --quiet --no-hardlinks . "$work/repo"
git -C "$work/repo" checkout --quiet "$branch"
# The clone's origin/main is built from this checkout's local main, not from its
# origin/main, and those are routinely different: /branch branches from
# origin/main without ever advancing local main, so local main here is whatever
# it was when it was last pulled. Left alone, the reviewer diffs the branch
# against a stale base and reports commits that are already on main as though
# this branch introduced them. Fetch the real remote-tracking ref, with its
# objects, so the review's base is the base the PR will merge into.
#
# Fetched through the clone's own `origin`, which git already pointed back at
# this checkout, rather than through a path built here: `$(pwd)` under MSYS is
# `/c/dev/...`, which a Windows git binary cannot resolve at all — it reports
# the source as "not a git repository", which reads like a broken checkout
# rather than a mistranslated path.
if git rev-parse --verify --quiet refs/remotes/origin/main >/dev/null; then
  git -C "$work/repo" fetch --quiet origin \
    "+refs/remotes/origin/main:refs/remotes/origin/main"
fi
# The recheck contract: an existing suggestions.md is the file the review
# re-verifies, so it crosses into the copy; nothing else does.
#
# A regular file or nothing, on both crossings. suggestions.md is untracked and
# the container can replace it with a link to /proc/self/environ, and `cp`
# follows links by default, so a following copy reads the environment of
# whichever process dereferences it. A FIFO or socket of that name passes the
# dirty-tree allow-list above as `?? suggestions.md`, then fails `-f` and would
# be skipped, silently turning a recheck into a full review.
if [ -L suggestions.md ] || { [ -e suggestions.md ] && [ ! -f suggestions.md ]; }; then
  echo "suggestions.md is a symlink or not a regular file; refusing to import it" >&2
  exit 9
fi
[ -f suggestions.md ] &&
  cp -P suggestions.md "$work/repo/suggestions.md"

# Built before the credential check below, which needs the image to run its
# preflight in.
# host_path here too: the build context and -f are paths Docker resolves, not
# paths bash does, so an MSYS spelling reaches the daemon as a directory that
# does not exist — which it reports as a missing context rather than as a path
# it could not translate.
sandbox_host=$(host_path "$sandbox")
# The reviewer's uid must match this host's, or the bind-mounted clone and the
# credential copies — which keep their host ownership — are unreadable and
# unwritable inside. Docker Desktop maps ownership and hides the problem, so
# this matters on Linux and is invisible on Windows. `id -u` is meaningless
# under MSYS, hence the guard.
#
# Native Linux only, not "anything that is not MSYS". On macOS the ids are
# passed by Docker Desktop's VM, which does not need them matched — and a
# typical macOS primary gid of 20 already exists in Debian as `dialout`, so
# `groupadd --gid 20` aborts the build over a problem that host did not have.
#
# Root is refused rather than passed through. Running the helper as root would
# send 0:0, which fails the build outright — root already exists in Debian —
# and would contradict the non-root boundary even if it succeeded. Better to
# say so than to hand back useradd's error.
build_args=()
if [ "$(uname -s)" = "Linux" ]; then
  [ "$(id -u)" -ne 0 ] ||
    { echo "refusing to build the reviewer as root: the image runs non-root by design" >&2; exit 11; }
  build_args+=(--build-arg "REVIEWER_UID=$(id -u)" --build-arg "REVIEWER_GID=$(id -g)")
fi

# The image is used by ID, never by the tag. `grok-reviewer:local` is global to
# the daemon and mutable, so a concurrent review — another checkout, other
# uid/gid arguments — can move it between this build and the runs below, and
# those runs hand the image credentials. Binding to the digest that this build
# produced makes what gets built and what gets trusted the same object.
image=$(docker build --quiet "${build_args[@]}" \
  --file "$sandbox_host/Dockerfile" "$sandbox_host")
[ -n "$image" ] ||
  { echo "docker build produced no image id" >&2; exit 10; }

# The network the reviewer runs on, and the one container allowed off it.
# `--internal` is the mechanism: Docker gives the network no gateway, so a
# member has a route to the other members and to nothing else, and the
# embedded resolver refuses to forward a name outside it. The proxy is a member
# with a second leg on the default bridge, running this image with no clone and
# no credential mounted — it already carries python3 for the licence gate, so
# the proxy is one stdlib script on PATH and no second image to pin.
# `--network-alias proxy` is what the reviewer's HTTPS_PROXY names, and it is
# unambiguous because the network is per review: both names derive from this
# run's own temp directory, so two reviews on one daemon never share either.
#
# Created here, after the build and before the credential probes, because the
# probes are model calls that carry the credential too. Every reviewer-side
# `docker run` from here down takes $net_args.
#
# Waited for, not assumed: `--detach` returns before the script binds, and a
# reviewer started into that gap fails its first call on a refused connection,
# which reads like a dead proxy rather than an early start. Ten seconds, then
# refuse — a proxy that never listened is not one to review behind.
net="grok-review-$(basename "$work")"
proxy="$net-proxy"
docker network create --internal "$net" >/dev/null ||
  { echo "could not create the reviewer's internal network" >&2; exit 15; }
docker run --detach --name "$proxy" --network "$net" --network-alias proxy \
  "$image" egress-proxy >/dev/null ||
  { echo "could not start the egress proxy" >&2; exit 15; }
docker network connect bridge "$proxy" ||
  { echo "could not give the egress proxy its leg on the bridge" >&2; exit 15; }
ready=0
for _ in 1 2 3 4 5 6 7 8 9 10; do
  if docker exec "$proxy" python3 -c \
       'import socket; socket.create_connection(("127.0.0.1", 8888), timeout=1).close()' \
       >/dev/null 2>&1; then
    ready=1
    break
  fi
  sleep 1
done
[ "$ready" -eq 1 ] ||
  { echo "the egress proxy did not start listening; the review did not run" >&2; exit 15; }
# HTTPS_PROXY is the variable grok reads for its API calls. HTTP_PROXY is set
# beside it so a plain-http attempt is refused by the proxy at once rather than
# timing out on a route that does not exist.
net_args=(--network "$net"
          --env HTTPS_PROXY=http://proxy:8888
          --env HTTP_PROXY=http://proxy:8888)

# Credentials. XAI_API_KEY is the better of the two and needs no file at all:
# grok falls back to it when no session token is present, and a fresh container
# has none. It carries no refresh token and none of the account holder's
# details, so it is tried first — mint one at console.x.ai.
#
# A set key is not a usable one: a key whose team has no credits answers every
# call with `403 permission-denied` while the OAuth session may work, so the
# key is probed with a trivial prompt before it is preferred.
#
# --env NAME, never --env NAME=VALUE. The second spelling puts the key in
# docker's argv, where every `ps` on this machine can read it for the life of
# the container; the first forwards the value this process already holds.
mounts=()
key_probe=""
if [ -n "${XAI_API_KEY:-}" ] &&
   key_probe=$(docker run --rm "${net_args[@]}" --env XAI_API_KEY "$image" \
     grok -p "ok" 2>&1); then
  mounts+=(--env XAI_API_KEY)
else
  # A key that answers with a limit signal is authenticated but out of window,
  # and the two need different exits. With a usable OAuth fallback, fall
  # through: the session may sit on a team whose window is open, and the
  # preflight below judges whatever auth was actually selected. Usable means
  # all three session files — auth.json alone with agent_id or config.toml
  # missing exits 8 below before the preflight ever runs, so a partial
  # session must not swallow the limit signal. With no usable fallback,
  # exit 8's "cannot authenticate" is the wrong class — authenticating is not
  # the problem — so this is the preflight's skip, issued one step earlier.
  oauth_ready=1
  for f in auth.json agent_id config.toml; do
    [ -f "$HOME/.grok/$f" ] || oauth_ready=0
  done
  if [ -n "${XAI_API_KEY:-}" ] && [ "$oauth_ready" = 0 ] &&
     grep -qiE "$limit_re" <<<"$key_probe"; then
    echo "grok is out of usage limits (API key, no usable OAuth fallback) — skipping this review, not failing it:" >&2
    grep -ioE "$limit_re" <<<"$key_probe" | head -1 >&2
    exit 12
  fi
  [ -z "${XAI_API_KEY:-}" ] ||
    echo "XAI_API_KEY is set but did not authenticate — no credits on its team? Using the OAuth session instead." >&2
  [ -f "$HOME/.grok/auth.json" ] ||
    { echo "no usable XAI_API_KEY and no $HOME/.grok/auth.json: the reviewer cannot authenticate" >&2; exit 8; }
  cp "$HOME/.grok/auth.json" "$auth/auth.json"
  chmod 600 "$auth/auth.json"
  mounts+=(--volume "$(host_path "$auth/auth.json"):/home/reviewer/.grok/auth.json:Z")
  # agent_id and config.toml are required as well: a container without this
  # machine's agent identity and settled configuration takes itself to be a
  # first run, registers a new team, and every call returns
  # `403 permission-denied: your newly created team doesn't have any credits`.
  # All three files are copies, discarded afterwards, and none is a credential
  # for anything but grok. Failing here costs a second; discovering it at the
  # model call costs the review.
  for extra in agent_id config.toml; do
    [ -f "$HOME/.grok/$extra" ] ||
      { echo "$HOME/.grok/$extra is missing; the OAuth session cannot resolve its team without it" >&2; exit 8; }
    cp "$HOME/.grok/$extra" "$auth/$extra"
    chmod 600 "$auth/$extra"
    mounts+=(--volume "$(host_path "$auth/$extra"):/home/reviewer/.grok/$extra:Z")
  done
fi

# Usage-limit preflight — skip, never fail. A rate limit or an exhausted quota
# answers the model call, not the handshake, so the credential probe above
# passes and the review would die mid-run as "did not run" (exit 4). A review
# the limits will not allow is not a defect in the branch, so exit 12 is
# distinct and ship.md skips the round rather than stopping the loop. Probed
# against the auth actually selected, so the OAuth path is covered too.
probe_rc=0
limit_probe=$(docker run --rm "${net_args[@]}" "${mounts[@]}" "$image" grok -p "ok" 2>&1) || probe_rc=$?
if grep -qiE "$limit_re" <<<"$limit_probe"; then
  echo "grok is out of usage limits — skipping this review, not failing it:" >&2
  grep -ioE "$limit_re" <<<"$limit_probe" | head -1 >&2
  exit 12
fi
# A dead fallback must not bury the key's limit signal. File presence made
# OAuth the selected auth, but an expired, revoked or corrupt session fails
# this probe with an auth-shaped answer, not a limit-shaped one — and
# proceeding would burn a full review run to reach exit 4 and learn what
# both probes already said. The key's limit is the operative fact; skip.
if [ "$probe_rc" -ne 0 ] && [ -n "${XAI_API_KEY:-}" ] &&
   grep -qiE "$limit_re" <<<"$key_probe"; then
  echo "grok is out of usage limits (API key) and the selected OAuth fallback failed its probe — skipping this review, not failing it:" >&2
  grep -ioE "$limit_re" <<<"$key_probe" | head -1 >&2
  exit 12
fi

# The reservation, and its position is the accounting rule: every path that
# can refuse before this line spends nothing — a dirty tree, no daemon, a
# missing credential, a bad suggestions.md shape, and the usage-limit skips
# above — which is why exit 12 has no release to post. It is written before
# the call for interruption safety (ship.md): written after, an interrupted
# run spends a check and leaves no record.
#
# The write is also an election. Two resumed /ship runs can read the same count
# and claim the same slot, and the ledger settles it after posting: a loser
# exits 4, which arrives here as exit 13 and means a concurrent run is mid-check
# on this PR. The loop stops rather than taking the next slot, because two Grok
# runs share one root suggestions.md. An unreachable ledger takes the same exit,
# because the caller does the same with each: not count this round, and stop.
#
# A slot is spent if the review's model call was launched, and also when the
# ledger posts its reservation but fails the trust check on the read that
# settles the election. That is conservative on purpose: releasing on a lookup
# that did not complete would fail open, and the cost is at most one check out
# of $ceiling.
#
# `$ledger` is the path resolved at the head of this file, not recomputed.
ledger_rc=0
bash "$ledger" "$pr" reserve "$slot" "$mode" >&2 || ledger_rc=$?
[ "$ledger_rc" -eq 0 ] ||
  { echo "could not reserve check $slot/$ceiling on PR $pr (ledger exit $ledger_rc); the review did not run" >&2; exit 13; }

set +e
docker run --rm \
  "${net_args[@]}" \
  --volume "$(host_path "$work/repo"):/review:Z" \
  "${mounts[@]}" \
  --workdir /review \
  "$image" \
  grok -p "/review-branch" --permission-mode bypassPermissions --output-format json >"$result"
grok_status=$?
set -e

# A review that did not run must never be mirrored into a clean verdict. An
# absent suggestions.md means both "nothing to report" and "the reviewer never
# looked", and only these checks separate them — the same fail-open shape §13.5
# names for an empty readiness predicate set.
[ "$grok_status" -eq 0 ] ||
  { echo "grok exited $grok_status; the review did not run" >&2; exit 4; }
[ -s "$result" ] ||
  { echo "grok produced no output; the review did not run" >&2; exit 5; }
# The allow-list declared at the head of this file, applied to the root of the
# document. Three failures collapse into one question, which is the point of
# parsing rather than matching: output that is not JSON, output whose root
# carries no stopReason, and output whose root carries the wrong one.
#
# A mention inside the review's own prose cannot be mistaken for the verdict
# either — `.stopReason` names a field, where a regex only ever named a
# substring.
# Reduce a reviewer-supplied field to something that cannot carry an
# instruction: an identifier alphabet, truncated. `tr -cd` deletes the
# complement of the set, so newlines, quotes and spaces are gone rather than
# escaped — escaping is a property of the consumer and this value is printed
# straight to a terminal and into /ship's context.
safe_token() {
  printf '%.40s' "$(printf '%s' "$1" | tr -cd 'A-Za-z0-9_.-')"
}

stop=$(jq -r 'if type == "object" then (.stopReason // "<absent>") else "<not-an-object>" end' \
         "$result" 2>/dev/null) ||
  { echo "grok's output is not valid JSON; the review did not run and suggestions.md is left as it was" >&2; exit 6; }
if [ "$stop" != "$stop_ok" ]; then
  # `$stop` and `.cancellationCategory` are fields of a document the reviewer
  # wrote, so echoed verbatim they would hand /ship reviewer prose, newlines
  # included, on the path with no clean verdict. Both are reduced to a token
  # alphabet: a real value is a bare identifier, so nothing diagnostic is lost.
  category_raw=$(jq -r '.cancellationCategory // empty' "$result" 2>/dev/null)
  category=$(safe_token "$category_raw")
  [ -z "$category" ] || echo "grok reported cancellation category: $category" >&2
  echo "grok did not finish its turn — the root stopReason is \"$(safe_token "$stop")\", not \"$stop_ok\"; the review did not run and suggestions.md is left as it was" >&2
  exit 6
fi
# The verdict is extracted; the transcript is not printed. Every byte of
# $result is reviewer-authored, and printed it would reach the caller's context
# as prose, where /review-grok holds `Edit` and `Write` and /ship runs that
# triage unattended — for no reader, since ship.md branches on the exit code
# and on whether suggestions.md exists. The findings cross by one route,
# suggestions.md, imported below under the shape guards.
echo "grok finished its turn (stopReason \"$stop\") — findings, if any, are in suggestions.md" >&2
# Import the one artefact the review owns. Its absence is the clean verdict —
# trustworthy only because the checks above have ruled out a cancelled run.
#
# The file is reviewer-controlled here, so a link planted inside the container
# would be dereferenced in a host process, against host paths. Every
# non-regular shape is refused, and before the host's copy is removed: a FIFO,
# socket or directory left by the reviewer would otherwise delete the findings
# on this side, fail `-f`, copy nothing, and report the run clean. The
# destination is removed first so a pre-existing link on this side cannot be
# written through.
out="$work/repo/suggestions.md"
if [ -L "$out" ] || { [ -e "$out" ] && [ ! -f "$out" ]; }; then
  echo "the review left suggestions.md as a symlink or not a regular file; refusing to import it" >&2
  exit 9
fi
rm -f suggestions.md
if [ -f "$out" ]; then
  cp -P "$out" suggestions.md
fi
