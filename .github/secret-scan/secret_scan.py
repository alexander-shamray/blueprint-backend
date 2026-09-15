#!/usr/bin/env python3
"""Fail the build on a credential written into the working tree.

Section 15.1 puts "SCA + secret scan" at the head of the pipeline and argues the
position: neither half needs a build, and a scan downstream of one is a scan
that a build failure skips. This is the secret half, on the licence gate's
terms — stdlib Python over text, no restore, no SDK, no network — so it runs
in the same job, ahead of everything.

What this does not do, stated here rather than inferred from a green run:

  * It reads the working tree, not history. A credential committed and then
    deleted is still in the pack and still compromised; `docs/secrets.md`
    states what applies then: rotate first, and rewrite history second.
  * It is a pattern scanner, not an entropy oracle. It recognises shapes: a PEM
    block, a provider's key prefix, a password inside a connection string, a
    credential-shaped name assigned a literal. A high-entropy string under a
    name nobody predicted passes. Section 13.4's redactor draws the same line,
    because an entropy test flags an id as readily as a secret, and a gate
    that cries wolf gets turned off.
  * It knows nothing about whether a value is live. `not-a-real-password` and a
    production password are the same shape, which is why the accepted ones are
    enumerated under allowed/ rather than guessed at by the patterns.

So the honest claim is narrow: a credential of a recognised shape cannot reach
`main` through a pull request without somebody writing down why it is there.

    py -3.12 .github/secret-scan/secret_scan.py
"""

from __future__ import annotations

import argparse
import hashlib
import os
import re
import sys
from pathlib import Path

GATE_DIR = Path(__file__).resolve().parent
REPO_ROOT = GATE_DIR.parents[1]

DEFAULT_ALLOWED = GATE_DIR / "allowed"

# Directories never descended into. Build output and vendored trees are not
# reviewed, so a finding in one is a finding nobody would act on; `.git` is
# excluded because this gate is deliberately about the tree and not the history,
# and scanning the pack would be a claim to a coverage it does not have.
#
# Every name here is matched by basename at every depth, which is right for all
# of them: `obj`, `bin` and `__pycache__` occur nested by nature, and a rule
# that only caught the topmost one would be a skip list in name only.
SKIP_DIRS = frozenset({
    ".git",
    ".vs",
    ".idea",
    "obj",
    "bin",
    "node_modules",
    "__pycache__",
    "TestResults",
})

# Declined at the repository root and nowhere else, which the set above cannot
# express. §4.1 puts build output in one `artifacts/` directory at the top of
# the tree, where a publish or pack output would put a rendered appsettings in
# front of the scanner. Anywhere else the name means whatever somebody called
# their code, and a basename match would silently take a source tree out of
# this gate's reach.
SKIP_ROOT_DIRS = frozenset({
    "artifacts",
})

# A file is binary when its first block holds a NUL. That is a heuristic and it
# is the right one here: every pattern below is ASCII, so a format that would
# hide a secret from a byte scan (a zip, a DLL, a PNG) is a format this gate
# could not read anyway, and refusing to guess further keeps the walk cheap.
PROBE_BYTES = 8192

# ------------------------------------------------------------------ values --

# A value that is a reference rather than a literal. `${SQL_PASSWORD}`,
# `$PGPASSWORD`, `%SQL_PASSWORD%`, `{{ .Values.db.password }}` and
# `<your-password-here>` all name a secret without carrying one.
REFERENCE = re.compile(
    r"^(?:"
    r"\$\{[^}]*\}"                       # ${VAR}, and ${VAR:-…} once unwrapped
    r"|\$[A-Za-z_][A-Za-z0-9_]*"         # $VAR
    r"|\$\([^)]*\)"                      # $(SqlCmdVariable), $(shell …)
    r"|%[A-Za-z0-9_]+%"                  # %VAR%
    r"|\{\{.*\}\}"                       # {{ .Values.x }} — Helm, Jinja, Go
    r"|\{[A-Za-z0-9_.\-]*\}"             # {Pwd} — a message template's hole
    r"|<[^>]*>"                          # <your-password-here>
    r")$")

