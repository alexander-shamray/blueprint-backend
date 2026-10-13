"""Raise the two 5 s timeouts in the codebase-index skill's example settings to the installed block's 15."""

import pathlib
import sys

path = pathlib.Path(sys.argv[1]) / ".claude" / "skills" / "codebase-index" / "examples" / "hooks" / "settings.json"
text = path.read_bytes().decode("utf-8")
old, new = '"timeout": 5,', '"timeout": 15,'
if text.count(old) != 2:
    sys.exit(f"expected two 5 s timeouts, found {text.count(old)}")
path.write_bytes(text.replace(old, new).encode("utf-8"))
