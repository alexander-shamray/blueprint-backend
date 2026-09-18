# The secret scan

**The claim, and it is deliberately narrow: a credential of a recognised
shape cannot reach `main` through a pull request without somebody writing
down why it is there.** It is the secret half of
[§15.1](../../docs/backend-architecture/15-cicd-deployment.md)'s first node;
[`docs/secrets.md`](../../docs/secrets.md) is what an operator does when it
fires.

## What it reads

The working tree, as text — no restore, no SDK and no network, on the
licence gate's terms, which is what lets it run first. Not all of it: it
never descends into the directories `SKIP_DIRS` and `SKIP_ROOT_DIRS` in
`secret_scan.py` name — build output, editor and vendored trees, and `.git`,
because this gate is about the tree and not the history — and it skips a file
it reads as binary. A credential written only there is outside its claim.
Against the rest it
applies named rules, each with a positive case and a near miss in the suite,
and it reads the accepted findings from [`allowed/`](allowed/), whose README
owns what an entry may say and which file an entry goes in.

An allow-list entry that matches no finding **fails the build**, because a
suppression whose finding has gone is a decision nobody has re-read.

## What it does not claim

- **History.** It reads the working tree, so a credential committed and then
  deleted is still in the pack and still compromised; `docs/secrets.md`
  states what applies then.
- **Anything it has no rule for.** It is a pattern scanner, not an entropy
  oracle: the list of rules is the list of things it can find, and a
  high-entropy string under a name nobody predicted passes.
- **Whether a value is live.** A sample password and a production one are the
  same shape, which is why the accepted ones are enumerated rather than
  guessed at.

## Its second caller

[§4.5](../../docs/backend-architecture/04-solution-structure.md)'s scaffold
imports `secret_scan.py`, runs it over what it has just rendered and appends
the allow-list lines that render needs, taking the fingerprints from the
scanner rather than computing them. So this gate is a library as well as a
job, and which substring each rule matches has one implementation.

## How it runs

First in the `licence-gate` job of [`ci.yml`](../workflows/ci.yml): its
suite, then the scan.