# `${VAR:-default}` is not a reference. The default is a literal committed to
# the tree, and the seam `docs/secrets.md` argues for is against deploying the
# value, not against writing it down. So the wrapper is peeled and the default
# judged, and Section 14.1's local-development defaults reach the allow-list as
# decisions with reasons.
DEFAULTED_REFERENCE = re.compile(r"^\$\{[A-Za-z_][A-Za-z0-9_]*:[-=]?(.*)\}$", re.S)

# Characters a mask is made of. A value composed only of these carries nothing.
MASK_CHARS = "*xX#.…_- \t"


def literal(value: str) -> str:
    """The literal a reader of this line would actually see, or "" for none.

    Structure only. This function decides whether a value can be a secret; it
    never decides whether a particular secret is acceptable, which is the
    allow-list's job and deliberately the only place that decision is made.
    """
    value = value.strip()

    # Nested defaults occur in §14.1's compose file — a connection string whose
    # default embeds the password's own default. Bounded rather than recursive
    # so a pathological line cannot spin.
    for _ in range(5):
        match = DEFAULTED_REFERENCE.match(value)
        if not match:
            break
        value = match.group(1).strip()

    if not value or REFERENCE.match(value):
        return ""
    if not value.strip(MASK_CHARS):
        return ""

    # A value with no alphanumeric character at all is punctuation the pattern
    # ran into, not a credential. This is the boundary at the short end of every
    # value rule here; the long end is each rule's own terminator.
    if not any(character.isalnum() for character in value):
        return ""
    return value


# ------------------------------------------------------------------- rules --

# Names that suggest the value beside them is a credential. Written once and
# shared by the two shape rules below, because two copies of a vocabulary is
# two vocabularies.
CREDENTIAL_WORDS = (
    r"passwd|password(?!less)|pwd|secret|token|api[_\-]?key|apikey|"
    r"client[_\-]?secret|connection[_\-]?string|conn[_\-]?str")

# A name containing one of those words, not equal to it. The field that leaks is
# never called `password` — it is `SQL_PASSWORD`, `ClientSecret`,
# `ConnectionStrings__Catalog`. Section 13.4's redactor reached the same
# conclusion from the other end and matches by substring for the same reason.
CREDENTIAL_NAME = rf"[A-Za-z0-9_.\-]*(?:{CREDENTIAL_WORDS})[A-Za-z0-9_.\-]*"

# The shortest value worth reporting under a name-based rule. Below it the
# name is doing all the work and an ordinary codebase supplies endless
# `Token = "n/a"`; a prefix-based rule has no such floor because the prefix is
# the evidence.
MIN_NAME_RULE_VALUE = 8


class Rule:
    """One named shape, its sentence, and the group carrying the credential.

    One rule per shape, never one regex for all of them. A single pattern would
    report every class under one id, so a suppression for the boring class would
    silence the interesting one.
    """

    def __init__(self, identifier: str, sentence: str, pattern: str, group: int | str = 1,
                 flags: int = 0, reject=None):
        self.id = identifier
        self.sentence = sentence
        self.pattern = re.compile(pattern, flags)
        self.group = group
        self.reject = reject

    def secrets(self, line: str) -> list[str]:
        """Every credential this rule finds on one line, unwrapped, in order."""
        found = []
        for match in self.pattern.finditer(line):
            if self.reject is not None and self.reject(match):
                continue
            value = literal(match.group(self.group))
            if value:
                found.append(value)
        return found


IDENTIFIER = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")


def names_itself(match: re.Match[str]) -> bool:
    """`self.secret = secret` — a constructor storing a parameter, not a value.

    The one false positive the bare-word rule below cannot narrow out by its
    value alone, because `secret` is a bare word. What distinguishes it is that
    the value is an identifier the name already contains, which is what
    a parameter assigned to the field it backs looks like in every language here
    — and what a password never looks like, since a credential equal to the name
    of the field holding it is not a credential.
    """
    name, value = match.group("name"), match.group("value")
    return bool(IDENTIFIER.match(value)) and value.lower() in name.lower()


