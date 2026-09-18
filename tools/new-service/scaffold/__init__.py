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

# The three casings, matched in one pass. Distinct strings under a
# case-sensitive match, so alternation order carries no meaning.
CASINGS = re.compile("|".join((TEMPLATE, TEMPLATE.lower(), TEMPLATE.upper())))


class ScaffoldError(Exception):
    """A precondition the script will not write over."""


@dataclass(frozen=True)
class Names:
    """The three casings every rename needs."""

    pascal: str

    @property
    def lower(self) -> str:
        return self.pascal.lower()

    @property
    def upper(self) -> str:
        return self.pascal.upper()

    @property
    def article(self) -> str:
        """The indefinite article a rendered sentence puts before the name.

        The rule is by letter, not by sound: a name starting with the vowel
        letter U still takes "an" even where it is pronounced with a
        consonant sound — `Users` said "yoo-zers" wants "a" — because
        English's own exception needs the pronunciation this scaffold has no
        way to know.
        """
        return "an" if self.pascal[:1].lower() in "aeiou" else "a"

    def rename(self, text: str) -> str:
        """One pass over the three casings, never three passes.

        Chained `str.replace` calls feed each replacement to the next, and a
        name that contains a later casing of the template token is rewritten
        twice: `CATALOGSearch` turned a source `Catalog` into
        `CATALOGSEARCHSearch`, because pass one produced text that pass three
        then matched. A single alternation cannot re-enter its own output.
        """
        return CASINGS.sub(
            lambda match: {
                TEMPLATE: self.pascal,
                TEMPLATE.lower(): self.lower,
                TEMPLATE.upper(): self.upper,
            }[match.group(0)],
            text,
        )


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
