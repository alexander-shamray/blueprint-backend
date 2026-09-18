# Style guide

**The dialect this repository's prose, C# and SQL are written in, and which
rules the build enforces rather than a reviewer.** **One dialect, both
phases**: these rules govern the blueprint's fenced samples and the compiled
solution alike, so a sample and its implementation read identically, and
`.editorconfig` is a build input rather than a documentation convenience.

It is load-bearing whenever you write or review prose, C# or SQL — which is
most changes, not every change, and *most* is what earns it a file. A rule you
are about to apply is a rule you can afford to open a file for; a rule you are
about to break without knowing it is the case `CLAUDE.md`'s short list under
*Style* exists for.

**The rules marked review-carried are why it exists**: no analyser reaches
them, so this file and a reviewer are the only things that do, and a summary
of one is how it gets "corrected" back into the corpus.

**A short list of these rules stays in `CLAUDE.md`**, under *Style*, because
they have to be true of an edit made before anyone opens this file. **This file
is the master copy**: where the two disagree, the disagreement is a bug report
against the shorter one, and the change that moves a rule has to reach both.

**`/style-pass` records a corrected form here**, in the C# style or SQL style
section below, in the established voice — the rule, an example, and the reason
it is not arbitrary — and then reconciles `.editorconfig` in the same change.
State the exceptions with the rule: half this file's value is the carve-outs,
and a rule recorded without its exceptions gets applied to the exceptions next
time.

## Prose conventions

- **Wrap prose at 80 columns.** Tables, links and code blocks may exceed it.
- **British spelling in prose** — `behaviour`, `serialisation`, `licence`,
  `normalise`, `organised`. **Identifiers keep their real spelling**:
  `IPipelineBehavior`, `AddAuthorization`, `[Authorize]`. Never "correct" a type
  name into British spelling, and never Americanise the surrounding prose to
  match a type name.
- **Chapter headings are numbered**: `# 9. Messaging`, `## 9.1 Integration
  events`. Third-level headings are unnumbered prose titles
  (`### Handler contracts`). Appendices use `# Appendix A — <title>`; an ADR
  is its own file under `adr/`, titled `# ADR-001 — <title>` and listed in
  Appendix A's table; `docs/pr-decision-log.md` is one file of
  `## PR-01 — <title>` entries.
- **Cross-references use the section sign**: `§9.3`, and link the first mention
  in a passage — `[§9.3](09-messaging.md)`. Within a chapter, `(§6.5)` bare is
  fine. Cite the section that actually states the claim; a reference to a
  section that only mentions the topic is a defect.
- **Callouts are blockquotes whose opening sentence is bold**, no emoji, no
  admonition syntax. Two forms are named and recurring — `**Trap — …**`
  for a mistake worth naming, and `**Decision — …**`, which always points at
  the ADR that records it:

  ```markdown
  > **Trap — projecting everything by default.** Each projection is a second
  > copy of the truth, with its own bugs and its own rebuild procedure.

  > **Decision — no mediator library.** See [ADR-004](adr/ADR-004-no-mediator-library.md).
  ```

  The rest are a bold assertion followed by its argument —
  `> **Unregistered, this fails silently and completely.** …`. That is the
  default; reach for `Trap` or `Decision` only when the callout genuinely is
  one. `**Decision.** / **Why.** / **Consequences.**` are the ADR body form,
  not callouts. Neither form is counted here: a count moves whenever a
  callout is added, and the contract's [§2](change-locality.md#2-the-rule)
  keeps such counts out of every document.
- **Em dashes** (`—`) for asides, **en dashes** (`–`) for ranges. Both are
  literal Unicode, not `--`.
- **Every chapter ends with a rule and a nav footer**, in this exact shape:

  ```markdown
  ---

  [← §5 Tactical DDD](05-tactical-ddd.md) · [Index](README.md) · [§7 Persistence →](07-persistence.md)
  ```

  Separator is ` · ` (U+00B7). First chapter omits the `←` link, last appendix
  omits the `→` link. One `---` only — a doubled rule before the footer is a
  regression that has been fixed before.
- **Tables carry the summary data**; prose carries the argument. A two-column
  borderless table (`| | |`) is the established form for metadata blocks.

## Comments — every language

The rule reaches `//` and `///` in C# — `/* */` appears only as the one-line
elision inside a sample block, never as a comment, so a block comment is
itself a finding — `#` and docstrings in Python, `#` in shell, and the
comment syntax of YAML, MSBuild and `.editorconfig`. No
analyser enforces any of it; the reviewer does, and the review commands
reach it through the contract's §2, which they already cite.
[`docs/change-locality.md`](change-locality.md) §2 is the same rule for
documents, and a comment is a document that happens to sit beside code:
the churn it produces is the same, and it was measured in
[`docs/churn-plan.md`](churn-plan.md).

