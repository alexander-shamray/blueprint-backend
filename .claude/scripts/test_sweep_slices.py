"""sweep-slices.sh and sweep-mark.sh: the partition, the budget and the ref.

Small repositories are cut by a helper copy reading a row file of the case's
own; this checkout's tree by its own, so that case runs in every target.
"""

import os
import re
import shutil
import tempfile
import unittest
from pathlib import Path

from review_helpers import SCRIPTS, setUpModule, run_bash, code_lines

SLICES = SCRIPTS / "sweep-slices.sh"
MARK = SCRIPTS / "sweep-mark.sh"
DETACH = SCRIPTS / "git-worktree-detach.sh"
DROP = SCRIPTS / "git-worktree-drop.sh"
REPO = str(SCRIPTS.parent.parent)
GIT_ID = "-c user.name=t -c user.email=t@example.invalid"


def git(repo, args):
    result = run_bash(f'cd "$R" && git {GIT_ID} {args}', R=repo)
    assert result.returncode == 0, result.stderr
    return result.stdout.strip()


class Sweep:
    """A detached sweep worktree of `repo`, dropped with its slices afterwards;
    under `root` as its temp root when one is given, cut by `script`, the
    checkout's own helper when none is."""

    def __init__(self, case, repo, root=None, script=SLICES):
        self.repo = repo
        self.script = str(script)
        self.env = {"TMPDIR": root} if root else {}
        sha = git(repo, "rev-parse HEAD")
        made = run_bash(
            'cd "$R" && bash "$DETACH" "$SHA" 2>/dev/null', R=repo, DETACH=str(DETACH), SHA=sha, **self.env
        )
        case.assertEqual(0, made.returncode, made.stderr)
        self.path = made.stdout.strip()
        case.addCleanup(
            lambda: run_bash(
                'cd "$R" && bash "$DROP" "$W" >/dev/null 2>&1; rm -rf "$W.slices"',
                R=repo, DROP=str(DROP), W=self.path, **self.env,
            )
        )

    def slices(self, kind, *extra, **env):
        return run_bash(
            'cd "$R" && bash "$SLICES" "$KIND" "$W" $EXTRA',
            R=self.repo, SLICES=self.script, KIND=kind, W=self.path, EXTRA=" ".join(extra),
            **self.env, **env,
        )


def parse(stdout):
    head, slices = {}, []
    for line in stdout.splitlines():
        word, _, rest = line.partition(" ")
        if word == "slice":
            n, row, files, size, listing = rest.split(" ")
            slices.append((row, int(files), int(size), listing))
        elif word == "tracked":
            words = line.split(" ")
            head["counts"] = {k: int(v) for k, v in zip(words[::2], words[1::2])}
        else:
            head[word] = rest
    return head, slices


def listed(slices):
    """The listed paths, read by bash: the helper prints its own spelling, which
    under MSYS is a POSIX path Python resolves to another directory."""
    paths = []
    for *_, listing in slices:
        read = run_bash('cat -- "$P"', P=listing)
        assert read.returncode == 0, read.stderr
        paths += read.stdout.splitlines()
    return paths


# Rows of backend's shape, and one file in every row both sweeps own, so a small
# repository partitions the way a target does; a clone of one checks out every
# file per test.
SMALL_ROWS = (
    "# a comment line\n"
    "bug\t^src/BuildingBlocks/\tbuilding-blocks\tthe building blocks\n"
    "bug\t^src/\tservices\tthe services\n"
    "bug\t^tests/\tsuites\tthe suites\n"
    "security\t^(src|tests)/\tsource\tthe source\n"
    "*\t^(tools|\\.github|\\.claude|\\.harness)/\ttooling\tthe tooling\n"
    "*\t^(deploy|\\.config)/\tdeploy\tdeployment\n"
    "*\t^docs/\tsamples\tfenced code\n"
    "*\t^.*/.*$\t-\ta directory no row names\n"
    "*\t^[^/]+$\tdeploy\tthe root\n"
)
SMALL_TREE = {"src/BuildingBlocks/a.cs": "x\n", "src/Services/b.cs": "x\n", "tests/c.cs": "x\n",
              "tools/d.py": "x\n", ".github/e.yml": "x\n", ".claude/f.md": "x\n", "deploy/g.yaml": "x\n",
              ".config/h.json": "x\n", "README.md": "x\n", "docs/i.md": "```sh\nx\n```\n"}


