"""Follow a Claude Code transcript (.jsonl) live, readably: the owner's window on a /dispatch executor. Ctrl+C stops.

Usage: py -3.12 watch_session.py <transcript.jsonl | project directory> [--raw], the path dispatch_board.py's `result`
or `wake` prints; a directory follows a session started up to ten minutes before the watcher, or after it. --raw
shows each Bash command, not its description."""

import json
import os
import re
import shutil
import sys
import textwrap
import time
import unicodedata
from datetime import datetime

RAW = "--raw" in sys.argv
WIDTH = max(60, min(shutil.get_terminal_size((120, 40)).columns, 140))
CD_PREFIX = re.compile(r"^\s*cd\s+\S+\s*&&\s*")
LOOKBACK = 600
# Transcripts carry issue-body text anyone can write; an escape sequence in it would drive the owner's terminal.
ESCAPES = re.compile(r"\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)?|\x1b\[[0-?]*[ -/]*[@-~]|\x1b[@-_]?|\x9b[0-?]*[ -/]*[@-~]")

DIM, BOLD, RED, GREEN, CYAN, YELLOW, MAGENTA, RESET = (
    "\033[2m", "\033[1m", "\033[31m", "\033[32m", "\033[36m", "\033[33m", "\033[35m", "\033[0m")

ICONS = {
    "Bash": ("⚙", CYAN), "Read": ("📖", DIM), "Grep": ("🔎", DIM), "Glob": ("🔎", DIM),
    "Edit": ("✏", YELLOW), "Write": ("✏", YELLOW), "Agent": ("🤖", MAGENTA),
    "Skill": ("📘", MAGENTA), "EnterWorktree": ("📂", CYAN), "ExitWorktree": ("📂", CYAN),
}


def local_time(stamp):
    try:
        return datetime.fromisoformat(stamp.replace("Z", "+00:00")).astimezone().strftime("%H:%M:%S")
    except (ValueError, AttributeError):
        return "--:--:--"


def clean(text, keep="\t\n"):
    """Transcript text with every escape sequence and control character gone but those in `keep`."""
    text = ESCAPES.sub("", str(text))
    return "".join(ch for ch in text if ch in keep or unicodedata.category(ch) != "Cc")


def one_line(text, limit):
    text = " ".join(clean(text).split())
    return text if len(text) <= limit else text[:limit - 1] + "…"


def tool_summary(name, args):
    return clean(_summary(name, args), keep="\t")


def _summary(name, args):
    if name == "Bash":
        command = CD_PREFIX.sub("", args.get("command", ""))
        if RAW or not args.get("description"):
            return one_line(command.splitlines()[0] if command else "", WIDTH - 16)
        return args["description"]
    if name in ("Read", "Edit", "Write"):
        path = args.get("file_path", "")
        return os.path.relpath(path) if os.path.isabs(path) and path.lower().startswith(os.getcwd().lower()) else path
    if name in ("Grep", "Glob"):
        return f"{args.get('pattern', '')}  {args.get('path', '')}".strip()
    if name == "Agent":
        return f"{args.get('subagent_type', 'agent')}: {args.get('description', '')}"
    if name == "Skill":
        return f"/{args.get('skill', '')} {args.get('args', '')}".strip()
    return one_line(args.get("description") or args.get("path") or json.dumps(args, ensure_ascii=False), WIDTH - 16)


def cell_width(text):
    # Emoji and CJK take two columns in a terminal; combining marks and variation selectors take none.
    width = 0
    for ch in text:
        if unicodedata.combining(ch) or ch in "‍️︎":
            continue
        width += 2 if unicodedata.east_asian_width(ch) in ("W", "F") else 1
    return width


def wrap_cell(text, width):
    """`textwrap.wrap` by display columns, which it counts as characters; a word wider than the cell is cut."""
    lines, line = [], ""
    for word in text.split():
        while cell_width(word) > width:
            cut = word[0]
            for ch in word[1:]:
                if cell_width(cut + ch) > width:
                    break
                cut += ch
            if line:
                lines.append(line)
                line = ""
            lines.append(cut)
            word = word[len(cut):]
        if not word:
            continue
        joined = f"{line} {word}" if line else word
        if cell_width(joined) <= width:
            line = joined
        else:
            lines.append(line)
            line = word
    if line or not lines:
        lines.append(line)
    return lines


def pad_cell(text, width):
    return text + " " * (width - cell_width(text))


