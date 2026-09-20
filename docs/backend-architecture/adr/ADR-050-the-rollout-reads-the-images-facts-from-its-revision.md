# ADR-050 — The rollout reads the image's facts from the image's revision

**Decision.** [§15.5](../15-cicd-deployment.md)'s rollout resolves the
dispatched image tag to a commit and reads **every fact it derives from
`src/` for the workload it is rolling out of that commit's tree**, never
out of the runner's checkout. An
image tag is a forty-character lower-case commit object name, because
[§15.2](../15-cicd-deployment.md) builds both of a service's images with the
commit as the tag; the rollout refuses a tag that is not one, a commit this
repository does not hold, and a commit that is not an ancestor of the ref it
is rolling from. That commit's `src/` is extracted to a tree the job passes
as `--source`, beside the `--workload` it belongs to, and that workload's
probe exclusion, its consumer and saga registration scans
([ADR-047](ADR-047-the-canary-judges-each-workload-on-the-signals-it-receives.md)),
its entry assembly and the series its declared signals read are taken from
it. **The other workloads are read from the checkout**, because they are
running images built from other revisions and this tag says nothing about
them; a `--source` given without a `--workload` is refused rather than
applied to all of them.

**Each track is judged on the probe routes of the image that track is
running.** The canary serves the candidate, and the stable track serves
what it was installed with, so the rollout reads that release's tag out of
`helm get values` and resolves it the same way. One exclusion for both
would filter the stable track's probes by the candidate's routes, which is
the mismatch this decision exists to remove, one track over.

**Both images have to build the assembly the workload is judged by.**
[§13.2](../13-observability.md) takes `service.name` from an image's entry
assembly, so a release whose assembly was renamed emits a label the
plan does not name; the baseline query then matches no series and every
rung rolls back a healthy release. The rollout refuses that before it
changes anything rather than measuring it for a dwell.

**Everything else stays the checkout's**: the chart,
[§13.6](../13-observability.md)'s thresholds, the rollout plan, and the
deciding code itself. `canary.py check` runs a second time inside the
rollout job against the image's tree, and a check over `deploy.yml`'s own
text asserts that both trees are still exported and still passed.

**Why.** The workflow checked out `main` and deployed whatever the tag named,
so every source-derived fact described one revision and the running pods
another. The probe exclusion is the sharpest case: it is an `http_route`
matcher built by scanning `MapHealthChecks`, so a route the image maps and
`main` does not has its probe traffic counted as real requests, and a route
`main` renamed but the image still serves is excluded from the measurement it
should dominate. Either way the rung is judged on the wrong request set, and
the canary that reports health is measuring something else. The registration
scans and the entry-assembly check fail the same way one step earlier, by
certifying a plan against source the image was not built from. This is not
hypothetical on this repository's own history: fifteen commits before this
decision `Catalog.Api` registered no consumer, and the plan now carries a
`consumeExemption` arguing why that consumer is not judged — so rolling that
image today applies an argument about a consumer it does not have, and
nothing noticed. **Ancestry rather than equality with the checked-out
commit**, because [§15.1](../15-cicd-deployment.md)'s `images` job builds an
image only for a service a commit changed, so the newest tag for a given
workload is routinely behind `main`; requiring the head would make most
dispatches impossible and the rest a fiction. An ancestor is a revision the
protected branch carries, which is the property actually wanted. The deciding
code is still the checkout's for the reason the `Production rolls from main`
step already gives: a job that runs its own gates from an arbitrary ref gates
nothing. Only the *subject* moves, and only `src/`, because the chart and the
alerts describe what this rollout installs rather than what the image is.

**Consequences.** A release whose installed tag names no commit this branch
carries stops the rollout, because the baseline half of the comparison
cannot be read; that is a new requirement on what is already deployed, and
the alternative is judging the stable track by routes that are not its own.
**An entry-assembly rename cannot be canaried by this mechanism at all**,
for the same reason and with the same remedy as §15.5's binding case: the
two tracks are told apart by a label one of them stops emitting, so the
release is a cutover or a pair of releases rather than a ladder.
**A tag answers for one workload and one track and for nothing else**: held
wider it refuses an Ordering rollout for a Catalog registration the Ordering
image predates, or judges a stable pod by routes it does not serve.
The checkout needs full history, so the rollout pays a
complete fetch it did not before; the alternative is a shallow clone that
cannot resolve any tag but the head's, which is the one case this decision
has nothing to catch. **A refusal costs a dispatch, not a rollout**: every
check here runs before the HPA floor moves, so the failure mode is a job that
declines to start rather than a canary left serving. An image built from an
unmerged branch can no longer be rolled to production at all, which is a
restriction and the intended one. A plan tuned against `main` can now refuse
an older image — the `consumeExemption` above is exactly that — and the right
response is to read why the two disagree rather than to widen the exemption.
**This binds the tag to a revision; it does not prove the image was built
from it.** A registry can hold anything under a given name, and what answers
that is §15.1's signing rather than a text check on a runner; the claim made
here is that the tag *names* the revision whose source the rollout reads, and
a signature is what makes the tag trustworthy. Facts the rollout does not
derive from `src/` are unaffected, including the realm, which is read from
the running release and not from any tree. The workflow check is the part
most likely to rot: the binding is a path exported per image and an argument
carrying it to each reader, any of which can go missing while every command
still runs and every other check still passes, which is why
`canary.py check` reads `deploy.yml` for them
rather than trusting that a rollout nobody can run is still wired up.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