class ASmallRepository(unittest.TestCase):
    """A small repository of its own, so a commit and a ref never touch this one;
    its sweeps under a temp root beside it, so a run stopped before its cleanups
    leaves one directory behind, not a worktree git can no longer reach."""

    def setUp(self):
        # Not rmtree with ignore_errors: git's objects are read-only, which
        # Windows refuses to delete, and the flag hides each one that stays.
        holder = tempfile.TemporaryDirectory(prefix="sweepslices-")
        self.addCleanup(holder.cleanup)
        top = holder.name
        self.repo, self.root = str(Path(top, "repo")), str(Path(top, "root"))
        os.mkdir(self.repo)
        os.mkdir(self.root)
        made = run_bash('cd "$R" && git init -q --initial-branch=main .', R=self.repo)
        self.assertEqual(0, made.returncode, made.stderr)
        for relative, text in SMALL_TREE.items():
            target = Path(self.repo, relative)
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(text)
        git(self.repo, "add -A")
        git(self.repo, 'commit -q -m "the small tree"')
        self.script = self.helper(SMALL_ROWS)

    def helper(self, rows):
        """A copy of the helper in a checkout of its own whose row file is `rows`,
        its bytes as given, or absent for None."""
        checkout = Path(tempfile.mkdtemp(prefix="checkout-", dir=Path(self.repo).parent))
        scripts = checkout / ".claude" / "scripts"
        scripts.mkdir(parents=True)
        shutil.copyfile(SLICES, scripts / SLICES.name)
        if rows is not None:
            (checkout / ".harness").mkdir()
            (checkout / ".harness" / "sweep-rows.tsv").write_bytes(rows.encode("utf-8"))
        return scripts / SLICES.name

    def sweep(self, script=None):
        return Sweep(self, self.repo, self.root, script or self.script)

    def commit(self, relative, text):
        # Bytes, so a sample holds exactly the line ends and characters it names,
        # and no autocrlf, which Git for Windows sets and which stores CRLF as LF.
        target = Path(self.repo, relative)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(text.encode("utf-8"))
        git(self.repo, f'-c core.autocrlf=false add -- "{relative}"')
        git(self.repo, f'commit -q -m "add {relative}"')
        return git(self.repo, "rev-parse HEAD")

    def mark(self, kind, sha):
        return run_bash(
            'cd "$R" && bash "$MARK" "$KIND" "$SHA"', R=self.repo, MARK=str(MARK), KIND=kind, SHA=sha
        )

    def test_a_top_level_directory_no_row_owns_refuses_the_run(self):
        self.commit("newtree/x.txt", "x\n")
        result = self.sweep().slices("bug")
        self.assertEqual(3, result.returncode)
        self.assertIn("no row owns: newtree/x.txt", result.stderr)

    def test_a_row_comes_from_the_file(self):
        # The positive control of the case above: the same tree passes once a
        # line of the checkout's row file names its directory.
        self.commit("newtree/x.txt", "x\n")
        script = self.helper("*\t^newtree/\textra\ta tree of the case's own\n" + SMALL_ROWS)
        result = self.sweep(script).slices("bug")
        self.assertEqual(0, result.returncode, result.stderr)
        rows = {row for row, *_ in parse(result.stdout)[1]}
        self.assertIn("extra", rows)
        self.assertIn("newtree/x.txt", listed([s for s in parse(result.stdout)[1] if s[0] == "extra"]))

    def test_a_missing_row_file_exits_3(self):
        result = self.sweep(self.helper(None)).slices("bug")
        self.assertEqual(3, result.returncode)
        self.assertIn(".harness/sweep-rows.tsv", result.stderr)
        self.assertEqual("", result.stdout)

    def test_a_malformed_line_exits_3_naming_it(self):
        for name, line in (
            ("three fields", "*\t^x/\tx"),
            ("five fields", "*\t^x/\tx\tholds\textra"),
            ("an unknown kind", "both\t^x/\tx\tholds"),
            ("an empty pattern", "*\t\tx\tholds"),
            ("a row that is not a word", "*\t^x/\tRow\tholds"),
            ("an empty holds", "*\t^x/\tx\t"),
            ("a pattern awk cannot compile", "*\t^x/(\tx\tholds"),
        ):
            with self.subTest(line=name):
                result = self.sweep(self.helper(SMALL_ROWS + line + "\n")).slices("bug")
                self.assertEqual(3, result.returncode, result.stderr)
                self.assertIn("sweep-rows.tsv", result.stderr)
                self.assertEqual("", result.stdout)
                if name != "a pattern awk cannot compile":
                    self.assertIn("line 11", result.stderr)

    def test_the_first_matching_row_wins(self):
        script = self.helper("*\t^tools/d\\.py$\tfirst\tone file\n" + SMALL_ROWS)
        self.commit("tools/e.py", "x\n")
        result = self.sweep(script).slices("bug")
        self.assertEqual(0, result.returncode, result.stderr)
        slices = parse(result.stdout)[1]
        self.assertEqual(["tools/d.py"], listed([s for s in slices if s[0] == "first"]))
        self.assertIn("tools/e.py", listed([s for s in slices if s[0] == "tooling"]))
        self.assertNotIn("tools/d.py", listed([s for s in slices if s[0] == "tooling"]))

    def test_a_crlf_row_file_reads_as_its_rows(self):
        # Git for Windows checks a text file out with CRLF under autocrlf.
        result = self.sweep(self.helper(SMALL_ROWS.replace("\n", "\r\n"))).slices("bug")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("tools/d.py", listed(parse(result.stdout)[1]))

    def test_the_rows_are_the_running_checkouts_not_the_swept_trees(self):
        # One row file per run, the running checkout's, as the helper is: a
        # copy in the swept tree is never a second source of rows.
        self.commit(".harness/sweep-rows.tsv", "*\t^.*$\tsamples\teverything\n")
        result = self.sweep().slices("bug")
        self.assertEqual(0, result.returncode, result.stderr)
        tooling = listed([s for s in parse(result.stdout)[1] if s[0] == "tooling"])
        self.assertIn("tools/d.py", tooling)

    def test_the_swept_trees_attributes_do_not_hide_a_file(self):
        # `grep -I` read the swept tree's attributes, so a line there marked a
        # chosen script binary and every slice dropped it.
        self.commit(".gitattributes", "tools/hidden.sh -diff\ntools/also.sh binary\n")
        self.commit("tools/hidden.sh", "rm -rf ~\n")
        self.commit("tools/also.sh", "rm -rf ~\n")
        result = self.sweep().slices("bug")
        self.assertEqual(0, result.returncode, result.stderr)
        head, slices = parse(result.stdout)
        self.assertIn("tools/hidden.sh", listed(slices))
        self.assertIn("tools/also.sh", listed(slices))
        self.assertEqual(0, head["counts"]["binary"])
        self.assertNotIn("binary", head)

    def test_a_binary_file_is_named_in_a_list_the_plan_points_at(self):
        # A NUL byte still makes a file binary, and a script can hold one, so
        # what no auditor reads is named rather than only counted.
        self.commit("tools/z.sh", "echo\0\nrm -rf ~\n")
        result = self.sweep().slices("bug")
        self.assertEqual(0, result.returncode, result.stderr)
        head, slices = parse(result.stdout)
        self.assertEqual(1, head["counts"]["binary"])
        count, listing = head["binary"].split(" ")
        self.assertEqual("1", count)
        read = run_bash('cat -- "$P"', P=listing)
        self.assertEqual(["tools/z.sh"], read.stdout.splitlines(), read.stderr)
        self.assertNotIn("tools/z.sh", listed(slices))

    def test_a_marked_ancestor_narrows_round_one_to_the_change(self):
        base = git(self.repo, "rev-parse HEAD")
        self.assertEqual(0, self.mark("bug", base).returncode)
        self.commit("tools/added.py", "print(1)\n")
        result = self.sweep().slices("bug")
        self.assertEqual(0, result.returncode, result.stderr)
        head, slices = parse(result.stdout)
        self.assertEqual(f"since {base}", head["mode"])
        self.assertEqual(["tools/added.py"], listed(slices))

    def test_a_sample_counts_its_fences_and_a_doc_without_one_is_counted_not_listed(self):
        self.assertEqual(0, self.mark("bug", git(self.repo, "rev-parse HEAD")).returncode)
        body = "```cs\nvar x = 1;\n```\n"
        nested = "````md\n```\ninner\n```\n````\nprose after\n"
        self.commit("docs/fenced.md", "# t\n" + body)
        self.commit("docs/nested.md", nested)
        self.commit("docs/plain.md", "no code here\n")
        result = self.sweep().slices("bug")
        self.assertEqual(0, result.returncode, result.stderr)
        head, slices = parse(result.stdout)
        self.assertEqual(["docs/fenced.md", "docs/nested.md"], sorted(listed(slices)))
        self.assertEqual(1, head["counts"]["fenceless"])
        # The fenced line alone; the nested fence's body is its three inner lines.
        self.assertEqual(len("var x = 1;\n") + len("```\ninner\n```\n"), sum(s[2] for s in slices))

    def test_a_sample_counts_bytes_and_one_without_a_last_newline_ends_where_it_should(self):
        # Read as one stream, so a blob's end is a byte count: a miscount there
        # runs one sample into the next, a character count undercounts é, and
        # an awk that drops a CR undercounts every CRLF line.
        self.assertEqual(0, self.mark("bug", git(self.repo, "rev-parse HEAD")).returncode)
        self.commit("docs/a.md", "```\né\n```")
        self.commit("docs/b.md", "```\r\nab\r\n```\r\n")
        self.commit("docs/c.md", "```\ncd\n```\n")
        result = self.sweep().slices("bug")
        self.assertEqual(0, result.returncode, result.stderr)
        head, slices = parse(result.stdout)
        self.assertEqual(["docs/a.md", "docs/b.md", "docs/c.md"], sorted(listed(slices)))
        self.assertEqual(len("é\n".encode("utf-8")) + len("ab\r\n") + len("cd\n"),
                         sum(s[2] for s in slices))

    def test_every_sample_is_read_through_one_cat_file(self):
        # A pipeline per sample is a fork per file, most of a run under MSYS;
        # the count is the subject, since a timing passes on a fast host.
        self.assertEqual(0, self.mark("bug", git(self.repo, "rev-parse HEAD")).returncode)
        for k in range(5):
            self.commit(f"docs/s{k}.md", "```sh\nx\n```\n")
        handle, trace = tempfile.mkstemp(prefix="sweepslices-trace-")
        os.close(handle)
        self.addCleanup(os.remove, trace)
        result = self.sweep().slices("bug", GIT_TRACE=trace)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(5, len(listed(parse(result.stdout)[1])))
        calls = re.findall(r"trace: built-in: git cat-file", Path(trace).read_text(encoding="utf-8"))
        self.assertEqual(1, len(calls))

    def test_full_and_a_non_ancestor_ref_both_read_everything(self):
        self.commit("tools/added.py", "print(1)\n")
        tip = git(self.repo, "rev-parse HEAD")
        sweep = self.sweep()
        self.assertEqual(0, self.mark("bug", tip).returncode)
        result = sweep.slices("bug", "full")
        self.assertEqual("full", parse(result.stdout)[0]["mode"])
        # A side commit is no ancestor of the swept one.
        git(self.repo, "checkout -q --detach HEAD~1")
        side = self.commit("tools/side.py", "print(2)\n")
        self.assertEqual(0, self.mark("bug", side).returncode)
        run_bash('rm -rf "$W.slices"', W=sweep.path)
        result = sweep.slices("bug")
        self.assertEqual("full", parse(result.stdout)[0]["mode"], result.stderr)

    def test_mark_takes_a_closed_kind_and_a_full_existing_commit(self):
        sha = git(self.repo, "rev-parse HEAD")
        for kind, commit, code in (
            ("other", sha, 2), ("bug", sha[:12], 2), ("bug", "0" * 40, 3), ("bug", "HEAD", 2)
        ):
            with self.subTest(kind=kind, commit=commit):
                self.assertEqual(code, self.mark(kind, commit).returncode)
        self.assertEqual(0, self.mark("security", sha).returncode)
        self.assertEqual(sha, git(self.repo, "rev-parse refs/sweeps/security"))

    def test_the_slices_go_with_the_worktree(self):
        sweep = self.sweep()
        self.assertEqual(0, sweep.slices("security").returncode)
        dropped = run_bash(
            'cd "$R" && bash "$DROP" "$W" && [ ! -e "$W.slices" ] && echo gone',
            R=self.repo, DROP=str(DROP), W=sweep.path, TMPDIR=self.root,
        )
        self.assertEqual("gone", dropped.stdout.strip(), dropped.stderr)


