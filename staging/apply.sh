#!/usr/bin/env bash
# Step 4 of docs/token-plan.md: move staging/ into .claude/, archive the external reviewers, test, commit, push.
# Run from the root of a worktree on the PR branch. NO_PUSH=1 stops before the tag and the push.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
[ -d staging/.claude ] || { echo "no staging/ here" >&2; exit 2; }
[ -z "$(git status --porcelain)" ] || { echo "tree not clean" >&2; exit 2; }
if command -v py >/dev/null 2>&1; then PY=(py -3.12); else PY=(python3); fi
BRANCH=claude/improve-token-usage-4fjpcs
git fetch -q origin main

archived=(
  .claude/agents/review-adjudicator.md .claude/agents/review-grok-triager.md
  .claude/commands/review-copilot.md .claude/commands/review-grok.md
  .claude/hooks/guard-triager-dispatch.py .claude/hooks/guard-triager-edit.py
  .claude/sandbox/Dockerfile .claude/sandbox/egress-proxy.py
  .claude/scripts/copilot-authors.sh .claude/scripts/copilot-request.sh .claude/scripts/copilot-request-count.sh
  .claude/scripts/grok-ledger.sh .claude/scripts/grok-review.sh
  .claude/scripts/test_grok_ledger.py .claude/scripts/test_grok_review.py .claude/scripts/test_triager_guards.py
  .claude/scripts/test_egress_proxy.py
  .claude/scripts/pr-comment-reply.sh .claude/scripts/pr-thread-resolve.sh .claude/scripts/pr-issue-comments.sh
  .claude/scripts/pr-review-bodies.sh .claude/scripts/pr-review-comments.sh .claude/scripts/pr-review-threads.sh
)
git rm -q -- "${archived[@]}"
git mv .claude/scripts/test_copilot_feeds.py .claude/scripts/test_pr_helpers.py
settings=staging/.claude/settings.json
mv "$settings" staging/settings.json
cp -R staging/.claude/. .claude/
git rm -r -q --cached staging >/dev/null
git add -A .claude

"${PY[@]}" -m unittest discover -s .claude/scripts -q
"${PY[@]}" .github/comment-gate/comment_gate.py --base origin/main --head "$(git write-tree | xargs git commit-tree -p HEAD -m probe)"
git commit -q -m "feat(ship): the local review replaces the external loops, and their machinery is archived" -m "branch-reviewer and /ship step 5's local review replace Grok's and Copilot's loops; the merge is step 6. The 18 files docs/token-plan.md step 4 counts and the six Copilot-only pr-* helpers leave the tree for archive/external-reviewers; test_copilot_feeds.py keeps its pull request helper cases as test_pr_helpers.py, the agent-grant and push rules move to test_command_grants.py, and each comment the archive touched is cut to the comment gate's budget."

cp staging/settings.json .claude/settings.json
rm -rf staging
"${PY[@]}" -m json.tool .claude/settings.json >/dev/null
git add .claude/settings.json
git commit -q -m "chore(settings): the sandbox and grok-ledger denies go with the files they guarded" -m "Last, and alone, because settings.json self-locks (CLAUDE.md, The harness)."
"${PY[@]}" -m unittest discover -s .claude/scripts -q
[ -z "$(git status --porcelain)" ] || { echo "tree not clean after commit" >&2; exit 1; }
git log --oneline -3
[ "${NO_PUSH:-}" = 1 ] && { echo "NO_PUSH: stopping before tag and push"; exit 0; }

git tag archive/external-reviewers origin/main
git push origin archive/external-reviewers
git push origin HEAD:"$BRANCH"
echo DONE
