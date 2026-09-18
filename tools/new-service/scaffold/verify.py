"""§15.1's secret scan over a render, and the allow-list entries it owes.

Apart from `render` because it is the one step that executes code out of the
tree being rendered into, and `load_scan_gate` carries the check that makes
that safe.
"""

from __future__ import annotations

import importlib.util
from pathlib import Path
from types import ModuleType
from typing import Any

from scaffold import Names, ScaffoldError, read, restore

# §15.1's secret scan reads the WORKING TREE, so the tree a render leaves
# behind is a tree that gate judges — and it refuses a credential-shaped
# literal nobody has written down a reason for. Catalog's literals all have
# one, added by hand the day Catalog landed; a rendered service carries the
# same literals under its own name and therefore under its own fingerprints,
# and nothing was writing those (#161). So the render ends by running the real
# scanner over what it has just built.
SCAN_GATE = ".github/secret-scan/secret_scan.py"
# The allow-list is a DIRECTORY, one file per tree, each declaring the prefix
# it may suppress. So this script no longer appends to one file: it appends to
# the file covering each entry's path, and refuses an entry no file covers
# rather than inventing a file for it — a new tree is a decision, exactly as a
# new credential-shaped literal is.
SCAN_ALLOW_LIST = ".github/secret-scan/allowed"

# The repository this script was checked out of, as opposed to the one it has
# been pointed at. They are the same thing whenever `--repo-root` is left alone,
# and the distinction only matters where trust does — `load_scan_gate` compares
# the gate it is about to execute against the copy here.
TOOL_ROOT = Path(__file__).resolve().parents[3]

# Everything this script calls on the module it loads. Named so a file that
# imports cleanly and is not the gate fails here, against the path the caller
# supplied, rather than three frames later against a symbol.
SCAN_GATE_MEMBERS = ("COVERS", "RULES", "covers_path", "read_allowed", "scan_text")

# The heading width the allow-list's own section headers are padded to.
SCAN_HEADING_WIDTH = 76

