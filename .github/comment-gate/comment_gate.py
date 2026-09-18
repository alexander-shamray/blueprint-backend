#!/usr/bin/env python3
"""The comments a pull request adds, judged against the style guide's rule.

`docs/style-guide.md`'s *Comments* section is the rule; what this reads, what
it judges and what it leaves to the reviewer are the README's. Exit 0 passes,
1 names each finding, and 2 is a diff or a file it could not read.
"""

import argparse
import ast
import io
import re
import subprocess
import sys
import tokenize
from dataclasses import dataclass
from pathlib import PurePosixPath

BLOCK_LIMIT = 10

# The third field exempts a tree: the harness's helpers are about the
# reviewers, so a reviewer named there is a subject rather than history.
PATTERNS = [
    ("an issue or pull request number", re.compile(r"#[0-9]+\b"), ()),
    ("a delivery-plan row", re.compile(r"PR-[0-9]+"), ()),
    ("a reviewer", re.compile(r"Copilot|Grok|CodeQL|found in review"),
     (".claude/",)),
    ("history", re.compile(r"\b(used to|went stale|"
                           r"this (line|sentence|comment) (said|carried))\b"),
     ()),
    ("emphasis", re.compile(r"\*\*[^*]+\*\*|</?b>"), ()),
]


class Unreadable(Exception):
    """A diff or a file the gate cannot read, which refuses the run."""


@dataclass(frozen=True)
class Comment:
    """One comment token: its whole extent, and its text inside the markers."""

    start: int
    end: int
    body_start: int
    body_end: int


def _line_end(text, i):
    while i < len(text) and text[i] not in "\r\n":
        i += 1
    return i


def _comment(start, end, marker, closer=0):
    return Comment(start, end, start + marker, end - closer)


def csharp(text):
    """`//`, `///` and `/* */`, never in a string, a character or a message."""
    found = []
    _cs_code(text, 0, found, closing=False)
    return found


_MESSAGE_DIRECTIVE = re.compile(r"#\s*(region|endregion|error|warning)\b")


def _cs_code(text, i, found, closing):
    depth = 0
    at_line_start = True
    n = len(text)
    while i < n:
        c = text[i]
        if c in "\r\n":
            at_line_start = True
            i += 1
            continue
        if c in " \t":
            i += 1
            continue
        if c == "#" and at_line_start:
            end = _line_end(text, i)
            comment = text.find("//", i, end)
            if comment >= 0 and not _MESSAGE_DIRECTIVE.match(text, i):
                found.append(_comment(comment, end, 2))
            i = end
            continue
        at_line_start = False
        if text.startswith("//", i):
            end = _line_end(text, i)
            marker = 3 if text.startswith("///", i) else 2
            found.append(_comment(i, end, marker))
            i = end
        elif text.startswith("/*", i):
            close = text.find("*/", i + 2)
            end = n if close < 0 else close + 2
            found.append(_comment(i, end, 2, 0 if close < 0 else 2))
            i = end
        elif c == "'":
            i = _cs_char(text, i)
        elif c in "$@\"":
            i = _cs_string(text, i, found)
        elif closing and c == "{":
            depth += 1
            i += 1
        elif closing and c == "}":
            if depth == 0:
                return i + 1
            depth -= 1
            i += 1
        else:
            i += 1
    return i


def _run(text, i, character):
    j = i
    while j < len(text) and text[j] == character:
        j += 1
    return j - i


def _cs_char(text, i):
    i += 1
    while i < len(text) and text[i] not in "'\r\n":
        i += 2 if text[i] == "\\" else 1
    return i + 1


def _cs_string(text, i, found):
    j = i
    while j < len(text) and text[j] in "$@":
        j += 1
    prefix = text[i:j]
    if not text.startswith('"', j):
        return j if j > i else i + 1
    dollars = prefix.count("$")
    quotes = _run(text, j, '"')
    if quotes >= 3 and "@" not in prefix:
        return _cs_raw(text, j + quotes, quotes, dollars, found)
    return _cs_quoted(text, j + 1, "@" in prefix, dollars, found)


def _cs_quoted(text, i, verbatim, dollars, found):
    n = len(text)
    while i < n:
        c = text[i]
        if c == '"':
            if verbatim and text.startswith('""', i):
                i += 2
                continue
            return i + 1
        if c == "\\" and not verbatim:
            i += 2
        elif c in "\r\n" and not verbatim:
            return i
        elif dollars and text.startswith("{{", i):
            i += 2
        elif dollars and c == "{":
            i = _cs_code(text, i + 1, found, closing=True)
        else:
            i += 1
    return i


