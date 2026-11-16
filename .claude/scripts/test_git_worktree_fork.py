"""git-worktree-fork.sh: the one worktree shape /branch step 5 creates.

The shared harness and the reason it shells out rather than
re-implementing are `review_helpers.py`'s.
"""

import json
import os
import shutil
import unittest
from pathlib import Path

from review_helpers import (
    SCRIPTS,
    code_lines,
    setUpModule,
    temp_root,
    run_bash,
)


FORK = SCRIPTS / "git-worktree-fork.sh"

# A repository with an origin/main to fork from, made by bash so the paths are
# the ones the script sees. `IGNORE` decides whether .gitignore names the
# worktree directory, which is the check the ignored/unignored cases turn on.
FIXTURE = """
set -e
git init -q -b main "$root/origin"
git -C "$root/origin" -c user.name=t -c user.email=t@t commit -q --allow-empty -m base
if [ "$HOOK" = yes ]; then
  mkdir -p "$root/origin/.claude/hooks"
  printf 'import os\\nopen("hook-ran", "w").write(os.getcwd())\\n' > "$root/origin/.claude/hooks/refresh-index.py"
  git -C "$root/origin" add .claude
  git -C "$root/origin" -c user.name=t -c user.email=t@t commit -q -m hook
fi
git clone -q "$root/origin" "$root/checkout"
# Committed after the clone, so only origin/main carries the dangling link. Its
# target is the native spelling, which a Windows link resolves where bash does.
if [ "$LINK" = yes ]; then
  native=$(cd "$root" && { pwd -W 2>/dev/null || pwd; })
  blob=$(printf %s "$native/outside.json" | git -C "$root/origin" hash-object -w --stdin)
  git -C "$root/origin" update-index --add --cacheinfo "120000,$blob,.claude/settings.local.json"
  git -C "$root/origin" -c user.name=t -c user.email=t@t commit -q -m link
  git -C "$root/checkout" fetch -q origin
fi
if [ "$IGNORE" = yes ]; then printf '.claude/worktrees/\\n' > "$root/checkout/.gitignore"; fi
mkdir -p "$root/checkout/src"
"""


