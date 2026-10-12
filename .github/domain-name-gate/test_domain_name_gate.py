#!/usr/bin/env python3
"""What the domain-name gate would catch, which running it against this repository does not say.

    py -3.12 -m unittest discover -s .github/domain-name-gate
"""
from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

import domain_name_gate

TREE = Path("docs/backend-architecture")
LINK = "[E.5](appendix-e-reference-domain.md#e5-9-messaging-and-the-sagas)"
MARKED = f"In the reference domain ({LINK}), Catalog publishes the price."
BARE = "Catalog publishes the price."


class Fixture(unittest.TestCase):
    """A tree shaped like this one, small enough to break on purpose."""

    def setUp(self) -> None:
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        for service in ("Catalog", "Ordering"):
            (self.root / "src/Services" / service).mkdir(parents=True)
        self.write("01-purpose.md", "# Purpose\n\nA rule with no service in it.\n")

    def write(self, name: str, text: str) -> None:
        target = self.root / TREE / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text, encoding="utf-8")

    def check(self) -> list[str]:
        return domain_name_gate.check(self.root)

    def scanned(self) -> list[str]:
        return [path.relative_to(self.root / TREE).as_posix() for path in domain_name_gate.scanned(self.root)]


class ScannedSet(Fixture):
    """The subject is the set the gate looks at, not what it found in it."""

    def test_a_chapter_added_later_is_scanned_and_a_bare_name_in_it_fails(self) -> None:
        self.write("16-new-chapter.md", f"# New\n\n{BARE}\n")
        self.assertIn("16-new-chapter.md", self.scanned())
        self.assertEqual(self.check(), ["docs/backend-architecture/16-new-chapter.md:3 names Catalog"])

    def test_the_exempt_list_is_the_named_files_and_nothing_more(self) -> None:
        self.assertEqual(domain_name_gate.EXEMPT, {
            "adr",
            "appendix-a-adrs.md",
            "appendix-b-licences.md",
            "appendix-c-delivery-plan.md",
            "appendix-d-type-inventory.md",
            "appendix-e-reference-domain.md",
        })

    def test_an_appendix_added_later_is_not_exempt_by_omission(self) -> None:
        self.write("appendix-f-something.md", f"# F\n\n{BARE}\n")
        self.assertIn("appendix-f-something.md", self.scanned())
        self.assertEqual(len(self.check()), 1)

    def test_the_named_appendices_and_the_adrs_are_not_scanned(self) -> None:
        names = sorted(domain_name_gate.EXEMPT - {"adr"})
        for name in names:
            self.write(name, f"# Exempt\n\n{BARE}\n")
        self.write("adr/ADR-001-something.md", f"# ADR\n\n{BARE}\n")
        self.assertEqual(self.scanned(), ["01-purpose.md"])
        self.assertEqual(self.check(), [])

    def test_every_chapter_on_disk_is_in_the_scanned_set(self) -> None:
        base = domain_name_gate.ROOT / TREE
        chapters = {path.name for path in base.glob("[0-9][0-9]-*.md")} | {"README.md"}
        on_disk = {path.relative_to(base).as_posix() for path in domain_name_gate.scanned(domain_name_gate.ROOT)}
        self.assertLessEqual(chapters, on_disk)
        self.assertTrue(chapters)

    def test_no_scanned_file_is_an_exempt_one(self) -> None:
        base = domain_name_gate.ROOT / TREE
        for path in domain_name_gate.scanned(domain_name_gate.ROOT):
            self.assertNotIn(path.relative_to(base).parts[0], domain_name_gate.EXEMPT)

    def test_the_real_tree_passes(self) -> None:
        self.assertEqual(domain_name_gate.check(), [])