- **A comment answers what the code cannot.** Why this and not the obvious
  alternative; the invariant a line relies on that nothing checks; the
  owner — a section, an ADR, a symbol — that states the rule. A comment that
  says what the next line does is deleted, because the line says it.
- **Cite, never copy.** An argument an ADR or a chapter makes is cited in one
  clause — `// EnableForHttps is true against BREACH (ADR-020).` A paragraph
  that restates it is a second copy of a fact with an owner, and the second
  copy is the one that goes stale.
- **No history.** Nothing about what the comment, the code or a review used
  to say; no pull request, issue, round or reviewer named; no "measured by
  deleting the line". The commit body is the record of how a line came to be,
  and `git log -L` finds it from the line.
- **No inventories, no test names.** A count of things that live elsewhere
  — tests, callers, files, occurrences — is a count nobody recomputes; the
  arithmetic that explains a constant is that constant's why and stays. A
  test cites the code it covers; the code does not cite the test, because
  a renamed test leaves the comment pointing at nothing and `rg` on the
  symbol finds the test anyway.
- **One argument.** A `//` block or a `<remarks>` argues one thing, as
  briefly as it can; a `<summary>` or a docstring's first line is a sentence
  or two. A comment that needs a second argument is a decision, and a
  decision is an ADR: write the ADR with `/new-adr` and cite it.
- **No emphasis.** No `**bold**`, `<b>`, capitals or `!` for stress in a
  comment. It is read once, beside the code, by someone who is not skimming.
- **A finding against a comment is closed by cutting.** When a review says a
  comment is stale or wrong, shorten or delete it. Never append the
  correction, and never explain in the comment why the previous wording was
  wrong — that is the sentence the next review finds stale.

What this leaves is short: `Program.cs` says `// §13.2` beside the call that
implements it, a timeout says in one line why it is longer than its
neighbour, and a registration that relies on the container omitting an
unsatisfied open generic says so in the sentence that a reader would
otherwise have to discover by deleting the line.

## C# style — samples now, source later

**One dialect, both phases.** The rules below govern the fenced samples and the
compiled solution alike, so a sample and its implementation read identically.
The repo `.editorconfig` is not a documentation convenience — it is a build
input. Change it deliberately, and reconcile any change with the samples
already written against it.

Each rule below says whether the build enforces it. **The ones marked
review-carried are why this section exists**: no analyser reaches them, so this
file and a reviewer are the only things that do.

### Layout and naming

- Four-space indent, spaces not tabs. CRLF line endings. Newline at end of file.
- Pascal case for types, properties, methods and events; `I` prefix on
  interfaces; namespace matches folder.
- **`using` directives outside the namespace**, no blank lines between
  consecutive usings (IDE0065, enforced). This binds source only — a sample is
  an excerpt rather than a compilable unit, so **do not "complete" one by
  adding the block it would need to build.** The blueprint carries exactly two
  `using` lines, both `using static` (§9.6's saga imports `Endpoints`, §12.4's
  subject suite imports `Principals`), because an unqualified
  `Authenticated(caller)` reads as a member of the type being shown unless
  something says otherwise. A third is fine on those terms; a plain `using` is
  not.
- **No unused `using` directives** — *review-carried*. A file that stops needing
  one drops it in the change that stopped needing it: a stale using is a claim
  that the file depends on something it does not, and the reader who trusts it
  looks in the wrong assembly first. **Nothing catches this.** IDE0005 is not
  reported by the build at all unless `GenerateDocumentationFile` is on, so
  `dotnet build` is silent even with `TreatWarningsAsErrors`, and so is
  `dotnet format style --diagnostics IDE0005` — both checked against a
  deliberately injected using. Turning it on costs a fourth entry in
  `Directory.Build.props`, because it also enables CS1591 against every
  public member with no XML comment, across `src/` and the test projects
  alike. Measure it as the unique diagnostic sites of `dotnet build
  Platform.slnx -p:GenerateDocumentationFile=true
  -p:TreatWarningsAsErrors=false --no-incremental`. **`--no-incremental` is
  load-bearing in that command**: an ordinary build recompiles nothing and
  reports zero, which reads exactly like a clean result. The greyed-out using
  in the IDE is the only live signal there is.
- **A blank line always follows the namespace declaration** (IDE0055,
  enforced). `namespace X;` is a statement about the whole file, not the first
  line of the type below it:

  ```csharp
  namespace Common.Application;

  public interface IDispatcher
  ```

### Statements and types