class ForkShape(unittest.TestCase):
    """The helper forks `.claude/worktrees/<name>` from the main checkout only."""

    def fixture(self, ignore="yes", hook="no", link="no"):
        root = temp_root(self)
        made = run_bash(FIXTURE, root=root, IGNORE=ignore, HOOK=hook, LINK=link)
        self.assertEqual(0, made.returncode, made.stderr)
        return root

    def fork(self, where, path, branch="feat/probe"):
        return run_bash('cd "$WHERE" && bash "$FORK" "$P" "$B"',
                        WHERE=where, FORK=str(FORK), P=path, B=branch)

    def local_settings(self, root, text):
        made = run_bash('mkdir -p "$C/.claude" && printf %s "$T" > "$C/.claude/settings.local.json"',
                        C=f"{root}/checkout", T=text)
        self.assertEqual(0, made.returncode, made.stderr)

    def probe_settings(self, root):
        return run_bash('cat "$W/.claude/settings.local.json"',
                        W=f"{root}/checkout/.claude/worktrees/probe")

    def checkout_file(self, root, name, text):
        made = run_bash('printf %s "$T" > "$C/$N"', C=f"{root}/checkout", N=name, T=text)
        self.assertEqual(0, made.returncode, made.stderr)

    def test_the_documented_shape_forks_with_no_upstream(self):
        root = self.fixture()
        result = self.fork(f"{root}/checkout", ".claude/worktrees/probe")
        self.assertEqual(0, result.returncode, result.stderr)
        upstream = run_bash(
            'git -C "$C" rev-parse --abbrev-ref feat/probe@{upstream}',
            C=f"{root}/checkout")
        self.assertNotEqual(0, upstream.returncode)
        # The worktree sits inside the checkout, and ignored is what keeps it
        # out of every `git status` the chain reads.
        status = run_bash('git -C "$C" status --porcelain', C=f"{root}/checkout")
        self.assertNotIn(".claude", status.stdout)

    def test_no_auto_setup_setting_gives_the_fork_an_upstream(self):
        # The start point is an oid, which names no branch to track, so the
        # no-upstream row of /pr holds whatever the machine's git says.
        for setting in ("always", "inherit"):
            with self.subTest(setting=setting):
                root = self.fixture()
                configured = run_bash('git -C "$C" config branch.autoSetupMerge "$S"',
                                      C=f"{root}/checkout", S=setting)
                self.assertEqual(0, configured.returncode, configured.stderr)
                result = self.fork(f"{root}/checkout", ".claude/worktrees/probe")
                self.assertEqual(0, result.returncode, result.stderr)
                upstream = run_bash(
                    'git -C "$C" rev-parse --abbrev-ref feat/probe@{upstream}',
                    C=f"{root}/checkout")
                self.assertNotEqual(0, upstream.returncode, upstream.stdout)

    def test_the_new_worktree_runs_its_own_index_refresh(self):
        # /branch enters the worktree mid-session, where no `SessionStart`
        # fires, so the fork starts the refresh that hook would have. The stub
        # is committed on origin/main, so the copy that runs is the worktree's.
        root = self.fixture(hook="yes")
        result = self.fork(f"{root}/checkout", ".claude/worktrees/probe")
        self.assertEqual(0, result.returncode, result.stderr)
        ran = run_bash(
            'for _ in $(seq 300); do '
            '[ -s "$W/hook-ran" ] && exec cat "$W/hook-ran"; sleep 0.1; '
            'done; exit 1',
            W=f"{root}/checkout/.claude/worktrees/probe")
        self.assertEqual(0, ran.returncode, "the fork never ran the refresh")
        self.assertTrue(
            ran.stdout.strip().replace("\\", "/").endswith("/.claude/worktrees/probe"),
            ran.stdout)

    def test_the_mcp_approval_crosses_and_no_permission_does(self):
        # A session started in the worktree reads that directory's local
        # settings, so the main checkout's approval is carried there alone.
        root = self.fixture()
        self.local_settings(root, json.dumps({
            "enableAllProjectMcpServers": True,
            "enabledMcpjsonServers": ["codebase-index"],
            "permissions": {"allow": ["Bash(ls:*)"]}}))
        result = self.fork(f"{root}/checkout", ".claude/worktrees/probe")
        self.assertEqual(0, result.returncode, result.stderr)
        copied = self.probe_settings(root)
        self.assertEqual(0, copied.returncode, copied.stderr)
        self.assertEqual({"enabledMcpjsonServers": ["codebase-index"]},
                         json.loads(copied.stdout))

    def test_the_approval_is_not_written_through_a_link_the_branch_carries(self):
        root = self.fixture(link="yes")
        self.local_settings(root, json.dumps({"enabledMcpjsonServers": ["codebase-index"]}))
        result = self.fork(f"{root}/checkout", ".claude/worktrees/probe")
        self.assertEqual(0, result.returncode, result.stderr)
        linked = run_bash('[ -L "$W/.claude/settings.local.json" ]',
                          W=f"{root}/checkout/.claude/worktrees/probe")
        if linked.returncode != 0:
            self.skipTest("git checked the link out as a file: no symbolic links here")
        self.assertNotEqual(0, run_bash('[ -e "$R/outside.json" ]', R=root).returncode,
                            "the approval was written through the link")

    def test_a_checkout_approving_nothing_gives_the_worktree_no_settings(self):
        root = self.fixture()
        self.local_settings(root, json.dumps({"permissions": {"allow": ["Bash(ls:*)"]}}))
        result = self.fork(f"{root}/checkout", ".claude/worktrees/probe")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertNotIn("could not copy", result.stderr)
        self.assertNotEqual(0, self.probe_settings(root).returncode)

    def test_an_unreadable_approval_still_forks_and_says_so(self):
        root = self.fixture()
        self.local_settings(root, "{not json")
        result = self.fork(f"{root}/checkout", ".claude/worktrees/probe")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("could not copy the MCP approval", result.stderr)
        self.assertIn("JSONDecodeError", result.stderr)
        self.assertNotEqual(0, self.probe_settings(root).returncode)

    def test_an_approval_list_that_is_not_names_warns(self):
        # Anything else would leave the worktree's servers pending with
        # nothing said.
        root = self.fixture()
        self.local_settings(root, json.dumps({"enabledMcpjsonServers": "codebase-index"}))
        result = self.fork(f"{root}/checkout", ".claude/worktrees/probe")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("could not copy the MCP approval", result.stderr)
        self.assertNotEqual(0, self.probe_settings(root).returncode)

    def test_approving_every_server_crosses_as_the_servers_defined(self):
        # Names cross, not the approve-all, so a server defined later still asks.
        for local in ({"enableAllProjectMcpServers": True},
                      {"enableAllProjectMcpServers": True, "enabledMcpjsonServers": ["codebase-index"]}):
            with self.subTest(local=local):
                root = self.fixture()
                self.checkout_file(root, ".mcp.json", json.dumps(
                    {"mcpServers": {"codebase-index": {}, "other": {}}}))
                self.local_settings(root, json.dumps(local))
                result = self.fork(f"{root}/checkout", ".claude/worktrees/probe")
                self.assertEqual(0, result.returncode, result.stderr)
                copied = self.probe_settings(root)
                self.assertEqual(0, copied.returncode, copied.stderr)
                self.assertEqual({"enabledMcpjsonServers": ["codebase-index", "other"]},
                                 json.loads(copied.stdout))

    def test_a_json_module_at_the_checkout_root_is_not_imported(self):
        # The approval is read by a Python run from the checkout root, whose
        # untracked files must not shadow the standard library.
        root = self.fixture()
        self.checkout_file(root, "json.py", 'raise SystemExit("shadowed")')
        self.local_settings(root, json.dumps({"enabledMcpjsonServers": ["codebase-index"]}))
        result = self.fork(f"{root}/checkout", ".claude/worktrees/probe")
        self.assertEqual(0, result.returncode, result.stderr)
        copied = self.probe_settings(root)
        self.assertEqual(0, copied.returncode, copied.stderr)
        self.assertEqual({"enabledMcpjsonServers": ["codebase-index"]},
                         json.loads(copied.stdout))

    def test_a_disabled_server_does_not_cross(self):
        # The worktree may approve no server the main checkout refuses.
        root = self.fixture()
        self.local_settings(root, json.dumps({
            "enabledMcpjsonServers": ["codebase-index", "other"],
            "disabledMcpjsonServers": ["other"]}))
        result = self.fork(f"{root}/checkout", ".claude/worktrees/probe")
        self.assertEqual(0, result.returncode, result.stderr)
        copied = self.probe_settings(root)
        self.assertEqual(0, copied.returncode, copied.stderr)
        self.assertEqual({"enabledMcpjsonServers": ["codebase-index"]},
                         json.loads(copied.stdout))

    def test_any_other_path_is_refused(self):
        root = self.fixture()
        for path in ("../sibling", ".claude/worktrees/../x", ".claude/worktrees/a/b",
                     "/tmp/x", ".claude/worktrees/-f"):
            with self.subTest(path=path):
                result = self.fork(f"{root}/checkout", path)
                self.assertEqual(2, result.returncode)
                self.assertIn("must be .claude/worktrees/<name>", result.stderr)

    def test_an_unignored_path_is_refused(self):
        root = self.fixture(ignore="no")
        result = self.fork(f"{root}/checkout", ".claude/worktrees/probe")
        self.assertEqual(2, result.returncode)
        self.assertIn("is not ignored", result.stderr)

    def test_a_subdirectory_is_refused(self):
        root = self.fixture()
        result = self.fork(f"{root}/checkout/src", ".claude/worktrees/probe")
        self.assertEqual(2, result.returncode)
        self.assertIn("checkout root", result.stderr)

    def test_a_linked_worktree_is_refused(self):
        root = self.fixture()
        first = self.fork(f"{root}/checkout", ".claude/worktrees/first", "feat/first")
        self.assertEqual(0, first.returncode, first.stderr)
        result = self.fork(f"{root}/checkout/.claude/worktrees/first",
                           ".claude/worktrees/second", "feat/second")
        self.assertEqual(2, result.returncode)
        self.assertIn("not a linked worktree", result.stderr)

    def test_an_existing_branch_is_refused_rather_than_reset(self):
        root = self.fixture()
        run_bash('git -C "$C" branch feat/probe', C=f"{root}/checkout")
        result = self.fork(f"{root}/checkout", ".claude/worktrees/probe")
        self.assertEqual(3, result.returncode)
        self.assertIn("branch already exists", result.stderr)

    def test_outside_a_repository_says_so(self):
        result = self.fork(temp_root(self), ".claude/worktrees/probe")
        self.assertEqual(2, result.returncode)
        self.assertIn("not in a git repository", result.stderr)