# One row per finding the render is known to produce: the file, the rule that
# reports it, a marker naming WHICH literal on that line it is, and the
# sentence the entry carries. Written in the template's own casings and put
# through `Names.rename` like every other string here, so `Catalog` below
# reaches the file as the service being rendered.
#
# A ROW IS NOT A SECOND MATCHER, and the distinction is the whole reason this
# imports the gate rather than reproducing it. The scanner decides what a
# finding is and what its fingerprint is; a marker only tells two findings of
# one rule in one file apart. The Compose file needs that much: a service's
# database default and its broker URI are both `credential-assignment` on
# adjacent lines, and they take different sentences because they record
# different decisions.
#
# A MARKER IS NEVER A TEST OF WHOSE FINDING IT IS, and it used to be read as
# one. Nothing in a row names the service being rendered, `"" in line` is true
# of every line, and the line the scanner reports for a password hash carries
# the value with the account name above it — so a render whose allow-list had
# lost a *previous* service's entry matched that service's finding, wrote a
# sentence naming the service being rendered, and cleared it. A suppression
# arriving for a credential the run is not writing is exactly what the
# allow-list's header says must never happen. `update_allowed_secrets`
# subtracts the findings the file already had, which settles ownership before
# a marker is consulted.
#
# EVERY MARKER IS NON-EMPTY, AND THAT IS ENFORCED RATHER THAN OBSERVED. Four
# of these rows carried `""` on the argument that the subtraction above made
# them safe, and that argument does not reach them: subtraction is defined
# over a path's ORIGINAL, so it protects the shared files in `updated` and
# nothing in `created`, which is where three of the four live. A rendered
# `HostSmokeTests.cs` gaining a SECOND `connection-string-password` literal
# would have been handed this row's sentence — a reason written about a
# different credential, which is the invented suppression the paragraph below
# refuses, arriving through the one path that skipped the refusal. So the
# emptiness is now a refusal of its own, because a row is a decision and `""`
# is the spelling of not having taken one.
#
# A NON-EMPTY MARKER IS STILL ONLY A NARROWING, so the run also checks what
# each row actually explained: one row that accounts for two findings with
# different fingerprints has not discriminated between them, whatever it
# matched on. That check is the subject-of-the-gate one — it reads what the
# table selected rather than trusting the table — and it is what catches a
# marker that is loose without being empty.
#
# THE COMPOSE MARKERS COME FROM THE VALUE, NOT THE KEY, and that is the second
# half of the same lesson. `ConnectionStrings__Catalog` renames to
# `ConnectionStrings__<Name>`, which is a substring of the broker line's
# `ConnectionStrings__RabbitMq` whenever the name is a prefix of `RabbitMq` —
# `R`, `Ra`, up to `RabbitMq` itself, eight legal PascalCase names that cleared
# every other precondition and then refused the run with a message blaming the
# template. A database name and a URI scheme are in the value, where no service
# name reaches them.
#
# A FINDING NO ROW EXPLAINS REFUSES THE RUN, on `classify`'s argument in
# `render`. A reason this script invented would be a suppression nobody
# wrote, which is the one thing the allow-list's own header says it must never
# hold — so a new credential-shaped literal in the template is a decision this
# script forces, exactly as a new file there is.
SCAN_REASONS = (
    (
        "tests/Catalog.Api.Tests/HostSmokeTests.cs",
        "connection-string-password",
        # The value, on the compose rows' argument. It says which literal this
        # sentence is about, so a second password in this file refuses the run
        # rather than inheriting a reason written for this one.
        "not-a-real-password",
        "A deliberately unusable value in a host that must not reach a database.",
    ),
    (
        "tests/Catalog.Api.Tests/MessageTypeMapValidatorTests.cs",
        "connection-string-password",
        "not-a-real-password",
        "The same unusable fixture in the validator suite.",
    ),
    (
        "tests/Catalog.Api.Tests/MessagingRegistrationTests.cs",
        "credential-assignment",
        # The URI scheme, exactly as the compose broker row uses it.
        "amqp://",
        "A broker URI pointing at a hostname that does not resolve.",
    ),
    (
        # The service's own Compose unit, which a render CREATES. Every finding
        # in it is therefore new — there is no previous copy of this path for
        # `update_allowed_secrets` to subtract — so the rows below cover the
        # whole block and not merely the two literals the spliced version
        # added to a file that already had the rest.
        "deploy/compose/services/catalog.yml",
        "connection-string-password",
        # The keyword, not the value. A marker naming the password itself would
        # make this script a second place §14.1's local default is written, and
        # a credential in two places is a credential nobody can retire — the
        # allow-list's own header, applied to the table that feeds it.
        "Password=",
        "Section 14.1's local database default, nested inside this service's "
        "two connection defaults.",
    ),
    (
        "deploy/compose/services/catalog.yml",
        "credential-assignment",
        # The database segment of the connection string, which the migrator's
        # line and the API's both carry — one value, one fingerprint, one
        # entry, one sentence. Not the `ConnectionStrings__` key: that renames
        # into a substring of the broker line for eight legal service names.
        "Database=Catalog",
        "Catalog's local connection default, in-cluster hostname.",
    ),
    (
        "deploy/compose/services/catalog.yml",
        "credential-assignment",
        # The URI scheme, for the same reason. No service name renames into
        # it, and no connection string carries it.
        "amqp://",
        "Section 14.1's broker default for Catalog, the per-service account "
        "that replaced guest.",
    ),
    (
        "deploy/compose/services/catalog.yml",
        "credential-assignment",
        # The host, which appears in the value and never in the key: the key is
        # `ConnectionStrings__RedisCache`, with no hyphen and no colon.
        "redis-cache:",
        "A Redis cache endpoint. Host and port only, no credential in it.",
    ),
    (
        "deploy/compose/services/catalog.yml",
        "credential-assignment",
        "redis-coordination:",
        "A Redis coordination endpoint. Host and port only, no credential.",
    ),
    (
        "deploy/compose/rabbitmq/definitions.json",
        "credential-assignment",
        # The one row whose marker is a KEY rather than a value, and the
        # exception is narrow rather than a softening: the hash is computed per
        # service, so no literal this table could carry would match the line
        # the render writes. `password_hash` is safe where
        # `ConnectionStrings__Catalog` was not, because it carries no service
        # name for `rename` to turn into a prefix of a neighbouring key.
        "password_hash",
        "The Catalog service account's password hash, Section 14.1's local default.",
    ),
)