RULES: list[Rule] = [
    # The key material itself, not a reference to one. The optional algorithm
    # word is what makes this one rule rather than five, and the words are
    # enumerated rather than `\w+` so that BEGIN PUBLIC KEY and BEGIN
    # CERTIFICATE — both ordinary, both harmless — cannot reach it.
    Rule(
        "private-key-block",
        "a PEM private key block: the key material itself, not a reference to it",
        r"(-----BEGIN (?:RSA |DSA |EC |OPENSSH |PGP |ENCRYPTED )?PRIVATE KEY-----)"),

    # §14.1 uses this shape with local defaults, which is why a real one would
    # be pasted in unnoticed. The value runs to the next `;` or quote, where a
    # connection string's segment ends. Whitespace also ends it, because the
    # keyword occurs in prose followed by a sentence; a parenthesis, because a
    # C# local of that name is often assigned a call; and a backtick, markdown's
    # code delimiter. A password containing a space is the stated cost.
    Rule(
        "connection-string-password",
        "a connection string carries an inline password",
        r"(?<![A-Za-z0-9_.])(?:password|pwd)[ \t]*=[ \t]*([^;\"'()`\s]*)",
        flags=re.IGNORECASE),

    # AWS publishes the prefix and the length, so this needs no name beside it.
    # The trailing guard is a lookahead rather than `\b`: an id is exactly
    # twenty characters, and `\b` would still match one with a longer tail.
    Rule(
        "aws-access-key-id",
        "an AWS access key id",
        r"(?<![A-Za-z0-9])(AKIA[0-9A-Z]{16})(?![A-Za-z0-9])"),

    # The secret half carries no prefix, so the name is the only evidence there
    # is. Forty characters of base64 is the published length.
    Rule(
        "aws-secret-access-key",
        "an AWS secret access key assigned to a name that says so",
        r"aws[_\-. ]?secret[_\-. ]?access[_\-. ]?key[^A-Za-z0-9]{1,10}"
        r"([A-Za-z0-9/+=]{40})(?![A-Za-z0-9/+=])",
        flags=re.IGNORECASE),

    # `ghp_` personal, `gho_` OAuth, `ghu_` user-to-server, `ghs_` server-to-
    # server, `ghr_` refresh. The body is at least 36 characters.
    Rule(
        "github-token",
        "a GitHub token",
        r"(?<![A-Za-z0-9_])(gh[pousr]_[A-Za-z0-9]{36,255})(?![A-Za-z0-9])"),

    Rule(
        "slack-token",
        "a Slack token",
        r"(?<![A-Za-z0-9])(xox[abpr]-[A-Za-z0-9\-]{10,})"),

    # `sk_live_` only. `sk_test_` is a test-mode key by construction and firing
    # on it would train people to ignore this rule.
    Rule(
        "stripe-live-key",
        "a Stripe live secret key",
        r"(?<![A-Za-z0-9_])(sk_live_[A-Za-z0-9]{16,})(?![A-Za-z0-9])"),

    Rule(
        "google-api-key",
        "a Google API key",
        r"(?<![A-Za-z0-9_\-])(AIza[0-9A-Za-z_\-]{35})(?![0-9A-Za-z_\-])"),

    # A compact JWT: three base64url segments, the first starting `eyJ` because
    # that is `{"` encoded. Matched bare rather than only in an assignment: a
    # bearer token pasted into a YAML list, a curl example or a test fixture is
    # how one arrives, and `eyJ` plus two dotted segments is unambiguous enough
    # that requiring an assignment adds only a way to miss it.
    Rule(
        "json-web-token",
        "a JSON Web Token in compact serialisation",
        r"(?<![A-Za-z0-9_\-])(eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}"
        r"\.[A-Za-z0-9_\-]{10,})"),

    # The providers this repository's own tooling authenticates against.
    Rule(
        "model-provider-api-key",
        "an xAI or Anthropic API key",
        r"(?<![A-Za-z0-9_\-])((?:xai|sk-ant)-[A-Za-z0-9_\-]{20,})"),

    # The catch-all, and the only rule whose evidence is a name rather than a
    # shape. A quoted literal is required: an unquoted right-hand side in C# is
    # an expression, and `bool useToken = enabled` is not a finding.
    Rule(
        "credential-assignment",
        "a credential-shaped name is assigned a literal",
        rf"[\"']?\b(?:{CREDENTIAL_NAME})\b[\"']?\s*[:=]\s*"
        rf"([\"'])(?P<value>[^\"'\r\n]{{{MIN_NAME_RULE_VALUE},}})\1",
        group="value",
        flags=re.IGNORECASE),

    # The one shape the rule above cannot see: a `.env` file, where the value
    # is not quoted. The value has to be a bare word, or this fires on every
    # module-level assignment in Python and C#: whitespace, brackets, quotes and
    # a trailing `;` or `,` describe an expression, not a password. The quoted
    # form is left to the rule above rather than reported twice.
    Rule(
        "env-assignment",
        "an environment-style assignment gives a credential-shaped name a value",
        rf"^[ \t]*(?:export[ \t]+)?(?P<name>{CREDENTIAL_NAME})[ \t]*=[ \t]*"
        r"(?P<value>[^\s\"'()\[\]<>;,]+)[ \t]*$",
        group="value",
        flags=re.IGNORECASE,
        reject=names_itself),
]


