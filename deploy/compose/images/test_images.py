"""The third-party images Dependabot can see are the ones the Compose stack runs.

    python -m unittest discover -s deploy/compose/images
"""

from __future__ import annotations

import re
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
COMPOSE = HERE.parent
ROOT = COMPOSE.parents[1]
IMAGE = re.compile(r"^\s*image:\s*(\S+)\s*$")
FROM = re.compile(r"^FROM\s+(\S+)(?:\s+AS\s+\S+)?\s*$")
# A repository's own image, built from this tree and tagged by the pipeline rather than pinned.
OWN = "${BLUEPRINT_REGISTRY"


def pinned(lines: list[str]) -> set[str]:
    """The pinned images among a Compose file's lines."""
    found = (IMAGE.match(line) for line in lines)
    return {match.group(1) for match in found if match and not match.group(1).startswith(OWN)}


def mirrored(lines: list[str]) -> set[str]:
    """The images a Dockerfile starts a stage FROM."""
    return {match.group(1) for match in map(FROM.match, lines) if match}


def drift(names: set[str], mirror: set[str]) -> tuple[set[str], set[str]]:
    """The images Dependabot cannot see, and the mirrored ones the stack no longer runs."""
    return names - mirror, mirror - names


def read(path: Path) -> list[str]:
    return path.read_text(encoding="utf-8").splitlines()


class MirrorTests(unittest.TestCase):
    def test_the_mirror_names_every_pinned_image_and_nothing_else(self):
        files = [COMPOSE / "infrastructure.yml", *sorted((COMPOSE / "services").glob("*.yml"))]
        names = set().union(*(pinned(read(path)) for path in files))
        self.assertGreaterEqual(len(names), 9, "the stack's images were not found, so nothing was compared")

        unseen, stale = drift(names, mirrored(read(HERE / "Dockerfile")))

        self.assertEqual(unseen, set(), "a pinned image Dependabot cannot see")
        self.assertEqual(stale, set(), "a mirrored image the stack no longer runs")

    def test_dependabot_reads_the_mirror(self):
        config = (ROOT / ".github" / "dependabot.yml").read_text(encoding="utf-8")
        self.assertRegex(config, r"(?m)^\s*-\s*/deploy/compose/images\s*$", "the docker ecosystem is not given it")

    def test_the_comparison_finds_a_drift_in_each_direction_and_ignores_the_repositorys_own_images(self):
        lines = ["    image: redis:7-alpine", "    image: ${BLUEPRINT_REGISTRY:-blueprint.local}/x:${TAG}", "  other:"]
        self.assertEqual(pinned(lines), {"redis:7-alpine"})
        self.assertEqual(mirrored(["FROM redis:8-alpine AS redis", "RUN true"]), {"redis:8-alpine"})
        self.assertEqual(drift({"redis:7-alpine"}, {"redis:8-alpine"}), ({"redis:7-alpine"}, {"redis:8-alpine"}))
        self.assertEqual(drift({"redis:7-alpine"}, {"redis:7-alpine"}), (set(), set()))


if __name__ == "__main__":
    unittest.main()