- **A single statement may omit braces; two or more always take them — and so
  does one that wraps.** The statement goes on the following line, never beside
  the condition, and if it does not finish on that line the body takes braces:

  ```csharp
  // Wrong — the body wraps, so the reader has to count indentation to find
  // where the condition's reach ends.
  if (!Known.Contains(message.Reason))
      throw new ContractMappingException(
          $"Unknown review reason '{message.Reason}'.");

  // Right.
  if (!Known.Contains(message.Reason))
  {
      throw new ContractMappingException(
          $"Unknown review reason '{message.Reason}'.");
  }
  ```

  It is *review-carried*: no analyser reaches it — IDE0055 governs the
  indentation of whatever shape is there and has no opinion on which shape it
  is — so the corpus is the only enforcement, and a new braceless wrapped body
  is a review comment rather than a failed build.

  A single statement that fits on its line still omits them:

  ```csharp
  if (amount < 0)
      throw new DomainException("Money cannot be negative.");

  foreach (IDomainEvent domainEvent in events.Where(projections.HasHandler))
      await publisher.StageAsync(domainEvent, OutboxLane.Local, ct);
  ```

  This holds across every braceless body in the blueprint's ` ```csharp `
  fences. `csharp_preserve_single_line_statements = false` keeps a format run
  from pulling any of them back up onto the condition's line. The one exception is a
  **wrapped** condition, which takes braces — see below.
- **Explicit types for locals**, except where the right-hand side names the
  type. A reader of a fenced code block has no hover and no go-to-definition,
  and this blueprint's job is to teach types and contracts. Code blocks may run
  past 80 columns, so length alone is never a reason to hide a type — if a
  declaration turns unwieldy, split the expression.

  ```csharp
  IReadOnlyDictionary<ProductId, Money> priceList =
      await prices.GetAsync(productIds, command.Currency, ct);
  ProductId[] missing = [.. productIds.Where(id => !priceList.ContainsKey(id))];
  ```

  Four cases keep `var`, and only these four:

  | | Example |
  |---|---|
  | The RHS names the type | `var order = Order.Place(…)`, `var id = Guid.CreateVersion7()` |
  | Anonymous types | `var args = new { OrderId = orderId.Value };` |
  | Tuple deconstruction | `foreach (var (product, qty, price) in items)` |
  | Fluent resource DSLs | The whole Aspire AppHost block in §14.2 — eleven of its thirteen locals are an `IResourceBuilder<T>` whose name only repeats what the `Add*` call already said. Explicit types are possible there and read worse; keep the block uniform |

- **Target-typed `new()` only where the type is already named beside it** —
  half enforced, half *review-carried*. This is the `var` rule applied to the
  right-hand side. Where the declaration names the type, `new()` repeats
  nothing and `.editorconfig` asks for it:

  ```csharp
  ServiceCollection services = new();
  private static readonly ConcurrentDictionary<Type, Invoker> Cache = new();
  public static Result Success() => new(null);
  ```

  Where nothing beside it names the type, spell the type out. The positions
  that hide it are **an argument**, **a collection expression in argument
  position**, and **the target of an indexer or property assignment**:

  ```csharp
  // Wrong — the reader cannot see what is being constructed in any of these.
  .MapHealthChecks("/health/live", new() { Predicate = _ => false })
  .AddAttributes([new("deployment.environment", builder.Environment.EnvironmentName)])
  scrubbed[i] = new(attribute.Key, "[redacted]");

  // Right.
  .MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
  .AddAttributes([new KeyValuePair<string, object>("deployment.environment", builder.Environment.EnvironmentName)])
  scrubbed[i] = new KeyValuePair<string, object?>(attribute.Key, "[redacted]");
  ```

  A collection expression assigned to a typed declaration is **not** one of
  them — the element type is on the left, so the elements stay bare:

  ```csharp
  KeyValuePair<string, object?>[] state =
  [
      new("NewPassword", "a"),      // fine — the array's type is on the left
      new("card_number", "b")
  ];
  ```

  **No analyser reaches the banned half**: IDE0090 fires only when the type *is*
  apparent, so it polices the first block and has nothing to say about the
  second. **Where naming the type breaks the 120-column budget, name it in a
  local — do not fall back to `new()`.** A local declaration carries the type,
  so the `new` beside it needs none and both lines fit.
- Prefer collection expressions, `is null` over `ReferenceEquals`, null
  propagation, compound assignment, simplified interpolation, primary
  constructors.
