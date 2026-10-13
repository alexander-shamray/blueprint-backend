"""Take the issue number out of test_hook_timeouts.py's two comments, which the comment gate refuses."""

import pathlib
import sys

path = pathlib.Path(sys.argv[1]) / ".claude" / "scripts" / "test_hook_timeouts.py"
text = path.read_bytes().decode("utf-8")
edits = (
    ("#989 measured `py -3.12 -P` start-up at 2.9 s on a busy machine,",
     "A measured `py -3.12 -P` start-up reached 2.9 s on a busy machine,"),
    ("The slowest `py` start #989 measured was 2.9 s;", "The slowest `py` start measured was 2.9 s;"),
)
for old, new in edits:
    if text.count(old) != 1:
        sys.exit(f"expected one occurrence of {old!r}, found {text.count(old)}")
    text = text.replace(old, new)
path.write_bytes(text.encode("utf-8"))