def _cs_raw(text, i, quotes, dollars, found):
    n = len(text)
    closer = '"' * quotes
    while i < n:
        if text.startswith(closer, i):
            return i + quotes
        if dollars and text[i] == "{":
            run = _run(text, i, "{")
            if run >= dollars:
                i = _cs_code(text, i + run, found, closing=True)
                i += dollars - 1
                continue
            i += run
            continue
        i += 1
    return i


def python(text):
    """`#` comments from the tokenizer, and docstrings from the syntax tree."""
    starts = _line_starts(text)
    try:
        tokens = list(tokenize.generate_tokens(io.StringIO(text).readline))
        tree = ast.parse(text)
    except (SyntaxError, tokenize.TokenError) as error:
        raise Unreadable(f"Python that does not parse: {error}") from None

    def offset(position):
        return starts[position[0] - 1] + position[1]

    found = []
    for token in tokens:
        if token.type == tokenize.COMMENT:
            found.append(_comment(offset(token.start), offset(token.end), 1))
    strings = [t for t in tokens if t.type == tokenize.STRING]
    for node in ast.walk(tree):
        if not isinstance(node, (ast.Module, ast.ClassDef, ast.FunctionDef,
                                 ast.AsyncFunctionDef)):
            continue
        body = node.body
        if not (body and isinstance(body[0], ast.Expr)
                and isinstance(body[0].value, ast.Constant)
                and isinstance(body[0].value.value, str)):
            continue
        first, last = body[0].lineno, body[0].end_lineno
        spans = [t for t in strings
                 if t.start[0] >= first and t.end[0] <= last]
        if not spans:
            continue
        head, tail = spans[0], spans[-1]
        quote = head.string.lstrip("rRbBuU")
        opener = len(head.string) - len(quote) + (3 if quote[:3] in
                                                   ('"""', "'''") else 1)
        closer = 3 if tail.string[-3:] in ('"""', "'''") else 1
        found.append(_comment(offset(head.start), offset(tail.end),
                              opener, closer))
    return found


def shell(text):
    """`#` where a word begins, never in quotes, expansions or a heredoc."""
    found = []
    _sh_code(text, 0, found, closing=None)
    return found


_SH_WORD_BREAK = " \t\r\n;&|()"
_HEREDOC = re.compile(
    r"<<(-?)[ \t]*(?:'([^'\n]*)'|\"([^\"\n]*)\"|\\?([A-Za-z_][\w]*))")


def _sh_code(text, i, found, closing):
    depth = 0
    pending = []
    n = len(text)
    while i < n:
        c = text[i]
        if c == "\\":
            i += 2
        elif c == "\n":
            i = _sh_heredocs(text, i + 1, pending)
            pending = []
        elif c == "#" and (i == 0 or text[i - 1] in _SH_WORD_BREAK):
            end = _line_end(text, i)
            found.append(_comment(i, end, 1))
            i = end
        elif c == "'":
            close = text.find("'", i + 1)
            i = n if close < 0 else close + 1
        elif text.startswith("$'", i):
            i = _sh_escaped(text, i + 2, "'")
        elif c == '"':
            i = _sh_double(text, i + 1, found)
        elif c == "`":
            i = _sh_escaped(text, i + 1, "`")
        elif text.startswith("$(", i):
            i = _sh_code(text, i + 2, found, closing=")")
        elif text.startswith("${", i):
            i = _sh_parameter(text, i + 2, found)
        elif text.startswith("<<<", i):
            i += 3
        elif text.startswith("<<", i):
            match = _HEREDOC.match(text, i)
            if match:
                word = next(g for g in match.groups()[1:] if g is not None)
                pending.append((word, match.group(1) == "-"))
                i = match.end()
            else:
                i += 2
        elif closing and c == "(":
            depth += 1
            i += 1
        elif closing and c == ")":
            if depth == 0:
                return i + 1
            depth -= 1
            i += 1
        else:
            i += 1
    return i


def _sh_heredocs(text, i, pending):
    for word, strip_tabs in pending:
        while i < len(text):
            end = text.find("\n", i)
            end = len(text) if end < 0 else end
            line = text[i:end].rstrip("\r")
            i = end + 1
            if (line.lstrip("\t") if strip_tabs else line) == word:
                break
    return i


def _sh_escaped(text, i, closer):
    while i < len(text) and text[i] != closer:
        i += 2 if text[i] == "\\" else 1
    return i + 1


def _sh_double(text, i, found):
    n = len(text)
    while i < n:
        c = text[i]
        if c == "\\":
            i += 2
        elif c == '"':
            return i + 1
        elif c == "`":
            i = _sh_escaped(text, i + 1, "`")
        elif text.startswith("$(", i):
            i = _sh_code(text, i + 2, found, closing=")")
        elif text.startswith("${", i):
            i = _sh_parameter(text, i + 2, found)
        else:
            i += 1
    return i