class TheCheckout(unittest.TestCase):
    """This checkout's own row file partitions its own tree, for each kind."""

    def test_the_checkout_partitions_with_no_unowned_path(self):
        for kind in ("bug", "security"):
            with self.subTest(kind=kind):
                result = Sweep(self, REPO).slices(kind, "full")
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertNotIn("no row owns", result.stderr)
                head, slices = parse(result.stdout)
                self.assertEqual("full", head["mode"])
                self.assertGreater(len(slices), 0)


class TheSliceLoop(unittest.TestCase):
    """The loop over the plan's lines starts no process of its own."""

    def test_the_loop_holds_no_command_substitution(self):
        # Under MSYS each `$(…)` is an emulated fork, and one per listed file
        # is most of a run over a real tree; the loop is read, since a fork is
        # invisible in the output. The rows are an awk pass, which forks once.
        source = SLICES.read_text(encoding="utf-8")
        start = source.index("while IFS=$'\\t' read -r row size file; do")
        body = "\n".join(code_lines(source[start:source.index("\ndone", start)]))
        self.assertIn("flush", body, "the loop pattern stopped matching")
        self.assertNotRegex(body, r"\$\((?!\()", "a command substitution; `$((` is arithmetic")
        self.assertNotIn("`", body)


class TheCommandsReadTheRowFile(unittest.TestCase):
    """The rows are the checkout's, so no command restates them as a table."""

    def test_each_command_names_the_row_file_and_holds_no_row_table(self):
        for kind in ("bug", "security"):
            with self.subTest(kind=kind):
                text = (SCRIPTS.parent / "commands" / f"{kind}-sweep.md").read_text(encoding="utf-8")
                self.assertIn(".harness/sweep-rows.tsv", text)
                self.assertEqual([], re.findall(r"^\s*\| `[a-z-]+` \|", text, re.MULTILINE))


class Arguments(unittest.TestCase):
    def test_a_path_not_shaped_like_a_sweep_worktree_is_refused(self):
        result = run_bash('bash "$SLICES" bug "$P"', SLICES=str(SLICES), P=REPO)
        self.assertEqual(2, result.returncode)

    def test_kind_and_mode_are_closed_sets(self):
        for args in ("other /tmp", "bug /tmp since", "bug"):
            with self.subTest(args=args):
                result = run_bash(f'bash "$SLICES" {args}', SLICES=str(SLICES))
                self.assertEqual(2, result.returncode)


if __name__ == "__main__":
    unittest.main()
