#!/usr/bin/env python3
"""Negative cases for the licence gate.

A gate is only worth having if it fails when it should, and every carve-out in
Section 4.4 is a branch where it deliberately does not. Those branches are what
this file pins down: run against the real repository the gate passes, which
proves nothing about what it would catch.
"""

from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

import licence_gate

ALLOWED = {"MIT", "Apache-2.0", "BSD-3-Clause"}

REGISTER_HEADER = """# Appendix B — Dependency licence register

## Chosen — free for commercial use

| Package | Licence | Role |
|---|---|---|
"""


def register(*rows: str) -> list[tuple[list[str], str]]:
    """Parse a Chosen table built from the given row bodies."""
    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / "appendix-b-licences.md"
        path.write_text(REGISTER_HEADER + "".join(f"{row}\n" for row in rows), encoding="utf-8")
        return licence_gate.read_register(path)


def pins(*identities: str) -> str:
    """A Directory.Packages.props naming the given packages."""
    entries = "".join(
        f'    <PackageVersion Include="{identity}" Version="1.0.0" />\n' for identity in identities)
    return f"<Project>\n  <ItemGroup>\n{entries}  </ItemGroup>\n</Project>\n"


def pins_doc(body: str) -> str:
    """A Directory.Packages.props whose ItemGroup is the given lines."""
    return f"<Project>\n  <ItemGroup>\n{body}  </ItemGroup>\n</Project>\n"


def chapters_with(name: str, text: str) -> list[str]:
    """The gate's reading of a one-file blueprint tree."""
    with tempfile.TemporaryDirectory() as directory:
        (Path(directory) / name).write_text(text, encoding="utf-8")
        return licence_gate.chapter_pins(Path(directory))


def read_pins(document: str) -> set[str]:
    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / "Directory.Packages.props"
        path.write_text(document, encoding="utf-8")
        return licence_gate.read_pins(path)


def scan(*projects: tuple[str, str]) -> list[str]:
    """Findings from a tree holding the given (relative path, document) pairs."""
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        for name, document in projects:
            path = root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(document, encoding="utf-8")
        return licence_gate.scan_projects(root)


def csproj(body: str, attributes: str = "") -> str:
    """A .csproj whose single ItemGroup or PropertyGroup is the given lines."""
    return f"<Project{attributes}>\n{body}</Project>\n"


class ReadPins(unittest.TestCase):
    def test_reads_every_pinned_identity(self):
        self.assertEqual(read_pins(pins("Dapper", "Scrutor")), {"Dapper", "Scrutor"})

    def test_reads_a_global_package_reference_as_a_pin(self):
        # A different element name in the same file, and it reaches further than
        # any PackageVersion row: the package is injected into every project.
        document = pins_doc(
            '    <GlobalPackageReference Include="Roslynator.Analyzers" Version="4.0.0" />\n')
        self.assertEqual(read_pins(document), {"Roslynator.Analyzers"})

    def test_reads_pins_out_of_a_namespaced_project(self):
        # An xmlns on <Project> renames every tag to {uri}PackageVersion, and
        # MSBuild restores the file either way. Matching the bare name only is a
        # gate one attribute switches off, with nothing to see in the output.
        namespace = ' xmlns="http://schemas.microsoft.com/developer/msbuild/2003"'
        document = (f"<Project{namespace}>\n  <ItemGroup>\n"
                    f'    <PackageVersion Include="Dapper" Version="1.0.0" />\n'
                    f"  </ItemGroup>\n</Project>\n")
        self.assertEqual(read_pins(document), {"Dapper"})

    def test_refuses_a_props_file_declaring_a_dtd(self):
        bomb = '<!DOCTYPE lolz [<!ENTITY lol "lol">]>\n' + pins("Dapper")
        with self.assertRaises(ValueError):
            read_pins(bomb)

    def test_refuses_a_pin_element_carrying_no_include(self):
        # `Update` sets a version on an item declared elsewhere, so this is a
        # legal MSBuild spelling that names a package this gate cannot read.
        # Skipping it would fail open, and a KeyError would name neither the
        # file nor the element.
        document = pins_doc(
            '    <PackageVersion Update="Dapper" Version="2.1.66" />\n')

        with self.assertRaises(ValueError) as refusal:
            read_pins(document)

        self.assertIn("no Include attribute", str(refusal.exception))
        self.assertIn("PackageVersion", str(refusal.exception))
        self.assertIn("Update", str(refusal.exception))
        # The file too, because a message naming only the element would pass
        # a suite that pinned only the element.
        self.assertIn("Directory.Packages.props", str(refusal.exception))


