"""commit_builds.py and the workflow step that runs it: what skips a build, and what never does."""
from __future__ import annotations

import os
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

import commit_builds
import pipeline_gate

HERE = Path(__file__).resolve().parent
DECIDE_STEP = "      - name: Decide which commits build\n"
RUN_STEP = "      - name: Every commit before the head builds\n"


def find_bash() -> str | None:
    # Git for Windows' bash first: a bare `bash` on a Windows PATH can be WSL's.
    if sys.platform == "win32" and shutil.which("git"):
        top = Path(shutil.which("git")).resolve().parent.parent
        for candidate in (top / "bin" / "bash.exe", top.parent / "bin" / "bash.exe"):
            if candidate.is_file():
                return str(candidate)
    return shutil.which("bash")


BASH = find_bash()


def setUpModule() -> None:
    # Not a skip: a skip reports a pass for a step nothing has run.
    if BASH is None:
        raise RuntimeError("bash is needed to run the commits job's step")


def real_filters() -> dict[str, list[str]]:
    return pipeline_gate.read_filters_by_name(pipeline_gate.WORKFLOW.read_text(encoding="utf-8"))


def run_block(step: str) -> str:
    """The step's `run: |` from the real workflow, dedented, so the job itself is what runs."""
    text = pipeline_gate.WORKFLOW.read_text(encoding="utf-8")
    after = text.split(step, 1)[1].split("        run: |\n", 1)[1]
    lines = []
    for line in after.splitlines():
        if line.strip() and not line.startswith(" " * 10):
            break
        lines.append(line[10:])
    return "\n".join(lines) + "\n"


class ScratchRepository(unittest.TestCase):
    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.temp = Path(directory.name)
        self.repo = self.temp / "repo"
        self.repo.mkdir()
        self.previous = Path.cwd()
        os.chdir(self.repo)
        self.addCleanup(os.chdir, self.previous)
        self.git("init", "--quiet", "--initial-branch=main")
        self.git("config", "user.email", "ci@example.invalid")
        self.git("config", "user.name", "CI")
        self.git("config", "core.autocrlf", "false")
        self.base = self.commit({"src/App/App.cs": "class App {}\n", "docs/guide.md": "guide\n"})

    def git(self, *arguments: str) -> str:
        return subprocess.run(
            ["git", *arguments], capture_output=True, text=True, encoding="utf-8", check=True
        ).stdout.strip()

    def commit(self, files: dict[str, str]) -> str:
        for name, content in files.items():
            path = self.repo / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content, encoding="utf-8", newline="\n")
        self.git("add", "--all")
        self.git("commit", "--quiet", "--allow-empty", "--message", "a commit")
        return self.git("rev-parse", "HEAD")


