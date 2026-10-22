#!/usr/bin/env bash
# Apply the argv guards' three fixes on fix/argv-guards-decode-utf8, each case seen failing before its code, and
# commit each. The push is /ship's. A step that fails puts its files back, so a rerun resumes at that step.
set -euo pipefail
# A function, so bash has read all of it before the run deletes this file.
main() {
  cd "$(dirname "$0")/.."
  local branch=fix/argv-guards-decode-utf8
  [ -f staging/patch_guards.py ] || { echo "no staging/ here" >&2; exit 2; }
  [ "$(git branch --show-current)" = "$branch" ] || { echo "not on $branch" >&2; exit 2; }
  [ -z "$(git status --porcelain)" ] || { echo "the tree is not clean:" >&2; git status --short >&2; exit 2; }
  if command -v py >/dev/null 2>&1; then PY=(py -3.12); else PY=(python3); fi
  local pr
  pr="$(bash .claude/scripts/pr-for-branch.sh "$branch" | "${PY[@]}" -c \
    "import json, sys; print(next((r['number'] for r in json.load(sys.stdin) if r['state'] == 'OPEN'), ''))" \
    | tr -d '\r')"
  [ -n "$pr" ] || { echo "no open pull request for $branch" >&2; exit 2; }
  TRAILERS=(--trailer "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
    --trailer "Claude-Session: https://claude.ai/code/session_01VsGF8Tb74uv1Y16xLCdUyB" --trailer "PR: #$pr")

  local git_hook=.claude/hooks/guard-git-argv.py index_hook=.claude/hooks/guard-index-argv.py
  local git_tests=.claude/scripts/test_git_argv_guard.py index_tests=.claude/scripts/test_index_argv_guard.py
  local g=test_git_argv_guard.TheGitArgvGuard i=test_index_argv_guard.GuardIndexArgv

  if ! done_already "decode the hook event as UTF-8"; then
    fix "fix(harness): the argv guards decode the hook event as UTF-8 and refuse one that is not" \
      "decode-tests" "decode-hooks" "$git_tests $index_tests" "$git_hook $index_hook" \
      "$g.test_a_character_cp1252_cannot_decode_does_not_skip_the_judgement $g.test_an_event_that_is_not_utf8_is_refused $i.test_a_character_cp1252_cannot_decode_does_not_skip_the_judgement $i.test_an_event_that_is_not_utf8_is_refused" \
      "Both guards read their event with json.load on sys.stdin, which Python decodes in the ANSI code page on this host, strictly. A command carrying a character whose UTF-8 bytes include one cp1252 leaves undefined, the hourglass among them, raised UnicodeDecodeError, which is a ValueError and so landed in the branch that admits a malformed event: the git guard's refusals and the index guard's chained-command and redirection refusals were skipped, and the prefix grants approved the line unjudged." \
      "Each now reads sys.stdin.buffer and decodes it as UTF-8, as guard-edit-target.py and the index hint hooks already do, and refuses an event that is not UTF-8, since it holds a command nothing read. An event that is UTF-8 but not JSON is still admitted, the one argued fail-open. The in-process runner the git suite uses now hands main a byte stream." \
      "The new cases send unescaped UTF-8 under PYTHONIOENCODING=cp1252, which stands in for the Windows pipe: a refused push and an admitted status carrying U+23F3, and an event holding a byte that is not UTF-8, in each guard's suite. apply.sh saw all four fail before the change and pass after it." \
      "Closes #1241" "Closes #1242" "Closes #1243"
  fi

  if ! done_already "unterminated heredoc"; then
    fix "fix(harness): the git argv guard refuses an unterminated heredoc that will not tokenise" \
      "heredoc-tests" "heredoc-hook" "$git_tests" "$git_hook" \
      "$g.test_an_unterminated_heredoc_that_will_not_tokenise_is_refused" \
      "heredoc_spans opens no span for a heredoc with no closing line, and scanning the tail as commands is safe only while the tail still tokenises. A body with an apostrophe makes shlex raise, and the ValueError fallback checks only raw needles and one whole-word push pattern, so a quote-split forced update ahead of the heredoc was admitted." \
      "heredoc_spans now reports the openers it found no close for, and the fallback refuses outright when there is one, as undecodable_heredoc refuses an undecodable delimiter. A closed body with an apostrophe still tokenises and is judged as before." \
      "test_an_unterminated_heredoc_that_will_not_tokenise_is_refused failed before the change and passes after it; apply.sh checks both." \
      "Closes #1246"
  fi

  if ! done_already "runs on PowerShell calls"; then
    fix "fix(harness): the git argv guard runs on PowerShell calls and refuses any that names git" \
      "powershell-tests" "powershell-hook settings" "$git_tests" "$git_hook .claude/settings.json" \
      "$g.test_git_through_powershell_is_refused $g.test_the_hook_is_registered_for_powershell_in_settings" \
      "The guard was registered on Bash alone and returned early for any other tool, and no permission rule names PowerShell, so a git command through the PowerShell tool was judged by nothing in this repository. A second PreToolUse entry now sends PowerShell calls to the git guard. Its parser reads Bash's grammar, not PowerShell's, so it refuses any PowerShell command that names git once backticks and quotes are removed, and git runs through the Bash tool." \
      "The index guard stays on Bash: the codebase-index skill's prefix grants are Bash rules, so a PowerShell call is prompted like any other and there is no grant to guard. The residual is a name PowerShell computes, such as a concatenation, which is the expansion residual docs/harness-boundaries.md names, in another shell. settings.json changes last, in this commit." \
      "test_git_through_powershell_is_refused and test_the_hook_is_registered_for_powershell_in_settings failed before the change and pass after it; apply.sh checks both." \
      "Closes #1277"
  fi

  # test_harness_denies wants every tracked tree edit-denied, so staging/ goes before it runs.
  git rm -r -q staging
  if ! (cd .claude/scripts && "${PY[@]}" -m unittest test_harness_denies test_command_grants test_edit_target_guard \
      test_index_search_hint test_hint_bound); then
    git reset -q HEAD -- staging && git checkout -q HEAD -- staging; exit 1
  fi
  git commit -q -m "chore: remove the applied staging/ for the argv guards" \
    -m "Its three fixes are the commits before this one." "${TRAILERS[@]}"
  "${PY[@]}" .github/comment-gate/comment_gate.py --base origin/main
  [ -z "$(git status --porcelain)" ] || { echo "tree not clean after the commits" >&2; exit 1; }
  git log --oneline -5
  echo DONE
}

# Whether a commit on this branch already carries this phrase in its subject.
done_already() {
  git log --format=%s origin/main..HEAD | tr -d '\r' | grep -qF "$1"
}

# fix <subject> <test part> <code parts> <test files> <code files> <cases> <body paragraphs...>
fix() {
  local subject=$1 test_part=$2 code_parts=$3 test_files=$4 code_files=$5 cases=$6
  shift 6
  local files="$test_files $code_files" case part
  "${PY[@]}" staging/patch_guards.py . "$test_part"
  # Each new case must fail on the code as it stands, or it pins nothing.
  for case in $cases; do
    if (cd .claude/scripts && "${PY[@]}" -m unittest "$case" >/dev/null 2>&1); then
      echo "passed before the fix: $case" >&2; git checkout -q HEAD -- $files; exit 1
    fi
  done
  for part in $code_parts; do "${PY[@]}" staging/patch_guards.py . "$part"; done
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