def render_table(rows):
    cells = []
    for row in rows:
        parts = [c.strip().replace("`", "").replace("**", "") for c in row.strip().strip("|").split("|")]
        if all(re.fullmatch(r":?-{2,}:?", p) for p in parts if p):
            continue
        cells.append(parts)
    if not cells:
        return []
    count = max(len(r) for r in cells)
    cells = [r + [""] * (count - len(r)) for r in cells]
    budget = WIDTH - 12 - 3 * count
    widths = [max(cell_width(r[i]) for r in cells) for i in range(count)]
    while sum(widths) > budget and max(widths) > 8:
        widest = widths.index(max(widths))
        widths[widest] -= 1
    out = ["┌" + "┬".join("─" * (w + 2) for w in widths) + "┐"]
    for n, row in enumerate(cells):
        wrapped = [wrap_cell(c, w) if cell_width(c) > w else [c] for c, w in zip(row, widths)]
        for k in range(max(len(w) for w in wrapped)):
            line = [pad_cell(col[k] if k < len(col) else "", w) for col, w in zip(wrapped, widths)]
            out.append("│ " + " │ ".join(line) + " │")
        if n == 0 and len(cells) > 1:
            out.append("├" + "┼".join("─" * (w + 2) for w in widths) + "┤")
    out.append("└" + "┴".join("─" * (w + 2) for w in widths) + "┘")
    return out


def render_text(text):
    lines, table = [], []
    for raw in clean(text).strip().splitlines() + [""]:
        if raw.lstrip().startswith("|"):
            table.append(raw)
            continue
        if table:
            lines.extend(render_table(table))
            table = []
        if raw.lstrip().startswith(("```", "    ")):
            lines.append(raw)
        else:
            lines.extend(textwrap.wrap(raw.replace("**", "").replace("`", ""), WIDTH - 11) or [""])
    while lines and not lines[-1]:
        lines.pop()
    return lines


def show(event):
    kind = event.get("type")
    if kind not in ("assistant", "user"):
        return
    stamp = f"{DIM}{local_time(event.get('timestamp'))}{RESET}"
    content = (event.get("message") or {}).get("content")
    if isinstance(content, str):
        content = [{"type": "text", "text": content}]
    for part in content or []:
        if not isinstance(part, dict):
            continue
        text = part.get("text")
        if part.get("type") == "text" and kind == "assistant" and isinstance(text, str) and text.strip():
            print()
            lines = render_text(text)
            pad = "\n" + " " * 11
            print(f"{stamp} {BOLD}{GREEN}💬{RESET} {GREEN}{pad.join(lines)}{RESET}")
            print()
        elif part.get("type") == "tool_use":
            name = clean(part.get("name", ""), keep="")
            icon, colour = ICONS.get(name, ("•", ""))
            args = part.get("input")
            print(f"{stamp} {colour}{icon} {name}{RESET}  {tool_summary(name, args if isinstance(args, dict) else {})}")
        elif part.get("type") == "tool_result" and part.get("is_error"):
            body = part.get("content")
            if isinstance(body, list):
                body = " ".join(p.get("text", "") for p in body if isinstance(p, dict))
            print(f"{stamp} {RED}❌ {one_line(body, WIDTH - 14)}{RESET}")


def newest_transcript(directory, since):
    # A directory means the session started at most LOOKBACK seconds before this watcher, or after it: wait for a
    # .jsonl created at or after `since`.
    while True:
        found = [os.path.join(directory, n) for n in os.listdir(directory) if n.endswith(".jsonl")] \
            if os.path.isdir(directory) else []
        # Creation time, not modification: a long-running session in the same directory keeps writing too.
        found = [f for f in found if os.path.getctime(f) >= since]
        if found:
            return max(found, key=os.path.getctime)
        time.sleep(2)


def main(path):
    if not path.endswith(".jsonl"):
        print(f"{DIM}waiting for a session under {path}{RESET}")
        path = newest_transcript(path, time.time() - LOOKBACK)
        print(f"{DIM}following {path}{RESET}")
    with open(path, encoding="utf-8", errors="replace") as handle:
        while True:
            line = handle.readline()
            if not line:
                time.sleep(1)
                continue
            try:
                show(json.loads(line))
            except (json.JSONDecodeError, KeyError, TypeError):
                pass


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8", line_buffering=True)
    # A constant empty command, no input reaches it: it only turns on ANSI colours in a Windows console.
    os.system("")
    paths = [a for a in sys.argv[1:] if a != "--raw"]
    if not paths:
        print(__doc__.split("\n\n", 1)[1], file=sys.stderr)
        sys.exit(2)
    try:
        main(paths[0])
    except KeyboardInterrupt:
        pass
