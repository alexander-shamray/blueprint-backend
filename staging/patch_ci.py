"""Give the two hint suites a `py` on PATH that runs their own interpreter, as hint-bound.sh launches `py`.

Usage: patch_ci.py <root>, the checkout holding .claude/. A runner without the Windows launcher has no `py`, and the
wrapper then reports the hook as not run (exit 127), which is how CI's review-helpers job failed. Each replacement
must match exactly once, or this exits non-zero and writes nothing.
"""

import sys
from pathlib import Path

HELPER = '''

def python_as_py(env, directory):
    """`env` with a `py` first on PATH that runs this interpreter and drops the launcher's version argument.

    hint-bound.sh starts its hook as `py -3.12`; a runner without the Windows launcher has none, and the wrapper
    would report every hook as not run.
    """
    shim = Path(directory) / "py"
    shim.write_text('#!/usr/bin/env bash\\nexec "' + Path(sys.executable).as_posix() + '" "${@:2}"\\n', encoding="utf-8")
    shim.chmod(0o755)
    return dict(env, PATH=str(directory) + os.pathsep + env["PATH"])
'''

EDITS = {
    ".claude/scripts/review_helpers.py": [
        ("import subprocess\nfrom pathlib import Path\n", "import subprocess\nimport sys\nfrom pathlib import Path\n"),
        ("\n\ndef code_lines(text):", HELPER + "\n\ndef code_lines(text):"),
    ],
    ".claude/scripts/test_index_query_hint.py": [
        ("from pathlib import Path\n", "from pathlib import Path\n\nfrom review_helpers import python_as_py\n"),
        ("env=dict(os.environ, CLAUDE_PROJECT_DIR=str(root)))",
         "env=python_as_py(dict(os.environ, CLAUDE_PROJECT_DIR=str(root)), scratch))"),
    ],
    ".claude/scripts/test_index_search_hint.py": [
        ("from unittest import mock\n", "from unittest import mock\n\nfrom review_helpers import python_as_py\n"),
        ("env=dict(environment(Path(scratch)), CLAUDE_PROJECT_DIR=str(root)))",
         "env=python_as_py(dict(environment(Path(scratch)), CLAUDE_PROJECT_DIR=str(root)), scratch))"),
    ],
}


def main(root: Path) -> int:
    staged = {}
    for rel, pairs in EDITS.items():
        with open(root / rel, encoding="utf-8", newline="") as f:
            text = f.read()
        for old, new in pairs:
            if text.count(old) != 1:
                print(f"{rel}: expected one match, found {text.count(old)}: {old[:60]}", file=sys.stderr)
                return 1
            text = text.replace(old, new)
        staged[rel] = text
    for rel, text in staged.items():
        with open(root / rel, "w", encoding="utf-8", newline="") as f:
            f.write(text)
    return 0


if __name__ == "__main__":
    sys.exit(main(Path(sys.argv[1])))
