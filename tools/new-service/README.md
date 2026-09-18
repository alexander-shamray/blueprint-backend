# The new-service scaffold

[§4.5](../../docs/backend-architecture/04-solution-structure.md) is the
specification for this directory; this file records what a developer needs at
the keyboard. One command creates a service:

```bash
python tools/new-service/new_service.py Yankee --port 5199
```

| | |
|---|---|
| `name` | The service, PascalCase. It becomes the namespace root, the project names, the database, the SQL schema, both connection-string keys and both Compose service names |
| `--port` | The host port the API publishes. **Required** — a port is an allocation recorded in [§14.1](../../docs/backend-architecture/14-local-development.md) and in `deploy/compose/README.md`, and a script that derived one would quietly disagree with a printed chapter. The run refuses a port another service already publishes |
| `--migration-id` | The `InitialCreate` id, and the base every later template migration is spaced from — one minute per entry in `TEMPLATE_MIGRATIONS`, in the order they apply. Defaults to the current UTC timestamp; the tests pass a fixed one |
| `--repo-root` | Defaults to this script's repository |

It creates a tree under `src/Services/<Name>` and `tests/<Name>.*`, and it
edits the tracked files below. **They are named rather than counted, because
the count rotted quietly**: PR-11's design spec said four, this sentence said
five, #44's broker account made the truth six without either of them moving,
and #161's allow-list made it seven. What a reader can check is whether the
table matches `plan()`.

| | |
|---|---|
| `Platform.slnx` | the service's project entries, in alphabetical position |
| `deploy/compose/services/<name>.yml` | the migrator and API pair — created, not spliced. Lower case: the unit's file name is the service key's casing, not the PascalCase input, so `Yankee` renders `services/yankee.yml` |
| `deploy/compose/docker-compose.yml` | one `include:` line for that file |
| `deploy/compose/docker-compose.infra-only.yml` | both halves of that pair, excluded |
| `deploy/compose/.env.example` | the two §7.1 connection variables |
| `deploy/compose/README.md` | one row in the ports table |
| `deploy/compose/rabbitmq/definitions.json` | the broker account the service authenticates as, since #44 |
| `.github/secret-scan/allowed/<tree>.txt` | one accepted-finding entry per credential-shaped literal the render carries, in the file covering that entry's tree, since #161 |

**The last one is the difference between a service that renders and a service
that can be committed.** §15.1's secret scan reads the working tree, so it
reads the tree this leaves behind, and it refuses a credential-shaped literal
nobody has written down a reason for. Catalog's literals all have an entry,
added by hand the day Catalog landed; a rendered service carries the same
literals under its own name and so under its own fingerprints. The scaffold
loads the real scanner, runs it over what it has just built, and writes an
entry per finding **the render introduced** — the fingerprints are the gate's
own, never a second implementation of which substring each rule matches, and
each shared file is scanned before and after so a finding that was already
there stays somebody else's. A suppression arriving for a credential the run
is not writing is the outcome the allow-list exists to refuse, and the
first version of this step could produce one.

**Run it on a clean worktree**, and undoing it is then two commands — the
generated tree is untracked and the edits above are tracked, so neither one
alone is enough. The Compose unit is on the `rm` line for that reason: it is
created rather than edited, so `git checkout` restores everything around it and
leaves it exactly where it was written:

```bash
rm -rf src/Services/Yankee tests/Yankee.* deploy/compose/services/yankee.yml
git checkout -- Platform.slnx deploy/compose/ .github/secret-scan/allowed/
```

(`Yankee` rather than a `<Name>` placeholder because this is a `bash` fence and
the shell reads `<Name>` as a redirection, not as something to fill in. It
named `Ordering` at port 5101 until PR-18 made that a real service — at which
point the create raised `ScaffoldError` on a published port, and the undo,
followed literally, deleted the service that had just landed. A probe the
suite already uses cannot become a service later, which is the property the
example wanted all along.)

**The paths in that command are the whole of it.** `git checkout .` — the
same verb with no pathspec — is **not** the undo, and this file used to say it
was: it leaves every generated file in place, because they are untracked, and
it discards any unrelated tracked edit you happened to have. So the two forms
read alike and behave nothing alike, which is why the block above names the
files rather than the directory it is standing in. `git status` after a run
shows exactly the tracked files above and the new directories, and that list
is what the pathspec has to cover.

## What you get

