# The messaging gate

**The claim: [§3.2](../../docs/backend-architecture/03-bounded-contexts.md)'s
table says what each host publishes, consumes and accepts, and the code does
exactly that.** The chapter stays the owner of the facts; this gate is the
check, and it fails naming the row and the name that differ.

| Subcommand | Refuses | Reads |
|---|---|---|
| `check` | a name a row's Publishes, Consumes or Accepts cell holds and its host's code does not, and the reverse; a host under `src/Services/` or `src/BFF/` with no row, and a row with no host; a name in a Consumes cell that is not in exactly one Publishes cell, and a name in a Publishes cell that is in no Consumes cell, which are §3.2's own two closure comparisons; and an `owed.txt` line that is malformed or no longer a difference | §3.2's table, and per host the contract types its `*IntegrationEventMapper.cs` maps to, the events its endpoints bind with `ConfigureConsumer<IntegrationEventConsumer<T>>`, the `Event<T>` properties of a `MassTransitStateMachine`, and the commands it binds with `ConfigureConsumer<CommandConsumer<T, …>>` |
| `report` | nothing | the same code, printed as JSON per host: the table the code implies, for a sibling repository's broker map to compare with |

**Bound, not registered.** An `AddConsumer` line with no `ConfigureConsumer`
binds nothing, and the broker never delivers to it, so only the binding counts
as consuming. A host is a directory under `src/Services/`, and the BFF is the
whole of `src/BFF/`, named by its one project whose name prefixes the others.

**`owed.txt` is the one way a difference passes**, and only in one direction:
a name the table states and the code has not built yet, with the issue that
builds it. Code ahead of the table cannot be owed, because the table is the
contract and its row is what is wrong. A line whose difference has gone fails
until it is deleted, so the list empties itself as the work lands.

**Nothing is committed but the check.** The generated table is printed, never
written to the tree: a committed copy would be a second owner of §3.2's facts,
and the diff against the chapter is what the gate is for.

## How it runs

Its suite, then `check`, in the fast job of [`ci.yml`](../workflows/ci.yml).
The suite holds a negative case for each refusal beside its positive control,
cases whose subject is the parser, and two whose subject is coverage of this
repository: every host under `src/` has a row and every row a host, and every
host's code yields at least one name.
