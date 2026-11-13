# The harness drift gate

**The claim: every file the harness manages in this repository is exactly
what the harness put there.** A sync records a SHA-256 per managed file in
`.harness/manifest.json`. The gate recomputes them on every pull request, so
a hand edit to a managed file, or a stray file beside the managed ones, is
caught where it is made and not at the next sync.

## What it refuses

- a manifest path that is missing from the tree;
- a manifest path whose content hash differs (line endings are folded, so a
  Windows checkout passes);
- a file under a managed directory that the manifest does not list and the
  `local` globs in `.harness/config.json` do not cover. The managed
  directories are `.claude/agents`, `.claude/commands`, `.claude/hooks`,
  `.claude/scripts`, `.claude/skills`, `docs/commands`, `docs/harness`
  and `.github/harness-drift`; `__pycache__/` and `*.pyc` are skipped. When
  the root is the top of a git checkout and a git on `PATH`, outside the
  working directory and the root, can list it, the candidates are what git
  would commit there (`git ls-files --cached --others --exclude-standard`),
  so a file the checkout ignores, such as an installer's `.skill_version`,
  is never judged: it cannot reach a pull request. A nested repository or
  submodule is judged by its files, or as itself when it holds none.
  Otherwise the gate walks the file system, and an ignored file is judged;
- a `.claude/settings.json` that is not what `.harness/render.py` renders
  from `.harness/settings.base.json` plus the `settings` in
  `.harness/config.json`. The renderer runs only if its bytes match the
  manifest and, with a payload, the payload; otherwise the gate reports that
  and does not execute it;
- a `local` or `exclude` glob that matches a file the gate needs delivered:
  `.harness/settings.base.json`, `.harness/render.py`, this directory's
  files, `.github/workflows/gates.yml`, or a path a hook command in
  the settings base names (`.claude/hooks/guard-git-argv.py`, for one).
  Without them a target stays green and is never checked again, or runs a
  guard hook that fails unseen; apply refuses the same globs. The list is
  `GATE_FILES` in `drift_gate.py`, and the harness repository tests that
  it names every file the payload ships here;
- a `.harness/config.json` that apply would refuse: an unknown key, a
  `repo` or `task_list` that is missing or not a string, a `local` or
  `exclude` that is not a list of strings, or a glob with a backslash or a
  leading slash. Its `settings` are refused by the renderer, as apply's
  are;
- a manifest, config, base or renderer that cannot be read, a renderer that
  refuses the config, a manifest path that is not a canonical relative POSIX
  path, and any fixed path or managed directory that is a link. Each is a
  line of output, never a traceback, and the gate never reads a tree file
  outside the root; in a checkout, the git it runs also reads that
  checkout's repository and git's own configuration.

With `--payload`, it also refuses:

- a payload file excluded by the config that exists in the tree;
- a payload file, not excluded by the config, that the manifest does not list
  with the payload's hash. The exception is a `local` path whose file in the
  tree is absent or identical to the payload's, as apply allows;
- a `local` path whose file differs from the payload's, which apply would
  have refused;
- a manifest entry other than `.claude/settings.json` that is not a payload
  file with an equal hash.

Without `--payload` it requires the manifest to list the base, the renderer
and the settings.

## Trust model

The manifest and the config sit in the tree a pull request controls, so a
pull request can edit a managed file and its manifest line together, or
delete the line. Three things guard against that, each a different part:

- **The base commit's copy of `drift_gate.py`** judges, so a pull request
  cannot weaken the gate that judges it. It does not cover the manifest or
  the config.
- **The payload fetch** covers the manifest and the config. The workflow reads
  `harness_commit` from the head manifest and fetches that commit's
  `payload/` from the harness repository, only if the commit is on that
  repository's `main` (GitHub serves fork-network commits by SHA, so a SHA
  alone proves nothing) and is the base manifest's commit or a descendant of
  it, so a pull request cannot roll a target back to a weaker real release.
  `--payload` makes the gate compare the manifest with it. A failed fetch
  fails the check. A pull request cannot change that repository.