[§4.1](../../docs/backend-architecture/04-solution-structure.md)'s five
service projects, three test projects and the `TestSupport` library — nine in
all, and §4.1 is explicit that the last is not a test project — with
everything the delivery plan has built into the template so far:
`DbContext` and conventions, `EfUnitOfWork`, the connection factory, the
readiness checks, the §7.4 migrator host, the `InitialCreate` migration that
creates the schema and the `AddOutbox`, `AddInbox`,
`AddOutboxRetentionIndex`, `AddIdempotencyMarkers`,
`IdempotencyMarkerCommittedAtDefault` and
`AddIdempotencyMarkerRowVersion` ones beside it, §9.4's
outbox with its dispatcher and allow-list mapper, §9.5's inbox filter, §8.5's
durable idempotency marker and the retention purge over all three of their
tables, the §9 bus
registration — a scaffolded host refuses to start without
`ConnectionStrings:RabbitMq` — §11.3's JWT validation and the test auth
scheme, both Dockerfiles, the Compose pair, and the architecture gates of
[§4.2](../../docs/backend-architecture/04-solution-structure.md).

**The `CommittedAt` default travels for a stronger reason than the table
it alters.** The column default and the SQL cutoff that reads it are two
halves of one guarantee, so a service scaffolded with the marker table and
without the default ages its rows on the writing pod's clock while the purge
ages them on the server's — the skew that migration exists to remove, shipped
to every new service by omission.

**And the last of them adds that table's `rowversion`, which the purge names
rather than derives.** `RetentionPurgeService` spells the column in both of
its marker statements — the candidate select and the delete that joins the
rows it returned — so a service scaffolded without the migration fails its own
purge with `Invalid column name 'RowVersion'` on the first pass.

The service builds and its eighty-nine tests pass before you have written a
line, and forty-six of them run against real SQL Server and RabbitMQ
containers:
the migrator's exit code, §7.1's two-key boundary, the readiness probe — 200
only once the bus connects — `EfUnitOfWork`'s commit, rollback and retry
semantics, the inbox filter's once-per-endpoint guarantee, §8.5's marker
committing with the work and vanishing with a rollback, the retention purge
over every table, and the outbox dispatcher's per-row isolation, attempt
cap and loud failure on a `Local` row with no registered handler.

Those two counts are measured rather than estimated, and they move whenever a
PR adds to the template — they read forty-one and sixteen until PR-18
recounted them against a rendered service, three PRs after they stopped being
true, fifty-six until PR-22 did it again, seventy-eight until PR-32 rendered a
service carrying §8.5's marker suite, seventy-nine until PR-33 put four
more into it across two of its review rounds, and eighty-three until PR-35 —
which found the pair already stale by four before it had added anything, PR-34
having put its own four into `RetentionPurgeTests` without recounting. A figure
nobody recounts goes stale on the next PR's clock rather than on its own.

**Adding to the number is not recounting it.** PR-22 put three tests into the
template and the total moved by four, so the only way to know this pair is to
render `Yankee` at 5199 and run all three of its suites — which is what PR-32
did, twice: 1, 18 and 60. PR-33 did the same and read 1, 18 and 64; its four
were the marker's database-generated stamp, the default constraint behind it,
the scoped inbox reader's own subject test and the skewed-clock pass that
separates the marker's cutoff from the other two, all of which land in
`Yankee.Api.Tests` and all of which want a container. **It rendered twice**,
because the fourth arrived in a later review round than the other three — the
same reason PR-32 rendered twice, one pull request on. PR-35 read 1, 18 and 70:
its own two are §8.5's marker `rowversion` — the mapping gate and the
replacement a stale delete must not remove — and the other four were PR-34's,
already in the template and never counted. **Two added, six apparent**, which
is the rule above failing in the direction it warns about. The forty-six is the
`Category=Integration` count, which is a filter anyone can rerun rather than a
tally somebody kept.

**The outbox arrives wired and empty**, which is the state to expect: the
allow-list mapper has no entries, so every domain event this service raises is
local-only until somebody adds one (§9.3 makes translation opt-in for exactly
that reason), and no `IProjectionHandler` is registered, so no `Local` row is
staged either. The table, the dispatcher and the type map are all live.

## What you do next

The scaffold copies **no domain**. That is the point — it renders the wiring
every service needs and none of Catalog's `Product`, so nothing has to be
deleted before the real aggregate can be written.