class VerdictTests(ScratchRepository):
    def verdict_of(self, files: dict[str, str]) -> str:
        return commit_builds.verdict(self.commit(files), real_filters()).split()[0]

    def test_a_commit_touching_only_what_no_stage_reads_is_skipped(self) -> None:
        for path in (
            "docs/guide.md",
            ".claude/x.py",
            ".harness/config.json",
            ".github/locality-gate/x.py",
            "CLAUDE.md",
        ):
            with self.subTest(path=path):
                self.assertEqual(self.verdict_of({path: f"changed {path}\n"}), "skip")

    def test_a_commit_touching_what_the_build_reads_is_built(self) -> None:
        for path in (
            "src/App/App.cs",
            "tests/App.Tests/AppTests.cs",
            "tools/bff-replay/Program.cs",
            "deploy/compose/services/shipping.yml",
            "Directory.Build.props",
            "Directory.Packages.props",
            "Platform.slnx",
            "global.json",
            ".editorconfig",
            ".config/dotnet-tools.json",
            "coverage.runsettings",
        ):
            with self.subTest(path=path):
                self.assertEqual(self.verdict_of({path: f"changed {path}\n"}), "build")

    def test_a_path_nobody_listed_is_built(self) -> None:
        for path in ("staging/new-thing.txt", "Directory.Build.targets", "nuget.config"):
            with self.subTest(path=path):
                self.assertEqual(self.verdict_of({path: "new\n"}), "build")

    def test_what_a_stage_reads_under_an_excluded_tree_is_built(self) -> None:
        for path in (
            "docs/personal-data.md",
            "docs/backend-architecture/13-observability.md",
            ".github/workflows/ci.yml",
            ".github/pipeline-gate/pipeline_gate.py",
            ".github/output-gate/output_gate.py",
            ".github/coverage/domain_coverage.py",
        ):
            with self.subTest(path=path):
                self.assertEqual(self.verdict_of({path: f"changed {path}\n"}), "build")

    def test_a_markdown_file_only_skips_at_the_root(self) -> None:
        self.assertEqual(self.verdict_of({"src/App/README.md": "read me\n"}), "build")
        self.assertEqual(self.verdict_of({"deploy/compose/README.md": "seeded products\n"}), "build")

    def test_one_built_path_beside_any_number_of_skipped_ones_builds(self) -> None:
        self.assertEqual(self.verdict_of({"docs/a.md": "a\n", ".claude/b.py": "b\n", "src/App/C.cs": "c\n"}), "build")

    def test_a_move_out_of_a_built_tree_is_built(self) -> None:
        self.commit({"src/App/Notes.md": "same content, long enough for a rename to be detected\n"})
        self.git("mv", "src/App/Notes.md", "docs/notes.md")
        self.git("commit", "--quiet", "--message", "move")
        self.assertEqual(commit_builds.verdict(self.git("rev-parse", "HEAD"), real_filters()).split()[0], "build")

    def test_a_merge_is_built(self) -> None:
        self.git("switch", "--quiet", "--create", "side")
        self.commit({"docs/side.md": "side\n"})
        self.git("switch", "--quiet", "main")
        self.commit({"docs/main.md": "main\n"})
        self.git("merge", "--quiet", "--no-edit", "side")
        self.assertEqual(commit_builds.verdict(self.git("rev-parse", "HEAD"), real_filters()).split()[0], "build")

    def test_an_empty_diff_is_built(self) -> None:
        self.assertEqual(self.verdict_of({}), "build")

    def test_a_root_commit_is_built(self) -> None:
        root = self.git("rev-list", "--max-parents=0", "HEAD")
        self.assertEqual(commit_builds.verdict(root, real_filters()).split()[0], "build")

    def test_a_path_git_would_quote_is_read_as_itself(self) -> None:
        self.assertEqual(self.verdict_of({"docs/Übersicht.md": "doc\n"}), "skip")
        self.assertEqual(self.verdict_of({"src/App/Übersicht.cs": "code\n"}), "build")

    def test_an_unreadable_commit_raises_rather_than_reading_as_a_skip(self) -> None:
        with self.assertRaises(subprocess.CalledProcessError):
            commit_builds.verdict("0" * 40, real_filters())


class MainTests(ScratchRepository):
    def test_the_head_is_not_listed_and_the_rest_come_oldest_first(self) -> None:
        first = self.commit({"docs/a.md": "a\n"})
        second = self.commit({"src/App/B.cs": "b\n"})
        head = self.commit({"src/App/C.cs": "c\n"})

        script = subprocess.run(
            [sys.executable, str(HERE / "commit_builds.py"), self.base, head],
            capture_output=True,
            text=True,
            encoding="utf-8",
            check=True,
        ).stdout.splitlines()

        self.assertEqual([line.split()[:2] for line in script], [["skip", first], ["build", second]])

    def test_a_workflow_with_no_filters_fails_rather_than_building_nothing(self) -> None:
        # One commit, so nothing past the guard would read the filters and fail on its own.
        head = self.commit({"docs/a.md": "a\n"})
        gate = self.repo / ".github" / "pipeline-gate"
        gate.mkdir(parents=True)
        shutil.copy(HERE / "commit_builds.py", gate)
        shutil.copy(HERE / "pipeline_gate.py", gate)
        workflows = self.repo / ".github" / "workflows"
        workflows.mkdir(parents=True)
        text = pipeline_gate.WORKFLOW.read_text(encoding="utf-8")
        self.assertIn("            code:\n", text)
        (workflows / "ci.yml").write_text(text.replace("            code:\n", "            renamed:\n"), encoding="utf-8")

        result = subprocess.run(
            [sys.executable, str(gate / "commit_builds.py"), self.base, head],
            capture_output=True,
            text=True,
            encoding="utf-8",
            check=False,
        )

        self.assertEqual(result.returncode, 1)
        self.assertIn("found no `code`", result.stderr)
        self.assertEqual(result.stdout, "")


