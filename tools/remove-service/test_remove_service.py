"""The remove tool's tests, run against the real repository.

The subject is the scaffold README's table of shared files: render then remove
must leave every file as it was. Run: cd tools/remove-service && python -m unittest
"""

import contextlib
import hashlib
import io
import json
import re
import shutil
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import remove_service  # first: it puts tools/new-service on sys.path
import new_service
from new_service import Names, ScaffoldError, plan as render_plan, apply as render_apply
from scaffold.verify import SCAN_GATE

REPO_ROOT = Path(__file__).resolve().parents[2]
README = REPO_ROOT / "tools" / "new-service" / "README.md"
MIGRATION_ID = "20260809120000"

# Names the platform will never take, as in the scaffold's own suite.
PROBE = "Zulu"
SECOND_PROBE = "Yankee"

# The three shapes a render has: an API on a port, a worker, and a pure consumer.
SHAPES = {
    "api": {"name": PROBE, "port": 5199, "host": new_service.API_HOST, "pure_consumer": False},
    "worker": {"name": PROBE, "port": None, "host": new_service.WORKER_HOST, "pure_consumer": False},
    "pure consumer": {"name": PROBE, "port": None, "host": new_service.WORKER_HOST, "pure_consumer": True},
}


def readme_table() -> list[str]:
    """The paths in the table that follows the README's sentence about `plan()`."""
    lines = README.read_text(encoding="utf-8").splitlines()
    anchor = next(i for i, line in enumerate(lines) if "table matches `plan()`" in line)
    rows: list[str] = []
    for line in lines[anchor + 1:]:
        if rows and not line.startswith("|"):
            break
        if (cell := re.match(r"\| `([^`]+)` \|", line)) is not None:
            rows.append(cell.group(1))
    return rows


def table_patterns(name: str) -> list[re.Pattern[str]]:
    """Each README row as a pattern: `<name>` is the service key, `<tree>` any one segment."""
    patterns = []
    for row in readme_table():
        spelt = re.escape(row).replace(re.escape("<name>"), re.escape(name.lower()))
        spelt = spelt.replace(re.escape("<tree>"), "[^/]+")
        patterns.append(re.compile(spelt))
    return patterns


def manifest(root: Path) -> dict[str, str]:
    """Every file under a root with a digest of its bytes, bytecode caches aside.

    Loading the secret scan writes its `__pycache__`, which is Python's and not
    the render's.
    """
    return {
        path.relative_to(root).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
        for path in sorted(root.rglob("*"))
        if path.is_file() and "__pycache__" not in path.parts
    }


def template_root(destination: Path) -> Path:
    """The template, every shared file the README names, and §15.1's secret scan.

    Copied from the real repository, because a fixture tree cannot drift from
    the table; the Compose index's units come too, which the port check reads.
    """
    for root in new_service.COPY_ROOTS:
        shutil.copytree(REPO_ROOT / root, destination / root, ignore=shutil.ignore_patterns("bin", "obj"))
    shutil.copytree(
        REPO_ROOT / Path(SCAN_GATE).parent,
        destination / Path(SCAN_GATE).parent,
        ignore=shutil.ignore_patterns("__pycache__"),
    )
    for row in readme_table():
        if "<" in row:
            continue
        (destination / row).parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(REPO_ROOT / row, destination / row)
    for _, entry in new_service.compose_included(REPO_ROOT):
        included = f"{new_service.COMPOSE_DIR}/{entry}"
        (destination / included).parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(REPO_ROOT / included, destination / included)
    return destination


def render_into(root: Path, name: str, shape: dict) -> new_service.Plan:
    rendered = render_plan(root, name, shape["port"], MIGRATION_ID, shape["host"], shape["pure_consumer"])
    render_apply(root, rendered)
    return rendered