1. Write the first aggregate, then **delete `AssemblyMarker.cs`** and
   re-anchor `ArchitectureTests` in `{Service}.Domain.Tests` and
   `{Service}.Application.Tests` on it.
2. Add the entity configuration and run `dotnet ef migrations add` — the
   generated snapshot already describes the model the service ships with,
   which is the outbox table and nothing else, so the first migration is the
   first aggregate's table and nothing else. Verified rather than argued: a
   scaffolded service was built, `migrations add` was run against it, the
   generated `Up` came out empty and EF's rewritten snapshot was byte-identical
   to the emitted one.
3. Add the first command or query. Four things come back, each with the one
   that needs it and each noted at the line concerned in the generated code —
   they are **not** a set to restore together:
   - **The first handler of either kind** brings the container wiring in
     `{Service}.Application.Tests.csproj`, and the test that the §6.2 scan
     produced a registration. That scan fails silently when lost.
   - **The first validator** brings the registration test for the validator
     scan, which fails silently in the same way, and re-anchors
     `AddValidatorsFromAssembly` on a type. A query-only slice has no
     validator and needs neither.
   - **The first query** brings `Dapper` to `{Service}.Application.csproj`,
     for §6.5's read side. A command-only slice must not add it: an unused
     package reference is a claim the project would not be making.
   - **The first domain event** brings §12.4's round-trip assertion, and with
     it a `JsonConverter` in `{Service}.Infrastructure` for any value object
     the event carries. This one is the least obvious and the most expensive
     to skip: a value object with a private constructor does not throw on the
     outbox's `Local` lane, it deserialises to its default, and the symptom is
     a projection running on a zero (§9.4).
4. Map the first endpoint in `Program.cs`, behind `RequireAuthorization()` at
   the group (§11.4) — fail closed, and let any deliberately public endpoint
   say `AllowAnonymous()` out loud. The permission it needs is a
   `{Service}Permissions` constant and a policy registered beside the other
   two `Add*` calls; add `AuthorizationPolicyTests` in the same change, which
   is what stops a policy name resolving to nothing.

Not in scope, and not silently missing: the gateway route (PR-17 builds the
gateway), the Helm chart (PR-23), and a Worker host in place of an API — §4.1
gives Shipping and Notifications one, and no such host exists yet to copy.

**`Shipping` and `Notifications` are refused by name** for that reason: the
script renders the API shape, §4.1 gives those two a Worker, and Notifications
no Domain project either. Both names are accepted again by the change that adds
the mode.

## The tests

```bash
cd tools/new-service && py -3.12 -m unittest        # Windows
cd tools/new-service && python3.12 -m unittest      # elsewhere
```

Stdlib `unittest`, no dependencies, and **3.12 by name rather than whatever
`python` resolves to** — that is the version both CI jobs pin and the floor
this tool is written to. A newer interpreter locally is the hazard rather than
an older one: it accepts APIs 3.12 does not, so the suite goes green on code CI
cannot run. `Path.read_text(newline=…)` is 3.13 and cost a CI round exactly
that way, which is why the floor is spelt into the command instead of being
mentioned underneath it.

They run against the **real repository**: `plan()` renders from the checkout
and returns a value, and `apply()` is the only thing that touches disk — which
is why they can. That is deliberate. The design
has no template directory — the template is Catalog itself, so there is one
copy of the wiring rather than two that drift — and the risk it accepts is
that Catalog moves under the script's anchors. Only rendering the tree that
actually exists catches that.

So a Catalog change that breaks the scaffold fails here, loudly, naming the
file. If it does, reconcile `scaffold/` with the template in the same
change: a file added under `src/Services/Catalog` has to be classified as
template or slice, and the script will not guess.

**A credential-shaped literal added to the template refuses the run the same
way**, and for the same reason. `SCAN_REASONS` gives one sentence per finding
§15.1's scanner reports over a render; a finding no row explains raises
`ScaffoldError` naming the file, the line and the rule, because a reason this
script invented would be a suppression nobody wrote — which is the one thing
the allow-list's README says it must never hold. The refusals
are in `RefusesToRun` beside the unclassified-file ones, which is where they
belong and where they were not: the paragraph above stated this property for a
release while nothing exercised either raise.

**And `.github/` missing is the only thing the step is quiet about.** A
checkout with that directory and without the scanner, or without its
allow-list, refuses — it is a real tree, and rendering into it produces a
service CI will not take.
