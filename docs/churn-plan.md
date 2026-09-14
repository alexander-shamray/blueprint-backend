# Churn — the measurement and the plan

**Goal.** A pull request is reviewed by a person, and what that person reads
is the diff. Every line the diff carries that is not the change — a comment
rewritten to describe its own correction, a count recomputed, a fourth
document told about a gate — is a line the reviewer has to read and cannot
approve or refuse on its own merits. This plan measures where those lines
come from and sequences the work that stops them, in the form
[`change-locality-plan.md`](change-locality-plan.md) set: measured on a
named commit, one PR per step, an exit test per step.

The rule the plan enforces is already written and this file does not repeat
it: [`change-locality.md`](change-locality.md) §2 for documents, and the
*Comments* section of [`style-guide.md`](style-guide.md) for the same rule at
the code's side. What this file adds is the evidence that the second was
needed, and the order in which the corpus is brought under it.

## 1. What was measured

**Measured on 2026-09-14 against `main` at 0848840, the merge of #204.**
Every figure in this section and the next is a record of that day, per the
contract's §2, and nothing here is kept current.

### The history

Over 95 merged pull requests and 1,413 non-merge commits:

| | |
|---|---|
| Commits by type | `fix` 665 · `docs` 370 · `test` 96 · `chore` 80 · `feat` 75 |
| Per pull request | 13.8 commits, 7.0 of them `fix`; 43 of the 95 carried 15 commits or more |
| What a `fix` commit touched | Markdown and code together 371 · Markdown only 190 · code only 116 |
| Largest pull requests | 31 of 95 changed more than 3,000 lines; the mean is +2,107 −385 over 25 files |

Nine `fix` commits land for every `feat`, and four in five of them edit a
document. A `fix` here is mostly a review round: the loops in `/ship` run
until Grok is clean twice and Copilot once, and each round that finds
something produces one commit.

The locality contract merged on 2026-09-03. The 75 pull requests before it
and the 20 after:

| | Before | After |
|---|---|---|
| Commits per PR | 15.2 | 8.6 |
| `fix` commits per PR | 8.1 | 2.9 |
| Files re-edited four or more times inside one PR | 4.8 | 2.1 |

The contract halved the churn. What it left is the subject of this plan.

### Where the documents churn

Files by the number of pull requests in which they were edited four or more
times — the same file re-opened round after round inside one branch:

| File | PRs | Re-edits |
|---|---|---|
| `CLAUDE.md` | 44 | 352 |
| `docs/pr-decision-log.md` | 17 | 117 |
| `docs/testing.md` | 16 | 91 |
| `docs/backend-architecture/09-messaging.md` | 12 | 101 |
| `docs/backend-architecture/12-test-strategy.md` | 12 | 73 |
| `docs/backend-architecture/appendix-d-type-inventory.md` | 11 | 66 |
| `.claude/commands/ship.md` | 10 | 97 |
| `docs/backend-architecture/04-solution-structure.md` | 10 | 58 |
| `docs/backend-architecture/appendix-a-adrs.md` | 9 | 68 |
| `.claude/commands/security-sweep.md` | 8 | 48 |

Most of that table is what the locality plan already closed: the primer was
rewritten, the decision log and lessons were frozen, the ADRs were split,
Appendix D was retired as a write surface. Since the contract, the files
edited most are different ones:

| File | Commits since 2026-09-03 |
|---|---|
| `docs/repo-map.md` | 21 |
| `docs/testing.md` | 14 |
| `docs/change-locality.md` | 13 |
| `docs/change-locality-plan.md` | 13 |
| `docs/backend-architecture/04-solution-structure.md` | 13 |
| `.claude/scripts/test_grok_helpers.py` | 12 |
| `CLAUDE.md` | 11 |

The shape behind the first five is one gate, four documents. #204 added
one gate and changed eight files, four of them Markdown: the repo map says
what the gate reads, `testing.md` says how it runs, §4.1 states the claim it
enforces, and the primer names it. The same four were opened for the
locality gate and the output gate, and every review round that sharpened a
gate's diagnostic re-opened them.

### Where the code churns

Inside `src/` and `tests/` the pull requests re-edit fewer files, and the
ones they re-edit are the ones with the most prose in them:

