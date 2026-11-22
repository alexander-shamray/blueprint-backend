# ADR-215 — The object store check asks the BFF's pair to write under products/ and must be refused (#2166), amending ADR-110

**Decision.** After the refusal probes of
[ADR-110](ADR-110-the-deployed-object-store-is-checked-between-rollouts-985.md),
`deploy/objectstore/store_check.py` asks the deployed store, with the BFF's
pair alone, for a `PUT` under `products/` at a key named for this run. The
request sends no body and is signed over a payload hash that is not the empty
body's (`UNSENT_BODY_SHA256`). It passes only when the store answers `403`
with the code `AccessDenied`, as ADR-110's probes do; any other answer fails
the run.
**Why.** [§10.8](../10-api-gateway.md) says the BFF's pair writes under the
upload prefix and nothing else, and the check asked only for deletes and a
list, so a store whose policy also let the BFF's pair write the public prefix
passed (#2166, item 12). Only a write can ask that question, and a write that
succeeded would leave an object under `products/`, which is served publicly
and never expires: the reason ADR-110 wrote nothing and
[ADR-148](ADR-148-the-object-store-check-writes-a-sentinel-with-the-bffs-pair-and-reads-it-back-with-catalogs-1221.md)
wrote only under `uploads/`. A store decides authorisation from the request's
headers before it reads the body, so a refusal answers `AccessDenied` whatever
hash is claimed, while a store that wrongly allows the write compares the
claimed hash with the body it received, refuses the mismatch (S3 answers
`400 XAmzContentSHA256Mismatch`) and stores nothing. The probe therefore asks
the question the way ADR-110's absent-key delete does: the answer a wrong
policy gives cannot change the bucket. Catalog's pair is not asked, because
§10.8 allows it that write.
**Consequences.** The check relies on the store verifying a signed payload
hash, which S3 does and the check does not establish of another store. A
store that skips the verification and wrongly allows the write keeps one
empty object under `products/` per run, on a run that is red for it, until
the policy is corrected and the object removed by hand. A store that read the
body before deciding authorisation would answer the mismatch to a refused
write too, which reads as a violation: the check fails closed, never open.
The probe proves the BFF's pair is refused that prefix and no other, so a
policy that allowed it a write elsewhere outside `uploads/` still passes.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