- **The repository ruleset and review** are the backstop for what is left.
  `pull_request` runs the head's copy of `gates.yml`, so a pull
  request can edit the workflow itself, and only the ruleset requiring this
  check, with review of workflow changes, stops that.

Exclude means "not delivered", never "replaced". A payload path the config
excludes that still exists in the tree is refused here and by apply, because
a target keeping its own copy of a managed file, a guard hook for example,
would fork the harness. `local` allows only an absent or identical copy, so a
target that needs different behaviour adds a new local path, never a
replacement.

## How to run it

```bash
py -3.12 .github/harness-drift/drift_gate.py [--root <dir>] [--payload <dir>]
```

It prints one line per problem and exits 1, or prints `harness-drift: clean`
and exits 0. `--root` defaults to the current directory. `--payload` is the
harness repository's `payload/` at the commit in the manifest. The gate is
stdlib only and imports nothing from the harness repository, which a target
does not have. Its hash and glob matcher are copies of the sync tool's; the
harness repository tests that they agree.

## How it runs

The `gates` job in [`gates.yml`](../workflows/gates.yml), whose verdict for
this gate the `report` job posts as the `harness-drift` check, on every pull
request with no path filter. It takes the gate from the base commit; when the
base carries none, which is the first sync into a target, the head's copy
judges and the run says so. It then fetches the payload, runs the gate, fixes
the verdict, and only then runs this directory's suite, so head test code
cannot tamper with the run before the gate has judged. A failed fetch fails
the check; a failed suite fails the `gates` job.

The suite includes `test_gate_reads_the_shipped_manifest`, which checks the
tree it sits in. It needs a rendered tree and is never run from the harness
repository's raw `payload/`.

## When it is red

Do not edit the managed file in this repository, or the next sync reverts it.

- To change the file for every repository, change it in the harness
  repository and sync.
- To keep it different here, declare its path `local` in
  `.harness/config.json`. A new local file under a managed directory needs
  the same line.
- A settings difference is fixed in `.harness/config.json`'s `settings`, and
  the same pull request re-renders `.claude/settings.json` whole (*Changing
  this repository's config*).

## Changing this repository's config

A pull request that changes `.harness/config.json`, whether its `settings`,
`local` or `exclude`, changes what a sync renders. So it carries the
re-rendered `.claude/settings.json` and `.harness/manifest.json` with it.
They are rendered at the commit the manifest already names, never a newer
one. The gate fetches that commit's payload and judges the tree against it,
and moving to a newer commit is a sync's job. The config is Edit-denied
with the rest of `.harness/`, so the edit is the owner's to make or lift
(`docs/harness-boundaries.md`, *Edit denies and agent self-protection*).
From a harness checkout:

```bash
commit="$(py -3.12 -I -c 'import json, sys; print(json.load(sys.stdin)["harness_commit"])' < <target>/.harness/manifest.json)"
git worktree add --detach ../harness-at "$commit"
cd ../harness-at
py -3.12 -m harness_sync apply --payload payload --target <target> --commit "$commit"
```

Run the tool from that worktree, not from a newer checkout, so the renderer
that writes the settings is the one the manifest records. Then commit what
apply changed beside the config.

## Changing a gate rule

The base commit's gate judges every pull request, the sync that changes the
gate included. So a sync that loosens a rule is judged by the old rule, and
is red if anything in it relies on the looser one. Change a rule in two
steps:

1. Ship the new rule on its own: a harness change to `drift_gate.py` that
   every target already passes under the old and the new rule, synced and
   merged into each target.
2. Only then ship what relies on it. That pull request's base carries the
   new gate, which judges it.

A rule that tightens is in force from the pull request after the sync that
carries it, so check that every target passes it before step 1, with
`py -3.12 -m harness_sync check --target <dir> --payload <payload>` from a
harness checkout that holds the new gate.

The command line flags follow the same rule, because the base gate judges
the head workflow that calls it. A new flag is added in one sync and used in
a later one, and a flag a workflow still passes is never renamed or removed:
either makes the sync that carries it red.