# origin/main gains a file whose blob the checkout then loses, so `git branch`
# still finds the commit and the checkout inside `git worktree add` cannot write
# the file: a real checkout failure on every platform, and no path to shorten.
LOSE_BLOB = """
set -e
printf payload > "$root/origin/payload.txt"
git -C "$root/origin" add payload.txt
git -C "$root/origin" -c user.name=t -c user.email=t@t commit -q -m payload
git -C "$root/checkout" fetch -q origin
oid=$(git -C "$root/origin" rev-parse HEAD:payload.txt)
rm -f "$root/checkout/.git/objects/$(printf %s "$oid" | cut -c1-2)/$(printf %s "$oid" | cut -c3-)"
"""

RESTORE_BLOB = """
git -C "$root/origin" cat-file blob HEAD:payload.txt | git -C "$root/checkout" hash-object -w --stdin
"""

WORKTREE_ADD = '[ "$1" = -c ] && [ "$3" = worktree ] && [ "$4" = add ]'


class NeitherCreatorTakesMain(unittest.TestCase):
    """git-worktree-fork.sh and git-branch-create.sh refuse `main` however it is spelled.

    With `main` packed only, `Main` passes an existence check, and on a case-insensitive filesystem its new loose ref
    is `main`'s.
    """

    SPELLINGS = ("main", "Main", "MAIN", "heads/main", "refs/heads/main", "origin/main", "refs/remotes/origin/main")
    CREATE = SCRIPTS / "git-branch-create.sh"
    fixture = ForkShape.fixture
    fork = ForkShape.fork

    def packed(self):
        root = self.fixture()
        made = run_bash('git -C "$C" pack-refs --all', C=f"{root}/checkout")
        self.assertEqual(0, made.returncode, made.stderr)
        return root

    def refs(self, root):
        return run_bash('git -C "$C" for-each-ref --format="%(refname) %(objectname)"', C=f"{root}/checkout").stdout

    def test_the_fork_refuses_main_however_it_is_spelled(self):
        root = self.packed()
        before = self.refs(root)
        for spelling in self.SPELLINGS:
            with self.subTest(spelling=spelling):
                result = self.fork(f"{root}/checkout", ".claude/worktrees/probe", spelling)
                self.assertEqual(3, result.returncode, f"{spelling!r}: {result.stderr}")
                self.assertIn("refusing to create main", result.stderr)
                self.assertFalse(Path(root, "checkout", ".claude", "worktrees", "probe").exists())
                self.assertEqual(before, self.refs(root))

    def test_the_in_place_create_refuses_main_however_it_is_spelled(self):
        root = self.packed()
        before = self.refs(root)
        for spelling in self.SPELLINGS:
            for base in ("HEAD", "origin/main"):
                with self.subTest(spelling=spelling, base=base):
                    result = run_bash('cd "$C" && bash "$H" "$B" "$S"', C=f"{root}/checkout", H=str(self.CREATE),
                                      B=spelling, S=base)
                    self.assertEqual(3, result.returncode, f"{spelling!r}: {result.stderr}")
                    self.assertIn("refusing to create main", result.stderr)
                    self.assertEqual(before, self.refs(root))

    def test_a_name_that_only_holds_main_is_created(self):
        root = self.packed()
        result = run_bash('cd "$C" && bash "$H" feat/main HEAD', C=f"{root}/checkout", H=str(self.CREATE))
        self.assertEqual(0, result.returncode, result.stderr)
        result = self.fork(f"{root}/checkout", ".claude/worktrees/probe", "fix/main")
        self.assertEqual(0, result.returncode, result.stderr)