def load_scan_gate(repo_root: Path, tool_root: Path = TOOL_ROOT) -> ModuleType | None:
    """§15.1's secret scan, loaded out of the tree being rendered into, or None.

    Resolved from `repo_root` and never from this file's own location. The
    suite renders into temporary roots holding the template and the shared
    files and no `.github/` at all, so a gate resolved from `__file__` would
    read the real repository's rules and the real allow-list while writing
    into a tree that has neither — green, and about nothing.

    **`.github/` missing is the degradation; the gate missing is a refusal**,
    and the two used to be one test. A checkout with the directory and without
    one of its two files is a real tree, and returning None there rendered a
    service the scanner refuses without saying so — #161 back, quietly, from
    the code that closed it. It is also the only shared file whose absence was
    tolerated: the other six raise inside `read`.

    Loaded by path rather than by `import`, and the difference is not a
    formality: `import` caches by module name, so a second render against a
    second root would silently reuse the first root's gate, and the suite
    renders against several roots in one process. There is no `sys.path`
    manipulation anywhere in this repository and this does not add the first.
    `spec_from_file_location` is the form already in use here —
    `deploy/compose/rabbitmq/test_check_permissions.py` loads its subject that
    way, and so does `.claude/scripts/test_git_argv_guard.py`; what is new is
    only the directory being crossed.

    **This executes a file chosen by `--repo-root`, and it is checked against
    the copy this script shipped with before it runs.** An earlier revision
    said the exposure "is not new" because the tool already reads the target's
    source and writes into its tree, and that was wrong in the way that
    matters: copying text is not running it, and a tool that renders a service
    into a checkout does not thereby earn the right to execute that checkout's
    Python with the developer's privileges.

    **Loading it from `TOOL_ROOT` instead was the obvious repair and it breaks
    a correctness invariant.** The entries written here carry the SCANNER'S
    fingerprints, and the scanner that will later verify them is the one in the
    tree being rendered into, because that is the checkout CI runs from. Two
    different implementations agreeing today is not the property needed; the
    entries have to be computed by the same code that checks them, or a
    fingerprint matching nothing becomes a stale entry and the build stops on
    it.

    So the two are reconciled by requiring them to be the SAME FILE rather than
    choosing between them: the target's gate is executed, and only after its
    bytes are established to be the ones here. Identical bytes make the
    invariant and the trust boundary the same statement. A target whose scanner
    differs is refused rather than run, which is also the honest answer for
    rendering at all — the template anchors this script matches are that
    repository's too.

    `secret_scan.py` is stdlib-only and guards its own entry point, so loading
    the file this repository ships runs no scan and touches nothing.
    """
    if not (repo_root / ".github").is_dir():
        return None

    gate = repo_root / SCAN_GATE
    if not gate.is_file():
        raise ScaffoldError(
            f"{repo_root} has a .github directory and no {SCAN_GATE}. §15.1's scan "
            f"is what says a rendered service may be committed; without it this "
            f"script cannot write the entries the gate would demand."
        )
    if not (repo_root / SCAN_ALLOW_LIST).is_dir():
        raise ScaffoldError(
            f"{SCAN_GATE} is here and {SCAN_ALLOW_LIST} is not. The scan reads that "
            f"directory to know what it may ignore, and this script appends to the "
            f"file in it that covers each entry's tree."
        )

    # Executed only if it is the file this script shipped with. Compared as
    # bytes rather than trusted, because the next statement runs it: `--repo-root`
    # is caller-supplied, and reading a checkout's source is not a reason to
    # execute it. Loading TOOL_ROOT's copy instead would be the wrong repair —
    # the fingerprints written here must come from the scanner that will later
    # verify them, which is the target's. Requiring one file satisfies both.
    trusted = tool_root / SCAN_GATE
    if gate.resolve() != trusted.resolve():
        if not trusted.is_file():
            raise ScaffoldError(
                f"{tool_root} carries no {SCAN_GATE}, so there is nothing to check "
                f"{repo_root}'s copy against, and this script will not execute an "
                f"unverified one."
            )
        if gate.read_bytes() != trusted.read_bytes():
            raise ScaffoldError(
                f"{repo_root}/{SCAN_GATE} is not the secret scan this script shipped "
                f"with, and this script executes it. Refusing rather than running a "
                f"copy it cannot vouch for — and the entries it would write carry that "
                f"scanner's fingerprints, so a different implementation is also the "
                f"wrong thing to compute them with. Render from a checkout whose gate "
                f"matches, or update this tool alongside it."
            )

    specification = importlib.util.spec_from_file_location("scaffold_secret_scan", gate)
    if specification is None or specification.loader is None:
        raise ScaffoldError(f"{SCAN_GATE} cannot be loaded as a Python module")

    module = importlib.util.module_from_spec(specification)
    try:
        specification.loader.exec_module(module)
    # Every exception class there is, because the subject is arbitrary
    # module-level code and nothing narrows the set. What matters is not which
    # one arrives but that it arrives as a ScaffoldError: `main` catches that
    # and nothing else, so an unwrapped SyntaxError here ends the run in a
    # traceback and breaks the one-line-on-stderr contract this script states.
    except Exception as error:
        raise ScaffoldError(f"{SCAN_GATE} did not load: {error}") from error

    # A module that loaded is not a module that is the gate. Checked because
    # the alternative is an AttributeError three frames later, naming a symbol
    # rather than the file the caller pointed at.
    if (absent := [name for name in SCAN_GATE_MEMBERS if not hasattr(module, name)]):
        raise ScaffoldError(
            f"{SCAN_GATE} declares no " + ", ".join(absent) + "; it is not the "
            f"secret scan this script knows how to drive."
        )
    return module