- **Materialise with a spread, not a terminal `.ToArray()` or `.ToList()`** —
  *review-carried*. The spread states the target and the terminal call states a
  conversion, and only one of those is what the line is for:

  ```csharp
  ProductId[] missing = [.. productIds.Where(id => !priceList.ContainsKey(id))];
  ```

  Two consequences: dropping `.ToArray()` often leaves a **single** call, and a
  single call is not a broken chain — join it. And a spread frequently brings
  the statement back under 120, in which case the one-line rule applies and the
  `[` does not get its own line.

  **A `ToArray` that is not a sequence materialisation is outside this rule.**
  `MemoryStream.ToArray()` is a stream accessor — the type implements no
  `IEnumerable`, so `[.. buffer]` does not compile (CS9212). The rule is about
  the *terminal LINQ operator*, where the spread and the call are two spellings
  of one thing. This was narrowed after a reviewer read "there are no
  `.ToArray()` calls left in the corpus" as the grep it literally is — **a rule
  whose stated test is a string match will be enforced as one.**

### Wrapping

- **A list is on one line, or one element per line. Never a ragged middle.**
  "List" means anything comma-separated inside brackets — parameters,
  arguments, collection expressions, initialisers, tuple members. The budget is
  **120 columns** (`max_line_length`); within it the list stays on one line,
  past it every element gets its own.
- **`[` and `{` each take a line of their own**, at the column of the construct
  they open, and their closers do too. **`(` is the single exception**: it ends
  the line it opens, and `)` trails the last element — `);`, not a line of its
  own:

  ```csharp
  string[] forbidden =
  [
      "Microsoft.EntityFrameworkCore",
      "MassTransit",
      "StackExchange.Redis",
      "Microsoft.AspNetCore"
  ];

  _deliveryLag = meter.CreateHistogram<double>(
      "messaging.delivery.lag",
      unit: "s",
      description: "OccurredAt to consumer start.");
  ```

  The exception is not arbitrary. A parenthesised argument list is *part of* the
  invocation, so it hugs the call at both ends; a braced or bracketed body is a
  *container* of elements, and giving it its own opening and closing line puts
  its extent in a column the eye can scan.

  **The two halves are enforced very differently.** `{` is not a review rule at
  all: the C# default for `csharp_new_line_before_open_brace` is `all`, IDE0055
  reports a trailing one, and ADR-019 turns that into a failed build. `[` has no
  such backing — Roslyn has no opinion on bracket placement and `dotnet format`
  neither introduces nor removes the break — so that half is *review-carried*.
- **A trailing lambda is an argument like any other**, and does not hang off the
  opening line. An argument list holding a lambda has exactly two legal shapes,
  tried in this order:

  ```csharp
  // 1. One line, if it fits inside 120. Always preferred.
  Publish(payload, type, c => { … }, ct);

  // 2. Otherwise one argument per line — the lambda included, braces and all.
  Publish(
      payload,
      type,
      c => { … },
      ct);

  // And if the lambda itself will not fit on its line, its braces open under
  // the rule that governs braces, at the argument's own column.
  Publish(
      payload,
      type,
      c =>
      {
          c.MessageId = message.MessageId;
          c.CorrelationId = message.CorrelationId;
      },
      ct);
  ```

  **There is no carve-out, and a braced body does not earn one.** Keeping
  leading arguments up whenever the trailing lambda has a braced body would
  make the rule undecidable from the call site: whether a leading argument
  could stay would depend on the *last* argument's body kind and on whether
  anything followed it, and a reviewer performing only the first lookahead
  gets `Publish(payload, type, c => { … }, ct)` wrong. The cost is real and
  accepted: every builder DSL in the blueprint breaks across four or five
  lines. It buys a rule with no lookahead.

  A **single**-argument call is untouched — `AddRateLimiter(options => { … })`
  keeps its shape. Nor is a lambda the only thing that can hang:
  `WriteAsJsonAsync(new ProblemDetails { … }, ct)` is the same shape with an
  object initialiser in the lambda's place.

  **Two greps narrow this down and neither closes it.** The arrow —
  `\(.+,\s*\w+\s*=>\s*$` — catches a lambda hanging off a call with a leading
  argument. The closer — `^\s*[]})],\s*\S` — catches a bracket closing at the
  head of a line with an element still after it. **Neither sees the plain
  one**: a continuation line carrying two ordinary arguments has no arrow and
  no leading bracket. Treat them as a sieve, not a proof.

  **Write the closer for the tool you are running it in.** Ripgrep reads `\]`
  as an escaped bracket and builds the class `}` `]` `)`; POSIX `grep` treats a
  backslash inside a bracket expression as literal, so the class closes at the
  first `]` and the pattern matches nothing, ever — reporting zero and exiting
  1, which reads exactly like a clean sweep. Put the `]` first — `[]})]` — and
  it means the same thing in both.
- Continuations indent **four**, never to a bracket column. A list wrapped under
  its opening bracket is a leftover from an older style. Two things keep this
  from being mechanical: a line comment on an element forces the broken form
  regardless of width, and the four `var` cases still apply inside the elements.
