#!/usr/bin/env bash
# Pin branch-reviewer, bug-auditor and security-auditor to model: opus, with the case that pins it, and commit.
# Run on chore/pin-agent-models; the push is /ship's. Resumes a run that patched the four files and stopped.
set -euo pipefail
# A function, so bash has read all of it before the run deletes this file.
main() {
  cd "$(dirname "$0")/.."
  [ -f staging/patch_models.py ] || { echo "no staging/ here" >&2; exit 2; }
  [ "$(git branch --show-current)" = chore/pin-agent-models ] || { echo "not on chore/pin-agent-models" >&2; exit 2; }
  if command -v py >/dev/null 2>&1; then PY=(py -3.12); else PY=(python3); fi
  TRAILER="Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01D8cqe8VgNhSJfsJceVq8Ff"
  PATCHED=(.claude/scripts/test_command_grants.py .claude/agents/branch-reviewer.md
    .claude/agents/bug-auditor.md .claude/agents/security-auditor.md)
  CASE=test_command_grants.AnAgentIsGrantedByExactType.test_the_reviewer_and_the_auditors_run_on_opus
  dirty="$(git status --porcelain | tr -d '\r' | sort)"
  wanted="$(printf ' M %s\n' "${PATCHED[@]}" | sort)"
  if [ -z "$dirty" ]; then
    "${PY[@]}" staging/patch_models.py . test
    # The case must fail on the profiles as they stand, or it pins nothing.
    if (cd .claude/scripts && "${PY[@]}" -m unittest "$CASE" 2>/dev/null); then
      echo "the new case passed before the profiles changed" >&2; exit 1
    fi
    "${PY[@]}" staging/patch_models.py . agents
  elif [ "$dirty" = "$wanted" ]; then
    # An earlier run proved the case failing and patched all four; check that is what is here.
    grep -q run_on_opus .claude/scripts/test_command_grants.py || { echo "case missing" >&2; exit 2; }
    for f in "${PATCHED[@]:1}"; do
      tr -d '\r' < "$f" | grep -qx 'model: opus' || { echo "no pin in $f" >&2; exit 2; }
    done
  else
    echo "tree holds something other than the four patched files:" >&2; echo "$dirty" >&2; exit 2
  fi
  (cd .claude/scripts && "${PY[@]}" -m unittest test_command_grants)
  # test_harness_denies wants every tracked tree edit-denied, so staging/ goes before it runs.
  git rm -r -q staging
  if ! (cd .claude/scripts && "${PY[@]}" -m unittest test_harness_denies); then
    git checkout -q HEAD -- staging; exit 1
  fi
  git add "${PATCHED[@]}"
  git commit -q -m "chore(harness): the reviewer and the auditors run on Opus" \
    -m "An error from branch-reviewer, bug-auditor or security-auditor is a silent miss, so none of them takes the session's model: a session started on Sonnet would otherwise review and audit on Sonnet. Each profile now carries model: opus; a spawn's model still overrides it, which keeps a sweep's re-verification on Sonnet. docs/token-plan.md step 7d owns the table." \
    -m "test_the_reviewer_and_the_auditors_run_on_opus failed on the three profiles before the edit and passes after it; apply.sh checks both." \
    -m "$TRAILER"
  "${PY[@]}" .github/comment-gate/comment_gate.py --base origin/main
  [ -z "$(git status --porcelain)" ] || { echo "tree not clean after commit" >&2; exit 1; }
  git log --oneline -4
  echo DONE
}
main "$@"
exit