class TheAddAsksForLongPaths(unittest.TestCase):
    def test_the_add_spells_core_longpaths_in_a_code_line(self):
        # Only a Windows run can fail on a path past 260 characters, so the spelling is held on every platform,
        # against code lines a comment cannot satisfy.
        self.assertIn("git -c core.longpaths=true worktree add",
                      "\n".join(code_lines(FORK.read_text(encoding="utf-8"))))


class FailedFork(unittest.TestCase):
    """A fork that fails leaves no branch of its own behind; what it did not make it keeps."""

    def fixture(self):
        root = temp_root(self)
        made = run_bash(FIXTURE, root=root, IGNORE="yes", HOOK="no", LINK="no")
        self.assertEqual(0, made.returncode, made.stderr)
        return root

    def fork(self, root, shim=""):
        # On PATH inside bash, and in bash's own form, as test_git_worktree_remove.py's shim says why.
        return run_bash('cd "$root/checkout" && PATH="${SHIM:+$SHIM:}$PATH" '
                        'bash "$FORK" .claude/worktrees/probe feat/probe',
                        root=root, FORK=str(FORK), SHIM=shim)

    def branch(self, root):
        return run_bash('git -C "$root/checkout" branch --list feat/probe', root=root).stdout.strip()

    def stub(self, root, body, when=WORKTREE_ADD):
        """A `git` that, on `when`, runs `body` and exits 128: a state git's own failure does not leave."""
        shim = Path(root) / "shim"
        shim.mkdir()
        real = Path(shutil.which("git")).as_posix()
        (shim / "git").write_bytes(chr(10).join((
            "#!/usr/bin/env bash",
            f'real="{real}"',
            f"if {when}; then",
            f"  {body}",
            "  exit 128",
            "fi",
            'exec "$real" "$@"',
            "")).encode("utf-8"))
        os.chmod(shim / "git", 0o755)
        resolved = shim.resolve()
        if os.name == "nt":
            return "/" + resolved.drive[0].lower() + resolved.as_posix()[2:]
        return str(resolved)

    def test_a_failed_checkout_removes_the_branch_and_a_retry_forks(self):
        root = self.fixture()
        broken = run_bash(LOSE_BLOB, root=root)
        self.assertEqual(0, broken.returncode, broken.stderr)
        result = self.fork(root)
        self.assertNotEqual(0, result.returncode)
        # The positive control: the failure is the checkout's, past the point
        # the helper has made the branch.
        self.assertIn("payload.txt", result.stderr)
        self.assertEqual("", self.branch(root))
        self.assertNotIn("probe", run_bash('git -C "$root/checkout" worktree list', root=root).stdout)
        self.assertEqual(0, run_bash(RESTORE_BLOB, root=root).returncode)
        again = self.fork(root)
        self.assertEqual(0, again.returncode, again.stderr)
        self.assertEqual("feat/probe", self.branch(root).lstrip("+* "))

    def test_a_directory_that_cannot_be_made_removes_the_branch(self):
        # `/branch`'s in-place fallback then creates the branch, which a
        # survivor would refuse. A file where the directory goes fails the same
        # step an unwritable one does, and a root user cannot bypass it.
        root = self.fixture()
        made = run_bash('printf ".claude/\\n" > "$root/checkout/.gitignore"; mkdir -p "$root/checkout/.claude"; '
                        ': > "$root/checkout/.claude/worktrees"', root=root)
        self.assertEqual(0, made.returncode, made.stderr)
        result = self.fork(root)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("could not create leading directories", result.stderr)
        self.assertEqual("", self.branch(root))

    def test_the_worktree_list_is_read_in_bash_not_by_a_tool(self):
        # Git Bash reported a `grep` fed by a process substitution as `Child exited`, which read as
        # "no worktree holds it", and the branch a worktree holds was deleted from under it. A `grep` that dies on
        # every call stands in for it: the held branch is still kept.
        root = self.fixture()
        shim = self.stub(root, '"$real" worktree add -q "$root/other" feat/probe')
        (Path(root) / "shim" / "grep").write_bytes(b"#!/usr/bin/env bash\necho 'Child exited' >&2\nexit 148\n")
        os.chmod(Path(root) / "shim" / "grep", 0o755)
        result = self.fork(root, shim)
        self.assertEqual(128, result.returncode, result.stderr)
        self.assertIn("feat/probe", self.branch(root))
        self.assertIn("is kept", result.stderr)

    def test_a_branch_moved_off_the_start_point_is_kept(self):
        # Its commit is on no other ref, so removing it would lose the commit.
        root = self.fixture()
        shim = self.stub(root, '"$real" update-ref refs/heads/feat/probe "$("$real" -c user.name=t -c user.email=t@t '
                               'commit-tree -m moved -p HEAD "HEAD^{tree}")"')
        result = self.fork(root, shim)
        self.assertEqual(128, result.returncode, result.stderr)
        self.assertIn("feat/probe", self.branch(root))
        self.assertIn("is kept", result.stderr)

    def test_a_branch_a_worktree_holds_is_kept(self):
        # `update-ref` would delete it from under that worktree without asking.
        root = self.fixture()
        shim = self.stub(root, '"$real" worktree add -q "$root/other" feat/probe')
        result = self.fork(root, shim)
        self.assertEqual(128, result.returncode, result.stderr)
        self.assertIn("feat/probe", self.branch(root))
        self.assertIn("is kept", result.stderr)

    def test_a_name_another_fork_took_first_is_left_to_that_fork(self):
        # The other fork passes the same check this one did, then makes the
        # branch before this one's git does, and has no worktree registered yet.
        root = self.fixture()
        shim = self.stub(root, '"$real" branch --no-track feat/probe origin/main; exec "$real" "$@"',
                         when=f'[ "$1" = branch ] || {{ {WORKTREE_ADD}; }}')
        result = self.fork(root, shim)
        self.assertEqual(128, result.returncode, result.stderr)
        self.assertIn("already exists", result.stderr)
        self.assertNotIn("removed", result.stderr)
        self.assertIn("feat/probe", self.branch(root))
        # What that fork does next: its add finds its branch and the path free.
        winner = run_bash('git -C "$root/checkout" worktree add .claude/worktrees/probe feat/probe', root=root)
        self.assertEqual(0, winner.returncode, winner.stderr)

    def test_a_fetch_during_the_fork_leaves_no_branch_behind(self):
        # Another session's fetch lands after the start point is read and before
        # the branch is cut: the branch must sit where the cleanup compares it.
        root = self.fixture()
        moved = run_bash('git -C "$root/origin" -c user.name=t -c user.email=t@t '
                         'commit -q --allow-empty -m moved', root=root)
        self.assertEqual(0, moved.returncode, moved.stderr)
        shim = self.stub(root, 'if [ "$1" = branch ]; then "$real" fetch -q origin; exec "$real" "$@"; fi',
                         when=f'[ "$1" = branch ] || {{ {WORKTREE_ADD}; }}')
        result = self.fork(root, shim)
        self.assertEqual(128, result.returncode, result.stderr)
        # The positive control: origin/main did move under the running fork.
        heads = run_bash('git -C "$root/origin" rev-parse HEAD; '
                         'git -C "$root/checkout" rev-parse origin/main', root=root)
        self.assertEqual(0, heads.returncode, heads.stderr)
        origin_head, tracked = heads.stdout.split()
        self.assertEqual(origin_head, tracked)
        self.assertIn("removed feat/probe", result.stderr)
        self.assertEqual("", self.branch(root))
        again = self.fork(root)
        self.assertEqual(0, again.returncode, again.stderr)

    def test_git_failing_before_the_branch_exists_says_nothing_of_one(self):
        root = self.fixture()
        result = self.fork(root, self.stub(root, ":", when='[ "$1" = branch ]'))
        self.assertEqual(128, result.returncode, result.stderr)
        self.assertEqual("", self.branch(root))
        self.assertNotIn("feat/probe", result.stderr)


if __name__ == "__main__":
    unittest.main()