- **A broken fluent chain puts every call on its own line**, at head + 4, never
  aligned under the receiver's dot — including the *first* call. If the chain
  breaks at all, nothing stays on the head's line:

  ```csharp
  builder.Services
      .AddReverseProxy()
      .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
  ```

  Unlike a list, a chain is **never joined back up to fit** — it is broken for
  reading order, not for width. One exception: a short qualifier and its
  subject count as one call (`.That().ResideInNamespaceContaining(…)` —
  NetArchTest's idiom reads as pairs).

  **The head is the line the chain starts on**, which is not always the
  statement's first line. When a declaration's initialiser wraps, that wrapped
  line is the head and the chain indents four past *it* — eight from the
  declaration. A spread element is a head the same way, so `.. x` takes the
  chain at + 4. A `})` closing a lambda mid-chain is a continuation, not a head.

  **"Contains no invocation" means no *dotted* call, and a receiver never
  outranks that.** `Types`, `app`, `_lines` and `Enumerable` all sit alone as
  heads, static classes and fields alike. What stays on the head is whatever has
  nothing to strand in front of it: object creation (`new MsSqlBuilder()`), a
  call with no receiver (`When(OrderPlaced)`), and a parenthesised expression
  (`(await Task.WhenAll(…))`).
- **A lambda body that is itself a call goes on its own line**, at + 4, rather
  than trailing after the `=>`. A bare parameter re-mention is not a call and
  stays — `p => p` heading its own chain is the fluent-DSL idiom:

  ```csharp
  builder.Services
      .AddCors(o =>
          o.AddDefaultPolicy(p => p
              .WithOrigins(builder.Configuration.GetRequiredSection("Cors:Origins").Get<string[]>()!)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()));
  ```

  So `o => o.AddDefaultPolicy(…)` breaks and `p => p` does not, in the same
  statement. This applies only when the construct is already broken across
  lines; inside 120 columns it stays on one.

  An **expression-bodied member** is not a lambda for this purpose:
  `public Money Total => _lines.Aggregate(…)` keeps its body on the `=>` line,
  because there is no nesting for the break to clarify. When such a member's
  body *does* need its own line, `=>` still trails the signature, and the body
  sits at the **declaration's** indent + 4 — not necessarily the `=>` line's.
- **Break at the outermost bracket, never a nested one.** Reaching past the
  outer parenthesis leaves the outer call glued to its argument:

  ```csharp
  // Wrong — the break is inside OrderPlacedDomainEvent, and Raise( is stranded.
  order.Raise(new OrderPlacedDomainEvent(
      order.Id, customerId, order.Total, order.SnapshotLines(), now));

  // Right.
  order.Raise(
      new OrderPlacedDomainEvent(order.Id, customerId, order.Total, order.SnapshotLines(), now));
  ```

  The argument moves to the next line **whole**; break it further only if it
  still does not fit. This composes with the lambda rule rather than competing:
  a call whose last argument is a lambda breaks at its **own** parenthesis,
  which puts the lambda on a line of its own, and never after the `=>`.
- **A multi-line condition takes trailing operators, four-space continuations,
  and braces on its body.** The braces are what make four safe: without them
  the last `&&` line and the body sit in the same column.

  ```csharp
  if (!command.IsSystemInitiated &&
      (!currentUser.IsAuthenticated ||
          (order.CustomerId.Value != currentUser.Id &&
              !currentUser.HasPermission("orders:admin"))))
  {
      return Result.Failure(OrderErrors.NotFound);
  }
  ```

  This is the **one exception to the braces rule above**. Prefer not to wrap at
  all; joining is the better fix and has been applied everywhere it fits. The
  block above is the corpus: one wrapped header, in §11.4, kept because an
  ownership check that fails closed does not join inside 120. A second one
  appearing is a signal to join, not a precedent. **A parenthesised group that
  breaks indents a further four**, so nesting depth is visible. The check above
  gained its second level when the guard was rewritten to fail closed; the
  earlier form led with `currentUser.IsAuthenticated &&`, which read as a guard
  and behaved as an exemption, admitting every caller arriving with no
  principal at all.
- **Operators go at the end of the line they continue from**
  (`dotnet_style_operator_placement_when_wrapping = end_of_line`). Each line
  then ends by announcing that more is coming. This holds for `&&`, `||`, `??`
  and `+`, in conditions and expressions alike; a leading `&&` or `??` is a
  leftover. It governs wrapped lambda predicates as much as `if` headers.
- A base-type list is the one continuation covered by none of the above: no
  bracket to hang off, no operator, already one entry per line. Leave it
  aligned under the `:`.

### Whitespace, suppressions and secrets

- **One space before `=`, `=>` and `{` — never a column of them.** Padding a
  token out to line up with the one above fails the build: IDE0055 reports
  it and ADR-019 makes that an error.

  ```csharp
  // Fails the build. Every line but the longest carries the diagnostic.
  public required Guid MessageId       { get; init; }
  public required decimal TotalAmount  { get; init; }

  // Correct.
  public required Guid MessageId { get; init; }
  public required decimal TotalAmount { get; init; }
  ```

  **A trailing `//` comment is the carve-out, and it is a real one.** IDE0055
  does not govern whitespace in front of a comment, so a comment may sit in
  whatever column the block reads best in. The carve-out stops at the end of
  the line: a trailing comment too long for one line cannot wrap into a second
  `//` line aligned under the first, because a line *starting* with `//` is a
  whole-line comment whose leading whitespace is indentation, and indentation
  IDE0055 does govern. Shorten it, or move it above the statement whole.

  `dotnet format` agrees with both halves — it collapses the code padding and
  leaves the comment column untouched — so a format run neither introduces this
  nor undoes it.

  **The corpus obeys this rule everywhere, and the apparent exceptions are
  SQL.** Searched with raw string literals excluded, there is not one
  aligned `=` column in C# — not in `src/`, not in `tests/`, not in the
  blueprint's samples. Every site that looks like one is inside a `"""` or
  `$"""` literal: `OutboxDispatcher`'s `UPDATE … SET`, Catalog's `SELECT`
  with the column aliases §6.5 uses, §6.6's `MERGE`. Those are the SQL
  section's, and it argues their column on its own terms below.

  **The exception is a local declaration statement, and it is the only
  one.** Measured with a probe compiled against `Common.Domain` and then
  deleted: a padded `{` on a member, a padded `=` in an object initialiser
  and a padded `=>` on an expression-bodied member each produce
  `error IDE0055`, while `int a      = 1;` inside a method body produces
  nothing. That last case is what
  `csharp_space_around_declaration_statements = ignore` reaches, and it is
  the whole of what it reaches. **The rule above therefore holds as
  written and as enforced**, and a reviewer may cite the build for it.

  **This paragraph argued the opposite for three review rounds, and the
  mistake is worth more than the rule.** A grep for a padded `=` across
  `src/` returned real hits on a green `main`, and they were read as C#
  initialisers proving a deliberate carve-out — so the rule was narrowed to
  admit them, `CLAUDE.md`'s short list was narrowed to match, and the SQL
  section was rewritten to stop leaning on a claim that had just been
  "corrected". Every one of those hits was SQL. **A grep does not know what
  language it is reading**, and a raw string literal is exactly where this
  repository keeps the other one — so a measurement over `.cs` files is a
  measurement over two languages unless it is told otherwise.

  **Then it hedged, which was the second error and the more tempting one.**
  Having found the evidence bad, the fix was to call the enforcement
  "unsettled" and mark the rule review-carried — which reads as caution and
  is just a second unmeasured claim wearing the opposite sign. One probe
  settled it in three seconds. **When a question is decidable by running
  something, hedging is not the safe answer, it is the unmeasured one.**
  `.editorconfig`'s own comment still names "78 declarations and initialiser
  members" as relying on that setting; an initialiser member does not, by
  the probe above, and the sites it means are the SQL ones. That comment is
  corrected in the same change.

  **Two places the analyser does not reach, and they are opposites.** Padding
  between a type and its identifier —
  `private readonly Counter<long>   _placed;` —
  is *not* reported, and is swept anyway — *review-carried*, because one
  dialect beats a rule that stops halfway. **SQL is the other way round and
  keeps its alignment**: it lives inside raw string literals, which no analyser
  or formatter reads, and the SQL section argues its columns on their own terms.
- **No `#pragma` suppressions** — there are none in the corpus, and a sample
  that needs one is a sample whose design is wrong. A genuinely warranted
  suppression belongs in `Directory.Build.props` with a comment, never inline.
- **No real credentials** — no production connection string, key or certificate
  path, in a sample or in source. Bind configuration or read the environment;
  deployed secrets come from a vault (§15.4).

  The deliberate exception is local development, and it is not a defect to be
  tidied away: §14.1's Compose file carries
  `${SQL_PASSWORD:-Local_Dev_Pa55w0rd!}` and
  `${BFF_CLIENT_SECRET:-local-dev-secret}`, and documents Keycloak as
  `admin/admin`. Those defaults are what make `docker compose up` work with
  no prior setup; the environment variable in front of each is the seam that
  keeps them out of anything deployed.

  **The broker is the one that is NOT fronted by a variable, and since
  ADR-036 it is not `guest/guest` either.** Each service carries a literal
  `{service}-svc` / `local-dev-{service}`, because those credentials are
  imported into the image from `definitions.json` and a `${...}` in front of
  the connection string would front only one half of a pair — the broker
  would still expect the compiled-in password. Rotating them locally is an
  edit to that file and a `docker compose down -v`, which is stated in
  `deploy/compose/README.md` rather than implied by a seam that is not
  there.

### Settled choices — do not "fix" them

| | |
|---|---|
| Namespaces | File-scoped (`namespace X;`), never block-scoped |
| Extension declarations | C# 14 `extension(T receiver)` blocks where a class groups several extensions on one receiver — `Common.Application.DependencyInjection` is the worked example. **The corpus is split**: every extension class in `Common.Web` still uses the classic `this`-parameter form. Four extend a receiver nothing else does — `ProblemDetailsExtensions` (`IServiceCollection`), `HealthCheckExtensions` (`IEndpointRouteBuilder`), `AuthorizationPolicyExtensions` (`AuthorizationPolicyBuilder`) and `ResultExtensions` (`Result`) — while two groups share one: `CorrelationIdExtensions` and `SecurityHeadersExtensions` on `IApplicationBuilder`, and `ObservabilityExtensions`, `AuthenticationExtensions` and the `CommonWebDefaultsExtensions` that composes them on `IHostApplicationBuilder`. Whether to group those three is open and deliberately unsettled — they are separate files because one composes the other two, and merging would put a caller-facing entry point in the same block as the pieces it calls. Converting anything is a decision about the whole corpus |
| Expression-bodied members | Used for one-line members, not for constructors |
| Braces | Optional for a single statement **that fits on its line**; required for two or more, and for one that wraps |
| Target framework | .NET 10 (LTS), C# 14 |

Each of these is a house style a reviewer might otherwise read as an oversight
and "correct". They are consistent across the blueprint's C# samples, and the
solution is written the same way. Changing one is a decision about the whole
corpus, not about the file in front of you.

**One more house non-finding is not this file's rule, and it is named here
because the commands that consult this table send a reviewer for the whole
set.** An external reviewer regularly flags `Aspire.*` packages as unpinned.
They are not pinned and that is correct: §14.2's AppHost is optional, **Aspire
is not adopted**, nothing references an `Aspire.*` package, and §4.4 therefore
pins none — the argument lives in [`repo-map.md`](repo-map.md)'s AppHost
entry, which is the authority for it. Reject the finding and cite that, not
this table.

**A pointer rather than a copy, deliberately.** `/review-branch` and
`/review-grok` list the house non-findings a reviewer may reject by name and
attribute the list to this file; four of the five are rules stated here and
the fifth is a fact about what the repository pins, which is nowhere near a
style rule. Restating it here would put a claim about `Directory.Packages.props`
in a document about prose and braces, and it would rot on that file's clock
rather than on this one's. **A set with a hole sends a reviewer back to
guessing, so the hole is filled with a direction and not with a second copy.**

**Fence languages in use:** `csharp`, `sql`, `yaml`, `bash`, `json`, `mermaid`,
`dockerfile`, `xml`, `promql`, and bare ``` for trees and console output.
Always tag a fence that contains a real language.

  `promql` was missing from that list for as long as the list existed, and
  §13's alert expressions have been fenced with it throughout — the inventory
  was written from the languages somebody remembered rather than from the
  corpus. The predicate is one command, so run it rather than trusting the
  sentence: grep the blueprint for lines opening a fence with a language tag,
  then `sort | uniq -c`. It returns exactly these nine.

  **That command is described rather than quoted, and the reason is a rule
  this file states elsewhere.** A fence marker cannot sit inside a
  single-backtick span, and a fenced block quoting one has to be opened with
  four backticks — so the literal form is a rendering trap in a paragraph
  whose whole subject is fence markers.

## SQL style

Most of the SQL lives inside C# raw string literals rather than `sql` fences, so
"the statement's own left margin" below means the literal's indent, not column
zero.

**Clause keywords start their own line at that margin, one space before what
follows; continuations indent four.** Continuations are the connectors that
extend a clause — `AND`, `OR`, and a `MERGE`'s `ON`. Each predicate gets its own
line: a chain packed onto one line hides the shape of the condition, which in
this blueprint is usually the point being made.

**An `AND` extending an `ON` aligns with the `ON`**, rather than indenting
another four. `ON` is itself a continuation — of `MERGE … USING` or of a
`JOIN` — so its predicates sit at the level it already occupies, and a second
indent would imply a nesting that is not there:

```sql
MERGE ordering.ProductPrices AS target
USING (SELECT ProductId = @ProductId, Currency = @Currency) AS source
    ON target.ProductId = source.ProductId
    AND target.Currency = source.Currency
```

This is the one place `AND` does not indent past the keyword it extends; under
`WHERE`, which is a clause keyword at the margin, it still does.

```sql
UPDATE ordering.OrderSummaries
SET FulfilmentCounted = 1
OUTPUT inserted.PlacedAt, inserted.ConfirmedAt
WHERE OrderId = @OrderId
    AND PlacedAt IS NOT NULL
    AND ConfirmedAt IS NOT NULL
    AND FulfilmentCounted = 0;
```

One space means one, `WHERE` included.

`UPDATE <table>` and `SET` are separate lines. The exception is `MERGE`'s
`UPDATE SET`, which names no table and stays one token.

**A `SET` that breaks keeps nothing on the keyword's line.** Like a fluent
chain, if the assignment list does not fit it goes below whole — the first
assignment does not stay up beside `SET` with the rest hanging under it:

```sql
UPDATE SET
    CustomerId  = @CustomerId,
    TotalAmount = @Total,
    Currency    = @Currency,
    LineCount   = @LineCount,
```

The `=` signs line up in a column here, and that alignment is deliberate. It
does **not** rest on parity with the C# initialisers, which keep no such
column — the rule above forbids it, IDE0055 enforces it, and the corpus has
none. SQL keeps the
column on its own merits: a statement inside a raw string literal is invisible
to every analyser and formatter in the toolchain, so nothing will fight it, and
one assignment per line with the names in a column is what makes `SET` read as
the row shape it produces rather than as a wrapped list.

**This sentence has been wrong in both directions inside one pull request.**
It said IDE0055 forbids the C# form, which overclaims what the analyser does;
it was then rewritten to say the C# initialisers keep a column of their own,
which was worse, because they do not and the evidence for it was SQL. What
the section needs from its neighbour is nothing at all: the raw string
literal is invisible to the toolchain whatever the C# rule says, which is
why that is the argument it makes.

**A SQL list obeys the same rule as a C# one: one line, or one element per
line, never a ragged middle.** That covers the column list after `INSERT`, the
values after `VALUES`, the columns after `OUTPUT` and `GROUP BY`, and a
function's arguments. The budget is the same 120 columns, measured from the
literal's margin:

```sql
INSERT (OrderId, CustomerId, Status, TotalAmount, Currency, LineCount, Products, PlacedAt, UpdatedAt)
VALUES (@OrderId, @CustomerId, @Status, @Total, @Currency, @LineCount, @Products, @PlacedAt, @UpdatedAt)
```

Past the budget the `(` ends its line and each element takes one, indented four
— **not** aligned under the first argument. `DATEADD(second, …)` wrapping its
arguments into a column under `second` is the keyword river again, one scope in.

The single exception is a **DDL body**, where alignment is a table rather than a
wrapped list: `CREATE TABLE` and `CREATE INDEX` keep their aligned type and
constraint columns, because there the columns carry the meaning.

A parenthesised sub-expression short enough for one line stays on one line;
`AND (LockedUntil IS NULL OR LockedUntil < SYSDATETIMEOFFSET())` is one
predicate, not two. A parenthesised group that *does* break indents its `OR`s a
further four, so nesting depth is visible:

```sql
WHERE o.CustomerId = @CustomerId
    AND (@AfterPlacedAt IS NULL
        OR o.PlacedAt < @AfterPlacedAt
        OR (o.PlacedAt = @AfterPlacedAt AND o.Id < @AfterId))
```

**Column aliases are assignments, not `AS`** — `Total = SUM(…)`, never
`SUM(…) AS Total`. The name being defined then starts the line, so a
projection reads as the row shape it produces, and the `=` column lines up the
way `SET`'s does. The running example is §6.5's history query, whose total is
summed from the order's lines: `ordering.Orders` stores no total, because the
aggregate computes one and §7.2 declines to keep a second copy of it. This is
the SELECT list only: `MERGE … AS target`,
`USING (…) AS source`, `WITH claimable AS (` and `CAST(x AS varchar(10))` are
required syntax and keep `AS`.

**`INNER JOIN` is spelled in full, and its `ON` gets its own line** at + 4. The
join condition is a predicate and belongs where predicates go, not trailing off
the end of a table name:

```sql
FROM ordering.Orders o
INNER JOIN ordering.OrderLines l
    ON l.OrderId = o.Id
```

An alias follows its table after one space. Padding table names into a column
(`FROM ordering.Orders      o`) is the old keyword river in another costume.

This replaced a right-aligned keyword river (`FROM   `, `WHERE  `, `  AND  `,
every argument at column 7). If you find one, it is a leftover — convert it.
