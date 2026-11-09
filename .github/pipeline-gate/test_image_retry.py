"""image_retry.sh: one retry, a record of it, and the command's own exit status."""
from __future__ import annotations

import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

HELPER = Path(__file__).resolve().parent / "image_retry.sh"


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
    # Not a skip: a skip reports a pass for a helper nothing has run.
    if BASH is None:
        raise RuntimeError("bash is needed to run image_retry.sh")


class ImageRetryTests(unittest.TestCase):
    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.temp = Path(directory.name)
        self.record = self.temp / "image-retries.txt"
        self.calls = self.temp / "calls"

    def helper(self, *arguments: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [BASH, HELPER.as_posix(), *arguments],
            capture_output=True,
            text=True,
            encoding="utf-8",
            check=False,
            env={**os.environ, "RUNNER_TEMP": self.temp.as_posix(), "IMAGE_RETRY_WAIT_SECONDS": "0"},
        )

    def failing(self, times: int, code: int = 7) -> tuple[str, ...]:
        """A command that counts its calls and fails the first `times` of them."""
        script = (
            f'echo call >> "{self.calls.as_posix()}"; '
            f'[ "$(wc -l < "{self.calls.as_posix()}")" -gt {times} ] || exit {code}'
        )
        return (BASH, "-c", script)

    def call_count(self) -> int:
        return len(self.calls.read_text(encoding="utf-8").splitlines()) if self.calls.exists() else 0

    def test_a_step_that_passes_runs_once_and_notes_nothing(self) -> None:
        result = self.helper("run", "Build", *self.failing(0))

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.call_count(), 1)
        self.assertFalse(self.record.exists())

    def test_a_step_that_recovers_passes_and_is_noted(self) -> None:
        result = self.helper("run", "Sign", *self.failing(1))

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.call_count(), 2)
        self.assertEqual(self.record.read_text(encoding="utf-8"), "Sign\n")
        self.assertIn("::warning::", result.stdout)

    def test_the_retry_is_bounded_and_a_second_failure_fails_the_step(self) -> None:
        """The failure closes: what follows the step does not run on an unsigned image."""
        result = self.helper("run", "Sign", *self.failing(5, code=7))

        self.assertEqual(result.returncode, 7)
        self.assertEqual(self.call_count(), 2)

    def test_the_command_keeps_its_arguments_word_for_word(self) -> None:
        output = self.temp / "out"
        script = f'printf "%s|" "$@" > "{output.as_posix()}"'

        result = self.helper("run", "Build", BASH, "-c", script, "bash", "one word", "$HOME", "two  spaces")

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(output.read_text(encoding="utf-8"), "one word|$HOME|two  spaces|")

    def test_the_note_appends_for_a_step_an_action_runs(self) -> None:
        self.assertEqual(self.helper("note", "SBOM").returncode, 0)
        self.assertEqual(self.helper("note", "Provenance").returncode, 0)

        self.assertEqual(self.record.read_text(encoding="utf-8"), "SBOM\nProvenance\n")

    def test_a_malformed_call_runs_nothing(self) -> None:
        for arguments in ((), ("run",), ("run", "Build"), ("note",), ("note", "SBOM", "extra"), ("other", "SBOM")):
            with self.subTest(arguments=arguments):
                self.assertEqual(self.helper(*arguments).returncode, 2)
        self.assertFalse(self.record.exists())


if __name__ == "__main__":
    unittest.main()