def digest(secret: str) -> str:
    """A stable, short fingerprint of one credential.

    Twelve hex characters of SHA-256. This is what an allow-list entry names,
    and it names a hash rather than the value. The reason is not to keep the
    value confidential, since it is already in the tree, but to keep the
    suppression file from being a second place the credential is written, a
    copy that outlives the first's rotation.
    """
    return hashlib.sha256(secret.encode("utf-8")).hexdigest()[:12]


def redact(secret: str) -> str:
    """A credential as it may appear in a CI log: three characters and a length.

    A gate that prints what it found has copied the secret into the log of every
    run that failed, where it is retained longer and read by more people than
    the branch ever was. Three characters is enough to find the line; the length
    is enough to tell two findings apart.
    """
    return f"{secret[:3]}... {len(secret)} chars"


# -------------------------------------------------------------- allow-list --


class Suppression:
    """One accepted finding: a path, a rule, a fingerprint and a reason.

    `source` is the allow-list file it was read from, and it is required
    rather than defaulted: there is more than one file, so a line number alone
    names no place, and a default would be a guessed filename in a diagnostic.
    """

    def __init__(self, path: str, rule: str, fingerprint: str, reason: str, line: int,
                 source: str):
        self.path = path
        self.rule = rule
        self.fingerprint = fingerprint
        self.reason = reason
        self.line = line
        self.source = source
        self.used = False

    def key(self) -> tuple[str, str, str]:
        return (self.path, self.rule, self.fingerprint)


# A reason has to be a sentence somebody wrote, not a word somebody typed to get
# past the parser. Fifteen characters refuses "ok", "test" and "local"; it
# cannot make a reason true, and nothing can. What it does buy is that the
# cheapest way past this gate is still to write down why.
MIN_REASON = 15

# Exactly four, never at least four. A fifth field is a reason someone put a
# pipe in, and reading it as an entry would silently truncate what they wrote.
FIELDS = 4

# The allow-list is a directory of per-tree files; why it is one is argued in
# `.github/secret-scan/allowed/README.md` and not restated here.
#
# What this file owns is the grammar: `# covers: <prefix>`, exactly once per
# file and before its first entry, and every entry's path must start with it.
COVERS = re.compile(r"^#\s*covers:\s*(\S+)\s*$")


def covers_path(prefix: str, path: str) -> bool:
    """Does a `covers:` declaration own this path?

    A trailing slash is what makes a prefix a tree. Without it `str.startswith`
    has no path boundary, and `# covers: d` would own entries from `docs/` and
    `deploy/` alike. A prefix not ending in `/` is one path, the narrowest
    reading of a declaration typed without the slash.

    `tools/new-service` calls this rather than reimplementing it, so ownership
    has one predicate.
    """
    return path.startswith(prefix) if prefix.endswith("/") else path == prefix


