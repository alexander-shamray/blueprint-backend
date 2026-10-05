# Procedure — a personal-data incident

**The clock: the deployment's statutory window for reporting a personal-data
breach, counted from when the incident is discovered.** This file holds no
figure for it. The window is a value of the deployment, as ADR-053's windows
are, and is stated in the deployment's own record of its jurisdiction;
[ADR-053](backend-architecture/adr/ADR-053-a-jurisdiction-is-a-value-the-deployment-is-given.md)'s
table says what each worked example's counsel is to confirm, and none of it
was read from this repository.

| | |
|---|---|
| Trigger | Personal data disclosed, lost, altered, or reached by someone who should not have it — reported by a person, a processor or a log, since no alert can see it |
| Map | [`docs/personal-data.md`](personal-data.md) |
| Owner | The deployment's data-protection lead, a role this platform does not name |

**This is an engineering procedure and nobody's legal advice.** It says what
an engineer can do before counsel arrives and what to hand counsel when they
do. Whether the incident is reportable, to whom and in what words is
counsel's.

**It lives here and not under `docs/runbooks/`**, because ADR-053 placed it
beside the personal-data extension rather than among the procedures an alert
points at: an incident with no signal has no alert, and
[§13.9](backend-architecture/13-observability.md)'s gate pairs every runbook
with one. The runbook index points here so an on-call engineer finds it.

## 1. Start the clock and tell the role

Write down when the incident was discovered and by whom, in UTC and in the
deployment's zone, before anything else; the clock runs from that moment.
Tell the data-protection lead and whoever holds incident command for the
deployment. Name roles in the incident record, not people's mailboxes —
the record outlives who held the role.

## 2. Establish what was exposed, and from which store

Read [`docs/personal-data.md`](personal-data.md) row by row against what is
known: which holding, which owner, what it holds. Most holdings keep a
customer's id and nothing more; the ones holding a mailbox or a postal
address are few and named there, and so are the processors shown either.
Record for each affected holding:

- what kind of data — an id, a mailbox, a postal address, a language;
- roughly how many customers, by counting rows, never by copying them;
- the time range the exposure covers;
- whether a processor holds a copy, and which.

Do not export the exposed rows to answer these questions. A count and a time
range are what counsel needs; a second copy of the data is a second incident.

## 3. Preserve the evidence

- **Logs and traces have a lifetime**
  ([§13.4](backend-architecture/13-observability.md)), and it keeps running.
  Export the window the incident covers from the log and trace stores before
  it lapses, to storage the incident record names and access is limited to.
- **A store's state** is preserved by the deployment's own backup, taken now
  and kept beside the incident record rather than restored over anything
  ([§15.3](backend-architecture/15-cicd-deployment.md) states whose backups
  those are).
- **Do not purge** an `_error` or `_skipped` queue, an abandoned outbox row or
  a retention window early to tidy up; each may be evidence of what moved
  where.

## 4. Contain it

- **Rotate the credential involved**, by its row in
  [`docs/secrets.md`](secrets.md#rotation): a client secret, a database
  credential, a broker credential. Rotate the one that was exposed first and
  only then any that might have been, so the record says which.
- **A stolen token stays valid until it expires**: revocation is bounded by
  the token's lifetime and no denylist exists
  ([ADR-033](backend-architecture/adr/ADR-033-revocation-is-bounded-by-the-token-lifetime-and-no-denylist-exists.md)).
  Disabling the client or the user in the realm stops new tokens; the ones
  already issued run out on their own.
- **A processor's copy is the processor's to contain**, under its agreement
  with the adopter. Tell them through the channel that agreement names.

## 5. Hand over to counsel

Bring the discovery time, the holdings and counts from step 2, the evidence
preserved in step 3 and what step 4 changed, each with its time. Counsel
decides whether the incident is reportable inside the clock above and
whether the customers are told.
