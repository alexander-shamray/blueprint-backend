#!/usr/bin/env bash
# Fork /branch step 5's worktree under .claude/worktrees/, and nothing else.
#
# The whole command is fixed: `git worktree add --no-track -b <branch> <path>
# origin/main`. A prefix grant on `git worktree add` also buys `-B`, which
# resets an existing branch to the start point past the `git branch --force`
# and `git branch -M` denies, and a prefix rule cannot exclude a flag.
set -euo pipefail
[ "$#" -eq 2 ] || { echo "usage: git-worktree-fork.sh <path> <branch>" >&2; exit 2; }
path="$1"
branch="$2"
# The checks below read `git rev-parse` inside `[ ]`, where a failure is an
# empty string that `set -e` never sees, so the repository is established first.
git rev-parse --git-dir >/dev/null 2>&1 ||
  { echo "not in a git repository" >&2; exit 2; }
# .claude/worktrees/<name>, which is the only shape step 5 creates: the one
# location EnterWorktree moves the session into without a confirmation no
# allow rule can pre-approve. Enforced here as well as there so the helper
# reads safely on its own terms: neither argument can begin with '-', so
# nothing a caller passes arrives as a flag.
[[ "$path" =~ ^\.claude/worktrees/[A-Za-z0-9][A-Za-z0-9._-]*$ ]] ||
  { echo "path must be .claude/worktrees/<name>" >&2; exit 2; }
# The path is relative, so it means the main checkout's directory only when
# run from the main checkout's root. From a linked worktree it would nest a
# second worktree inside the first, which /branch step 0 refuses.
[ -z "$(git rev-parse --show-prefix)" ] ||
  { echo "run from the checkout root" >&2; exit 2; }
[ "$(git rev-parse --git-dir)" = "$(git rev-parse --git-common-dir)" ] ||
  { echo "run from the main checkout, not a linked worktree" >&2; exit 2; }
# A worktree inside the checkout is untracked content of it unless ignored,
# and every `git status` the chain reads — grok-review.sh's clean-tree
# refusal, /commit's unscoped sweep — would then see it.
git check-ignore -q "$path" ||
  { echo "$path is not ignored — add .claude/worktrees/ to .gitignore" >&2; exit 2; }
[ ! -e "$path" ] || { echo "path already exists: $path" >&2; exit 2; }
# Branch names here are <type>/<kebab>, and feat(scope)/ carries parentheses.
case "$branch" in
  -*) echo "branch name may not start with '-'" >&2; exit 2 ;;
  *..*) echo "branch name may not contain '..'" >&2; exit 2 ;;
esac
[[ "$branch" =~ ^[A-Za-z0-9][A-Za-z0-9._/()-]*$ ]] ||
  { echo "not a branch name this helper will take: $branch" >&2; exit 2; }
# It must not exist: refusing here is what makes the missing -B harmless
# rather than merely unavailable, since a caller cannot reset a branch by
# passing its name.
! git show-ref --verify --quiet "refs/heads/$branch" ||
  { echo "branch already exists: $branch" >&2; exit 3; }
git show-ref --verify --quiet refs/remotes/origin/main ||
  { echo "no refs/remotes/origin/main — fetch first (step 1)" >&2; exit 4; }
# `--no-track` is fixed because the start point is a remote-tracking ref, so
# without it the new branch's upstream becomes origin/main and /pr never sets
# the right one. origin/main is fixed because step 5 forks only from the
# fetched base.
git worktree add --no-track -b "$branch" "$path" origin/main
# python3 stands in where the `py` launcher the hooks name does not exist.
python=python3
command -v py >/dev/null 2>&1 && python="py -3.12"
# A session started in the worktree reads that directory's own untracked local
# settings, where the main checkout's approval of its `.mcp.json` servers does
# not reach, so the approval crosses as the names it covers, an approve-all
# expanded to the servers defined now; never the file, whose `permissions` rows
# are the main checkout's own grants. Definitions are found by walking up.
settings=.claude/settings.local.json
if [ -f "$settings" ] && [ ! -e "$path/$settings" ]; then
  # -I keeps a checkout-root json.py off the import path, as the hooks' -P does.
  if ! $python -I - "$settings" "$path/$settings" .mcp.json <<'PY'; then
import json, os, sys
local = json.load(open(sys.argv[1], encoding="utf-8"))
servers = local.get("enabledMcpjsonServers", [])
if not (isinstance(servers, list) and all(isinstance(s, str) for s in servers)):
    sys.exit("enabledMcpjsonServers is not a list of server names")
if local.get("enableAllProjectMcpServers") is True and os.path.isfile(sys.argv[3]):
    defined = json.load(open(sys.argv[3], encoding="utf-8")).get("mcpServers")
    if not isinstance(defined, dict):
        sys.exit(f"{sys.argv[3]} has no mcpServers object")
    servers += [name for name in defined if name not in servers]
disabled = local.get("disabledMcpjsonServers", [])
if not isinstance(disabled, list):
    sys.exit("disabledMcpjsonServers is not a list")
servers = [s for s in servers if s not in disabled]
if servers:
    os.makedirs(os.path.dirname(sys.argv[2]), exist_ok=True)
    with open(sys.argv[2], "w", encoding="utf-8") as out:
        json.dump({"enabledMcpjsonServers": servers}, out)
PY
    echo "warning: could not copy the MCP approval from $settings; the worktree's MCP servers stay unapproved" >&2
  fi
fi
# Seed the new worktree's code index now: /branch enters it mid-session, where
# no `SessionStart` fires. Its own hook does the work, silenced as its hook
# entries are, and with no event it takes the working directory; it detaches
# its own refresh, so the `&` only spares a wait.
hook=.claude/hooks/refresh-index.py
if [ -f "$path/$hook" ]; then
  (cd "$path" && env -u CLAUDE_PROJECT_DIR $python "$hook") </dev/null >/dev/null 2>&1 &
fi