def read_allowed(path: Path, known: set[str] | None = None) -> tuple[list[Suppression], list[str]]:
    """Every tree's allow-list, and every complaint about their syntax.

    `path` is the directory the per-tree files live in; a single file is
    accepted too, and reads exactly the same way, which is what keeps
    `--allowed` usable against one file while debugging.

    An empty directory is a missing allow-list, not an empty one: a gate
    without its allow-list cannot judge what it may ignore, and reporting it as
    a clean empty list would drop every accepted finding at once.
    """
    if not path.exists():
        return [], [f"{path.name} is missing: the gate cannot judge what it may ignore"]

    files = sorted(path.glob("*.txt")) if path.is_dir() else [path]
    if not files:
        return [], [
            f"{path.name}/ holds no allow-list file: the gate cannot judge what it "
            f"may ignore"
        ]

    entries: list[Suppression] = []
    problems: list[str] = []
    declared: dict[str, str] = {}

    for source in files:
        found, said, covers = read_allowed_file(source, known)
        if covers is not None:
            if (already := declared.get(covers)) is not None:
                said.append(
                    f"{source.name}: `{covers}` is already covered by {already}. "
                    f"Two files covering one tree is two places an entry could go, "
                    f"and two places to fail to find it"
                )
            else:
                declared[covers] = source.name
        entries.extend(found)
        problems.extend(said)

    # The longest declared prefix owns the entry, and no other file may hold
    # it. With nested prefixes such as `deploy/` and `deploy/compose/`, an entry
    # under the child satisfies both files' own check, so placement stays
    # mechanical only if the longest prefix wins. Settled here because no file
    # can see the others' declarations.
    for entry in entries:
        owner = max(
            (prefix for prefix in declared if covers_path(prefix, entry.path)),
            key=len,
            default=None,
        )
        # None only where the entry's own prefix lost the duplicate check above,
        # which is already reported; saying it twice would name one defect as
        # two.
        if owner is not None and declared[owner] != entry.source:
            problems.append(
                f"{entry.source}:{entry.line}: `{entry.path}` is covered by "
                f"`{owner}`, which {declared[owner]} declares. The file with the "
                f"longest prefix covering a path owns its entries, or two files "
                f"could hold this one and a reader has two places to look")

    return entries, problems


