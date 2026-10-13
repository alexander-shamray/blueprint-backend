"""Raise the five 5 s hook timeouts in .claude/settings.json to 15."""

import pathlib
import sys

settings = pathlib.Path(sys.argv[1]) / ".claude" / "settings.json"
text = settings.read_bytes().decode("utf-8")
old, new = '"timeout": 5', '"timeout": 15'
if text.count(old) != 5:
    sys.exit(f"expected five 5 s timeouts, found {text.count(old)}")
settings.write_bytes(text.replace(old, new).encode("utf-8"))