| File | Commits | Comment lines / total |
|---|---|---|
| `src/Services/Ordering/Ordering.Infrastructure/Messaging/OrderFulfilmentSaga.cs` | 48 | 1,093 / 1,544 |
| `tests/Ordering.Application.Tests/OrderFulfilmentSagaTests.cs` | 50 | 964 / 2,975 |
| `src/Gateway/Gateway.Api/Program.cs` | 21 | 242 / 429 |
| `src/BuildingBlocks/Common.Infrastructure/Messaging/RetentionPurgeService.cs` | 17 | 216 / 547 |
| `tests/Platform.IntegrationTests/ContractTests.cs` | 21 | 263 / 1,029 |
| `tests/Common.Web.Tests/RealmImportTests.cs` | 24 | 212 / 699 |
| `tools/new-service/new_service.py` | 67 | 729 / 3,240, plus 367 docstring lines |
| `.claude/scripts/test_grok_helpers.py` | 71 | 2,117 / 7,067, plus 752 docstring lines |
| `deploy/helm/smoke.sh` | 30 | 533 / 1,274 |

Across the whole C# corpus, 26,942 of 69,260 lines are comment lines —
12,541 `//` and 14,401 `///`. By tree: the building blocks are 59% comment,
the gateway 64%, the BFF 58%, each service about a third, the tests 35%.
The Python and shell under `.claude/`, `.github/`, `deploy/` and `tools/`
carry 8,062 `#` lines in 34,501, before docstrings. The configuration files
are the same: `.editorconfig` is 204 comment lines of 323,
`Directory.Build.props` 90 of 160, `ci.yml` 383 of 660.

The comments are not the kind the phrase "remove obvious comments" brings
to mind. A random sample of seventy C# comment lines held almost none that
restate the line below. What it held was argument — long, bold, and about
itself. Counted over every comment line in C#, Python, shell and YAML:

| Pattern | Lines |
|---|---|
| A pull request or issue number | 642 |
| "used to", "went stale", "this sentence said", "carried a count" | 406 |
| Copilot, Grok, CodeQL, "found in review", "round" | 467 |
| "measured" | 219 |
| `**bold**` | 846 |
| `//` blocks of ten lines or more | 343 (150 in `src/`, 193 in `tests/`) |
| `<remarks>` blocks | 562 |

Three specimens, each in a file the table above names:

- `Program.cs` in the gateway explains `EnableForHttps = true` in
  forty-three lines that are ADR-020's argument, paragraph for paragraph —
  BREACH not CRIME, the forwarded scheme, the decision at first write, the
  default type list — and names the two tests that pin it.
- The saga's property block says of one sentence: *"This line carried a
  count and it went stale exactly as counts here do. It said 'the SECOND of
  the two', having already been corrected once from 'the only one'; #126
  made it three without touching this comment's subject."*
- `IdempotencyBehavior`'s summary says: *"This sentence named a gate that
  did not exist until a review asked for it."*

The same prose is in the documents that describe the rules. The style
guide's *Settled choices* row for extension declarations narrates two
recounts of its own list; its callout paragraph explains at length why a
total is no longer written there and then keeps two other counts current.

## 2. Why it happens

1. **The contract's rule stopped at the document boundary.** §2 forbade
   "since PR-NN" and "this used to say" in *any document*, and nobody read a
   comment as one. The chapters were cleaned and the same sentences moved
   into `///` blocks, docstrings and `.editorconfig`, where no gate and no
   audit pass looks.
2. **A review finding against prose was answered by narrating the fix.**
   The loops flag a stale claim; the agent corrects it and, to keep the next
   reviewer from asking why, writes down what it used to say and which round
   changed it. That sentence is the one the round after finds stale. A
   comment that has been corrected three times is three paragraphs long,
   and the property block above is the worked example.
3. **Comments cite the tests and the tests cite the comments.** A source
   comment names the test that pins it; the test's summary names the
   sentence in the source it was written against. A rename on either side
   is a fix commit on both, and a review that reads one is sent to the
   other.
4. **The argument for a decision was written where the decision is used.**
   ADR-020 is one file; its argument is in `Program.cs` too, and in
   `10-api-gateway.md`, and each copy has been reconciled to the others by
   hand. A comment that argues at ADR length is an ADR without a number.
5. **One gate has four documents.** `repo-map.md`, `testing.md`, §4.1 and
   `CLAUDE.md` each describe every gate from their own angle, so a gate's
   every change is four edits, and a diagnostic sharpened in review round
   five re-opens four files for one sentence.
6. **The plan records its own status.** "Both landed on 2026-09-10" is a
   changelog line in a document the contract says is not one, and a plan
   that says what landed is edited by every step that lands.
7. **Some files are one file.** `ship.md` is 1,621 lines, `new_service.py`
   3,240, `test_grok_helpers.py` 7,067, the saga 1,544 of which 1,093 are
   comment. A file that big is a mutex for every agent that touches its
   subject and a diff nobody reads whole.