class TheMarker(Fixture):
    def test_a_marked_paragraph_passes(self) -> None:
        self.write("02-a.md", f"# A\n\n{MARKED}\n")
        self.assertEqual(self.check(), [])

    def test_a_link_without_the_phrase_fails(self) -> None:
        self.write("02-a.md", f"# A\n\nCatalog publishes the price ({LINK}).\n")
        self.assertEqual(len(self.check()), 1)

    def test_the_phrase_without_the_link_fails(self) -> None:
        self.write("02-a.md", "# A\n\nIn the reference domain, Catalog publishes the price.\n")
        self.assertEqual(len(self.check()), 1)

    def test_a_link_to_another_file_does_not_mark(self) -> None:
        self.write("02-a.md", "# A\n\nIn the reference domain ([§9](09-messaging.md#x)), Catalog publishes.\n")
        self.assertEqual(len(self.check()), 1)

    def test_one_marker_covers_every_name_in_its_paragraph(self) -> None:
        self.write("02-a.md", f"# A\n\n{MARKED} Ordering reads it.\n")
        self.assertEqual(self.check(), [])

    def test_a_marker_in_the_next_paragraph_does_not_cover_this_one(self) -> None:
        self.write("02-a.md", f"# A\n\n{BARE}\n\n{MARKED}\n")
        self.assertEqual(self.check(), ["docs/backend-architecture/02-a.md:3 names Catalog"])

    def test_the_phrase_may_wrap_across_a_quoted_line(self) -> None:
        self.write("02-a.md", f"# A\n\n> In the\n> reference domain ({LINK}), Catalog publishes.\n")
        self.assertEqual(self.check(), [])

    def test_the_phrase_is_not_case_sensitive(self) -> None:
        self.write("02-a.md", f"# A\n\nIN THE REFERENCE DOMAIN ({LINK}), Catalog publishes.\n")
        self.assertEqual(self.check(), [])

    def test_a_table_row_is_a_block_of_its_own(self) -> None:
        self.write("02-a.md", f"# A\n\n| Service | Fact |\n|---|---|\n| Catalog | {BARE} |\n| Ordering | {MARKED} |\n")
        self.assertEqual(self.check(), ["docs/backend-architecture/02-a.md:5 names Catalog"])

    def test_a_fence_belongs_to_the_paragraph_before_it(self) -> None:
        self.write("02-a.md", f"# A\n\n{MARKED}\n\n```csharp\n\nnew CatalogOptions();\nCatalog.Run();\n```\n")
        self.assertEqual(self.check(), [])

    def test_a_fence_after_an_unmarked_paragraph_is_judged_with_it(self) -> None:
        self.write("02-a.md", "# A\n\nA rule.\n\n```csharp\nCatalog.Run();\n```\n")
        self.assertEqual(self.check(), ["docs/backend-architecture/02-a.md:3 names Catalog"])

    def test_a_blank_line_inside_a_fence_does_not_end_the_block(self) -> None:
        self.write("02-a.md", f"# A\n\n```csharp\n{LINK} in the reference domain\n\nCatalog.Run();\n```\n")
        self.assertEqual(self.check(), [])

    def test_a_heading_is_judged_on_its_own(self) -> None:
        self.write("02-a.md", f"## Catalog\n\n{MARKED}\n")
        self.assertEqual(self.check(), ["docs/backend-architecture/02-a.md:1 names Catalog"])


class TheNames(Fixture):
    """Read from src/Services, matched as whole words in their capitalised spelling."""

    def test_a_service_added_to_the_tree_is_covered_the_day_it_lands(self) -> None:
        self.write("02-a.md", "# A\n\nBilling publishes the invoice.\n")
        self.assertEqual(self.check(), [])
        (self.root / "src/Services/Billing").mkdir()
        self.assertEqual(self.check(), ["docs/backend-architecture/02-a.md:3 names Billing"])

    def test_a_service_removed_from_the_tree_leaves_the_set(self) -> None:
        self.write("02-a.md", f"# A\n\n{BARE}\n")
        (self.root / "src/Services/Catalog").rmdir()
        self.assertEqual(self.check(), [])

    def test_a_name_is_a_whole_word(self) -> None:
        self.write("02-a.md", "# A\n\nCatalogue and CatalogOptions and Catalog.Api are other words.\n")
        self.assertEqual(self.check(), ["docs/backend-architecture/02-a.md:3 names Catalog"])
        self.write("02-a.md", "# A\n\nCatalogue and CatalogOptions are other words.\n")
        self.assertEqual(self.check(), [])

    def test_a_name_is_case_sensitive(self) -> None:
        self.write("02-a.md", "# A\n\nThe catalog and the ordering of a list.\n")
        self.assertEqual(self.check(), [])

    def test_a_file_under_src_services_is_not_a_service(self) -> None:
        (self.root / "src/Services/Notes.md").write_text("x", encoding="utf-8")
        self.write("02-a.md", "# A\n\nNotes are not a service.\n")
        self.assertEqual(self.check(), [])


class WhenTheReaderIsBroken(Fixture):
    def test_no_service_directory_is_a_finding_and_not_a_pass(self) -> None:
        (self.root / "src/Services/Catalog").rmdir()
        (self.root / "src/Services/Ordering").rmdir()
        self.assertIn("found no service directory", self.check()[0])

    def test_no_file_to_scan_is_a_finding_and_not_a_pass(self) -> None:
        (self.root / TREE / "01-purpose.md").unlink()
        self.assertIn("found no file to scan", self.check()[0])


if __name__ == "__main__":
    unittest.main()