class WorkflowStepTests(ScratchRepository):
    """The real step's text, with `dotnet` stubbed, in a repository with real commits."""

    def setUp(self) -> None:
        super().setUp()
        self.stubs = self.temp / "stubs"
        self.stubs.mkdir()
        self.calls = self.temp / "dotnet-calls"
        self.stub("dotnet", f'#!/bin/sh\necho "$(git rev-parse HEAD) $1" >> "{self.calls.as_posix()}"\n')
        self.stub("python", f'#!/bin/sh\nexec "{Path(sys.executable).as_posix()}" "$@"\n')

    def stub(self, name: str, body: str) -> None:
        path = self.stubs / name
        path.write_text(body, encoding="utf-8", newline="\n")
        path.chmod(path.stat().st_mode | stat.S_IEXEC)

    def run_step(self, base: str, head: str) -> subprocess.CompletedProcess[str]:
        # Untracked, as a checkout of the merge ref holds what older commits lack:
        # the loop's clean takes these away, so a read after it finds nothing.
        gate = self.repo / ".github" / "pipeline-gate"
        gate.mkdir(parents=True, exist_ok=True)
        shutil.copy(HERE / "commit_builds.py", gate)
        shutil.copy(HERE / "pipeline_gate.py", gate)
        workflows = self.repo / ".github" / "workflows"
        workflows.mkdir(parents=True, exist_ok=True)
        shutil.copy(pipeline_gate.WORKFLOW, workflows)
        env = {
            **os.environ,
            "PATH": f"{self.stubs}{os.pathsep}{os.environ['PATH']}",
            "BASE_SHA": base,
            "HEAD_SHA": head,
            "RUNNER_TEMP": self.temp.as_posix(),
            "GITHUB_OUTPUT": self.output.as_posix(),
        }
        # The two steps in order, as the job runs them; the second runs only if the first passed.
        for step in (DECIDE_STEP, RUN_STEP):
            script = self.temp / "step.sh"
            script.write_text(run_block(step), encoding="utf-8", newline="\n")
            result = subprocess.run(
                [BASH, script.as_posix()], capture_output=True, text=True, encoding="utf-8", check=False, env=env)
            if result.returncode:
                break
        return result

    @property
    def output(self) -> Path:
        return self.temp / "github-output"

    def sdks(self) -> list[str]:
        """The SDK versions the decide step handed setup-dotnet."""
        text = self.output.read_text(encoding="utf-8")
        return text.split("sdks<<EOF\n", 1)[1].split("EOF\n", 1)[0].splitlines()

    def built(self) -> list[str]:
        return self.calls.read_text(encoding="utf-8").splitlines() if self.calls.exists() else []

    def test_a_docs_commit_is_skipped_by_name_and_a_source_commit_is_built(self) -> None:
        docs = self.commit({"docs/a.md": "a\n"})
        source = self.commit({"src/App/B.cs": "b\n"})
        props = self.commit({"Directory.Build.props": "<Project />\n"})
        head = self.commit({"docs/c.md": "c\n"})

        result = self.run_step(self.base, head)

        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(
            self.built(),
            [f"{source} restore", f"{source} build", f"{props} restore", f"{props} build"],
        )
        self.assertIn("3 commit(s) besides the head", result.stdout)
        self.assertIn(f"skip {docs} ", result.stdout)
        self.assertIn(f"build {source} ", result.stdout)

    def test_a_build_after_a_skip_finds_the_tree_it_asked_for(self) -> None:
        self.commit({"docs/a.md": "a\n"})
        source = self.commit({"src/App/B.cs": "b\n"})
        head = self.commit({".claude/c.py": "c\n"})

        result = self.run_step(self.base, head)

        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.built(), [f"{source} restore", f"{source} build"])

    def test_each_built_commit_s_pinned_sdk_is_installed_once(self) -> None:
        """An earlier commit can pin another SDK under rollForward disable, and fails on its absence."""
        self.commit({"global.json": '{"sdk": {"version": "10.0.100", "rollForward": "disable"}}\n'})
        self.commit({"src/App/B.cs": "b\n"})
        self.commit({"docs/a.md": "a\n", "global.json": '{"sdk": {"version": "10.0.302"}}\n'})
        self.commit({"src/App/C.cs": "c\n"})
        head = self.commit({"global.json": '{"sdk": {"version": "10.0.400"}}\n'})

        result = self.run_step(self.base, head)

        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.sdks(), ["10.0.100", "10.0.302"])

    def test_a_one_commit_pull_request_builds_nothing_and_passes(self) -> None:
        head = self.commit({"src/App/B.cs": "b\n"})

        result = self.run_step(self.base, head)

        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.built(), [])
        self.assertIn("0 commit(s) besides the head", result.stdout)

    def test_a_failed_classifier_fails_the_step_and_is_not_read_as_no_commits(self) -> None:
        head = self.commit({"docs/a.md": "a\n"})

        result = self.run_step("0" * 40, head)

        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn("commit(s) besides the head", result.stdout)


if __name__ == "__main__":
    unittest.main()
