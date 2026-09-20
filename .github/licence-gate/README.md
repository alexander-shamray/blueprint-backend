# The licence gate

**The claim: every package `Directory.Packages.props` pins has a licence
somebody cleared, no project steps past that file by the `PackageReference`
and central-management forms listed below, and no chapter writes a version
the file owns as an MSBuild `Include`/`Version` pair.**
[§4.4](../../docs/backend-architecture/04-solution-structure.md) states the
rule; this file owns what the gate reads to enforce it, what it refuses, and
where its claim stops — which is narrower than "no package reaches a restore
uncleared", for the reason under *What it does not claim*.

## What it reads

- `Directory.Packages.props` — the pins, `PackageVersion` and
  `GlobalPackageReference` alike, read from this file and no other.
- [Appendix B](../../docs/backend-architecture/appendix-b-licences.md) — the
  register of what is cleared, matched on the backticked package identities
  its rows carry, never on the product a package is named after.
- `allowed-licences.txt` beside this file — the licences the register may
  name.
- Every `.csproj`, `.props` and `.targets` outside the directories
  `SKIPPED_DIRECTORIES` in `licence_gate.py` names, the props file included,
  for the two ways a
  project steps past central pinning: a `PackageReference` naming its own
  version — a `Version` attribute, a `Version` child element or a
  `VersionOverride` — and `ManagePackageVersionsCentrally` set to anything
  but `true`. A shared `.props` does either for every project that imports
  it — every project, for `Directory.Build.props` — which is why the scan
  reaches past the projects.
- Every chapter and appendix under `docs/backend-architecture/`, for an
  MSBuild `Include`/`Version` pair — a pin printed into the blueprint.
  [`docs/change-locality.md`](../../docs/change-locality.md) §2 gives a
  version one owner and names Appendix B as the single exception, so that
  file is passed over and every other is read. A version named in prose, as
  Appendix B names one, is a different claim and not this gate's.

Everything it reads is text, so nothing needs restoring first, and that is
what lets [§15.1](../../docs/backend-architecture/15-cicd-deployment.md) put
it ahead of the build.

## What it refuses

- a pin the register does not name;
- a registered identity pinned nowhere — a dropped pin, or a row that
  outlived its dependency. §4.4's carve-outs are encoded as exceptions to this
  one: the Aspire rows are deliberately unpinned, and an either/or row
  expects only its chosen half;
- a project that pins for itself rather than through the props file;
- a registered licence any part of which is outside `allowed-licences.txt`.
  **Every** part of a multi-part cell has to be inside it: the gate reads a
  `/` and cannot tell a disjunction from a conjunction, so a row cleared
  because one half was allowed would clear the other with it. So where a
  package really is offered under either licence, its row names the one
  taken here;
- a licence whose spelling its map does not know. That is a separate finding
  from the one above because it has a separate repair — a misspelt cell is
  fixed in the register, a spelling nobody has taught the gate in
  `licence_gate.py` — and teaching it is not clearing it, since a newly
  nameable licence still needs an allow-list line. The vocabulary is closed
  on purpose, so a real identifier the map has never been shown is refused
  too;
- no MSBuild project file found at all, because a scan that matched nothing
  reports exactly what a repository with no fault reports.

## What it does not claim

- **A pin written anywhere but the props file.** A `PackageVersion` or
  `GlobalPackageReference` in another imported `.props` or `.targets` is not
  read as a pin, so a package supplied that way passes unregistered. That is
  a gap in the gate rather than a decision, and closing it is a change to
  `licence_gate.py` and its suite.
- **Whether a version is current or safe.** It asks where a `Version` is
  written and nothing else about it, so currency and vulnerability scanning
  are a separate obligation and nothing here meets it.
- **A pin printed in prose.** The chapter check reads MSBuild's attribute
  pair, which is what a transcription of the props file looks like. A
  sentence naming a version in words passes, and the reviewer carries it.

## How it runs

The `licence-gate` job in [`ci.yml`](../workflows/ci.yml) tests it and then
runs it, after the secret scan and before the build.
