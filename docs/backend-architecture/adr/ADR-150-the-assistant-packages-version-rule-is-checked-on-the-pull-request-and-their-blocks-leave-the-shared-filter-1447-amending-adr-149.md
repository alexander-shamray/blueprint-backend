# ADR-150 — The assistant packages' Version rule is checked on the pull request, and their blocks leave the shared filter (#1447), amending ADR-149

**Decision.** On a pull request, CI's `packages` job refuses a change to
`Common.Assistant` or `Common.Assistant.Anthropic` whose project file keeps
the `Version` it has at the pull request's base: the step reads the project's
`<Version>` in the pull request's merge commit, which is what the job checks
out, and at the base commit, for each package whose `assistant` or
`anthropic` filter matched, and fails when they are equal. A project the base
does not hold is new and passes. The feed's refusal of a
version it holds stays, on `main`, as the backstop
[ADR-149](ADR-149-the-ai-assistants-core-and-its-adapter-are-two-building-blocks-this-repository-publishes-as-nuget-packages-1346.md)
made it. And §15.1's `shared` filter excludes the two blocks' directories, so
a change inside them alone marks no deployable changed and builds no image.
**Why.** ADR-149 accepted both as costs. A change that forgot its version bump
merged green and turned `main`'s run red at the push, and the fix was a second
pull request; read at the pull request, the same comparison fails where the
author can still amend it. And no image holds either block, since no host
references them, so a change to either rebuilding every service, the gateway
and the BFF bought minutes and proved nothing. The exclusions are negations
inside `shared`, not a list of the blocks that stay, so a building block added
later is shared by default; `pipeline-gate filters` still sees
`src/BuildingBlocks/**` and counts no negation as coverage.
**Consequences.** The pull request's check compares with the base it was
opened or last pushed against, so two pull requests that each raise a package
to the same new version both pass, and the second to land is refused by the
feed on `main`, as before. The step reads `<Version>` as text from the project
file, and fails rather than passes when it reads none, so moving the version
into `Directory.Build.props` breaks the step until it is taught that file.
Under `some-with-excludes` any matching negation wins, and every deployable's
filter inherits `*shared`, so a host's filter cannot name the two directories
back while it takes the anchor: the change that makes a host reference them
(#1347) gives that host's filter the shared lines without the exclusions, or
drops them and rebuilds every image again. ADR-149 is otherwise unchanged; this
decision is read beside it, as an ADR is amended and never rewritten.

**Amended by
[ADR-151](ADR-151-the-web-bff-hosts-the-ai-assistant-for-signed-in-customers-off-by-default-and-scoped-to-the-caller-1347-amending-adr-053-adr-149-and-adr-150.md)**:
the BFF now holds the two blocks, and its filter takes an `inputs` anchor that
keeps them, while `shared` is `inputs` less the two blocks. This record is
otherwise unchanged.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