def scan_reason(rows: list[tuple[str, str, str, str]], finding: Any,
                line: str) -> tuple[int, str]:
    """The row explaining one finding and the sentence it carries, or a refusal.

    Exactly one row, never the first of several: two rows that both explain a
    finding is a table nobody can read, and no row at all is the template
    having gained a literal this script has never been shown. Both refuse,
    because the alternative is a suppression with a reason nobody wrote.

    **This decides WHICH sentence and never WHOSE finding it is.** Ownership is
    settled before anything reaches here, by the subtraction in
    `update_allowed_secrets`; a marker that had to carry it as well would be a
    second thing for the same string to be wrong about, which is how a row for
    one service came to be written over another service's credential.

    **The row's index comes back with its sentence so the caller can audit the
    selection**, which is the half a marker cannot do on its own: one row
    explaining two fingerprints has not told two credentials apart, however
    specific the substring it matched on looked.

    `finding` is typed `Any` because its class comes from a module loaded at
    run time out of `repo_root` — there is no name to annotate it with that
    this file could import.
    """
    matched = [
        (index, reason)
        for index, (path, rule, marker, reason) in enumerate(rows)
        if path == finding.path and rule == finding.rule.id and marker in line
    ]
    if len(matched) != 1:
        raise ScaffoldError(
            f"{finding.path}:{finding.line}: the secret scan reports "
            f"`{finding.rule.id}` here and SCAN_REASONS gives {len(matched)} "
            f"sentence(s) for it. The template has gained a credential-shaped "
            f"literal, or moved one; add the row saying why it is accepted. "
            f"A reason this script invented is a suppression nobody wrote."
        )
    return matched[0]