def read_allowed_file(
    path: Path, known: set[str] | None = None
) -> tuple[list[Suppression], list[str], str | None]:
    """One tree's entries, the tree it declares, and its own complaints.

    Four pipe-separated fields. Exact repository-relative paths, never globs:
    a glob is how a suppression arrives for a file nobody has written yet.
    """
    entries: list[Suppression] = []
    problems: list[str] = []
    covers: str | None = None
    declared_at: int | None = None
    first_entry: int | None = None
    lines = path.read_text(encoding="utf-8").splitlines()

    # The directive gets its own pass, and a file without one is refused as a
    # file: judged per entry, an empty or comment-only `.txt` would pass with
    # nothing to complain about, though every allow-list file declares one tree.
    for number, raw in enumerate(lines, start=1):
        line = raw.strip()
        if not line:
            continue
        if not line.startswith("#"):
            if first_entry is None:
                first_entry = number
            continue
        if (declaration := COVERS.fullmatch(line)) is None:
            continue
        if covers is not None:
            problems.append(
                f"{path.name}:{number}: a second `covers:` directive. One "
                f"file speaks for one tree, or the prefix below it means "
                f"nothing")
            continue
        covers, declared_at = declaration.group(1), number

    if covers is None:
        problems.append(
            f"{path.name}: declares no `# covers: <prefix>`, so it speaks for no "
            f"tree. Every allow-list file declares exactly one, before its first "
            f"entry")
        return [], problems, None

    # Stated in the grammar, so enforced rather than trusted: a directive below
    # an entry reads, to anyone scanning the file, as though the lines above it
    # were covered by something else.
    if first_entry is not None and declared_at > first_entry:
        problems.append(
            f"{path.name}:{declared_at}: the `covers:` directive is below the entry "
            f"on line {first_entry}. It declares the whole file, so it goes above "
            f"the first entry where a reader will find it")

    for number, raw in enumerate(lines, start=1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue

        fields = [field.strip() for field in line.split("|")]
        if len(fields) != FIELDS:
            problems.append(
                f"{path.name}:{number}: expected `path | rule | fingerprint | reason`, "
                f"got {len(fields)} field(s)")
            continue

        entry_path, rule, fingerprint, reason = fields

        # A rule id nobody declares would otherwise surface as a stale entry,
        # which is the right verdict reported as the wrong diagnosis: the reader
        # goes looking for a finding that moved when what happened is a typo, or
        # a rule renamed without its entries. Say which.
        if known is not None and rule not in known:
            problems.append(
                f"{path.name}:{number}: `{rule}` is not a rule this scanner declares")
            continue

        if not entry_path or "*" in entry_path or "?" in entry_path:
            problems.append(
                f"{path.name}:{number}: `{entry_path}` is empty or a glob; "
                f"an entry names one exact path")
            continue

        # The prefix, before the reason, because an entry in the wrong file is
        # the failure this directive exists for and the reason it carries has
        # no bearing on it.
        if not covers_path(covers, entry_path):
            problems.append(
                f"{path.name}:{number}: `{entry_path}` is outside `{covers}`, which "
                f"is the tree this file covers. The entry belongs in the file that "
                f"covers its path")
            continue
        if len(reason) < MIN_REASON:
            problems.append(
                f"{path.name}:{number}: the reason is {len(reason)} character(s). "
                f"An entry states WHY, in a sentence")
            continue

        entries.append(
            Suppression(entry_path, rule, fingerprint, reason, number, path.name))

    return entries, problems, covers


# ---------------------------------------------------------------- scanning --


def is_binary(blob: bytes) -> bool:
    return b"\0" in blob[:PROBE_BYTES]


def walk(root: Path) -> list[Path]:
    """Every file worth reading, in a stable order.

    A filesystem walk rather than `git ls-files`, and the difference matters in
    the direction this gate cares about: the file that is about to be committed
    is untracked at the moment somebody wants to be told about it. Shelling out
    would also cost the property every gate here shares — that it runs over a
    plain checkout with nothing installed.
    """
    found: list[Path] = []
    root_path = Path(root)
    for directory, subdirectories, filenames in os.walk(root):
        skip = SKIP_DIRS | SKIP_ROOT_DIRS if Path(directory) == root_path else SKIP_DIRS
        subdirectories[:] = sorted(name for name in subdirectories if name not in skip)
        for name in sorted(filenames):
            found.append(Path(directory) / name)
    return found


class Finding:
    def __init__(self, path: str, line: int, rule: Rule, secret: str):
        self.path = path
        self.line = line
        self.rule = rule
        self.secret = secret
        self.fingerprint = digest(secret)

    def key(self) -> tuple[str, str, str]:
        return (self.path, self.rule.id, self.fingerprint)

    def __str__(self) -> str:
        return (f"{self.path}:{self.line}: {self.rule.id}: {self.rule.sentence} "
                f"[{redact(self.secret)}, sha256:{self.fingerprint}]")


def scan_text(path: str, text: str, rules: list[Rule]) -> list[Finding]:
    findings: list[Finding] = []
    for number, line in enumerate(text.splitlines(), start=1):
        for rule in rules:
            for secret in rule.secrets(line):
                findings.append(Finding(path, number, rule, secret))
    return findings


def scan_tree(root: Path, rules: list[Rule]) -> tuple[list[Finding], int]:
    findings: list[Finding] = []
    scanned = 0

    for file_path in walk(root):
        # The probe is read before the rest, so a large binary costs one block
        # rather than its whole length. PROBE_BYTES would otherwise be a comment
        # about a slice of something already in memory.
        try:
            with file_path.open("rb") as handle:
                head = handle.read(PROBE_BYTES)
                if is_binary(head):
                    continue
                blob = head + handle.read()
        except OSError:
            continue

        # errors="replace" rather than a guess at the encoding. Every pattern
        # here is ASCII, so a replacement character can only ever break a match
        # apart — it can never assemble one — and the failure direction is a
        # missed finding in a file that is not UTF-8, which is stated rather
        # than hidden.
        text = blob.decode("utf-8", errors="replace")
        scanned += 1
        relative = file_path.relative_to(root).as_posix()
        findings.extend(scan_text(relative, text, rules))

    return findings, scanned


def audit(findings: list[Finding], entries: list[Suppression]) -> list[str]:
    """Findings the allow-list does not cover, then entries that covered nothing.

    The second half keeps the first honest. A suppression whose finding has
    gone is a decision nobody has re-read, so when a finding clears, the build
    says so.
    """
    by_key: dict[tuple[str, str, str], Suppression] = {}
    problems: list[str] = []

    for entry in entries:
        if entry.key() in by_key:
            problems.append(
                f"{entry.source}:{entry.line}: duplicates the entry on "
                f"{by_key[entry.key()].source}:{by_key[entry.key()].line}")
            continue
        by_key[entry.key()] = entry

    unexplained: list[str] = []
    for finding in findings:
        entry = by_key.get(finding.key())
        if entry is None:
            unexplained.append(str(finding))
            continue
        entry.used = True

    stale = [
        f"{entry.source}:{entry.line}: `{entry.path}` no longer matches "
        f"{entry.rule} with sha256:{entry.fingerprint}. The finding is gone, "
        f"or the value changed. Re-read the entry and delete it or update it"
        for entry in entries if not entry.used
    ]

    return problems + unexplained + stale


def say(message: str, stream=None) -> None:
    """Print one line, with anything outside ASCII replaced.

    Every line this gate emits passes through here. A finding's path and value
    prefix come from the tree, so ASCII source says nothing about output, and a
    gate reporting a failure must not fail on the runner's stdout encoding.

    The stream is resolved on the call: a default argument binds `sys.stdout`
    at import, past any redirection a caller makes afterwards.
    """
    print(message.encode("ascii", "replace").decode("ascii"),
          file=sys.stdout if stream is None else stream)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Scan the working tree for credentials.")
    parser.add_argument("--root", type=Path, default=REPO_ROOT)
    parser.add_argument("--allowed", type=Path, default=DEFAULT_ALLOWED)
    args = parser.parse_args(argv)

    entries, findings = read_allowed(args.allowed, {rule.id for rule in RULES})

    # The gate's own subject, before anything that rests on it. A scan of no
    # files and a scan with no rules would both print the sentence a clean tree
    # prints, so both are refused.
    if not RULES:
        findings.append("no rules are defined: the gate would clear any tree at all")

    matches, scanned = ([], 0) if findings else scan_tree(args.root, RULES)

    if not findings and scanned == 0:
        findings.append(
            f"scanned no files under {args.root}: a clean report over an empty subject")

    if not findings:
        findings = audit(matches, entries)

    if findings:
        say(f"Secret scan: {len(findings)} finding(s) across {scanned} file(s).\n", sys.stderr)
        for finding in findings:
            say(f"  {finding}", sys.stderr)
        # Named in the shape the caller passed: `--allowed` takes the directory
        # or one file out of it.
        where = (
            f"the {args.allowed.name}/ file covering its tree"
            if args.allowed.is_dir()
            else args.allowed.name
        )
        say(f"\nA finding is cleared by fixing it, or by an entry in {where}, "
            f"naming the path, the rule, the fingerprint above and the reason.",
            sys.stderr)
        return 1

    # The accepted count is printed because on this repository it is non-zero,
    # which makes the summary a positive control: a scanner that had silently
    # stopped matching would report nothing accepted and fail above.
    say(f"Secret scan: {scanned} file(s), {len(RULES)} rule(s), "
        f"{len(entries)} accepted finding(s), 0 unexplained.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
