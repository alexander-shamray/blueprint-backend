# Lessons that travel

**Grep it for the lesson you need; never read it whole.** It is about 20k
tokens and closed, and one lesson is what a question about one area needs.

**What this repository has learned that generalises past the PR that found
it.** These were `CLAUDE.md`'s *Lessons that travel* section until that section
reached 755 lines — a quarter of a file loaded into every session's context,
holding knowledge that is load-bearing while you are working in the area an
entry covers and inert the rest of the time.

**The entries below are verbatim in their arguments**, on
[`pr-decision-log.md`](pr-decision-log.md)'s terms and for its reason: a summary
of an argument is how a rule gets "corrected" back. Three edits were made on the
way out, and naming them is cheaper than a claim that does not survive a grep.
The `### Lessons that travel` heading became this file's title. Every link
*target* pointing at the decision log was rebased from `docs/pr-decision-log.md`
to `pr-decision-log.md`, because a path written relative to the repository root
does not resolve the same way from inside `docs/` — the link **labels** were
left alone, and deliberately, since a label naming the repo-root path is what a
reader greps for. And one **self-reference** was rebased: the string-match entry
cited "the C# style section below", which did not travel and would otherwise now
point at nothing. Not one argument was shortened, and no entry was dropped.

**That citation has since been re-pointed a second time**, and the reason is
worth keeping beside the first: the C# style section itself later left
`CLAUDE.md` for [`style-guide.md`](style-guide.md), so an entry rebased onto
`CLAUDE.md` was aimed at a section that had moved on. A rebased reference is not
a settled one — it names wherever the prose lives *today*, and the next
extraction has to grep for it again.

**Three lines were also rewrapped**, on `pr-decision-log.md`'s precedent — it
reflowed one 136-column line on the way out and said so. Three entries here ran
to 116, 91 and 81 columns against the 80 this repository wraps prose at; they
were over on `main` too, inside `CLAUDE.md`, where nothing had caught them.
Rewrapping moves whitespace and no words, so the verbatim claim above survives
it — but it is declared rather than left to be discovered, because "verbatim"
and "I reflowed three lines" are exactly the pair a reader deserves to be told
about rather than to diff for.

**Read this before working in an area an entry covers**, exactly as you would
read the decision log. `CLAUDE.md` keeps the rules an agent needs in order to
*act*; this file keeps the measurements those rules were bought with. Where one
of these disagrees with a blueprint chapter, the chapter wins and the
disagreement is a bug report against one of them.

> **This file is frozen as written.** A new lesson is a test or a gate whose
> subject is what the gate is looking at — the rule the first entry below
> states, and the one `CLAUDE.md` keeps — and not a paragraph here; the
> entries stay verbatim, on the terms above. Where a lesson genuinely has no
> test to carry it, its entry is one paragraph under a new `##` heading at
> the end of this file, and nothing above that heading moves. That is rare
> enough that two pull requests meeting at the tail is a conflict worth
> accepting, where a file every PR appends to is the cost
> [`change-locality.md`](change-locality.md) exists to withdraw.

> **This file is outside the blueprint tree, so nothing structural catches its
> drift.** `/check-links` checks `docs/backend-architecture/` and the root
> README's entry point into it, so a file anywhere else is outside its scope
> whatever links it, and `/validate-blueprint` does not name this one —
> `testing.md` and `roadmap.md` are the only two of those siblings it does. The
> one rule in `CLAUDE.md` covers this file, and that is all that does.
>
> **The siblings are described rather than listed on purpose.** Three copies of
> this callout named three different sets the moment two new files arrived,
> which is what a list with no code to check it against does.
>
> **A predicate is only better than a list if it is true.** These three
> callouts first said this file is in no index “like every document under
> `docs/` that is not the blueprint tree”, which is false twice over:
> `docs/runbooks/README.md` is an index of the runbooks beside it, and the
> root `README.md` links `docs/roadmap.md`. Scope is the durable fact and
> being unindexed never was — a false predicate is a stale list with an
> argument in front of it.

---

These generalise past the PR that found them.
[`docs/pr-decision-log.md`](pr-decision-log.md) carries the full argument
and the measurement behind all but one of them; the exception says so on its
own line rather than sending a reader to a file that does not hold it.

- **A gate that silently stops covering the newest surface is this
  repository's most-repeated failure.** The only defence is a test whose
  subject is *what the gate is looking at*, not what it found.
- **Do not write down an ordering claim a test is not making.** Middleware- and
  behaviour-order rules are testable case by case, and which ones are has to be
  measured: `UseAuthentication` deleted breaks nothing a test catches, while
  `UseForwardedHeaders` moved does. Keep the line; do not believe a test is
  watching it.
- **A middleware acting on the *response* decides below everything under it.**
  Reasoning about what it "sees" from the position of its `Use` call is
  reasoning about the wrong moment.
- **The fix that lands in code and not in the sample it came from re-arms the
  defect for the next reader.** After fixing a line that came from a sample,
  grep the blueprint for *the line you replaced*, not for the topic.
- **A `ProjectReference` — and a linked `.proto` — is a `COPY` line in a
  Dockerfile.** Forgetting it breaks the images silently until someone runs
  one; `dotnet build Platform.slnx` cannot see it and neither can the
  path-filtered compose smoke.
- **Blank counts as missing.** An environment variable set to the empty string
  reaches `Configuration` as `""`, not null, so `GetRequiredSection` proves
  only that a section exists. Guard on the bound values.
- **A premise about who calls a method is falsified by the next PR that calls
  it.** Both `EfUnitOfWork`'s tracker and the `No_client_secret_is_committed`
  rule were correct until a second caller existed.
