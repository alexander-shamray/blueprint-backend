"""What the scaffold's modules share: the template's name, the rename, the
refusal, and the one way a template file is read and written back.

`new_service.py` is the command line and the only entry point, and these
modules are what it composes: `patch` holds the anchored edits, `render` the
files a run creates and the shared files it edits, and `verify` §15.1's secret
scan over the result.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path

TEMPLATE = "Catalog"

# The host project's suffix in the template, and the one §4.1 gives Shipping
# and Notifications. A service is one or the other, and the difference reaches
# a project name, a namespace, a Compose service key, a Dockerfile entry point
# and a test fixture's type name — so it belongs to the rename rather than to a
# patch table, which can only edit a file's text and not its path.
API_HOST = "Api"
WORKER_HOST = "Worker"
HOSTS = (API_HOST, WORKER_HOST)


class ScaffoldError(Exception):
    """A precondition the script will not write over."""


@dataclass(frozen=True)
class Names:
    """The three casings every rename needs, and the host the service runs as."""

    pascal: str
    host: str = API_HOST

    @property
    def lower(self) -> str:
        return self.pascal.lower()

    @property
    def upper(self) -> str:
        return self.pascal.upper()

    @property
    def replacements(self) -> dict[str, str]:
        """Every token the rename maps, the three compound tokens first.

        Python's alternation is ordered, so `Catalog.Api` is tried at a
        position before `Catalog` is; a host rename run as a second pass would
        see the first one's output. The needles are the three compounds rather
        than a bare `Api`, a token `AddOpenApi` also holds.
        """
        return {
            f"{TEMPLATE}.{API_HOST}": f"{self.pascal}.{self.host}",
            f"{TEMPLATE}{API_HOST}": f"{self.pascal}{self.host}",
            f"{TEMPLATE.lower()}-{API_HOST.lower()}": f"{self.lower}-{self.host.lower()}",
            TEMPLATE: self.pascal,
            TEMPLATE.lower(): self.lower,
            TEMPLATE.upper(): self.upper,
        }

    def rename(self, text: str) -> str:
        """One pass over every token, never one pass each.

        Chained `str.replace` calls feed each replacement to the next, and a
        name that contains a later casing of the template token is rewritten
        twice: `CATALOGSearch` turned a source `Catalog` into
        `CATALOGSEARCHSearch`, because pass one produced text that pass three
        then matched. A single alternation cannot re-enter its own output.
        """
        replacements = self.replacements
        pattern = re.compile("|".join(re.escape(token) for token in replacements))
        return pattern.sub(lambda match: replacements[match.group(0)], text)


def read(repo_root: Path, relative: str) -> tuple[str, str]:
    """The file with LF endings, and the endings it actually had.

    **The template does not have one line ending, and which files have which
    depends on the platform.** `.gitattributes` forces `*.cs text eol=crlf`, so
    C# is CRLF everywhere; every other file here — `.csproj`, `.slnx`, the
    Compose YAML, the Markdown, the Dockerfiles — carries no attribute, so it
    checks out CRLF on Windows and LF on the Ubuntu runner. Anchors written
    with CRLF therefore match on a developer machine and match nothing in CI,
    which is exactly how this was found. Every anchor in the package is spelt
    with LF and matched against normalised text; the file's own endings go back
    on by `restore` on the way out.
    """
    raw = (repo_root / relative).read_bytes()
    text = raw.decode("utf-8-sig")
    # Escaped, never the character itself: a U+FEFF sitting invisibly inside a
    # string literal is one "strip the BOM" editor command away from silently
    # changing what this script writes.
    if raw.startswith(b"\xef\xbb\xbf"):
        text = "\ufeff" + text

    newline = "\r\n" if "\r\n" in text else "\n"
    return text.replace("\r\n", "\n"), newline


def restore(text: str, newline: str) -> str:
    """Put a file's own line endings back on rendered text."""
    return text if newline == "\n" else text.replace("\n", newline)


def require_once(text: str, needle: str, where: str) -> None:
    """Assert an anchor is there exactly once, or refuse the whole run."""
    count = text.count(needle)
    if count != 1:
        first = needle.splitlines()[0] if needle else repr(needle)
        raise ScaffoldError(
            f"{where}: expected exactly one occurrence of\n"
            f"    {first}\n"
            f"  found {count}. The template has moved; reconcile "
            f"tools/new-service with it."
        )
