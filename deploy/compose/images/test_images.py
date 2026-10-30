"""The third-party images Dependabot can see are the ones the Compose stack runs.

    py -3.12 -m unittest discover -s deploy/compose/images
"""

from __future__ import annotations

import re
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
COMPOSE = HERE.parent
ROOT = COMPOSE.parents[1]
KEY = re.compile(r"^\s*image:")
IMAGE = re.compile(r"^\s*image:\s*(\S+)\s*(?:#.*)?$")
FROM = re.compile(r"^FROM\s+(\S+)(?:\s+AS\s+\S+)?\s*$")
# A repository's own image, built from this tree and tagged by the pipeline rather than pinned.
OWN = "${BLUEPRINT_REGISTRY"
# The platform suffixes a multi-arch release also publishes one image under, as the collector does beside each tag.
PLATFORM = re.compile(r"-(?:386|amd64|arm64(?:v8)?|armv[5-7]|arm|ppc64le|s390x|riscv64|windows-.*)$")


def pinned(lines: list[str]) -> set[str]:
    """The pinned images among a Compose file's lines; an `image:` line that does not parse is a failure."""
    found: set[str] = set()
    for line in lines:
        if not KEY.match(line):
            continue
        match = IMAGE.match(line)
        if match is None:
            raise AssertionError(f"an image line this test cannot read: {line.strip()!r}")
        if not match.group(1).startswith(OWN):
            found.add(match.group(1))
    return found


def mirrored(lines: list[str]) -> set[str]:
    """The images a Dockerfile starts a stage FROM."""
    return {match.group(1) for match in map(FROM.match, lines) if match}


def drift(names: set[str], mirror: set[str]) -> tuple[set[str], set[str]]:
    """The images Dependabot cannot see, and the mirrored ones the stack no longer runs."""
    return names - mirror, mirror - names


def single_platform(images: set[str]) -> set[str]:
    """The images pinned to one platform's tag rather than the release's multi-arch one."""
    return {image for image in images if PLATFORM.search(image.rsplit("/", 1)[-1].partition(":")[2])}


def docker_directories(config: str) -> str:
    """The text of the `package-ecosystem: docker` entry, up to the next entry."""
    entries = re.split(r"(?m)^  - package-ecosystem:", config)
    return next((entry for entry in entries if entry.split(None, 1)[:1] == ["docker"]), "")


def read(path: Path) -> list[str]:
    return path.read_text(encoding="utf-8").splitlines()


class MirrorTests(unittest.TestCase):
    def test_the_mirror_names_every_pinned_image_and_nothing_else(self):
        files = [*sorted(COMPOSE.glob("*.yml")), *sorted((COMPOSE / "services").glob("*.yml"))]
        names = set().union(*(pinned(read(path)) for path in files))
        self.assertGreaterEqual(len(names), 9, "the stack's images were not found, so nothing was compared")

        unseen, stale = drift(names, mirrored(read(HERE / "Dockerfile")))

        self.assertEqual(unseen, set(), "a pinned image Dependabot cannot see")
        self.assertEqual(stale, set(), "a mirrored image the stack no longer runs")

    def test_no_mirrored_image_is_pinned_to_one_platform(self):
        # Dependabot reads a numeric suffix such as -386 as a build number newer than the release, and no ignore
        # entry can name every such tag, so a bump to one is refused here: close it, which Dependabot does not
        # reopen for that version, and move the pin to the release's own tag by hand.
        self.assertEqual(single_platform(mirrored(read(HERE / "Dockerfile"))), set(),
                         "a mirrored image pinned to one platform's tag; close the bump and pin the release by hand")

    def test_a_platform_suffix_is_told_apart_from_a_variant_or_a_build_number(self):
        flagged = {"otel/opentelemetry-collector-contrib:0.163.2-386", "otel/c:0.163.2-arm64",
                   "otel/c:0.163.2-windows-2019-amd64", "registry:5000/otel/c:0.163.2-s390x"}
        kept = {"otel/opentelemetry-collector-contrib:0.163.2", "quay.io/keycloak/keycloak:26.8.1-1",
                "redis:7.4.11-alpine", "mcr.microsoft.com/mssql/server:2022-CU26-ubuntu-22.04", "registry:5000/x"}
        self.assertEqual(single_platform(flagged | kept), flagged)

    def test_the_docker_ecosystem_is_given_the_mirror(self):
        config = (ROOT / ".github" / "dependabot.yml").read_text(encoding="utf-8")
        self.assertRegex(docker_directories(config), r"(?m)^\s*-\s*/deploy/compose/images\s*$",
                         "the docker ecosystem's directories do not name the mirror")

    def test_the_comparison_finds_a_drift_in_each_direction_and_ignores_the_repositorys_own_images(self):
        own = "    image: ${BLUEPRINT_REGISTRY:-blueprint.local}/x:${T}"
        self.assertEqual(pinned(["    image: redis:7-alpine  # pinned", own, "  o:"]), {"redis:7-alpine"})
        self.assertEqual(mirrored(["FROM redis:8-alpine AS redis", "RUN true"]), {"redis:8-alpine"})
        self.assertEqual(drift({"redis:7-alpine"}, {"redis:8-alpine"}), ({"redis:7-alpine"}, {"redis:8-alpine"}))
        self.assertEqual(drift({"redis:7-alpine"}, {"redis:7-alpine"}), (set(), set()))
        with self.assertRaises(AssertionError):
            pinned(["    image: redis 7-alpine"])

    def test_an_ecosystem_other_than_docker_naming_the_path_does_not_count(self):
        config = ("updates:\n  - package-ecosystem: nuget\n    directories:\n      - /deploy/compose/images\n"
                  "  - package-ecosystem: docker-compose\n    directories:\n      - /deploy/compose/images\n"
                  "  - package-ecosystem: docker\n    directories:\n      - /src\n")
        self.assertNotRegex(docker_directories(config), r"/deploy/compose/images")


if __name__ == "__main__":
    unittest.main()