8. **The subject line is an aphorism.** *"the third grant along, and a gate
   that asserts on grants"*, *"a hash begins a comment where a word begins,
   and ship.md's last six"*. The body argues well; the subject, which is what
   `git log --oneline` and the PR's commit list show a reviewer, says nothing
   they could search for.

## 3. The rule that stops it

Stated once each, and cited from here:

- a comment says why, cites the owner, and carries no history, count, test
  name or copy of an argument — the style guide's *Comments* section;
- the contract's §2 now names comments, XML docs, docstrings and
  configuration comments as documents for its purposes;
- a finding against a comment is closed by shortening or deleting it — the
  contract's §5, which the review commands already cite for a restatement;
- a commit subject names what changed in words a reviewer would search for,
  and the aphorism goes in the body — the primer's *Commit messages* line.

## 4. The work, in order

Each step is one pull request unless it says otherwise. Steps marked ∥ run
in parallel once step 0 has merged. A sweep changes comment lines only, and
the proof is not `git diff -w`, which also hides whitespace inside strings:
it is the two sides of every file compared with their comments stripped —
`//` and `///` in C#, `#` lines and docstrings in Python, `#` in shell and
`.editorconfig`, `<!-- -->` in MSBuild — which must be byte-identical. The
exit test is the grep in step 1 over the tree the step names, with that
tree's comment syntax in place of `//`. The scaffold is the exception: its
templates are string literals, so its proof is the service it renders,
compared before and after with C#'s comments stripped, and its own suite.

### Step 0 — the rule, and the first sweep

The pull request this file arrives in. It adds the *Comments* section to the
style guide, names comments in the contract's §2, states the rule in the
primer's *Style* and *Commit messages* bullets, and sweeps a first set of
the files section 1 measured: the saga and its suite, the gateway's
composition root, the retention purge and its policy, the
authentication extensions, the idempotency behaviour, each service's
`DependencyInjection.cs` files, the suites of the same mechanisms, the
Python gates, the Helm smoke script, `.editorconfig` and
`Directory.Build.props`. Class D for the documents, A and B for the trees,
named in the touch-set row.