class RoundTripTests(unittest.TestCase):
    """Render and remove, then compare the whole tree."""

    @classmethod
    def setUpClass(cls):
        cls._directory = tempfile.TemporaryDirectory()
        cls.base = template_root(Path(cls._directory.name) / "base")

    @classmethod
    def tearDownClass(cls):
        cls._directory.cleanup()

    def fresh_root(self) -> Path:
        copy = Path(tempfile.mkdtemp(dir=self._directory.name)) / "root"
        shutil.copytree(self.base, copy)
        self.addCleanup(shutil.rmtree, copy.parent, ignore_errors=True)
        return copy

    def test_remove_undoes_a_render_in_every_shape(self):
        for label, shape in SHAPES.items():
            with self.subTest(shape=label):
                root = self.fresh_root()
                before = manifest(root)
                render_into(root, PROBE, shape)
                self.assertNotEqual(manifest(root), before, "the render changed nothing, so this proves nothing")

                remove_service.apply(root, remove_service.plan(root, PROBE))

                self.assertEqual(manifest(root), before)

    def test_removal_is_independent_of_the_order_services_were_rendered_in(self):
        for first, second in ((PROBE, SECOND_PROBE), (SECOND_PROBE, PROBE)):
            with self.subTest(removed_first=first):
                root = self.fresh_root()
                before = manifest(root)
                render_into(root, PROBE, SHAPES["api"])
                render_into(root, SECOND_PROBE, {**SHAPES["worker"]})
                both = manifest(root)

                remove_service.apply(root, remove_service.plan(root, first))
                self.assertNotEqual(manifest(root), both)
                self.assertNotEqual(manifest(root), before, f"{second} is still rendered")
                remove_service.apply(root, remove_service.plan(root, second))

                self.assertEqual(manifest(root), before)

    def test_every_path_a_render_writes_is_a_row_of_the_readme_table(self):
        root = self.fresh_root()
        for label, shape in SHAPES.items():
            with self.subTest(shape=label):
                rendered = render_plan(
                    root, PROBE, shape["port"], MIGRATION_ID, shape["host"], shape["pure_consumer"])
                patterns = table_patterns(PROBE)
                outside = [
                    path for path in {**rendered.created, **rendered.updated}
                    if not path.startswith(("src/Services/", "tests/"))
                    and not any(pattern.fullmatch(path) for pattern in patterns)
                ]
                self.assertEqual(outside, [], "the render writes a path the README table does not name")

    def test_every_row_of_the_readme_table_is_something_a_render_writes(self):
        root = self.fresh_root()
        rendered = render_plan(root, PROBE, 5199, MIGRATION_ID)
        written = {**rendered.created, **rendered.updated}
        for row, pattern in zip(readme_table(), table_patterns(PROBE)):
            with self.subTest(row=row):
                self.assertTrue(any(pattern.fullmatch(path) for path in written), row)

    def test_a_path_the_remove_tool_does_not_know_fails_the_round_trip(self):
        """The control: a splice and a created file that nothing removes show as differences."""
        root = self.fresh_root()
        before = manifest(root)
        render_into(root, PROBE, SHAPES["api"])
        slnx = root / "Platform.slnx"
        slnx.write_bytes(b"<!-- a splice only the render knows -->\n" + slnx.read_bytes())
        (root / "deploy" / "extra.txt").write_text("created by a render that learned a new file\n")

        remove_service.apply(root, remove_service.plan(root, PROBE))

        differing = {path for path, digest in manifest(root).items() if before.get(path) != digest}
        self.assertEqual(differing, {"Platform.slnx", "deploy/extra.txt"})

    def test_plan_writes_nothing(self):
        root = self.fresh_root()
        render_into(root, PROBE, SHAPES["api"])
        before = manifest(root)

        remove_service.plan(root, PROBE)

        self.assertEqual(manifest(root), before)

    def test_a_missing_splice_refuses_and_leaves_the_tree_alone(self):
        for relative in ("Platform.slnx", "deploy/compose/docker-compose.yml"):
            with self.subTest(file=relative):
                root = self.fresh_root()
                render_into(root, PROBE, SHAPES["api"])
                original = (root / relative).read_bytes()
                (root / relative).write_bytes(
                    re.sub(rb"(?m)^.*(?:zulu\.yml|Services/Zulu/).*\r?\n", b"", original, count=1))
                before = manifest(root)

                with self.assertRaises(ScaffoldError):
                    remove_service.plan(root, PROBE)

                self.assertEqual(manifest(root), before)

    def test_a_splice_found_twice_is_refused(self):
        root = self.fresh_root()
        render_into(root, PROBE, SHAPES["api"])
        users = root / "deploy" / "compose" / "redis" / "users.conf"
        users.write_bytes(users.read_bytes() + b"user zulu-svc on >again\n")

        with self.assertRaisesRegex(ScaffoldError, "2 Redis user zulu-svc"):
            remove_service.plan(root, PROBE)

    def test_a_splice_the_service_no_longer_carries_is_reported_and_skipped(self):
        root = self.fresh_root()
        render_into(root, PROBE, SHAPES["api"])
        users = root / "deploy" / "compose" / "redis" / "users.conf"
        users.write_bytes(re.sub(rb"(?m)^user zulu-svc .*\r?\n", b"", users.read_bytes()))

        rendered = remove_service.plan(root, PROBE)

        self.assertNotIn("deploy/compose/redis/users.conf", rendered.updated)
        self.assertEqual(
            rendered.absent, ["deploy/compose/redis/users.conf: no Redis user zulu-svc"])

    def test_a_permissions_entry_left_without_its_user_goes_and_is_reported(self):
        root = self.fresh_root()
        render_into(root, PROBE, SHAPES["api"])
        definitions = root / "deploy" / "compose" / "rabbitmq" / "definitions.json"
        data = json.loads(definitions.read_text(encoding="utf-8"))
        data["users"] = [entry for entry in data["users"] if entry["name"] != "zulu-svc"]
        definitions.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8", newline="")

        rendered = remove_service.plan(root, PROBE)

        remaining = json.loads(rendered.updated["deploy/compose/rabbitmq/definitions.json"])
        self.assertEqual([entry for entry in remaining["permissions"] if entry["user"] == "zulu-svc"], [])
        self.assertIn("deploy/compose/rabbitmq/definitions.json: no broker user zulu-svc", rendered.absent)

    def test_half_a_compose_pair_is_refused(self):
        root = self.fresh_root()
        render_into(root, PROBE, SHAPES["api"])
        override = root / "deploy" / "compose" / "docker-compose.infra-only.yml"
        override.write_bytes(
            re.sub(rb'(?m)^  zulu-migrator:\r?\n    profiles: \[ "excluded" \]\r?\n', b"", override.read_bytes()))

        with self.assertRaisesRegex(ScaffoldError, "will not remove half of one"):
            remove_service.plan(root, PROBE)


class RefusalTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        (self.root / "src" / "Services" / "Yankee").mkdir(parents=True)
        (self.root / "src" / "Services" / "Catalog").mkdir()

    def test_the_template_is_refused(self):
        with self.assertRaisesRegex(ScaffoldError, "scaffold's template"):
            remove_service.plan(self.root, "Catalog")

    def test_a_service_that_is_not_there_is_refused(self):
        with self.assertRaisesRegex(ScaffoldError, "does not exist"):
            remove_service.plan(self.root, "Xray")

    def test_a_different_casing_is_not_the_service(self):
        with self.assertRaisesRegex(ScaffoldError, "does not exist"):
            remove_service.plan(self.root, "YANKEE")

    def test_a_name_that_is_not_pascal_case_is_refused(self):
        for name in ("yankee", "Yan kee", "../Yankee", "Yankee\n", ""):
            with self.subTest(name=name), self.assertRaisesRegex(ScaffoldError, "PascalCase"):
                remove_service.plan(self.root, name)


class ReferenceServiceTests(unittest.TestCase):
    """The six services the repository carries were rendered once and edited since."""

    def test_every_service_but_the_template_plans_against_the_real_tree(self):
        services = sorted(
            path.name for path in (REPO_ROOT / "src" / "Services").iterdir()
            if path.is_dir() and path.name != "Catalog")
        self.assertGreaterEqual(len(services), 6)
        for name in services:
            with self.subTest(service=name):
                rendered = remove_service.plan(REPO_ROOT, name)
                self.assertIn(f"src/Services/{name}", rendered.deleted)
                self.assertIn(f"deploy/compose/services/{name.lower()}.yml", rendered.deleted)
                self.assertIn("Platform.slnx", rendered.updated)
                self.assertNotIn(f'Services/{name}/', rendered.updated["Platform.slnx"])
                self.assertNotIn(f"services/{name.lower()}.yml", rendered.updated[new_service.COMPOSE_INDEX])


    def test_a_block_before_the_bff_does_not_take_the_bffs_with_it(self):
        """ENV_MARKER skips "The BFF's", and Ordering's block sits directly above it."""
        rendered = remove_service.plan(REPO_ROOT, "Ordering")

        env = rendered.updated["deploy/compose/.env.example"]

        self.assertNotIn("# Ordering's two", env)
        self.assertIn("# The BFF's two §7.1 keys", env)


class MentionTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)

    def write(self, relative: str, text: str) -> None:
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8", newline="")

    def test_mentions_are_files_outside_the_removal_with_their_lines(self):
        self.write("src/BuildingBlocks/Common.Contracts/Zulu/V1/Events.cs", "ok\nrecord ZuluMade();\n")
        self.write("src/Services/Zulu/Zulu.Domain/Order.cs", "class Zulu {}\n")
        self.write("docs/ch.md", "the zulu service\n")
        self.write("docs/clean.md", "nothing here\n")
        self.write("docs/bin-copy.md", "ZULU\n")
        self.write(".git/config", "Zulu\n")
        self.write("obj/x.txt", "Zulu\n")
        self.write(".claude/worktrees/other/docs/ch.md", "Zulu\n")
        self.write("docs/image.bin", "")
        (self.root / "docs" / "image.bin").write_bytes(b"\xff\xfeZulu")
        rendered = remove_service.Plan(deleted=["src/Services/Zulu"], updated={"docs/ch.md": "no mention\n"})

        found = remove_service.mentions(self.root, Names("Zulu"), rendered, [])

        self.assertEqual(
            found,
            {
                "docs/bin-copy.md": [1],
                "src/BuildingBlocks/Common.Contracts/Zulu/V1/Events.cs": [2],
            })

    def test_a_directory_sharing_the_prefix_is_not_the_removed_one(self):
        self.write("src/Services/Zulu2/Notes.md", "Zulu\n")
        rendered = remove_service.Plan(deleted=["src/Services/Zulu"])

        found = remove_service.mentions(self.root, Names("Zulu"), rendered, [])

        self.assertEqual(list(found), ["src/Services/Zulu2/Notes.md"])

    def test_a_file_that_cannot_be_read_is_reported_and_does_not_stop_the_search(self):
        self.write("docs/locked.md", "Zulu\n")
        self.write("docs/open.md", "Zulu\n")
        read_bytes = Path.read_bytes

        def refuse_one(path: Path) -> bytes:
            if path.name == "locked.md":
                raise PermissionError("locked")
            return read_bytes(path)

        unread: list[str] = []
        with mock.patch.object(Path, "read_bytes", refuse_one):
            found = remove_service.mentions(self.root, Names("Zulu"), remove_service.Plan(), unread)

        self.assertEqual(unread, ["docs/locked.md"])
        self.assertEqual(list(found), ["docs/open.md"])


