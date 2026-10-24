# ADR-148 — The object store check writes a sentinel with the BFF's pair and reads it back with Catalog's (#1221)

**Decision.** Before its refusal probes, `deploy/objectstore/store_check.py`
asks the deployed store for a write and a read-back that
[§10.8](../10-api-gateway.md) allows. The BFF's pair writes an empty object
under `uploads/`, at a key named for this run alone, and the store must answer
`200`. Catalog's pair then reads that key back, and the store must answer `200`
with an empty body. A write that is not answered `200` fails the run and skips
the read, because there is nothing written to read. The delete and list
probes of
[ADR-110](ADR-110-the-deployed-object-store-is-checked-between-rollouts-each-image-credential-pair-is-asked-to-delete-and-to-list-and-must-be-refused-985.md)
then run unchanged.
**Why.** A refusal proves nothing about a bucket the pairs were never granted.
An existing bucket that grants them nothing, such as staging or another
account's, answers every one of ADR-110's probes `403 AccessDenied`, as the
right bucket's policy does. A wrong `OBJECT_STORE_BUCKET` therefore read as a
pass, the heartbeat was sent, and the real bucket's policy was never examined
([#1221](https://github.com/alexander-shamray/blueprint-backend/issues/1221)).
Only a request a pair must be allowed can tell the two buckets apart. The BFF's
pair may only write under `uploads/`, and Catalog's may read there, so the
write and the read-back need no object staged beforehand. Catalog's write under
`products/` is left out. Both pairs answering for one object also shows both
secrets are live. ADR-110 wrote nothing because an object under `products/` is
served publicly and never expires; `uploads/` is neither, and its lifecycle
rule expires it after a day
([Appendix E.6.11](../appendix-e-reference-domain.md)).
**Consequences.** The check now writes to a production bucket, one empty object
per run. The key is fresh each run, so a read that succeeds can only have found
this run's write. The objects are removed by `uploads/`'s one-day expiry, which
the check relies on and does not verify: a store without that rule keeps one
empty object a day. A fixed key overwritten each run was rejected. It would not
depend on the expiry, but the read would then need to prove it found this run's
object, which means a body and signing over it. The check still does not prove
the policy allows everything the services need, such as Catalog's write under
`products/`, which it never makes. A store that answers `200` to everything
would pass the control, which is why the refusal probes still follow it.

**Supersedes in part
[ADR-110](ADR-110-the-deployed-object-store-is-checked-between-rollouts-each-image-credential-pair-is-asked-to-delete-and-to-list-and-must-be-refused-985.md)**:
its Decision's clauses that each pair signs three requests and that a request
passes only on `403 AccessDenied`, which now hold of the refusal probes alone,
and its Why's clause that the check never writes to a production bucket.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