class ScanProjects(unittest.TestCase):
    """The three project-level spellings that restore an unpinned package.

    Every case here is a document the pin reader sees nothing in, which is the
    whole point of the pass: none of them puts a PackageVersion element in
    Directory.Packages.props.
    """

    def test_the_scan_finds_the_projects_it_is_checking(self):
        """The subject, before the assertions. A glob that matched nothing would
        pass every negative case below by finding no fault in an empty set."""
        found = {path.as_posix() for path in licence_gate.find_projects(licence_gate.REPO_ROOT)}
        self.assertIn(
            (licence_gate.REPO_ROOT / "src" / "BFF" / "Web.Bff" / "Web.Bff.csproj").as_posix(),
            found)
        self.assertGreater(len(found), 30)

    def test_the_scan_reaches_the_files_that_import_into_every_project(self):
        """The other half of the subject, and the one a csproj glob misses. A
        PackageReference written in Directory.Build.props reaches every project
        at once, so a scan that stopped at the projects would have closed the
        narrow spelling and left the wide one open."""
        found = {path.as_posix() for path in licence_gate.find_projects(licence_gate.REPO_ROOT)}
        for name in ("Directory.Build.props", "Directory.Packages.props"):
            self.assertIn((licence_gate.REPO_ROOT / name).as_posix(), found)

    def test_fails_an_imported_props_file_that_pins_a_version(self):
        body = ('  <ItemGroup>\n'
                '    <PackageReference Include="Evil" Version="1.0.0" />\n'
                '  </ItemGroup>\n')
        findings = scan(("Directory.Build.props", csproj(body)))
        self.assertEqual(len(findings), 1)
        self.assertIn("Directory.Build.props", findings[0])
        self.assertIn("Evil", findings[0])

    def test_passes_a_project_that_pins_nothing_of_its_own(self):
        body = '  <ItemGroup>\n    <PackageReference Include="Dapper" />\n  </ItemGroup>\n'
        self.assertEqual(scan(("src/Thing/Thing.csproj", csproj(body))), [])

    def test_fails_a_package_reference_carrying_a_version_attribute(self):
        body = ('  <ItemGroup>\n'
                '    <PackageReference Include="Evil" Version="1.0.0" />\n'
                '  </ItemGroup>\n')
        findings = scan(("src/Thing/Thing.csproj", csproj(body)))
        self.assertEqual(len(findings), 1)
        self.assertIn("Evil", findings[0])
        self.assertIn("Version attribute", findings[0])

    def test_fails_a_package_reference_carrying_a_version_override(self):
        # Legal under central package management and needing no PackageVersion
        # row at all, which is exactly what makes it invisible to read_pins.
        body = ('  <ItemGroup>\n'
                '    <PackageReference Include="Evil" VersionOverride="1.0.0" />\n'
                '  </ItemGroup>\n')
        findings = scan(("src/Thing/Thing.csproj", csproj(body)))
        self.assertEqual(len(findings), 1)
        self.assertIn("VersionOverride attribute", findings[0])

    def test_fails_a_package_reference_carrying_a_version_child_element(self):
        # A multi-line PackageReference with children reads to a line pattern
        # as unrelated lines.
        body = ('  <ItemGroup>\n'
                '    <PackageReference Include="Evil">\n'
                '      <Version>1.0.0</Version>\n'
                '    </PackageReference>\n'
                '  </ItemGroup>\n')
        findings = scan(("src/Thing/Thing.csproj", csproj(body)))
        self.assertEqual(len(findings), 1)
        self.assertIn("Version child element", findings[0])

    def test_fails_a_project_that_opts_out_of_central_package_management(self):
        body = ('  <PropertyGroup>\n'
                '    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>\n'
                '  </PropertyGroup>\n')
        findings = scan(("src/Thing/Thing.csproj", csproj(body)))
        self.assertEqual(len(findings), 1)
        self.assertIn("ManagePackageVersionsCentrally", findings[0])

    def test_reads_a_namespaced_project(self):
        namespace = ' xmlns="http://schemas.microsoft.com/developer/msbuild/2003"'
        body = ('  <ItemGroup>\n'
                '    <PackageReference Include="Evil" Version="1.0.0" />\n'
                '  </ItemGroup>\n')
        findings = scan(("src/Thing/Thing.csproj", csproj(body, namespace)))
        self.assertEqual(len(findings), 1)
        self.assertIn("Evil", findings[0])

    def test_refuses_a_project_declaring_a_dtd(self):
        body = '  <ItemGroup />\n'
        document = '<!DOCTYPE lolz [<!ENTITY lol "lol">]>\n' + csproj(body)
        with self.assertRaises(ValueError):
            scan(("src/Thing/Thing.csproj", document))

    def test_does_not_read_the_output_of_a_restore(self):
        # obj/ is where a restore writes its own MSBuild files. A scan reading
        # those would be reading the restore it exists to check.
        body = ('  <ItemGroup>\n'
                '    <PackageReference Include="Evil" Version="1.0.0" />\n'
                '  </ItemGroup>\n')
        findings = scan(
            ("src/Thing/Thing.csproj", csproj('  <ItemGroup />\n')),
            ("src/Thing/obj/Thing.csproj", csproj(body)))
        self.assertEqual(findings, [])

    def test_fails_when_there_is_no_project_to_scan(self):
        # An empty subject is a finding, not a clean result: a glob matching
        # nothing reports what a repository with no fault reports.
        findings = scan()
        self.assertEqual(len(findings), 1)
        self.assertIn("read nothing", findings[0])


