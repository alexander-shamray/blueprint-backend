#!/usr/bin/env bash
# Apply review round 1's two findings on fix/argv-guards-decode-utf8, each case seen failing before its code, and
# commit each. The push is /ship's. A step that fails puts its files back, so a rerun resumes at that step.
set -euo pipefail
# A function, so bash has read all of it before the run deletes this file.
main() {
  cd "$(dirname "$0")/.."
  local branch=fix/argv-guards-decode-utf8
  [ -f staging/patch_review.py ] || { echo "no staging/ here" >&2; exit 2; }
  [ "$(git branch --show-current)" = "$branch" ] || { echo "not on $branch" >&2; exit 2; }
  [ -z "$(git status --porcelain)" ] || { echo "the tree is not clean:" >&2; git status --short >&2; exit 2; }
  if command -v py >/dev/null 2>&1; then PY=(py -3.12); else PY=(python3); fi
  TRAILERS=(--trailer "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
    --trailer "Claude-Session: https://claude.ai/code/session_01VsGF8Tb74uv1Y16xLCdUyB" --trailer "PR: #1345")
  local g=test_git_argv_guard.TheGitArgvGuard

  # Matched on the fix's own wording: the staging commits end in the same (review R1).
  if ! done_already "admits a PowerShell command only as a read-only cmdlet"; then
    fix "fix(harness): the git argv guard admits a PowerShell command only as a read-only cmdlet over plain words (review R1)" \
      r1-tests r1-hook "$g.test_powershell_is_admitted_only_as_a_read_cmdlet_over_plain_words" \
      "powershell_git removed only PowerShell's own escapes before looking for git, so a call that handed git to another interpreter with that interpreter's escape, cmd's caret or the backslash bash and wsl read, showed no git and was admitted, as was a call to a name PowerShell computes, which shows nothing to match. A list of launchers to refuse would trail PowerShell's the way any deny-list does." \
      "So the PowerShell path is an allow-list: a command is admitted only when its first word is one of eight read-only cmdlets (Get-ChildItem, Get-Content, Get-Item, Get-Location, Resolve-Path, Select-String, Test-Path, Write-Output), the whole is plain words, letters, digits, spaces and the characters a path or an argument needs, and none is git. A quote, an escape, a variable, a call, a pipe, a separator, a wildcard or a backslash refuses it, and so does any launcher, since an encoded command passed to pwsh is plain words that carry git. Bash runs git and everything else here, so the refusal costs nothing a session needs." \
      "test_powershell_is_admitted_only_as_a_read_cmdlet_over_plain_words failed before the change and passes after it; apply.sh checks both."
  fi

  if ! done_already "untokenised fallback reads a push through its quotes"; then
    fix "fix(harness): the git argv guard's untokenised fallback reads a push through its quotes (review R2)" \
      r2-tests r2-hook "$g.test_a_quote_split_push_before_an_untokenisable_line_is_refused" \
      "The unterminated-heredoc refusal closed one way into the ValueError fallback and left another: a quote-split push on one line and an unbalanced quote on the next fails shlex with no heredoc open, and bash runs the first line before it meets the second's syntax error. The fallback's needle scan and push pattern read the string with its quotes in place, so pu''sh was not a push." \
      "Both now read it with quotes and backslashes removed, as bash removes them. The case that pinned the fallback admitting a quoted --output now asserts the refusal, and the refusal no longer says it read the raw string." \
      "test_a_quote_split_push_before_an_untokenisable_line_is_refused failed before the change and passes after it; apply.sh checks both."
  fi

  if ! done_already "states the fallback's rule, not its history"; then
    "${PY[@]}" staging/patch_review.py . comment
    git add .claude/scripts/test_git_argv_guard.py
    git commit -q -m "test(harness): the degraded-check case's comment states the fallback's rule, not its history" \
      -m "The comment gate refuses a comment that names history, and this one said what the fallback used to read. It now says what the fallback does." \
      "${TRAILERS[@]}"
  fi

  # test_harness_denies wants every tracked tree edit-denied, so staging/ goes before it runs.
  git rm -r -q staging
  if ! (cd .claude/scripts && "${PY[@]}" -m unittest test_harness_denies); then
    git reset -q HEAD -- staging && git checkout -q HEAD -- staging; exit 1
  fi
  git commit -q -m "chore: remove the second applied staging/ for review round 1" \
    -m "Its fix and the comment change are the commits before this one." "${TRAILERS[@]}"
  "${PY[@]}" .github/comment-gate/comment_gate.py --base origin/main
  [ -z "$(git status --porcelain)" ] || { echo "tree not clean after the commits" >&2; exit 1; }
  git log --oneline -4
  echo DONE
}

# Whether a commit on this branch already carries this phrase in its subject.
done_already() {
  git log --format=%s origin/main..HEAD | tr -d '\r' | grep -qF "$1"
}

# fix <subject> <test part> <code part> <case> <body paragraphs...>
fix() {
  local subject=$1 test_part=$2 code_part=$3 case=$4
  shift 4
  local files=".claude/scripts/test_git_argv_guard.py .claude/hooks/guard-git-argv.py"
  "${PY[@]}" staging/patch_review.py . "$test_part"
  # The new case must fail on the code as it stands, or it pins nothing.
  if (cd .claude/scripts && "${PY[@]}" -m unittest "$case" >/dev/null 2>&1); then
    echo "passed before the fix: $case" >&2; git checkout -q HEAD -- $files; exit 1
  fi
  "${PY[@]}" staging/patch_review.py . "$code_part"
  if ! (cd .claude/scripts && "${PY[@]}" -m unittest test_git_argv_guard test_index_argv_guard); then
    echo "the suites fail after: $subject" >&2; git checkout -q HEAD -- $files; exit 1
  fi
  # Each paragraph wrapped at 80, as /commit's bodies are; the CR strip is for Windows' print.
  local message
  message="$("${PY[@]}" -c 'import sys, textwrap
print(sys.argv[1] + "\n\n" + "\n\n".join(textwrap.fill(p, 80) for p in sys.argv[2:]))' "$subject" "$@" | tr -d '\r')"
  git add $files
  git commit -q -F - "${TRAILERS[@]}" <<<"$message"
}

main "$@"
exit