Done when the solution builds, the unit suites and the gates' suites are
green, the scaffold's suite is green and the service it renders builds:
Catalog is its template, the scaffold anchors its rewrites on Catalog's
comments, so an anchor a sweep moves is re-anchored in the same pull
request, and its suite reads text without compiling it, so only the
rendered build (`docs/testing.md`, *The scaffold's suite*) proves a Catalog
test change.

### Step 1 — sweep `src/` ∥ (one PR per project)

`Common.Domain` and `Common.Contracts` together, then `Common.Application`,
`Common.Infrastructure`, `Common.Web`, `Catalog`, `Ordering`, `Gateway.Api`,
`Web.Bff`. Class A or B by the tree. Catalog's PR runs the scaffold's suite
and the dogfood sequence in `repo-map.md`, because the scaffold reads
those files at run time and asserts on the comments it renders.

Done for a tree when these are empty over it — the patterns are the ones
section 1 counted, and the counts are the baseline:

```bash
rg -n -e '#[0-9]{2,}\b' -e 'PR-[0-9]+' -e 'Copilot|Grok|CodeQL' \
   -e 'used to|went stale|this (line|sentence|comment) (said|carried)' \
   -e '\*\*' --glob '*.cs' <tree> | rg '^\S+:\d+:\s*//'
```

and no `//` block in the tree runs to more than ten lines:

```bash
awk 'function flush(f, l) { if (run > 10) print f ": " l - run; run = 0 }
     FNR == 1 { flush(pf, pl) }
     /^[[:space:]]*\/\/\/?/ { run++; pf = FILENAME; pl = FNR + 1; next }
     { flush(FILENAME, FNR) }
     END { flush(pf, pl) }' $(git ls-files '<tree>/*.cs')
```

### Step 2 — sweep `tests/` ∥ (one PR per suite)

The same rule and the same greps, one suite per pull request. A test's
summary cites the section whose rule it checks and stops; it does not quote
the source comment it was written against. Class A for the service's
suites, B for a building block's.

### Step 3 — sweep the Python and shell ∥

`tools/new-service`, `deploy/`, the gate directories under `.github/` —
docstrings included, one tree per pull request, Class D; the workflows are
step 5's. The scaffold is the hard one: its tests assert on
comments it renders into a new service, and the assertion is a claim about
what a generated comment may say. Those assertions stay and are the exit
test for that tree; the comments they read are rewritten to the rule and the
assertions to match, in the same PR.

### Step 4 — sweep the harness

`.claude/` is one agent, and `docs/harness-boundaries.md` is read first.
`ship.md` carries its own review history in its step bodies — *"round 2's
finding resolved from the other end"*, *"two rounds of the same
disagreement"* — and the sweep moves that into commit bodies where it
already is. Class D.

### Step 5 — sweep the configuration ∥

`.editorconfig`, `Directory.Build.props`, `Directory.Packages.props`, the
workflows under `.github/workflows/`. A setting keeps one paragraph saying
why it is set the way it is; a suppression keeps its argument, because the
primer requires one; nothing keeps the history of how its comment was
corrected. Class D.

### Step 6 — one owner per gate ∥

Each `.github/<gate>/README.md` owns what its gate reads and what it claims.
`repo-map.md` keeps one line per entry and cites the README; `testing.md`
keeps the invocation and nothing about the claim; §4.1 keeps the rule the
gate enforces, in one sentence, and names the gate. `CLAUDE.md`'s
workflow line lists the gates and says nothing else about any of them.

Done when a pull request adding a gate touches its own directory, `ci.yml`,
one line of `repo-map.md` and, if the gate enforces a chapter's rule, one
sentence of that chapter — and the locality gate's Class D row says so.

### Step 7 — the plans stop recording status ∥

`change-locality-plan.md` loses its "landed on" sentences and the
qualifications written after the fact; a step's closure is the pull request
that closed it, found from `git log`. The style guide's own narration goes
under the rule it now states: the *Settled choices* row that recounts its
list twice keeps the list, the callout paragraph keeps the two forms and
drops both counts and the story of the total. Class D. This file is under
the same rule from the day it merges: nothing in it is updated to say a
step landed.

### Step 8 — split what is still too big, after its sweep

Measured again after steps 1 to 4, and split only where the code rather
than the prose is still over about eight hundred lines. The candidates and
their seams:

- `OrderFulfilmentSaga.cs` — one partial class per state
  (`Initially`, `AwaitingStock`, `AwaitingPayment`, `AwaitingConfirmation`,
  `Confirmed`, `Compensating`), the events and the activities that only one
  state uses beside it;
- `OrderFulfilmentSagaTests.cs` — one class per state, sharing the harness
  fixture;
- `new_service.py` — a package: render, patch, verify, with the
  command line as the fourth module;
- `test_grok_helpers.py` — one module per helper under test.

Class A for the saga and its suite, D for the tools and the harness. A
split is not done until the tests that were green before it are green after
it, unchanged.

### Step 9 — a gate for comments

`.github/comment-gate/`: a Python gate over the pull request's *added* lines
that fails on the patterns in step 1's grep and on a comment block over ten
lines, with a README that says what it reads and a suite whose subject is
what the gate is looking at — a file it must find, a pattern it must match,
a line it must not judge because the line is code. Added lines only, so the
corpus is brought under the rule by the sweeps and not by the gate refusing
every pull request until they land. Class D. It makes the mechanical half
of the *Comments* section — the patterns and the block length — a build
failure; citing the owner and not copying an argument stay with the
reviewer, as the guide says.

### Step 10 — the subject line

`/commit`'s guidance names the subject rule the primer states, with the
form: `<type>(<scope>): <what changed>, <where>` — a symbol, a file or a
behaviour a reviewer could search for. The harness is read before it is
edited. Class D.

## 5. What is deliberately not touched

- `docs/superpowers/`, the decision log and the lessons file: frozen, and the
  history they hold is the history the comments should have pointed at.
- The ADRs: appended, never rewritten. A comment that argued at ADR length
  and turns out to state a decision no ADR records gets a new ADR, not a
  longer comment.
- The commit bodies already written: the record of how a line came to be,
  which is where the sweeps send a reader.

## 6. Questions for review

1. **Whether a `<summary>` on every public member is worth keeping.** The
   build does not generate a documentation file, so a summary is read only
   in the source and in tooltips. The sweeps keep one that says something
   the name does not and delete one that restates it; the alternative is to
   delete them all and let the name carry the member.
2. **Whether the comment gate should judge only added lines.** Recommended
   above; the other choice is to judge every changed file, which turns each
   sweep into a prerequisite for touching the file at all.
3. **Whether a test may name the invariant's owner.** The rule says a test
   cites the section and not the source comment; a test that says *"the
   registration below relies on the container omitting an unsatisfied open
   generic"* is stating the invariant, which is allowed, and a test that says
   *"see the remark on `IdempotencyBehavior.Retention`"* is not.