def _sh_parameter(text, i, found):
    depth = 0
    n = len(text)
    while i < n:
        c = text[i]
        if c == "\\":
            i += 2
        elif c == "'":
            close = text.find("'", i + 1)
            i = n if close < 0 else close + 1
        elif c == '"':
            i = _sh_double(text, i + 1, found)
        elif text.startswith("$(", i):
            i = _sh_code(text, i + 2, found, closing=")")
        elif c == "{":
            depth += 1
            i += 1
        elif c == "}":
            if depth == 0:
                return i + 1
            depth -= 1
            i += 1
        else:
            i += 1
    return i


_BLOCK_SCALAR = re.compile(r"(?:^|[\s:\-])[|>][-+0-9]*$")
_INDICATOR = re.compile(r"[|>][-+0-9]*")
_ITEM_PREFIX = re.compile(r"^[ ]*(?:-[ ]+)*")


def yaml(text):
    """`#` outside quotes and block scalars, and a `run:` block as shell."""
    found = []
    starts = _line_starts(text)
    quote = None
    scalar_parent = None
    script = None
    for start in starts:
        end = _line_end(text, start)
        line = text[start:end]
        indent = len(line) - len(line.lstrip(" "))
        if scalar_parent is not None:
            if not line.strip() or indent > scalar_parent:
                if script is not None:
                    script.append((start, end))
                continue
            scalar_parent = None
            _run_block(text, script, found)
            script = None
        code_end = len(line)
        j = 0
        while j < len(line):
            c = line[j]
            if quote == "'":
                if line.startswith("''", j):
                    j += 2
                    continue
                if c == "'":
                    quote = None
            elif quote == '"':
                if c == "\\":
                    j += 2
                    continue
                if c == '"':
                    quote = None
            elif c == "#" and (j == 0 or line[j - 1] in " \t"):
                found.append(_comment(start + j, start + len(line), 1))
                code_end = j
                break
            elif c in "'\"" and line[:j].rstrip()[-1:] in ("", ":", "-", "[",
                                                          "{", ",", "?"):
                quote = c
            j += 1
        code = line[:code_end].rstrip()
        if quote is None and _BLOCK_SCALAR.search(code):
            prefix = _ITEM_PREFIX.match(code).group(0)
            owner = code[len(prefix):]
            if not _INDICATOR.fullmatch(owner):
                scalar_parent = len(prefix)
                script = [] if _RUN_KEY.match(owner) else None
            elif prefix.strip():
                scalar_parent = code.rfind("-", 0, len(prefix))
            else:
                scalar_parent = indent - 1
    _run_block(text, script, found)
    return found


_RUN_KEY = re.compile(r"run:\s")


def _run_block(text, rows, found):
    """A `run:` block's comments, read as the runner's shell reads it."""
    content = [text[s:e] for s, e in rows or [] if text[s:e].strip()]
    if not content:
        return
    indent = min(len(line) - len(line.lstrip(" ")) for line in content)
    script = []
    where = []
    for start, end in rows:
        cut = min(indent, end - start)
        script.append(text[start + cut:end] + "\n")
        where.extend(range(start + cut, end + 1))
    for comment in shell("".join(script)):
        found.append(Comment(where[comment.start], where[comment.end - 1] + 1,
                             where[comment.body_start],
                             where[comment.body_end - 1] + 1
                             if comment.body_end > comment.body_start
                             else where[comment.body_start]))


def msbuild(text):
    """`<!-- -->`, never inside a CDATA section."""
    found = []
    i = 0
    while True:
        comment = text.find("<!--", i)
        cdata = text.find("<![CDATA[", i)
        if comment < 0:
            return found
        if 0 <= cdata < comment:
            close = text.find("]]>", cdata)
            i = len(text) if close < 0 else close + 3
            continue
        close = text.find("-->", comment + 4)
        end = len(text) if close < 0 else close + 3
        found.append(_comment(comment, end, 4, 0 if close < 0 else 3))
        i = end


def editorconfig(text):
    """A line whose first character past its indent is `#` or `;`."""
    found = []
    for start in _line_starts(text):
        end = _line_end(text, start)
        stripped = text[start:end].lstrip(" \t")
        if stripped[:1] in ("#", ";"):
            first = end - len(stripped)
            found.append(_comment(first, end, 1))
    return found


READERS = {
    ".cs": csharp,
    ".py": python,
    ".sh": shell,
    ".yml": yaml,
    ".yaml": yaml,
    ".csproj": msbuild,
    ".props": msbuild,
    ".targets": msbuild,
}


def reader_for(path):
    """The comment reader for a repository path, or None for one it skips."""
    name = PurePosixPath(path).name
    if name == ".editorconfig":
        return editorconfig
    return READERS.get(PurePosixPath(path).suffix)


