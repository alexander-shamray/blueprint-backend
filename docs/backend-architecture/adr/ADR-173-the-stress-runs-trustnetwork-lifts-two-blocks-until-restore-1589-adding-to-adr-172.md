# ADR-173 — The stress run's -TrustNetwork lifts two blocks until -Restore (#1589), adding to ADR-172

**Decision.** `stress/remote/stack.ps1 -TrustNetwork` may change two settings
of the stack's machine for two-machine stress runs, and only when the user
passes it: a Public network profile becomes Private, and the enabled inbound
firewall rules naming Docker Desktop's programs that apply to the Private
profile, the one the run requires, are disabled. Both stay changed across
every later run until `-Restore`. Each original value goes to
`artifacts/stress/remote-state.json` before it changes, and a rerun keeps the
values first recorded. It refuses before changing anything when the Private
firewall profile is off or admits inbound by default, and while the file
records an interface other than the one holding the default route.
`-Restore` puts every published port back on loopback, checks it, and only
then enables those rules and gives the network its profile back. Without the
switch, stack.ps1 refuses a network that is not Private and an enabled
inbound rule on the Private profile naming Docker's programs; one the switch
disabled earlier no longer refuses a run.
**Why.** The first runs of
[ADR-172](ADR-172-the-stress-runs-generator-may-run-on-a-second-machine-1589-adding-to-adr-170.md)'s
scripts stopped twice on settings every such machine has: Windows calls home
Wi-Fi Public, and Docker Desktop's installer adds inbound allow rules for its
backend, either of which would let the whole network past the run's
one-address rule. Each refusal handed the user an administrator command, and
a reminder to undo it later in the one safe order: a Docker rule enabled while
a port is still bound to the LAN address admits every peer, so the rules come
back only after the ports leave it.
**Consequences.** The script now changes machine security settings, and it
cannot tell a café from a home, so the switch is the user's statement that
the network is trusted.
A machine whose `-Restore` never runs keeps Docker's rules disabled, harmless
while every port is on loopback, and a network made Private stays Private.
`-Restore` deletes the state file once it has put both back, so the next
`-TrustNetwork` records the machine as it then is. These paths are run by no
suite.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