class ReadRegister(unittest.TestCase):
    def test_skips_the_header_and_separator_rows(self):
        self.assertEqual(register("| `Dapper` | Apache 2.0 | Data |"), [(["Dapper"], "Apache 2.0")])

    def test_reads_every_identity_in_a_multi_package_row(self):
        rows = register("| `Grpc.Tools`, `Google.Protobuf` | Apache 2.0 / BSD-3 | gRPC |")
        self.assertEqual(rows, [(["Grpc.Tools", "Google.Protobuf"], "Apache 2.0 / BSD-3")])

    def test_ignores_tables_other_than_chosen(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "appendix-b-licences.md"
            path.write_text(
                REGISTER_HEADER
                + "| `Dapper` | Apache 2.0 | Data |\n"
                + "\n## Avoided — commercial, with the replacement used here\n\n"
                + "| Package | Change | Replacement |\n|---|---|---|\n"
                + "| **MediatR** v13+ | Commercial from 2025 | Hand-rolled dispatcher |\n",
                encoding="utf-8")
            self.assertEqual(licence_gate.read_register(path), [(["Dapper"], "Apache 2.0")])


class Audit(unittest.TestCase):
    def test_passes_when_every_pin_is_registered_and_cleared(self):
        rows = register("| `Dapper` | Apache 2.0 | Data |")
        self.assertEqual(licence_gate.audit({"Dapper"}, rows, ALLOWED), [])

    def test_fails_a_pin_absent_from_the_register(self):
        rows = register("| `Dapper` | Apache 2.0 | Data |")
        findings = licence_gate.audit({"Dapper", "MediatR"}, rows, ALLOWED)
        self.assertEqual(len(findings), 1)
        self.assertIn("MediatR", findings[0])
        self.assertIn("never been cleared", findings[0])

    def test_fails_a_registered_licence_outside_the_allow_list(self):
        rows = register("| `RabbitMQ.Client` | MPL 2.0 | Broker |")
        findings = licence_gate.audit({"RabbitMQ.Client"}, rows, ALLOWED)
        self.assertEqual(len(findings), 1)
        self.assertIn("MPL-2.0", findings[0])
        self.assertIn("outside the allow-list", findings[0])

    def test_clears_a_dual_licence_when_every_half_is_allowed(self):
        rows = register("| `Google.Protobuf` | Apache 2.0 / BSD-3 | gRPC |")
        self.assertEqual(licence_gate.audit({"Google.Protobuf"}, rows, ALLOWED), [])

    def test_fails_a_dual_licence_row_whose_other_half_is_outside_the_allow_list(self):
        # The reversal. The gate reads a `/` and cannot tell a disjunction from
        # a conjunction, so a row cleared because one half was allowed is a row
        # that clears a forbidden licence by putting it in good company.
        rows = register("| `Something` | MIT / MPL 2.0 | Whatever |")
        findings = licence_gate.audit({"Something"}, rows, ALLOWED)
        self.assertEqual(len(findings), 1)
        self.assertIn("MPL-2.0", findings[0])
        self.assertIn("outside the allow-list", findings[0])

    def test_fails_a_dual_licence_row_whose_other_half_it_cannot_name(self):
        rows = register("| `Something` | MIT / GPL-3.0-only | Whatever |")
        findings = licence_gate.audit({"Something"}, rows, ALLOWED)
        self.assertEqual(len(findings), 1)
        self.assertIn("GPL-3.0-only", findings[0])
        self.assertIn("not a licence spelling this gate knows", findings[0])

    def test_fails_a_licence_spelling_the_gate_cannot_map(self):
        # Fail closed, and say which failure it is. A licence read and refused
        # is repaired by a decision about the allow-list; a spelling with no
        # identifier behind it was never read, and adding a line for it would
        # be admitting a licence nobody has named.
        rows = register("| `Something` | Business Source Licence | Whatever |")
        findings = licence_gate.audit({"Something"}, rows, ALLOWED)
        self.assertEqual(len(findings), 1)
        self.assertIn("not a licence spelling this gate knows", findings[0])
        self.assertNotIn("outside the allow-list", findings[0])

    def test_reports_a_registered_identity_that_is_pinned_nowhere(self):
        rows = register("| `Dapper` | Apache 2.0 | Data |")
        findings = licence_gate.audit(set(), rows, ALLOWED)
        self.assertEqual(len(findings), 1)
        self.assertIn("pinned nowhere", findings[0])

    def test_ignores_a_row_that_names_a_product_rather_than_a_package(self):
        # Section 4.4: match on package identity, never on the product a package
        # is named after. Keycloak carries no identity, so it never enters.
        rows = register("| Keycloak | Apache 2.0 | Identity provider |")
        self.assertEqual(licence_gate.audit(set(), rows, ALLOWED), [])

    def test_skips_the_deliberately_unpinned_aspire_rows(self):
        rows = register(
            "| `Aspire.Hosting.*` (`AppHost`, `SqlServer`) | MIT | Topology for the AppHost |",
            "| `Aspire.*` client integrations | MIT | The service-side half |")
        self.assertEqual(licence_gate.audit(set(), rows, ALLOWED), [])

    def test_does_not_report_the_unchosen_half_of_an_either_or_row(self):
        rows = register("| `Shouldly` *or* `AwesomeAssertions` | BSD-3 / Apache 2.0 | Assertions |")
        self.assertEqual(licence_gate.audit({"Shouldly"}, rows, ALLOWED), [])


class ReadAllowed(unittest.TestCase):
    def allowed(self, text: str) -> set[str]:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "allowed-licences.txt"
            path.write_text(text, encoding="utf-8")
            return licence_gate.read_allowed(path)

    def test_reads_an_identifier_and_skips_a_comment(self):
        self.assertEqual(self.allowed("# A comment\nMIT\n"), {"MIT"})

    def test_skips_an_indented_comment(self):
        # Testing the raw line for `#` and storing the stripped one would admit
        # an entry spelled `# GPL-3.0`.
        self.assertEqual(self.allowed("MIT\n    # GPL-3.0\n"), {"MIT"})


class ChapterPins(unittest.TestCase):
    """A chapter printing a pin is a second owner of the version.

    `docs/change-locality.md` §2 keeps every package version in
    `Directory.Packages.props`, Appendix B excepted, and Class E's row keeps
    a dependency change out of the chapters that use the package. A pin in a
    chapter contradicts both, so the gate refuses it rather than comparing it.
    """

    PIN = '    <PackageVersion Include="Dapper" Version="2.1.66" />\n'

    def test_a_chapter_printing_a_pin_is_a_finding(self):
        findings = chapters_with("04-solution-structure.md", self.PIN)
        self.assertEqual(len(findings), 1)
        self.assertIn("Dapper", findings[0])
        self.assertIn("2.1.66", findings[0])

    def test_the_attribute_order_does_not_decide_it(self):
        findings = chapters_with(
            "04-solution-structure.md",
            '    <PackageVersion Version="2.1.66" Include="Dapper" />\n')
        self.assertEqual(len(findings), 1)
        self.assertIn("Dapper", findings[0])
        self.assertIn("2.1.66", findings[0])

    def test_single_quotes_are_the_same_pin(self):
        findings = chapters_with(
            "04-solution-structure.md",
            "    <PackageVersion Include='Dapper' Version='2.1.66' />\n")
        self.assertEqual(len(findings), 1)
        self.assertIn("Dapper", findings[0])
        self.assertIn("2.1.66", findings[0])

    def test_one_tag_may_quote_its_two_attributes_differently(self):
        findings = chapters_with(
            "04-solution-structure.md",
            '    <PackageVersion Version=\'2.1.66\' Include="Dapper" />\n')
        self.assertEqual(len(findings), 1)
        self.assertIn("Dapper", findings[0])
        self.assertIn("2.1.66", findings[0])

    def test_two_tags_are_not_one_pin(self):
        # The pair is read inside a single tag: an Include in one element and a
        # Version in another are the central-management form the chapters are
        # free to print, and pairing them across the gap would refuse it.
        findings = chapters_with(
            "04-solution-structure.md",
            '<PackageReference Include="Dapper" />\n<PackageVersion Version="2.1.66" />\n')
        self.assertEqual(findings, [])

    def test_a_chapter_that_cites_the_file_is_not(self):
        findings = chapters_with(
            "04-solution-structure.md",
            "`Directory.Packages.props` pins every package version once.\n")
        self.assertEqual(findings, [])

    def test_the_floor_trap_is_not_a_pin(self):
        # Section 4.4 quotes `Version="8.*"` to say what not to write. It
        # carries no Include, which is what keeps an illustration of the
        # forbidden form from reading as the forbidden form.
        findings = chapters_with(
            "04-solution-structure.md", 'Writing `Version="8.*"` means a restore can drift.\n')
        self.assertEqual(findings, [])

    def test_appendix_b_is_the_contract_s_one_exception(self):
        # docs/change-locality.md section 2 names it, because there the
        # version is the decision rather than a copy of one.
        findings = chapters_with("appendix-b-licences.md", self.PIN)
        self.assertEqual(findings, [])

    def test_every_chapter_is_read_and_not_only_the_one_that_used_to_print_them(self):
        # The gate-coverage rule: a check aimed at one filename stops
        # covering the copy that lands in the next chapter along.
        findings = chapters_with("09-messaging.md", self.PIN)
        self.assertEqual(len(findings), 1)


class RealRepository(unittest.TestCase):
    def test_the_repository_passes_its_own_gate(self):
        self.assertEqual(licence_gate.main([]), 0)

    def test_no_project_in_the_repository_pins_a_version_of_its_own(self):
        self.assertEqual(licence_gate.scan_projects(licence_gate.REPO_ROOT), [])


if __name__ == "__main__":
    unittest.main()