- **A premise is a statement about one revision; the operation runs over
  another.** `/review-grok`'s defence against an edit through a symbolic link
  was a gate on `main` — the helper suite fails on any tracked mode `120000`,
  on every push — while the command it protected reads the *branch* under
  review, locally, before CI has said anything about it. Both halves of the
  premise were true and neither covered the exposure, because a branch is what
  introduces files. When a control reads "this repository never contains X",
  ask which revision that is true of and which one the operation opens; where
  the two differ, the premise is defence in depth and the check is still owed
  (#181).
- **"A test that would pass" is a claim about a run nobody performed.** Measure
  the counterfactual — three such claims in one PR were all the opposite of
  what happens. **And a counterfactual that does not rebuild is the same
  claim wearing the evidence of a measurement**: patching a file to its old
  behaviour with a script that normalised the line endings failed IDE0055, so
  `--no-build` ran the stale assembly and reported six passes where the same
  six fail. Read the build result before reading the test result — a green
  counterfactual is the outcome that should be checked hardest, because it is
  the one that says the tests are worthless.
  **The same trap sits on the way back out, and it points the other way.**
  Restoring the file with `shutil.copy2` restores its *modification time* along
  with its bytes, so MSBuild sees nothing to do and the next `--no-build` run
  executes the assembly built from the broken version. Measured on the branch
  that added §8.5's marker: five tests reported failing against source that was
  already correct, in a run whose whole purpose was proving the branch green.
  It fails in the direction that wastes time rather than the direction that
  ships a defect — which is exactly why it is worth writing down, because a
  failure that looks like a real one gets debugged as one. `touch` the restored
  files, or build with `--no-incremental`, before believing either outcome.
- **A suite that enumerates from `git ls-files` is blind to the work you have
  not committed, so a green run taken before `git add` says nothing about the
  state you are about to commit.** Measured on the branch that extracted this
  file: `test_validate_blueprint_may_only_edit_what_it_audits` reads
  `git ls-files docs/` and requires every entry a command does not audit to be
  denied to it. Two new files sat untracked while the suite ran, so the
  subtests that would have failed **did not exist**, and it reported 248 green;
  the same suite on the same content failed twice in CI one commit later. The
  failure is silent in the worst direction — a smaller enumeration is not a
  smaller *result*, it is the same "OK" with the cases removed. This is the
  gate-coverage lesson at the top of this file with the clock moved: not a gate
  that stopped covering a surface, but one whose subject **had not arrived
  yet**. Where a check derives its cases from the index rather than the working
  tree, commit first and then run it — and treat a pre-commit pass as
  provisional whatever it prints.
- **A measurement says what the code *does*; it never says what it *may* do.**
  Fetch the specification section and paste the sentence.
- **A field a caller chooses cannot answer a question about provenance**,
  however strongly its values correlate with one. §9.6's saga needed to tell
  its own echo of a cancellation from somebody else's, and
  `OrderCancelled.Reason` looked like the discriminator because the saga's
  four reasons and the customer's one partition the vocabulary — until you
  read §11.4, whose endpoint parses **all five** codes. The correlation was
  real and the inference was not. What closed it was a field written as a
  literal at the entry point that knows the answer and never bound from a
  request, which is the same rule `CommandOrigin` one layer out already
  states. **Ask what would be true if the caller were hostile, or merely
  unusual** — the two give the same answer here, and only the second has to
  be plausible for the branch to be wrong.
- **A rule whose stated test is a string match will be enforced as one**, by a
  reviewer or by whoever greps next. State the rule, not the grep. This one's
  argument is the `.ToArray()` narrowing in [`style-guide.md`](style-guide.md)'s
  *Statements and types*, not the log.
- **`ASPNETCORE_ENVIRONMENT=Development` leads every host-run block naming an
  authority.** No project ships a `launchSettings.json`, so `dotnet run`
  selects Production, where `RequireHttpsMetadata` is on and a plain-HTTP local
  authority is never reached. Containers set it, which is why the Compose path
  never shows it. **And it is the wrong variable for half the hosts here**: a
  `*.Migrator` is a generic host — `Host.CreateApplicationBuilder` — which
  binds its environment from `DOTNET_ENVIRONMENT` and ignores the
  `ASPNETCORE_` one entirely. Measured, because the failure is silent in the
  safe-looking direction: a guard written on `IsDevelopment()` there is not
  merely
  fail-closed, it is permanently closed, and the person debugging it goes
  looking at the feature rather than the variable. Two more measurements sit
  beside it. `GetValue<bool>` **throws** on an environment variable set to the
  empty string rather than returning false, so a blank value blocks a
  pre-upgrade hook instead of disabling a flag — `bool.TryParse` on the raw
  string is what refuses `""`, `null`, `"1"` and `"yes"` alike. And a blank
  environment name leaves `IsProduction()` false as well as `IsDevelopment()`,
  so `!IsProduction()` fails **open** on exactly the value a templating mistake
  produces. Name the environment you want, never the one you do not.
- **A registration nothing resolves at startup fails at the first message, and
  the suite that catches it reports the symptom.** `ValidateOnBuild` never
  constructs an open generic and no host resolves a scheduler while it boots, so
  the service connects, declares and reports ready. The test then times out
  saying what did not happen, never why. Assert the registration itself where
  the container can still be enumerated.
- **Drive `TestServer` for what the *application* decides and a real server for
  what the *server* decides.** `ConfigureKestrel` is a silent no-op under
  `TestServer`, and the two are indistinguishable from the test.
- **A measurement taken through a tool that normalises reports the absence of
  the defect it was taken to find.** A CRLF Helm template renders a `\r` onto
  every line, and `deploy/helm/smoke.sh` went green against it here — MSYS
  `grep` strips the CR before matching, where the Linux runner does not. The
  first run said the hazard did not exist. Reading the bytes said it did. When
  a measurement clears a platform-specific hazard, check what the measuring
  tool did to the evidence.
- **A gate cannot fail on a file that is not there.** "The gateway renders no
  migration Job" passed against a gateway declaring `image.migrator`, because
  that chart carries no migration template — so the values key the comment
  credited was never consulted. This is the gate-coverage lesson at its
  earliest point: not a gate that stopped covering a surface, but one whose
  subject never existed. Assert the **agreement** between the two halves, which
  fails from either side.
- **A path the shell created is not necessarily a path the built-in readers
  resolve.** Under MSYS the two halves of this session disagree: `mktemp -d`
  prints a POSIX path, `Read`/`Grep`/`Glob` and every subagent resolve
  host-native ones, and on this host `/tmp` is `D:\tmp\alexa` for the shell and
  `C:\tmp` for the readers — different directories, both populated. Ask a
  tool that already knows the host spelling rather than converting one:
  `git worktree list --porcelain` prints `worktree D:/tmp/alexa/secsweep-nlPuf1`
  for a root the shell called `/tmp/secsweep-nlPuf1`, is usually already
  granted, and has no flag that takes a path. It is a **labelled record**, so
  compare the `worktree `-prefixed lines and strip that prefix before anything
  uses the value — and select the line that *appeared*, never the first one,
  which is the caller's own worktree and would pass a named-file proof against
  a snapshot nobody pinned. Take `--porcelain` and not the default: the aligned
  form repads every row when a longer path joins, so a set difference over it
  reports them all as new. `cygpath -m` is the obvious answer and the
  wrong one — a prefix grant of it also buys `cygpath -f <file>`, which prints
  an arbitrary file, so the
  translation smuggles in a shell reader. Keep both spellings in named variables
  and never leave the reader-side one unset: unbound, it turns the next absolute
  path into a workspace-relative one, which is how a check passes against the
  caller. The failure is silent in the direction that matters: a
  `Glob` **pattern** under an unresolvable root returns `No files found`, which
  is exactly what a clean scope returns. Only the `path=` form errors. The
  divergence was already diagnosed twice in-tree for *subprocesses*
  (`git-worktree-drop.sh`, `grok-review.sh`'s `host_path()`) and never for the
  readers.
- **The same host re-parses an argument on its way into `bash.exe`, so a `"`
  inside an argv element does not arrive.** A test that passed a regex
  containing `"` as an argument found nothing and reported the pattern under
  test as broken when the pattern was fine — silent in the direction that
  matters, because "matched nothing" reads exactly like "the input carried
  nothing". **Environment variables and stdin are not re-parsed**; use them for
  anything whose bytes matter and keep argv for values with no shell
  metacharacters. This is the divergence above one layer down: the argument,
  not the path.
- **“Stdin is not re-parsed” is a fact about the shell, and the *reader* on
  the far side still chooses an encoding.** The lesson above sends anything
  whose bytes matter through stdin, which is right and is not sufficient:
  `json.load(sys.stdin)` on this host decodes with the console codepage rather
  than UTF-8, so a pull request body read back through a pipe returned every
  non-ASCII character as its UTF-8 bytes read as cp1252 — an em dash as three
  characters, an emoji as four. Patched and uploaded, that rewrote fifteen em
  dashes, an arrow, a minus sign and the emoji in a live PR description.
  **It is invisible at every point a person would look**: the terminal renders
  both forms as *some* punctuation, the file compares equal to itself because
  both copies are corrupt, and the round trip reports success. It surfaced
  only when a `.count()` that had to be 1 came back 0, and was confirmed by
  reading code points. `sys.stdin.buffer.read().decode("utf-8")` is the fix.
  **The repair is worth knowing too, because the obvious one fails**: once a
  later edit has inserted correctly-encoded text, the document is mixed, and
  re-encoding the whole of it throws on the first clean character. Reverse it
  per run of non-ASCII and keep the run whenever the re-encoded bytes are not
  valid UTF-8 — which is exactly the test that tells a genuine character from
  a mangled one.
- **One tool's "valid" is not the next tool's, and the gap is where a value
  crosses between them.** PR-23 hit this three times in one file: `Release_1`
  is a legal OCI tag and an illegal Job name; `https://shop.example.com:443` is
  a legal URI and never matches `WithOrigins`; and `010.0.0.0/8` is legal to
  `IPNetwork.Parse`, which reads it as **octal** and silently yields
  `8.0.0.0/8`. A value validated against the alphabet of the system it comes
  from, and then handed to a system with a narrower one, fails at the far end —
  after the deploy has started. Validate against the **intersection**, and say
  in the guard which system each rejection is for.
  **The argv guard is the same shape with the systems one step apart**, and it
  is worth carrying because the value was not exotic: `git commit -m '<body>'`
  hands git one element that git reads as a *message*, and a guard written for
  *flags* read it as an argument list — so a commit body arguing about a
  forbidden transport was indistinguishable from a command using it, and a
  message reading `--output is bad` matched a prefix check. **A guard that
  inspects argv has to know which flags take a value**, because everything
  after one of those is data and no amount of care about the flag list fixes
  it. It refused its own commit, which is the loud version; the quiet version
  is a guard that has been silently reading somebody's prose as commands.
  **And the quiet version happened too, in the same file, on the same
  premise about position**: `git -C <dir> push …` puts `-C` where the
  subcommand goes, so a check written as `segment[0] != "push"` never fired and
  every refspec rule was bypassed. Nothing reported anything; it was found only
  because pushing a worktree's branch *is* a `git -C` command, so the guard's
  own branch happened to exercise it. **A subcommand has to be found, not
  assumed to be first** — and the general form is the one worth carrying: a
  parser that assumes a position is a parser that a prefix argument walks past.
  **Then it refused a second commit, on the other end of the same mistake**:
  `shlex` is a word splitter and not a shell, so it knows nothing about
  heredocs, and `git commit -F - <<'EOF'` with an apostrophe in the body is
  unbalanced to it and perfectly valid to bash. The guard had been written to
  refuse anything it could not tokenise — fail-closed, and wrong, because
  *unparseable* is not *hostile* and the reasoning behind it ("bash would fail
  on this too") was simply false about the parser actually in use. **When a
  guard cannot read its input, ask what the real consumer would do with it
  before deciding the failure direction**; here the answer was to degrade to
  the weaker check the settings deny already performs, which is never worse
  than the status quo and never a silent pass.
- **A validator must check the value it emits.** A CORS origin was trimmed,
  validated, and then written out untrimmed, so a trailing space passed every
  check and failed the host's own comparison. A check on one string and a write
  of another is worse than no check, because the check reads as authoritative.
- **A list drifts exactly as a number does.** PR-23 lost count of the same
  inventory three times — the source files its chart gate reads — and each fix
  was a copy that went stale again. What ended it was declaring the list once,
  beside the code that reads it, and asserting the other copy matches. The
  prose then carries the argument for each entry rather than the entries.
- **A registered name is not a live signal, and the registration is what makes
  the absence invisible.** §13.2 registers the HybridCache meter and
  `Microsoft.Extensions.Caching.Hybrid` 10.0.0 **publishes no meter at all** —
  it reports through `HybridCacheEventSource` with `PollingCounter`, which is
  EventCounters. A reviewer asking "is the meter registered" gets a yes, the
  dashboard is empty, and empty reads as healthy. The same shape reaches
  further than metrics — a declared dependency, a mounted config key, a bound
  endpoint. **Ask what writes to it, not whether it exists.**
- **"What is it owed" is a question to answer by reading, not by inferring
  from what is absent.** The same alert was first diagnosed as owed a
  *consumer*, because at the time nothing called `AddRedisConnections` — true,
  visible from this repository, and not the reason. A gate was written against
  that premise, observed red and removed once the package was read: gating on a
  consumer would have gone green→red the day Redis was wired and moved a
  silent alert into the loaded file. **A plausible cause you can see beats an
  actual cause you have not looked for, which is what makes it dangerous.**
  **PR-28 wired Redis into both services and the alert did not move**, which is
  the counterfactual arriving rather than being argued: the removed gate would
  have fired that day, and the meter still publishes nothing.
- **A list of things known to be missing needs a gate asserting they are still
  missing.** PR-24's four unloaded alerts would otherwise become four alerts
  nobody ever turned on: the gate that says "these metrics are published by
  nothing" is what turns the list into a red build on the day one of them
  lands. A TODO nothing re-checks is a decision, not a deferral.
- **A filter that names a state the system does not have excludes nothing and
  therefore matches everything.** §13.6 excludes a saga "awaiting despatch";
  the state is called `Confirmed`, so `state!="AwaitingDespatch"` would have
  paged on every healthy confirmed order. Prose describing a state and code
  selecting one are different acts — **read the enum, not the sentence.**
- **A test that asserts on which exception wins a race is a test that fails on
  a loaded runner and nowhere else.** `WebApplicationFactory` drives a
  top-level-statements `Program` through `DeferredHostBuilder`, so when
  `ValidateOnStart` throws, `app.Run()` disposes the host while the deferred
  host is still resolving from it — and the loser gets `ObjectDisposedException`
  with the real exception **destroyed rather than wrapped**. No assertion can
  recover it afterwards. Split the claim instead: the host-level test asserts
  that it refused, and a second test asserts *why*, against the options
  pipeline where nothing races. Measured on this repository: intermittent on a
  two-core CI runner, never once locally.
- **Fixing the instance of a race that failed leaves every other instance
  armed, and per-site discipline is what makes them instances.** The saga
  suite's flake (#107) was closed by interleaving waits into the one test that
  had failed; twenty unfenced publishes remained across fourteen of the
  twenty-seven harness tests that file held then, and **the CI run of the merge
  commit that closed
  it went red on the next one**. A publish returns when the message reaches the
  transport, not when the consumer has handled it — so the failure surfaces at
  the next waiting assertion, which bills the inactivity bound and reports a
  command the saga did not send. **Move the barrier into the helper every test
  already calls.** It leaves nothing to forget, which is the argument
  `Common.Web.Tests`' assembly-wide parallelisation attribute won over a shared
  collection, and it costs nothing on a green run. Fence on the **message's own
  id**: a type-level wait matches the first delivery and silently fences
  nothing wherever a suite delivers one type twice. And give the barrier a test
  whose subject is the barrier — every other test stays green without it on any
  machine a developer has, which is exactly why one is owed.
- **A declared-inputs list checks itself against the workflow, never against
  the reads — so an omission is invisible from inside the gate.** The Helm
  tree's `SOURCE_INPUTS` pattern was adopted twice more and then failed a third
  time in the obvious way: `canary.py` declared two paths and opened three, and
  its "both triggers cover every entry" assertion stayed green throughout,
  because a list can only be compared for the entries it already contains. **A
  gate cannot see a read it was never told about.** The fix is a test whose
  subject is the *reads* — the same shape as asserting a parser found anything
  at all — and it was owed by every copy of this pattern rather than by the one
  that was caught. **All three have it now**: `canary.py`'s in its suite,
  `check.py`'s as a check of its own, and `smoke.sh`'s over its own `$ROOT/…`
  literals. Each was observed red against a removed entry. A fourth copy of
  `SOURCE_INPUTS` arrives owing the same test.
  **The fifth copy had that test and shipped broken anyway**, which sharpens
  the lesson rather than repeating it: `realm_check.py`'s reads-direction
  pattern allowed a dot only at the start of a path segment, so `Common.Web/`
  matched nothing, so the one read that gate most depends on was invisible to
  the check written to declare it — and the check reported a pass. **A
  self-check whose own matcher can find nothing is the vacuous gate one level
  up.** What caught it was the negative case that empties `SOURCE_INPUTS` and
  demands a failure naming a specific path; the positive case, which is the
  natural one to write, was green throughout. Write the emptying test, and
  assert on *which* path is named rather than on a non-empty list.
- **A pattern matched against raw source will match the prose about the
  source.** Two of the same PR's matchers had this shape and only one of them
  had been thought about. A gate reading a C# constant out of a file matched
  `AccessTokenLifetime = TimeSpan.FromSeconds(300)` anywhere in the text,
  including a `///` comment quoting the assignment — so a comment plus a
  *reformatted* declaration leaves exactly one match, in the comment, and the
  gate asserts a number the platform has stopped holding. A gate scanning its
  own source for path literals then did it in reverse: the sentence describing
  the pattern satisfied it, and the check reported a read of a path nothing
  opens. **The tell is a matcher whose corpus includes documentation of the
  matcher** — which is every self-check, and every gate that reads code as
  text rather than through a parser. Strip the comment lines, and anchor on
  something a sentence would not say: `readonly TimeSpan <name> =` is a
  declaration, where the bare name is a word.
- **A `helm upgrade --install` of a new release inherits nothing.** A second
  release of the same chart takes the chart's defaults for every value the
  environment overlay would have supplied — authority, OTLP endpoint, database
  — unless it is given them. Only one chart here failed loudly (the gateway
  refuses an empty `ingress.trustedNetworks`); the other three would have
  installed and been quietly wrong. Drive a sibling release from
  `helm get values` of the one it is a sibling of.
- **A tool that changes where it writes when you ask it for something else is
  a premise you did not know you had.** `domain_coverage.py` asserted exactly
  one Cobertura file per run, correctly, until `--logger trx` was added for an
  unrelated gate — and the TRX logger makes the collector leave one partial
  attachment per test project beside the merged one. Nothing about the flag
  says so. **When a step's output feeds another step, adding a flag to the
  first is a change to the second.**
- **Floating point is wrong at the input a ladder starts from, not at the
  exotic ones.** The canary's weight arithmetic read
  `ceil(stable * f / (1 - f))`, and at 5% against 19 replicas `19 * 0.05 / 0.95`
  is `1.0000000000000002` — so it bought two pods and served 9.5% under a label
  reading 5%. Every quantity was a count of pods or a whole percentage, so the
  exact answer was available the whole time. **Where the inputs are integers,
  the float route is not merely imprecise, it is available to be wrong.**
- **Two functions deriving one number will disagree, and the test that pairs
  them is cheaper than the one that finds out later.** `required_stable` named
  the replica count the step needs and `plan` refused it — not because of the
  arithmetic above, but because the two had been written to different rules,
  one taking the smallest canary at or above the weight and the other the
  largest at or below. The bug was the rule, and only asserting the round trip
  said so.
- **A pattern that is one token too strict silently covers less than it
  claims.** The observability gate's instrument reader required `Create…<T>(`
  and found every histogram and counter while missing all three observable
  gauges, because `CreateObservableGauge` infers its type argument. It reported
  four correct alerts as having no signal — loudly, this time. The quiet
  version of the same bug is what the gate-coverage lesson at the top of this
  list is about.
- **A hand-written double is a second specification, and only the real provider
  can falsify it.** `StubCatalog` had drifted from Catalog in four places and
  the suite it serves stayed green throughout, because a double cannot disagree
  with itself. The consequence is sharper than a stale stub: a guard written
  *for* the provider's real behaviour becomes untestable, since the double never
  produces the input the guard exists for. Measured here — the BFF's
  case-insensitive currency comparison could be tightened to `Ordinal` with all
  62 of that suite's container-free tests still passing — the fast half of the
  66 it ran before this PR, not the 77 it runs now — over a change that
  answers 500 to every lower-case currency in production. **Ask what would
  falsify the double, not whether its suite is green.**
- **A library's way of saying "this does not apply" may be to throw, and a
  comment saying it is harmless does not make it so.** §9.6's saga was
  documented as idempotent against a redelivered event because "the transition
  is simply not applicable" — true of the state machine, and MassTransit's
  spelling of it is `UnhandledEventException`, so a duplicate that reached the
  machine was retried six times into the error queue the design depends on
  staying empty. **Not "every routine duplicate", which is how this lesson was
  first written**: §9.5's inbox suppresses the completed redelivery, because
  the outbox persists the event's message id and restores it on every publish.
  What gets through is the delivery whose inbox row was never written — the
  filter records it only after the consumer returns, so a crash between the
  saga state committing and that write is the window. Narrow, and it was the
  whole justification for an `OnUnhandledEvent(x => x.Ignore())` catch-all.
  **And naming it exactly was still not exact enough**: the in-memory outbox
  flushes inside that window, so half of it is a crash with the instance
  advanced and its commands unsent — not a duplicate, but the last delivery
  that could notice. **A window named to one boundary can still contain a
  second one.**
- **A catch-all answers every arrival the same way, so it is only ever as
  right as its worst case.** The callback above was written, defended over
  several review rounds, given a log line, and then removed. Three things
  reach it and it cannot tell them apart: a post-flush duplicate (quiet is
  right), a pre-flush crash that lost the instance's commands (quiet is
  permanent loss), and a misroute (a configuration fault). The log line was
  not a rescue — §13.6 pages on the **error queue**, which is exactly what
  ignoring keeps the event out of, so it moved the case from silent to
  searchable and no further. **What replaced it is enumeration**: every
  legitimate arrival written out with its own `Ignore`, and a structural test
  partitioning the machine's declared next-events so a new one cannot be
  forgotten. The measurement that had justified the catch-all was a test
  republishing an event rather than anything observed in production, which is
  its own lesson: **check whether the traffic a mitigation removes is traffic
  that actually occurs.**
  The suite was green throughout, because `harness.Consumed` records a delivery
  whether the pipeline returned or threw: "no transition ran" is what a fault
  looks like from every assertion in a saga test. **Assert the absence of the
  exception, not the absence of the effect** — and ask what the library does
  with the case your comment calls benign.
- **A tool a plan names may not reach the case the plan made it conditional
  on.** Appendix C made Pact conditional on a consumer relationship becoming
  contentious; the relationship that did is gRPC, and PactNet ships HTTP and
  message pacts only — gRPC is a plugin whose .NET binding has been an open pull
  request since September 2025. The plan was written against the tool's
  reputation rather than its surface. **Check the binding, not the ecosystem**:
  a capability present in a project's Rust core, its JVM binding and its
  marketing is not thereby present in the one language this repository compiles.
- **A bound whose two halves are two commands is not a bound.** `/ship`'s
  twelve-check Grok cap was specified as prose — reserve, then invoke the review
  helper — over two separately granted commands, and neither half was enforced:
  the review helper never touched the ledger, and `release` was accepted for any
  slot at any time with no skip behind it. So a run that invoked without
  reserving spent a check that left no record, and a resumed run ran past
  twelve against a paid API. **Accounting and the thing it accounts for have to
  be one operation**, and where they are, the *placement* of the write becomes
  the accounting rule: reserving immediately before the model call makes a slot
  spent by no path that refuses earlier, which is what deleted the release path
  rather than merely tidying it. **Not "spent if and only if the review ran"** —
  the ledger settles its election *after* posting, so a failed read there leaves
  a slot spent with nothing launched. That case is kept deliberately, because
  after a failed read the state is what is not known; the point is that a claim
  has to be the ordering the code guarantees, not the tidier one beside it.
  **And one operation is not enough if the operation can be aimed
  elsewhere** — the first version of that fix took the pull request number as
  an argument while cloning the *current branch*, so a typo or a substituted
  number spent someone else's slot and left this branch's cap re-armed, with
  both halves looking correct in isolation. Resolve the
  subject from the thing being acted on rather than accepting it:
  `gh pr list --head "$branch"` beside the clone of `$branch`, the same way
  `gh-label-ensure.sh` resolves the repository from the checkout.
- **A boundary on one side of a pattern is not a boundary, and the fix for one
  side is where the next hole appears.** One usage-limit regex took three
  corrections across three review rounds, each finding the side the previous fix
  had not covered: `402` matched inside `47402`, so it became `\b402\b`; that
  matched `"input_tokens": 402`, because a quote and a space are word boundaries
  too, so it became a status *context*; and that matched `status 4021`, because
  nothing stopped the code alternative at the third digit. The same false
  positive three times, walking from the middle of the number to its front to
  its back. **When you constrain one end, write the case for the other end in
  the same change** — and note that each round's negatives looked thorough while
  testing only the end that had just been fixed.
- **Never edit a shell script while it is running.** `bash` reads a script
  incrementally, by byte offset, rather than parsing it whole — so an edit that
  shifts the offsets makes the still-running process resume at the wrong place.
  Measured here rather than reasoned about: editing `grok-review.sh` during a
  live review produced `line 376: ing: command not found` — the tail of a word
  the new offset landed inside — and then re-executed a region that had already
  run, posting a **second** ledger reservation for a slot ten minutes after the
  first. The helpers in `.claude/scripts/` are long-running by nature, so this
  is a live hazard here and not a curiosity: hold an edit until the run ends, or
  copy the script and edit the copy.
- **A concurrency control is only ever proved by a collision you did not
  intend.** The duplicate above is the only real contention the ledger's
  election has ever seen, and it behaved as written: the second claim lost to
  the first and exited 4, `grok-review.sh` turned that into exit 13 and refused
  to run, and `count` folded the two rows for that slot into one spend. A
  deliberate test can show the arithmetic; only an accident shows the whole
  mechanism under load.
- **A run whose integrity is in doubt is a run that did not happen**, whatever
  its artefacts look like. That corrupted review left no `suggestions.md`, which
  is the same evidence a clean pass leaves — and reading it as clean would be
  the exact fail-open the stop-reason allow-list exists to refuse, arriving
  through the script rather than through the model. Spend the next slot and run
  it again.
- **A multi-target edit that aborts has applied a *prefix* of its changes, and
  the targets after the failure are silently absent rather than wrong.** A
  three-file substitution script wrote the first file, failed an assertion on
  the second, and never attempted the third; the follow-up was then derived from
  the *error message*, so it covered the file that had errored and not the one
  that had never been reached. The two files named in the failure were verified
  to agree and the third was never re-read. **Resume from the original list,
  never from the error**, and re-grep every target before claiming the batch
  landed — the absent change leaves no trace to grep for, which is why only the
  list finds it.
- **An `exit` in the last stage of a pipeline ends a subshell, and the consumer
  on the other side has already answered.** The ledger's trust check bailed with
  `exit 3` inside `gh api … | while …`; the `awk` on the other side of
  `ledger_rows | awk` saw EOF, ran its END block and printed `0` — "nothing
  spent", re-arming the cap — and only *then* did `pipefail` abort with 3. A
  caller reading stdout had its answer before the failure existed. **Buffer, and
  check the status while nothing has been written**: command substitution hands
  back the status, a pipe hands the reader an EOF it cannot tell from empty
  input. Worse, the empty case was legitimate and documented, which is exactly
  what made the two indistinguishable — so the separation has to happen upstream
  of the fold, never inside it.
- **A deny-list of terminal states passes every state nobody listed, including
  the ones the next version invents.** The Grok verdict check refused
  `cancelled|refusal|error*` and passed everything else, so a reviewer that
  exhausted its output or turn budget exited 0, wrote JSON, left no
  `suggestions.md`, and had that absence read as a clean review. No attacker is
  required — a long branch is the ordinary way there, and a long branch is when
  review matters most. **Enumerate what is acceptable**: one accepted value,
  every other value and the field's absence refused, pinned like a version so a
  bump must re-verify the string.
- **A regex over a serialised structure answers a different question from the
  one being asked.** Inverting that deny-list to an allow-list of `end_turn` was
  still not enough, because a pattern cannot tell a ROOT field from a nested
  one: `{"modelUsage":{"stopReason":"end_turn"}}` matched exactly once, matched
  the accepted value, and was read as a finished turn — a document whose turn
  never ended, passing the check that exists to notice. A regex also cannot
  establish that the input is well-formed at all, so a truncated write reads as
  a verdict. **Parse it and name the field**: `.stopReason` on the root settles
  shape, nesting and well-formedness together, where three greps could not
  settle any of them. The general form — *if the thing you are matching has
  structure, matching is the wrong tool* — is worth more than the instance,
  and it took three rounds to reach: deny-list, allow-list, parse, each fix
  blind in the way the next one found.
- **Pinning a version is not pinning an artefact.** The review sandbox pinned
  the grok *client version* and refetched `https://x.ai/cli/install.sh` on every
  build, executing it unverified inside the one image built to be a security
  boundary. Pinning the installer's digest is half the fix: reading the
  installer showed it performs **no** checksum or signature check on the 163 MB
  binary it downloads, so the larger artefact was still arriving unchecked over
  the same channel. Pin every artefact that crosses, and make an unrecorded
  platform **fail** rather than build unverified — the scaffold's rule, that a
  tool refusing input it has never been shown beats one that guesses.
- **A verification that runs after the thing it verifies has already executed
  does not verify anything.** Pinning both artefacts above was *still* not
  enough while the installer stayed: it smoke-runs the binary before the hash
  can be taken, as a user with the network and a writable `$HOME`, so a
  malicious artefact gets one execution in which to put the expected bytes where
  the check will read them — and the check then passes. That was written down as
  a "narrow, stated residual" and was the whole property. **Naming a residual is
  not bounding it**: the bound has to be argued against someone who gets to run
  first, and where it cannot be, move the execution after the check rather than
  the check after the execution.
- **`?` in a bash `case` matches `/`.** A `case` performs no pathname expansion,
  so `"$tmproot"/secsweep-??????` accepted `$tmproot/secsweep-a/bbbb` as readily
  as `$tmproot/secsweep-abc123` — a guard whose comment called it a direct-child
  check for as long as it was not one. Compare `dirname` against the root and
  match the *basename*, which cannot be talked past because a basename contains
  no `/`. The general form: **a glob's alphabet is not the shell's, and a
  pattern is not a parser.**
- **A program has more than one name on this platform, and every guard here
  was written with one of them.** The argv hook matched the literal `git` and a
  `/git` suffix; `git.exe push origin +HEAD:main` is the same command to
  Windows and walked straight past it, as did `bash.exe -c`. Measured rather
  than assumed — `git.exe --version` prints `git version 2.45.1.windows.1` on
  this host. It is the POSIX-spelling divergence already recorded for paths and
  for argv, arriving a third time at the *program name*, and the general form is
  worth more than the instance: **a comparison against a name is a comparison
  against a spelling**, so normalise to what the operating system resolves —
  basename, both separators, executable suffix, case — before matching. The
  tell is that every test in the file agreed with the code, because both had
  been written by someone thinking in POSIX on a machine that answers to both.

- **A guard that models another parser needs ONE model of it, in one place.**
  The argv hook keeps being defeated by one shape: a function that knew a rule
  and a neighbouring function that did not. **No count here on purpose** — the
  first draft of this bullet said five and was made false twice in the same
  branch, which is this file's own restated-total failure appearing inside a
  lesson about carefulness. The enumeration is the checkable part. A heredoc
  opener was recognised inside a comment; a paren counter balanced quoted
  characters; the parse-failure fallback scanned the raw string the parse path
  had stripped; the substitution extractor ran ahead of the stripper with a
  quote tracker of its own; `shlex`'s `commenters` disagreed with bash about
  where a comment starts; the escape handling in `_closing_paren` covered
  double quotes and not the unquoted state; and the `DATA_ONLY_COMMANDS`
  boundary was applied to the `git` scan and not to the evaluator pass one
  function below it; and `shlex`'s `punctuation_chars` peeled the file
  descriptor off `2>&1` as an ordinary word, which the run splitter, the push
  grammar and `subcommand_of` each read as something different and none read
  as syntax; and the quote scanner had no notion of a heredoc body, so an
  apostrophe in one opened a quote that ran to the end of the command and hid
  every later opener from the function whose whole job is finding them — and
  then the same oversight was still standing in `undecodable_heredoc` and in
  `stdin_scripts`, one function either side of the fix; and `stdin_scripts`
  had a parse of a shell WORD of its own, ending a here-string at the first
  metacharacter, which is the fail-open `word_end` already had recorded in its
  own docstring; and then the escape-awareness taught to the quote scanner was
  missing from all **five** of its siblings at once, so one prefix carrying an
  escaped ANSI-C quote walked past four passes in a single command; and the
  round that consolidated those five missed the two nested closers, so the
  same prefix inside `$(…)` walked past the seventh copy. Each fix was
  correct, and each left the next copy of the model standing.
  **Several were fail-open** — a force push to `main` admitted,
  more than once — because a model that merely *differs* from the shell's is
  wrong in whichever direction the difference falls, and only one of those
  directions is loud. Where a tool has to agree with another parser, make every
  caller share one implementation of the part that disagrees, and pin the
  agreement with cases in both directions.

  **And when the count of copies stops being small, stop fixing them one at a
  time.** Five scanners each held their own reading of bash's quoting, and four
  rounds of review found four separate consequences of that before the fifth
  found all five at once. What closed it was not a sixth careful copy but a
  primitive the copies became adapters over — one walk, two public shapes. The
  signal to make that move is not elegance, it is having fixed the same
  sentence in a third place.

  **Consolidating copies is a search, and the search is what gets skipped.**
  Five became adapters over one primitive in a single commit, and the next
  review found two more — `_closing_paren` and `_closing_brace`, whose own
  docstrings advertised that they tracked quotes. Grep for the *state* the
  model keeps, not for the function you remember: `in_single` found all seven
  in one command, and nobody ran it.

  **And when two of those models feed each other, interleave them rather than
  alternating.** The heredoc scanner needs the body spans to read the command
  correctly, and finding the body spans is what it is for. Feeding them back
  between whole passes converges — one body per pass, measured at n+1 passes
  for n heredocs — which turns a linear scan into a quadratic one, and this
  guard's timeout is a fail-open. Appending to the list the scanner is
  walking, so the spans arrive as they are found, is one pass and the same
  answer.

  **And measure work, not wall-clock.** The replacement for that test asserted
  a timing *ratio*, which contradicted this repository's own rule — a timing
  assertion on CI is a flake — and a contended runner can violate it with no
  regression at all. What the defect did was re-read a list once per opener, so
  the instrument is a list that tallies its own reads: 2.00 then 2.00 when the
  passes are linear, 3.93 then 3.97 when they are not. Deterministic, exact,
  and it names the mechanism rather than its symptom.

  **And a cost measured on one function is not a cost measured.** That pass
  was pinned linear by a test over `heredoc_spans`, while the containment scan
  it had removed still stood in two other functions that every `offence` call
  reaches — ten million comparisons for 3,200 heredocs, found by profiling
  rather than by the test that was watching. Measure the entry point, and
  measure a rate rather than a total: a total goes stale on a faster runner
  while the shape it stands in for does not.

- **`git checkout <commit> -- <path>` writes the INDEX as well as the working
  tree, and a green suite says nothing about what is staged.** Taking a
  counterfactual means swapping an older file in, running the new cases against
  it, and putting the current one back — and putting it back with `cp` restores
  the working tree only. The reverted version stays staged. It is harmless for
  as long as the next commit happens to `git add` that same path, which is why
  it survived a dozen counterfactuals here before the one commit that added
  only a test file shipped a guard eleven fixes old beside it. **The suite that
  ran before that commit read the working tree and passed**, so nothing about
  the run was a warning. Restore with `git checkout HEAD -- <path>`, which
  moves both, and read `git show --stat` after committing rather than the test
  output before it — a green run and a correct commit are two claims, and only
  one of them was checked. CI went red, which is the gate earning its keep, and
  is also the reason this is a lesson rather than a defect that shipped.

- **Every exemption owes an exact boundary; a guard with no exemption owes
  none.** The argv hook was over-refusing `echo git push …`, so a data-only
  exemption was added — correctly, and it immediately produced two bypasses
  that the guard without it did not have. `shlex(punctuation_chars=True)` emits
  a maximal run of punctuation as ONE token, so `);` matched no separator by
  name and `git log -1; (echo ok);git push origin +HEAD:main` left the push
  inside a run still led by `echo`; and `<(…)` is executed *before* the command
  it is an argument to, so `echo <(git push …)` put the push inside a printer's
  run that never runs it. Both measured with a `git` shim — the second needed a
  marker file, because a process substitution's output goes into a FIFO nobody
  can read from the terminal. **The exemption was the right fix and it cost two
  rounds**, which is the trade to price in rather than avoid: narrowing a guard
  to stop refusing honest traffic moves the difficulty from "what do I refuse"
  to "where exactly does the thing I am exempting end", and the second question
  has more edges. Where a guard gains an exemption, write the boundary cases in
  the same change as the exemption.

- **A gate built on `git ls-files` is blind to `.git` itself.** This is the
  MSBuild auto-import lesson one directory over and it arrived from the same
  reviewer two rounds later: `Directory.Build.targets` was invisible because it
  did not exist, and `.git/config` is invisible because git never tracks it.
  With an unrestricted `Edit`, a command could set `diff.external` there and
  take host execution out of its own approved `git diff` — measured in a
  scratch repository, the external command runs. **Ask what an inventory's
  SOURCE cannot see, not only what the inventory is missing**; and note that a
  worktree's `.git` is a file rather than a directory, so a boundary drawn at
  it needs both spellings.

- **An abbreviation is a spelling, and this repository has now missed it
  twice.** #23 recorded that git accepts `--for` for `--force-with-lease`, and
  that argument is what turned the push check into an allow-list. The forbidden
  *flag* check beside it stayed a canonical-prefix test for six review rounds
  after that — and `git fetch --upl=<cmd> origin` runs `<cmd>`, measured
  against a real remote. Only `--u` is refused, for being ambiguous rather than
  unknown. **The lesson landing in one function and not its neighbour is the
  bullet above; what this one adds is that a rule you have already written down
  is not thereby applied.** When a fix rests on how a tool parses its input,
  grep for every other place that parses the same input, in the same change.
- **A helper whose stdout is its return value owes every subcommand a
  redirection.** `git worktree add` writes "Preparing worktree" to stderr and
  `HEAD is now at <sha> <subject>` to *stdout*, so a helper that printed a path
  after calling it returned a commit subject followed by a path, and the caller
  failed later with a `not an existing directory` naming a whole commit message.
  Found by running the round trip, not by reading it — which is the reusable
  half: **a helper's contract is its stdout, so test what a caller captures,
  never what the code appears to print.**
- **A cheaper fix can be *unreachable* rather than merely weaker, and from
  inside the issue the two read alike.** #125 offered two closures: a second
  `ReleaseStock` from the saga's `Compensating` state, "reachable today", and
  an Inventory tombstone, "the better shape and the more expensive". They were
  ranked by cost while the question beside them — #130, does a release of
  nothing publish anything — was still open, and answering it deleted the cheap
  one: the no-op release publishes, so the exit has already finalised the
  instance and the branch that would send the second release is one nothing
  enters. (**Still true after #124 made that exit conditional**, and it is
  worth knowing why rather than re-deriving it: a late `StockReserved` can only
  reach `Compensating` through the `AwaitingStock` door, which never sent an
  `AuthorisePayment`, so no verdict is ever outstanding on the one path this
  argument is about.) **Two open questions were being weighed independently and
  one decided the other.** Before ranking options by cost, ask whether each can
  still run once its neighbours are settled. **It has now happened twice, which
  is what makes it a rule rather than an anecdote**: #141 ranked "make
  `ReleaseStock` the only trigger" as its cleanest option while #143 was open
  beside it, and
  answering #143 deleted that option — the second producer #141 would remove
  is the only evidence #143's fix reads.
- **Where two mechanisms answer one question and only one of them is editable,
  the editable one is where the lie lives.** GitHub honours closing keywords in
  a PR body and in a commit body independently, and
  `gh pr view --json closingIssuesReferences` reports the **body** only — so
  withdrawing a closure from the description reads as sufficient and is not,
  and the discrepancy is invisible from the one place a reviewer would check.
  **A field whose name is the question is not thereby the answer to it.**
  Reconcile toward the record that cannot be edited, and gate the two against
  each other rather than trusting the half that is easy to look at. The same
  shape reaches past issue closure: a version in a tag and a version in a
  manifest, a threshold in prose and a threshold in a rule file.
- **An in-tree comment calling a gap deliberate is not a control, and the
  comment is usually the thing that is wrong.** §13.4's redactor documented
  that it does not read log scopes and argued the two the platform opens are
  safe, naming `CorrelationId` as "a trace ID or a GUID" — which is the
  *fallback* branch, one file over from a middleware that adopted a
  client-supplied header verbatim. The channel named as provably safe was the
  one already carrying attacker-controlled data. **Read the code the comment
  is vouching for, not the comment**, and where a gap is genuinely accepted,
  file it rather than reasoning about it in place — this repository's own rule
  that a TODO nothing re-checks is a decision, applied to prose.
- **The API surface decides the layer, so read it before designing the fix.**
  The obvious repair for that gap is to walk `record.ForEachScope(...)` in the
  processor. Measured against OpenTelemetry 1.17: `LogRecord` exposes
  `ForEachScope` and **no settable scope provider**, so a processor can read a
  scope and can never rewrite or suppress one — the repair would have let the
  type notice a secret it had no way to remove. The fix had to sit one layer
  lower, at the `IExternalScopeProvider` the logger factory hands every
  provider, which is also what makes it cover scopes opened by EF Core and
  MassTransit. One probe settled it; a plausible design would have shipped.
- **Match the shape the caller produces, not the one the framework's own type
  happens to implement.** That scope wrapper first matched
  `IReadOnlyList<KeyValuePair<string, object?>>` — which is what MEL's
  `FormattedLogValues` is, and what a `Dictionary` is **not**. Every scope this
  platform actually opens comes from `BeginScope(new Dictionary<,>)`, so all of
  them fell through unredacted while the unit tests over the list shape stayed
  green. The interface a sample implements is not the interface the call site
  hands you.
- **A negative assertion about an endpoint is also an assertion that the
  endpoint exists, and only the positive half carries it.** Once authorization
  is deny-by-default, "this path answers 401 to an anonymous caller" is
  satisfied by a path that has stopped existing, by a host that no longer maps
  it, and by a document that no longer generates. Pair every such assertion
  with one from a caller who gets through — which is what cost this PR a second
  test factory rather than a second assertion.
- **A fallback in the building block and a test in one service's suite are not
  alternatives, and the distance from the request is why.** §11.4 proposed an
  `EndpointDataSource` test asserting every endpoint carries a policy; it
  reaches the services §4.5's scaffold has already rendered and fails at test
  time. A fallback policy reaches every host that will ever compose
  `AddCommonWebDefaults` and fails at the request. Where a rule has to survive
  code nobody has written yet, put it where that code cannot avoid it.
- **Two processes that derive a filename from the same value race on the FILE,
  not on the thing the value names.** Testcontainers writes a Dockerfile build
  context to a tar named after the image, so two test hosts building one image
  name collide on that tar — not on Docker, which handles concurrent builds of
  a tag perfectly well. The loser dies with "the process cannot access the
  file" on Windows and `Cannot locate specified Dockerfile` on Linux, reading a
  tar the winner has not finished writing.
  **The unit is the PROCESS, and getting that wrong cost a second round.** The
  first fix gave each *fixture class* its own image name, was measured green
  locally, and failed on CI — because one fixture class had two consumers,
  `Catalog.Api.Tests` and `Catalog.Application.Tests`, which `dotnet test` runs
  as separate hosts. A per-class name is per-process only while the class has
  one caller, which is a premise about callers rather than a property.
  What ended it was not building at all where the build was not needed:
  Catalog needs the broker's *configuration*, not ADR-021's plugin, so it maps
  the two files onto the stock image and no tar exists to race for.
  **The symptom named neither the file nor the fixture**: the second host to
  start failed *every* test in tens of milliseconds while each passed alone. A
  failure count equal to the suite's size, at a duration too short to have run
  anything, is a fixture fault — read the duration before the message.
- **A runtime capture shows what RAN, not what CAN run.** The broker topology
  behind ADR-036's permissions was read off a live stack with both services
  connected and an order placed — and still missed two resources, in opposite
  ways. `MassTransit:ReceiveFault` is declared only when a consumer faults, so
  no capture of a *healthy* system contains it; `payments-commands` is reached
  only after a stock reservation that no Inventory service exists to answer.
  Both are in `Endpoints.cs` and in the framework's contract, where a reader
  can find them. **The code is the inventory and the broker is a sample of
  it** — so derive the list from source and use the running system to falsify
  the derivation, never as the derivation.
- **A negative test that cannot observe the negative reports the property as
  absent.** The probe that attempted #44's exploit reported all four forbidden
  publishes ACCEPTED while the broker's own log showed four refusals:
  `basic_publish` on AMQP 0-9-1 is fire-and-forget, so the refusal arrives as a
  channel exception after the call returns and a probe that publishes and
  closes never sees it. One line — `confirm_delivery()` — is the difference
  between a measurement and a rumour. It failed in the loud direction here,
  which is luck rather than design: the same blindness in a test asserting a
  publish SUCCEEDS would have passed. **Ask what the tool does with the answer
  before believing either outcome**, and pair the negative with a positive
  control so "refused everything" and "credential is broken" cannot read alike.
- **A rule is reversed everywhere it is stated, or nowhere — and do not
  write down how many places that is.** §10.2 said three times that naming no
  authorization policy was the only correct way to declare a route public, and
  the claim had been copied into chapters, appendix rows, a compose README, a
  k6 comment, source comments and the tests that gave it as their reason.
  Deny-by-default reverses it. The reversal is cheap; finding the copies is
  not, and a copy left behind is a rule that is now actively wrong rather than
  merely stale. This bullet carried a count of four while the decision-log
  entry beside it was retiring that very number for being wrong twice — the
  restated-total failure appearing inside the lesson about it.
  [`docs/pr-decision-log.md`](pr-decision-log.md) keeps the inventory;
  this bullet keeps the rule.
- **A change that makes a transaction longer, wider or stricter is a change to
  every lock ordering it participates in**, and the test fixture is where that
  surfaces first. ADR-032 turned one endpoint's consume into a three-table
  `Serializable` transaction; Respawn's `ResetAsync` deletes every row in the
  schema in its own dependency order while a consumer from the previous test may
  still be committing, and two multi-table transactions taking locks in opposing
  order deadlock. It surfaced as an intermittent error 1205 in a
  command-endpoint test *with nothing to do with sagas*, and passed on re-run,
  so it read as a flake. **Capturing the exception is what named it; re-running
  until green would never have.**
- **Removing one participant from a race is not removing the race, and the
  tidier story is the one to distrust.** The same change also registered a
  background writer, and taking that out of the test host was right on its own
  merits — a writer nothing drives makes "the pass never happened" and "the
  pass spared the row" the same green. It was then written up as the fix, and
  the bounded retry was deleted on the sentence *there is no second deleter to
  race*. **A deadlock needs two transactions with opposing lock order, not two
  deleters.** It reproduced with the writer gone, on the second of six runs.
  Both changes are right and only one of them was the fix — and the sentence
  that got it wrong was written while deleting the code that contradicted it,
  which is the most expensive moment to reason instead of run. Six runs cost
  twelve minutes.
- **An API name nobody has run travels further than a number nobody has
  recomputed.** #128's fix was recorded as `AddEntityFrameworkOutbox` with
  `UseBusOutbox`, and that name reached §9.6's callout, two entries in
  `docs/pr-decision-log.md`, and — the one that matters —
  `OrderFulfilmentSaga.cs`'s own comment. `UseBusOutbox()` is a bus-side option
  that never touches a receive endpoint; the call that closes the defect is
  `UseEntityFrameworkOutbox<T>(context)`. They agreed with each other for
  months because agreeing is free and compiling is not. **A restated
  identifier is a claim to reconcile exactly as a restated total is** — and it
  is worse in one way, because a wrong number looks wrong to somebody
  eventually and a plausible method name never does.
  **A name sitting in a source comment is not thereby checked**, which is the
  half a reader is most likely to assume: the compiler reads the code beside it
  and nothing reads the comment, so the site that looks most authoritative is
  the one with the least behind it.
  **The sites are named here and not counted, because the first draft of this
  bullet counted them and got it wrong twice in one sentence** — it said three
  decision-log entries where two carry it, then said four after listing five
  things, and omitted the source comment entirely. The restated-total failure,
  inside the lesson about restated identifiers, in the pull request that added
  it. A reviewer caught it.
- **An exemption stated with a condition expires silently, because nothing
  re-reads the condition when the fact changes.** `.claude/hooks/**` was off the
  deny list on the written grounds that "no hook is configured here" — true when
  written, and false the moment #30's argv guard landed, with no signal in
  between. The dangerous half is that the sentence still *reads* as reasoned:
  it gives its condition, so a reviewer checks the logic rather than the fact.
  Where an exemption has a condition, the condition needs a test, exactly as a
  list of things known to be missing needs a gate asserting they are still
  missing.
- **A permission lift has to happen in the copy of `settings.json` the session
  actually reads, and in a worktree-per-PR layout that is not the one in the
  main checkout.** `.claude/settings.json` is tracked, so every worktree
  carries its own; a lift applied at `C:/dev/<repo>` while the session runs at
  `C:/dev/<repo>-<slug>` looks identical from the outside — same repository,
  same path suffix, same bytes edited — and the deny stays live exactly where
  the work is. This is *verify a restore by reading the file* with a second
  failure mode attached: **reading the wrong copy passes just as
  convincingly**, so the check is `grep` in the session's own working
  directory, never in the repository generally.
- **The lift lags the same way the lock does, so a refusal immediately after
  one is early rather than evidence it failed.** `CLAUDE.md` records the lock
  direction — a deny written back is not enforced at the instant it is
  written — and this is its converse, measured on the branch that extracted
  this file: with the deny genuinely gone from the only settings file in play,
  the very next `Edit` was still refused with *"File is in a directory that is
  denied by your permission settings"*, and the same edit succeeded after a
  few minutes with nothing else changed. **The danger is the diagnosis, not
  the wait**: the refusal reads as "the lift did not take", which sends you
  editing settings files that were already correct, or concluding the harness
  cannot do what it can. Confirm the lift by reading the file, then retry the
  edit rather than re-cutting the permission.
- **A replacement narrower than the thing it replaces is a regression wearing a
  fix's clothes.** The argv guard was written to close what
  `Bash(git *--output*)` could not, and its first form matched a flag exactly or
  with `=` — which admitted `--exec-path=<dir>`, a directory of binaries for git
  to run, that the crude substring deny had been catching all along. A precise
  mechanism replacing a blunt one has to be checked against the blunt one's
  catches, not only against the case that motivated it.
- **A stub that hands back post-filter data cannot test the filter.** The
  ledger's `gh` stub supplied rows *after* the jq shape filter, so four
  behavioural cases written to guard a migration of that very filter passed
  against a deliberately narrowed one; a single pattern assertion caught it.
  The tell is that the fixture's shape matches the code's *output* rather than
  its *input*. Feed the stage its real input, or state in the test that the
  stage is not under test — the stub said so, and four cases were written past
  the sentence anyway.

- **A margin that stands in for an unbounded quantity is a guess with a number
  on it, and the number is the part that reads as rigour.** §8.5's retention
  floor carried a five-minute allowance covering two terms that reordered a
  claim's expiry against a marker's: the lag between two writes, which a
  process stall stretches arbitrarily, and the skew between two pods' clocks,
  which is bounded only by NTP working. Five minutes covered every ordinary
  value of each and the shipped default carried six days of margin regardless,
  so nothing was ever exposed — and **nothing detected either term exceeding
  it**, in a mechanism whose whole purpose is that a write happens once and
  whose failure is silent. Both were closable at the source for a change
  smaller than the allowance's own documentation: start the two windows at
  events with a fixed order, and compare one row's age on one clock. **The tell
  is a constant whose own comment says it bounds rather than removes** —
  written honestly, and honest about being a guess. Ask what makes the quantity
  unbounded before choosing the number that covers it.
- **When a term will not be bounded, check whether the comparison it sits in is
  standing in for a fact something already owns.** The entry above closed two
  terms at the source and left a third: the claim's window is counted by
  Redis's clock and the marker's by the database's, and a clock step is bounded
  by nothing a repository can assert — so a fourth margin was as wrong as the
  first three. The move that worked was not a better number but noticing what
  the arithmetic was *for*. *The marker outlives the claim* is a statement about
  two instants two servers produce; *the claim is gone* is a question, and the
  store holding the claim is the only thing that can answer it. The purge now
  selects by age and then asks, so no clock is read on either side of the
  decision. **The tell is a comparison whose two sides are produced by different
  systems** — one of those systems usually knows the answer directly, and asking
  it removes the term instead of bounding it. It costs a round trip and a
  dependency, and both are cheaper than a guess whose failure is silent.
- **A validation can outlive the reason it was added, and the message is where
  that goes unnoticed.** The same floor still refuses a marker window below the
  claim's, and its refusal message argued that a shorter window purges the
  marker first and lets a committed command run twice. After the change above
  that sentence is simply false — the purge waits for the claim whatever the
  window says — while the check it justifies is still right, for a different
  and smaller reason: a shorter window asks for a guarantee shorter than the
  claim already gives, so it is a setting that cannot do what it says. **A guard
  whose stated reason has been closed is a guard nobody can evaluate.** When a
  fix removes the failure a validation was written against, re-argue the
  validation or delete it; leaving the old argument in an exception message
  ships a claim the code disproves, to the one reader who has already hit it.
- **A test that pins a design correctly is the thing you have to edit when the
  design changes, and the edit is where the change gets hidden.** Two tests
  asserted the behaviour the two fixes above inverted — that completing an
  idempotency claim *re-armed* its TTL, and that a marker window equal to the
  claim's was *refused* — and both were good: one had already been strengthened
  by a review round so that it could fail, and the other carried its argument
  in place. Neither is evidence against the change and neither may be quietly
  flipped either. **Rewrite the assertion, keep the history in the comment, and
  where you can, move the assertion's subject off the number and onto the
  relationship** — the floor's test now also asserts that the floor *equals* the
  claim's window, so a later reader who reinstates an allowance fails a test
  about the rule rather than one about a duration.
- **"By construction" is a note that the constraint is missing, and it is worth
  writing the note down.** Three successive rules here were true because
  something ordinarily moves — a clock advances, so two writes under one key
  carry different timestamps — rather than because anything enforced it. That
  reads as a proof right up to the fault that violates it, and the violating
  fault is usually a *different kind* from the ones already handled: a clock
  step is bounded by a magnitude and can be argued about, where a clock set to
  an exact historical instant is a coincidence with no quantity to bound. **The
  useful move is to say which of the two you have** — the delete that named a
  row by `(Key, CommittedAt)` recorded, in the decision that shipped it, that
  the pair was an identity by construction and not by constraint, and that
  sentence is what turned a silent duplicate write into a filed issue and then
  into a `rowversion`. Where a constraint exists that says the same thing, take
  it: it costs a column and removes the argument.
- **A column no code reads is still a claim, and a shadow property is how you
  say so.** The version the purge joins on is read by one raw SQL statement and
  by nothing in the object model, so a CLR property for it would be a member
  that exists to be ignored — and, being an array, a mutable one on a public
  type. **What the shadow property costs is discoverability, and it is payable:
  name the column once as a constant and let both the mapping and the statement
  read it**, so the two cannot drift, and add a test whose subject is the
  mapping rather than the behaviour. The behavioural test would go on passing
  if the column silently became an ordinary `binary(8)` — two rows in one test
  still differ in a value nobody updates — so what has to be asserted is that
  the *database* generates it.
- **A refusal that names a constraint may be naming a *place*, and the two read
  identically.** ADR-040 set aside a pipeline realm check because one "needs
  admin credentials in CI", listed that beside two refusals that are properties
  of the world — a discovery document publishes no token lifetime, and
  committing somebody's production realm makes their input this repository's
  artefact — and the three then travelled together into four chapters as one
  settled answer. Only the first was about a *job*: the credential CI must not
  hold is one a deploy job under a scoped environment already holds, and the
  same sentence is true of CI and irrelevant to the pipeline. **The tell is a
  refusal whose subject is a capability rather than a fact** — "needs X we do
  not have" is a claim about a location, and locations differ inside one
  pipeline. Re-read the refusals a record inherited before treating them as
  the shape of the problem; two of these three survived unchanged, which is
  what made the third worth finding.
- **When two suites assert opposite things about one setting, the parameter
  they differ on has to be named, not defaulted.** The Compose realm needs
  `directAccessGrantsEnabled` on — §14.1's documented login is a password
  grant — and a deployed realm must have it off. One predicate covers both only
  if it is told which realm it is judging, and the tempting default is the
  local one, because that is the subject every test run has. **A default there
  is a production artefact judged on a development artefact's terms**, which is
  the failure the check exists to prevent, arriving inside the fix. Make the
  discriminator a required argument, and write the test that runs the *same*
  document under both values and asserts it passes exactly one.
- **A taint alert names a source, and the sink is the part you recognise.**
  Three CodeQL findings on one pull request survived two rounds of fixing,
  because both fixes were aimed at the printing end: first the credential keys
  in the document were redacted, then the messages stopped echoing attribute
  values, and the count did not move either time. The source was a *test
  helper's parameter named* `trusted`, which the query classifies as a secret
  by name and then follows through an argv list into the code under test. No
  realm and no credential were anywhere in that flow. **The tell is a fix that
  changes nothing**: an alert that survives a plausible remedy is one whose
  cause has not been read yet, and the SARIF carries the whole path —
  `gh api -H "Accept: application/sarif+json" .../code-scanning/analyses/<id>`
  prints every step with its file and line, including the steps in files you
  were not looking at. Read the flow before the second attempt, not after the
  third.
- **A name-based classifier that is wrong about a value is answered by
  renaming the value, not by suppressing the rule.** The query was right about
  its own mechanism and wrong only about what this variable held; writing the
  name that says what it holds corrects the input rather than silencing the
  output, and leaves the rule catching the next real one. Reach for a
  dismissal when the *flow* is impossible, not when the *name* is unlucky.
