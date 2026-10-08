# ADR-082 — Every pod meets Pod Security "restricted", with a read-only root filesystem

**Decision.** Every pod the library chart renders, the Deployment's and the
migration Job's, meets the Kubernetes Pod Security Standard "restricted":
`runAsNonRoot`, a `RuntimeDefault` seccomp profile, no privilege escalation,
every capability dropped. Each container also runs with
`readOnlyRootFilesystem`, and an `emptyDir` of at most 64Mi mounted at `/tmp`
is the one writable path. `deploy/helm/smoke.sh` holds every pod document in a
render to all of it, the container fields per container.
**Why.** Without a profile a pod runs Unconfined on any node whose kubelet
does not default to `RuntimeDefault`, which is the kubelet's own default, so
code execution in a host, or in the migrator holding the DDL credential
([§7.1](../07-persistence.md)), met the full syscall surface. A writable image
filesystem let the same foothold rewrite the application it runs in. Neither
was decided anywhere: `deploy/helm/README.md` called the read-only filesystem
the right posture and left it untested against the chiselled images. It was
tested: the Compose stack with every host and migrator read-only and a tmpfs
at `/tmp` migrated, went healthy and walked an order from placed to delivered
(`deploy/compose/walk-an-order.sh`), with no filesystem error in any log. A
host also starts read-only with nothing writable at all; what the runtime puts
in `/tmp` is its diagnostic socket and debugger pipes, which is why the mount
exists: without it `dotnet-counters` and `dotnet-trace` cannot attach.
**Consequences.** A namespace can now enforce "restricted" through its
`pod-security.kubernetes.io/enforce` label, and should, but the label is the
installer's, as the namespace is, and no chart sets it. A host that starts
writing outside `/tmp` fails at that write in Kubernetes and not in a test,
because nothing here runs the images read-only but a by-hand Compose override.
A full process dump does not fit in 64Mi, so taking one needs the limit raised
for that pod or a debug container with its own volume.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