def allow_list_trees(repo_root: Path, gate: ModuleType) -> dict[str, str]:
    """Which allow-list file covers which tree, read out of the files.

    The prefixes are the gate's own `covers:` directives rather than a table
    here, for the reason every anchor in this script is read rather than
    restated: a second copy of the map is one that goes stale the day a tree is
    added, and the failure would be an entry written to a file that does not
    cover it — which the gate then reports against a service somebody has just
    scaffolded.
    """
    covers: dict[str, str] = {}
    for source in sorted((repo_root / SCAN_ALLOW_LIST).glob("*.txt")):
        for line in source.read_text(encoding="utf-8").splitlines():
            if (declaration := gate.COVERS.fullmatch(line.strip())) is not None:
                covers[declaration.group(1)] = f"{SCAN_ALLOW_LIST}/{source.name}"
                break
    return covers


def update_allowed_secrets(repo_root: Path, names: Names, created: dict[str, str],
                           updated: dict[str, str],
                           reasons: tuple[tuple[str, str, str, str], ...],
                           tool_root: Path) -> dict[str, str] | None:
    """One allow-list entry per finding THIS RENDER ADDS, or None (#161).

    **The return is a map now, because the allow-list is a directory.** An
    entry goes to the file whose `covers:` prefix its path starts with, so a
    render that writes both a Compose unit and a test fixture appends to two
    files — and two services being scaffolded at once meet in fewer of them
    than they used to.

    Two paths return None and both are ordinary. The tree has no `.github/`
    at all — the suite's synthetic roots, where a render has nothing to
    reconcile — or the render adds no finding, which is what a second run of a
    service already entered would produce. Silent in both cases: `main`
    reports what it wrote on stdout and nothing on stderr, and a warning about
    a directory the caller never asked for would be the tool complaining about
    its own fixture. A `.github/` that exists and is missing a piece is a
    refusal instead, and `load_scan_gate` argues that.

    **A finding the file already had is not this render's to explain.** Every
    path in `updated` has an original in the tree; both are scanned and only
    the keys the render INTRODUCED are eligible. Ownership is a property of
    the diff, not of the allow-list — the earlier version asked only whether a
    finding was already accepted, so a render whose allow-list had lost an
    entry for a service rendered earlier picked that finding up, wrote a
    sentence naming the wrong service, and cleared it. Paths in `created` did
    not exist a moment ago, so every finding in them is new by construction.

    The fingerprints are the SCANNER'S, taken from the findings it reported.
    A digest computed here would be a second implementation of which substring
    each rule matches, and being wrong at it is silent in the worst direction:
    an entry matching nothing is a stale entry, which is the failure the gate
    reports and the build stops on.
    """
    gate = load_scan_gate(repo_root, tool_root)
    if gate is None:
        return None

    entries, problems = gate.read_allowed(
        repo_root / SCAN_ALLOW_LIST, {rule.id for rule in gate.RULES})
    if problems:
        raise ScaffoldError(
            f"{SCAN_ALLOW_LIST}: {problems[0]}. This script appends to those files "
            f"and will not append to a set that does not already parse."
        )

    covers = allow_list_trees(repo_root, gate)
    if not covers:
        raise ScaffoldError(
            f"{SCAN_ALLOW_LIST} declares no `covers:` prefix in any file, so there "
            f"is nowhere an entry could be filed."
        )

    rows = [
        (names.rename(path), rule, names.rename(marker), names.rename(reason))
        for path, rule, marker, reason in reasons
    ]

    # An empty marker matches every line of its file, so the row stops asking
    # which literal it is about and becomes a blanket acceptance of its rule
    # there. Refused here rather than trusted to the table, because the four
    # rows that carried one read as deliberate for as long as no second finding
    # existed to be swallowed.
    blank = [f"{path} | {rule}" for path, rule, marker, _ in rows if not marker]
    if blank:
        raise ScaffoldError(
            f"SCAN_REASONS: {len(blank)} row(s) carry an empty marker "
            f"({'; '.join(blank)}). A marker says WHICH literal on the line a "
            f"sentence explains, and an empty one matches every line — so the "
            f"next finding of that rule in that file would be allow-listed with "
            f"a reason written for a different credential."
        )

    # Deduplicated on the key the gate itself judges by. Two lines carrying one
    # value under one rule in one file are one accepted finding, and a second
    # entry for it is reported as duplicating the first — a failed build, from
    # the tool that was supposed to prevent one.
    accepted = {entry.key() for entry in entries}
    explained: dict[int, set[str]] = {}
    lines: dict[str, list[str]] = {}
    for relative, body in {**created, **updated}.items():
        before: set[tuple[str, str, str]] = set()
        if relative in updated:
            original, _ = read(repo_root, relative)
            before = {
                finding.key()
                for finding in gate.scan_text(relative, original, gate.RULES)
            }

        source = body.splitlines()
        for finding in gate.scan_text(relative, body, gate.RULES):
            if finding.key() in before or finding.key() in accepted:
                continue
            accepted.add(finding.key())
            index, reason = scan_reason(rows, finding, source[finding.line - 1])
            explained.setdefault(index, set()).add(finding.fingerprint)

            # The longest matching prefix, so a tree split further later takes
            # its own entries rather than leaving them with its parent. One
            # entry, one file: a finding no prefix covers is refused, because
            # the alternative is choosing a file for it, and which file a
            # suppression lives in is the whole of what makes it findable.
            #
            # `gate.covers_path` rather than `startswith`, because a prefix
            # without a trailing slash is one path and not a tree — and because
            # the gate is what will judge the entry this writes, so asking it
            # is the only way the two cannot disagree.
            home = max(
                (prefix for prefix in covers if gate.covers_path(prefix, finding.path)),
                key=len,
                default=None,
            )
            if home is None:
                raise ScaffoldError(
                    f"{finding.path}: no file under {SCAN_ALLOW_LIST} covers this "
                    f"path, so its entry has nowhere to go. Add the file that "
                    f"covers the tree, with its `covers:` line — this script will "
                    f"not create one, because a new tree is a decision."
                )
            lines.setdefault(covers[home], []).append(
                f"{finding.path} | {finding.rule.id} | {finding.fingerprint} | "
                f"{reason}"
            )

    # What each row actually selected, which is the question a non-empty marker
    # only narrows. A row accounting for two fingerprints has explained two
    # different credentials with one sentence — the empty marker's defect
    # surviving a marker that is merely too loose.
    loose = sorted(index for index, seen in explained.items() if len(seen) > 1)
    if loose:
        path, rule, marker, _ = rows[loose[0]]
        raise ScaffoldError(
            f"SCAN_REASONS: the row for {path} | {rule} with marker "
            f"'{marker}' explains {len(explained[loose[0]])} findings with "
            f"different fingerprints. One sentence cannot be the reason for two "
            f"credentials; narrow the marker, or add the row the second one owes."
        )

    if not lines:
        return None

    heading = f"# --- {names.pascal}, rendered by tools/new-service "
    heading += "-" * max(3, SCAN_HEADING_WIDTH - len(heading))

    written: dict[str, str] = {}
    for relative, block in sorted(lines.items()):
        text, newline = read(repo_root, relative)
        appended = "\n".join([
            "",
            "",
            heading,
            "#",
            "# One entry per finding the gate reported over this render, carrying the",
            "# fingerprints it computed rather than any this script worked out. The",
            "# equivalent literals for a hand-built service are entries above, written",
            "# the day that service landed; these are the same decision, taken by the",
            "# tool that rendered them (#161).",
            "",
        ] + block) + "\n"
        written[relative] = restore(text + appended, newline)

    return written