class AllowListTests(unittest.TestCase):
    HEAD = "# covers: tests/\n\ntests/Catalog.Api.Tests/A.cs | rule | f00 | hand-written\n"

    def block(self, name: str, entries: list[str]) -> str:
        heading = f"# --- {name}, rendered by tools/new-service "
        return "\n".join(
            ["", "", heading + "-" * (76 - len(heading)), "#", "# a sentence", ""] + entries) + "\n"

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        (self.root / remove_service.SCAN_ALLOW_LIST).mkdir(parents=True)

    def write(self, text: str) -> None:
        (self.root / remove_service.SCAN_ALLOW_LIST / "tests.txt").write_text(text, encoding="utf-8", newline="")

    def edited(self, name: str) -> str | None:
        found = remove_service.without_allowed_entries(self.root, Names(name))
        return found.get(f"{remove_service.SCAN_ALLOW_LIST}/tests.txt")

    def test_the_last_block_goes_and_the_file_keeps_its_newline(self):
        yankee = self.block("Yankee", ["tests/Yankee.Api.Tests/B.cs | rule | f01 | r"])
        self.write(self.HEAD + yankee)

        self.assertEqual(self.edited("Yankee"), self.HEAD)

    def test_a_block_in_the_middle_goes_and_its_neighbours_stay(self):
        yankee = self.block("Yankee", ["tests/Yankee.Api.Tests/B.cs | rule | f01 | r"])
        zulu = self.block("Zulu", ["tests/Zulu.Api.Tests/C.cs | rule | f02 | r"])
        self.write(self.HEAD + yankee + zulu)

        self.assertEqual(self.edited("Yankee"), self.HEAD + zulu)

    def test_an_entry_written_by_hand_after_the_last_block_stays(self):
        yankee = self.block("Yankee", ["tests/Yankee.Api.Tests/B.cs | rule | f01 | r"])
        later = "\ntests/Other.Api.Tests/E.cs | rule | f04 | added by hand later\n"
        self.write(self.HEAD + yankee + later)

        self.assertEqual(self.edited("Yankee"), self.HEAD + later)

    def test_hand_written_entries_for_the_services_trees_go_and_others_stay(self):
        self.write(
            self.HEAD
            + "tests/Yankee.Api.Tests/B.cs | rule | f01 | hand\n"
            + "tests/Yankee2.Api.Tests/D.cs | rule | f03 | another service\n")

        self.assertEqual(
            self.edited("Yankee"),
            self.HEAD + "tests/Yankee2.Api.Tests/D.cs | rule | f03 | another service\n")

    def test_a_file_with_nothing_of_the_services_is_not_rewritten(self):
        self.write(self.HEAD)

        self.assertIsNone(self.edited("Yankee"))


class CommandLineTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls._directory = tempfile.TemporaryDirectory()
        cls.base = template_root(Path(cls._directory.name) / "base")
        render_into(cls.base, PROBE, SHAPES["api"])

    @classmethod
    def tearDownClass(cls):
        cls._directory.cleanup()

    def run_main(self, *argv: str) -> tuple[int, str, str]:
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = remove_service.main(list(argv))
        return code, out.getvalue(), err.getvalue()

    def copy(self) -> Path:
        root = Path(tempfile.mkdtemp(dir=self._directory.name)) / "root"
        shutil.copytree(self.base, root)
        self.addCleanup(shutil.rmtree, root.parent, ignore_errors=True)
        return root

    def test_a_dry_run_names_the_removal_and_changes_nothing(self):
        root = self.copy()
        before = manifest(root)

        code, out, _ = self.run_main(PROBE, "--dry-run", "--repo-root", str(root))

        self.assertEqual(code, 0)
        self.assertIn(f"would delete", out)
        self.assertIn(f"src/Services/{PROBE}", out)
        self.assertEqual(manifest(root), before)

    def test_a_run_removes_the_service_and_says_nothing_else_names_it(self):
        root = self.copy()

        code, out, err = self.run_main(PROBE, "--repo-root", str(root))

        self.assertEqual((code, err), (0, ""))
        self.assertFalse((root / "src" / "Services" / PROBE).exists())
        self.assertIn(f"Nothing else names {PROBE}.", out)

    def test_a_refusal_is_one_line_on_stderr_and_exit_one(self):
        code, out, err = self.run_main("Catalog", "--repo-root", str(self.base))

        self.assertEqual((code, out), (1, ""))
        self.assertRegex(err, r"^remove_service\.py: Catalog is the scaffold's template")


if __name__ == "__main__":
    unittest.main()
