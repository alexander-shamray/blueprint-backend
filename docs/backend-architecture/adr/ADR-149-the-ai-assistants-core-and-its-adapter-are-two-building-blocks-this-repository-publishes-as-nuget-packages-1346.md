# ADR-149 — The AI assistant's core and its adapter are two building blocks this repository publishes as NuGet packages (#1346)

**Decision.** The AI assistant's provider-neutral core and its Anthropic
adapter move here from `blueprint-admin`, renamed, with the comments
that named the admin host made host-neutral, as
the building blocks `Common.Assistant` and `Common.Assistant.Anthropic` under
`src/BuildingBlocks/` ([§4.1](../04-solution-structure.md)), with their suites
`Common.Assistant.Tests` and `Common.Assistant.Anthropic.Tests`. The core
references no package and no host type; the adapter references the core and
the `Anthropic` SDK, and takes the SDK client its host builds. This repository
owns the assistant's protocol from now on: its C# types and the golden JSON
files under `src/BuildingBlocks/Common.Assistant/Protocol/golden/`, which every
client mirrors. The design they implement is `blueprint-admin`'s
`docs/superpowers/specs/2026-10-22-ai-assistant-design.md`, which the code's
comments cite as *AI assistant design §n*. **They are the only projects this
repository publishes for another repository to restore.** Each sets
`IsPackable` and its own `Version` in its project file; CI's `packages` job
packs both on every run once the test stages have passed, and on a push to
`main` pushes to GitHub Packages each one whose project directory changed. A
change to either project ships under a new `Version`, and a consumer pins one
exactly. No service may reference them — §4.2's architecture tests keep their
`BuildingBlocks` allow-list at the five a service is built from — and no host
here references them yet: the BFF's hosting, its credential and the decision on
what customer data may reach the provider are #1347's.
**Why.** The assistant has two hosts, the admin console and the BFF, and the
code both run needs one owner. The BFF is its production host and is here, and
the console is already a client of this repository's contracts, so a package
published from here points ownership the way the dependency already runs; a
copy in each repository would be two owners of one protocol, and a new
repository would add CI, releases and rules and buy nothing. A package rather
than a project reference, because `blueprint-admin` is another repository and
can restore only what is published. Pushing without `--skip-duplicate` is what
makes the version rule hold for a change inside either project's directory: a
changed project that kept its version is refused by the feed and fails the
job, where skipping would leave the feed holding a package that project's
source no longer matches, unseen.
**Consequences.** The rule is enforced on `main` and not on the pull request:
a change that forgets its version bump merges green and turns `main`'s run red
at the push, and the fix is a second pull request that bumps it. A comment-only
edit to either project costs a version too, because the filter is a directory
and not a diff of the shipped assembly; and since §15.1's shared filter holds
all of `src/BuildingBlocks/`, a change to either also rebuilds every image,
though no image holds them. A shared input — a pin in
`Directory.Packages.props`, `Directory.Build.props`, `global.json` — is in
neither filter, so a change to one ships nothing until either project's next
version: the `Anthropic` pin that Dependabot raises reaches the feed with the
adapter's next change, and a filter that held it would turn `main` red on every
such bump instead. The two `Version`s move apart — the
adapter's package depends on the core's version as of its own pack, which a
consumer pinning both exactly overrides — so a core change that alters the
adapter's behaviour has to bump the adapter as well, and nothing checks that
it did. Restoring from GitHub Packages needs a token even for a public
package, so every build of `blueprint-admin` now needs one. CA1716 and CA1711
stay suppressed, on the ground that each consumer is C#, which a published
library no longer guarantees: a consumer in another .NET language would be
the case they protect. And the building blocks stop being only what a
service is built from (§4.2, §4.3): these two are a product's core and its
adapter, kept small for a different reason — a host's code stays out of them so
that two hosts can take them whole.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