def _line_starts(text):
    starts = [0]
    for line in io.StringIO(text, newline=""):
        starts.append(starts[-1] + len(line))
    if starts[-1] == len(text) and len(starts) > 1:
        starts.pop()
    return starts


def judge(path, text, added):
    """Findings for one file after the change, given its added line numbers."""
    comments = reader_for(path)(text)
    starts = _line_starts(text)
    whole = bytearray(len(text))
    body = bytearray(len(text))
    for comment in comments:
        whole[comment.start:comment.end] = (
            b"\1" * (comment.end - comment.start))
        body[comment.body_start:comment.body_end] = (
            b"\1" * (comment.body_end - comment.body_start))

    lines = []
    for number, start in enumerate(starts, 1):
        end = starts[number] if number < len(starts) else len(text)
        lines.append((number, start, _line_end(text, start), end))

    findings = []
    for number, start, stop, _ in lines:
        if number not in added:
            continue
        said = "".join(ch if body[k] else " "
                       for k, ch in enumerate(text[start:stop], start))
        for name, pattern, exempt in PATTERNS:
            if not path.startswith(exempt) and pattern.search(said):
                findings.append((path, number, f"a comment names {name}"))

    run = []
    for number, start, stop, end in lines + [(None, 0, 0, 0)]:
        if number is not None and _comment_only(text, whole, start, stop, end):
            run.append(number)
            continue
        if len(run) > BLOCK_LIMIT and added.intersection(run):
            findings.append((path, run[0],
                             f"a comment block runs {len(run)} lines, over "
                             f"{BLOCK_LIMIT}"))
        run = []
    return findings


def _comment_only(text, whole, start, stop, end):
    content = [k for k in range(start, stop) if text[k] not in " \t"]
    if content:
        return all(whole[k] for k in content)
    return (stop < end and bool(whole[stop])
            and start > 0 and bool(whole[start - 1]))


def _git(*args):
    result = subprocess.run(["git", *args], capture_output=True)
    if result.returncode:
        raise Unreadable(f"git {' '.join(args)}: "
                         f"{result.stderr.decode(errors='replace').strip()}")
    return result.stdout


def added_lines(diff):
    """Each file's added line numbers, from a `git diff -U0` of the change."""
    added = {}
    lines = diff.split("\n")
    i = 0
    path = None
    hunk = re.compile(r"@@ -\d+(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")
    while i < len(lines):
        line = lines[i]
        i += 1
        if line.startswith("+++ "):
            name = line[4:]
            if name.startswith('"'):
                raise Unreadable(f"a path git quotes: {name}")
            if name.startswith("b/"):
                path = name[2:]
            elif name == "/dev/null":
                path = None
            else:
                raise Unreadable(f"a diff header it does not know: {name}")
            continue
        match = hunk.match(line)
        if not match or path is None:
            continue
        removed = 1 if match.group(1) is None else int(match.group(1))
        first = int(match.group(2))
        count = 1 if match.group(3) is None else int(match.group(3))
        added.setdefault(path, set()).update(range(first, first + count))
        remaining = removed + count
        while i < len(lines) and (remaining or lines[i].startswith("\\")):
            if not lines[i].startswith("\\"):
                remaining -= 1
            i += 1
    return added


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--base", required=True)
    parser.add_argument("--head", default="HEAD")
    args = parser.parse_args(argv[1:])
    span = f"{args.base}...{args.head}"
    try:
        changed = _git("diff", "--name-only", "-z", "--diff-filter=AMR", span)
        if not changed.strip(b"\0"):
            raise Unreadable(f"{span} changes no file, so the base or the "
                             "head is not the pull request's")
        diff = _git("-c", "core.quotePath=false", "diff", "-U0", "--no-color",
                    "--no-ext-diff", "--src-prefix=a/", "--dst-prefix=b/",
                    "-M", "--diff-filter=AMR", span)
        added = added_lines(diff.decode("utf-8", errors="strict"))
        findings = []
        judged = 0
        for path, numbers in sorted(added.items()):
            if reader_for(path) is None:
                continue
            raw = _git("show", f"{args.head}:{path}")
            try:
                text = raw.decode("utf-8-sig")
            except UnicodeDecodeError:
                raise Unreadable(f"{path} is not UTF-8") from None
            judged += 1
            try:
                findings.extend(judge(path, text, numbers))
            except Unreadable as error:
                raise Unreadable(f"{path}: {error}") from None
    except (Unreadable, UnicodeDecodeError) as error:
        print(f"::error::comment gate refused the run: {error}")
        return 2
    for path, line, message in findings:
        print(f"::error file={path},line={line}::{message}")
        print(f"{path}:{line}: {message}")
    print(f"judged the added lines of {judged} file(s) it reads, "
          f"{len(findings)} finding(s)")
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
