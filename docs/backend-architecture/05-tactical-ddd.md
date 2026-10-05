# 5. Tactical DDD

## 5.1 The building blocks

| Block | Definition | Test |
|---|---|---|
| **Entity** | Has identity that persists through change. | Two instances with the same ID are the same thing regardless of other fields. |
| **Value object** | Defined entirely by its values; immutable. | Two instances with equal values are interchangeable. |
| **Aggregate** | A cluster of entities and value objects with one root, forming a consistency boundary. | Can you state an invariant that must hold across all of it, atomically? |
| **Aggregate root** | The only entity outside code may hold a reference to. | Every path into the aggregate goes through it. |
| **Domain event** | A record that something meaningful happened, in past tense. | A domain expert would recognise the name. |
| **Repository** | Collection-like access to aggregate roots. One per aggregate. | It loads and saves whole aggregates, never fragments. |
| **Domain service** | Logic that belongs to no single aggregate. | It genuinely spans aggregates or needs external data. Rare — be suspicious. |

## 5.2 Strongly typed identifiers

Primitive `Guid` identifiers allow `GetOrder(customerId)` to compile. Typed IDs
make that a compile error, which over the lifetime of a system prevents a
recurring class of bug at essentially zero cost.

```csharp
namespace Ordering.Domain.Orders;

public readonly record struct OrderId(Guid Value)
{
    // Version 7 rather than 4: the leading 48 bits are a millisecond
    // timestamp, so every identifier carries when it was made. Read the trap
    // below before assuming that also makes it a sequential key.
    public static OrderId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
```

> **Trap — a time-ordered Guid is not a sequential key in SQL Server.** UUIDv7
> puts its timestamp in the **first** six bytes. `uniqueidentifier` compares the
> **last** six first and works backwards from there, so it never reaches the
> timestamp until every random byte has already decided the order. Version 7
> values therefore arrive in a clustered index as scattered as version 4 ones,
> and the page splits the version was reached for happen anyway.
>
> This is the shape the blueprint deploys, not a hypothetical:
> `HasKey(o => o.Id)` ([§7.2](07-persistence.md)) leaves SQL Server to make
> that primary key clustered, which it does by default.
>
> What version 7 actually buys is a creation time readable inside every
> identifier, which earns its place for support and forensics on its own. If
> insert locality ever becomes the binding constraint, the fix is a physical
> one — a different column type, or a clustered key that is not the identifier
> — and it belongs in §7 with the rest of the storage shape rather than here.

## 5.3 Value objects

```csharp
namespace Ordering.Domain.Common;

public readonly record struct Money
{
    public decimal Amount { get; }
    public string Currency { get; }

    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public static Money Of(decimal amount, string currency)
    {
        if (amount < 0)
            throw new DomainException("Money cannot be negative.");

        // Letters as well as length: "1$?" is three characters and no
        // currency, and a guard that admits it makes the exception message a
        // stricter claim than the type keeps.
        if (currency is not { Length: 3 } || !currency.All(char.IsAsciiLetter))
            throw new DomainException("Currency must be a 3-letter currency code.");

        // The currency's own exponent, half to even: none for the yen, three
        // for the dinar (ADR-067).
        return new Money(
            decimal.Round(amount, CurrencyMinorUnits.Of(currency), MidpointRounding.ToEven),
            currency.ToUpperInvariant());
    }

    public static Money Zero(string currency) => Of(0m, currency);

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator *(Money money, int quantity)
    {
        // Without this guard the operator is a back door past Of: a negative
        // quantity would construct the negative Money the factory refuses.
        if (quantity < 0)
            throw new DomainException("Money cannot be multiplied by a negative quantity.");

        return new Money(money.Amount * quantity, money.Currency);
    }

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (left.Currency != right.Currency)
        {
            throw new DomainException(
                $"Cannot combine {left.Currency} with {right.Currency}.");
        }
    }
}
```

The constructor is private and `Of` is the only way in. An invalid `Money`
cannot be constructed, so no code downstream needs to check for one. This is the
**always-valid** principle, and applying it consistently removes a surprising
amount of defensive code from the rest of the system.

**The rounding is the currency's, not the type's.** `Of` takes its exponent
from `CurrencyMinorUnits` in `Common.Domain`, ISO 4217's minor units held once
for every context that keeps its own `Money`
([ADR-067](adr/ADR-067-a-currencys-minor-unit-is-iso-4217s-held-once.md)).
That is the one fact the contexts share here: the value object itself stays
each context's, as [§4.3](04-solution-structure.md) asks.

## 5.4 An aggregate

`Order` is the core aggregate of the blueprint. Note what it does *not* have:
public setters, a parameterless public constructor, references to other
aggregates by object, or any knowledge of persistence.

```csharp
namespace Ordering.Domain.Orders;

public sealed class Order : AggregateRoot<OrderId>
{
    private readonly List<OrderLine> _lines = [];

    public CustomerId CustomerId { get; private set; }
    public OrderStatus Status { get; private set; }
    public Address ShippingAddress { get; private set; }
    public DateTimeOffset PlacedAt { get; private set; }
    public IReadOnlyList<OrderLine> Lines => _lines.AsReadOnly();

    public Money Total => _lines.Aggregate(Money.Zero(_currency), (sum, line) => sum + line.LineTotal);

    /// <summary>
    /// An immutable copy of the lines, for events. `Lines` returns a read-only
    /// *view* over the live list, so an event holding it would keep changing
    /// after the fact — a record of what happened must not track what happens
    /// next.
    /// </summary>
    private IReadOnlyList<OrderLineSnapshot> SnapshotLines() =>
        [.. _lines.Select(l => new OrderLineSnapshot(l.ProductId, l.Quantity, l.UnitPrice))];

    private readonly string _currency;

    // EF Core materialisation only.
    private Order() { }

    private Order(OrderId id, CustomerId customerId, Address shippingAddress, string currency, DateTimeOffset placedAt)
    {
        Id = id;
        CustomerId = customerId;
        ShippingAddress = shippingAddress;
        _currency = currency;
        PlacedAt = placedAt;
        Status = OrderStatus.Draft;
    }

    public static Order Place(
        CustomerId customerId,
        Address shippingAddress,
        IEnumerable<(ProductId Product, int Quantity, Money UnitPrice)> items,
        string currency,
        DateTimeOffset now)
    {
        // Of rather than a bare assignment: the order's currency is what every
        // line is checked against, so an unvalidated one makes AddLine's
        // comparison meaningless and Money.Zero(_currency) throw later, far
        // from the call that caused it. The validator refuses a malformed code
        // at the edge; the aggregate does not depend on having been called
        // through one.
        string normalised = Money.Zero(currency).Currency;

        var order = new Order(OrderId.New(), customerId, shippingAddress, normalised, now);

        foreach (var (product, quantity, unitPrice) in items)
            order.AddLine(product, quantity, unitPrice);

        if (order._lines.Count == 0)
            throw new DomainException("An order must contain at least one line.");

        order.Status = OrderStatus.AwaitingStock;
        // Lines travel with the event: the integration contract needs them so
        // Inventory can reserve stock (§9.6), and a handler must not have to
        // reload the aggregate to find out what was ordered.
        order.Raise(
            new OrderPlacedDomainEvent(order.Id, customerId, order.Total, order.SnapshotLines(), now));

        return order;
    }

    private void AddLine(ProductId product, int quantity, Money unitPrice)
    {
        if (quantity <= 0)
            throw new DomainException("Quantity must be positive.");
        if (unitPrice.Currency != _currency)
            throw new DomainException("All lines must share the order currency.");

        OrderLine? existing = _lines.SingleOrDefault(l => l.ProductId == product);
        if (existing is not null)
        {
            // The merge keeps the price already held, so a second line at a
            // different one would silently reprice the first line's quantity
            // as well — a Total no consumer could derive from the request.
            if (existing.UnitPrice != unitPrice)
                throw new DomainException("A product cannot appear twice at different prices.");

            existing.IncreaseQuantity(quantity);
        }
        else
        {
            _lines.Add(OrderLine.For(product, quantity, unitPrice));
        }
    }

    public void ConfirmStock(DateTimeOffset now)
    {
        EnsureStatus(OrderStatus.AwaitingStock);
        Status = OrderStatus.AwaitingPayment;
        Raise(new OrderStockConfirmedDomainEvent(Id, Total, now));
    }

    public void ConfirmPayment(PaymentReference reference, DateTimeOffset now)
    {
        EnsureStatus(OrderStatus.AwaitingPayment);
        Status = OrderStatus.Confirmed;
        // Total and Lines are required by the V1.OrderConfirmed contract (§9.1);
        // the mapper has only the event to work from.
        Raise(
            new OrderConfirmedDomainEvent(Id, CustomerId, reference, ShippingAddress, Total, SnapshotLines(), now));
    }

    public void MarkShipped(TrackingNumber tracking, DateTimeOffset now)
    {
        EnsureStatus(OrderStatus.Confirmed);
        Status = OrderStatus.Shipped;
        Raise(new OrderShippedDomainEvent(Id, CustomerId, tracking, now));
    }

    // Origin is recorded, never checked: no invariant here turns on who
    // asked, and §11.4 has already decided whether this caller may. It
    // travels because §9.6's saga has to tell its own echo from a
    // cancellation somebody else caused, and Reason cannot answer that.
    public void Cancel(CancellationReason reason, CancellationOrigin origin, DateTimeOffset now)
    {
        if (Status is OrderStatus.Shipped or OrderStatus.Delivered)
        {
            throw new DomainException(
                $"A {Status} order cannot be cancelled; raise a return instead.");
        }
        if (Status is OrderStatus.Cancelled)
            return;   // Idempotent — cancelling twice is not an error.

        Status = OrderStatus.Cancelled;
        Raise(new OrderCancelledDomainEvent(Id, CustomerId, reason, origin, now));
    }

    private void EnsureStatus(OrderStatus expected)
    {
        if (Status != expected)
        {
            throw new DomainException(
                $"Expected order to be {expected} but it is {Status}.");
        }
    }
}
```

Points worth noticing:

- **`Place` is a factory, not a constructor.** It names the business operation
  and can enforce rules a constructor cannot express cleanly.
- **`_lines` is private; `Lines` is read-only.** Callers cannot bypass `AddLine`
  and its invariants.
- **`Cancel` is idempotent.** Because events arrive at-least-once, aggregate
  methods driven by events should tolerate repetition.
- **`now` is a parameter.** The domain never reads the clock. This makes every
  time-dependent rule trivially testable without freezing time globally.
- **Other aggregates are referenced by ID**, never by object. `CustomerId`, not
  `Customer`. This is what keeps the aggregate loadable in one query and the
  transaction boundary honest.

## 5.5 Domain events

```csharp
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}

/// <summary>Non-generic, so §7.5 can filter the change tracker by it without a key type.</summary>
public interface IHasDomainEvents
{
    IReadOnlyList<IDomainEvent> DomainEvents { get; }
    void ClearDomainEvents();
}

/// <summary>Non-generic, so §6.3's one-aggregate assertion can test <c>is IAggregateRoot</c>.</summary>
public interface IAggregateRoot;

/// <summary>
/// §5.1's first row, made executable: identity persists through change, so two
/// entities of the same type with the same Id are the same thing however much
/// else differs between them.
/// </summary>
public abstract class Entity<TId> : IEquatable<Entity<TId>>
    where TId : struct
{
    public TId Id { get; protected set; }

    // Type as well as identifier. A comparison that comes down to the Id alone
    // makes an OrderLine equal to the Order it belongs to the moment the two
    // share a key type, and nothing about that reads as wrong at the call site.
    public bool Equals(Entity<TId>? other) =>
        other is not null &&
        GetType() == other.GetType() &&
        EqualityComparer<TId>.Default.Equals(Id, other.Id);

    public override bool Equals(object? obj) => Equals(obj as Entity<TId>);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);

    // Declared, not inherited. Without these two, `==` goes on comparing
    // references while `Equals` compares identifiers — the same pair equal by
    // one operator and not the other, in a language where both read the same.
    public static bool operator ==(Entity<TId>? left, Entity<TId>? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !(left == right);
}

public abstract class AggregateRoot<TId>
    : Entity<TId>, IAggregateRoot, IHasDomainEvents
    where TId : struct
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();

    // Optimistic concurrency token, mapped to SQL Server rowversion.
    public byte[] Version { get; private set; } = [];
}
```

> **Both markers must be on the base class, and neither failure is loud.** A
> marker used only in a predicate is a filter that silently matches nothing:
> `Entries<IHasDomainEvents>()` returns empty, `CollectAndClear()` returns
> empty, the dispatcher exits early, and the command commits having staged no
> outbox rows at all — no projection, no integration event, no saga start. The
> write succeeds and every downstream mechanism in this document runs on an
> empty list. `IAggregateRoot` fails the same way, more quietly: the
> one-aggregate assertion ([§6.3](06-cqrs.md)) counts zero and never fires.

The events themselves are records in the Domain project, free to carry domain
types — that freedom is the point of keeping them separate from contracts:

```csharp
namespace Ordering.Domain.Orders.Events;

/// <summary>Immutable copy of a line as it stood when the event was raised.</summary>
public sealed record OrderLineSnapshot(ProductId ProductId, int Quantity, Money UnitPrice);

public sealed record OrderPlacedDomainEvent(
    OrderId OrderId,
    CustomerId CustomerId,
    Money Total,
    IReadOnlyList<OrderLineSnapshot> Lines,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record OrderConfirmedDomainEvent(
    OrderId OrderId,
    CustomerId CustomerId,
    PaymentReference Reference,
    Address ShippingAddress,
    Money Total,
    IReadOnlyList<OrderLineSnapshot> Lines,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record OrderStockConfirmedDomainEvent(
    OrderId OrderId,
    Money Total,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record OrderShippedDomainEvent(
    OrderId OrderId,
    CustomerId CustomerId,
    TrackingNumber Tracking,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record OrderCancelledDomainEvent(
    OrderId OrderId,
    CustomerId CustomerId,
    CancellationReason Reason,
    CancellationOrigin Origin,
    DateTimeOffset OccurredAt) : IDomainEvent;
```

All five name the identifier `OrderId`, not `Id`. The projection in §6.6 reads
`e.OrderId` across every handler, so one record calling it something else would
break only that handler — the kind of divergence a positional `Raise(Id, …)`
call site hides completely.

Two rules these illustrate:

**Carry everything the contract needs.** `OrderConfirmedDomainEvent` holds
`Total` and `Lines` it makes no domain use of, because `V1.OrderConfirmed`
requires them and the mapper ([§9.3](09-messaging.md)) sees only the event. An event missing a
field its contract declares is a mapper that cannot be written. **The rule
reaches those two fields and stops there** — `ShippingAddress` on the same
record is carried for a reason no contract supplies, argued in the callout
below.

**Snapshot, never alias.** `OrderLineSnapshot` exists because `Order.Lines` is a
read-only *view* over a live list of mutable entities. An event holding that
view would report whatever the aggregate looks like later, not what happened
when it was raised.

`Money`, `OrderId` and `ProductId` here would be forbidden in an integration
event (§9.1) — the mapper is where they are flattened to primitives.

> **`ShippingAddress` is on this record and on no contract, and the asymmetry
> is the point rather than an omission.** `V1.OrderConfirmed` carries no
> address at all
> ([ADR-035](adr/ADR-035-an-integration-event-carries-identifiers-not-personal-data.md)),
> so §9.3's mapper no longer reads this field — and the mapper was its only
> reader outside the aggregate, which is exactly why the rule above stopped
> covering it. What covers it instead is the aggregate's own record:
> confirmation is the point at which an order commits to a destination, `Order`
> holds the `Address` it was placed with, and [§7.2](07-persistence.md) maps
> that address into `ordering.Orders` as a complex property. An event naming
> only the payment reference would state less than the transition decided.
>
> **A domain event may hold it because it never leaves the service.**
> [§11.7](11-identity-authorization.md) governs what travels between services,
> not what one service keeps about its own data — so the same value is
> unremarkable here and forbidden one mapper later, and nothing about the field
> changed between the two. **A field is not dead because no contract wants
> one**: the domain event and the contract are separate artefacts by design,
> and this record is where that separation is visible in both directions at
> once — `Total` and `Lines` carried for the contract, `ShippingAddress`
> carried for the domain alone.

Events accumulate on the aggregate. Raising one does not publish it, and the
two halves of what happens next are worth keeping apart, because [§7.5](07-persistence.md) is
normative about both and one of them is easy to state backwards:
`DomainEventDispatcher` runs **inside** the transaction, before `SaveChanges`,
and only **stages** outbox rows — it invokes no handler. Everything that
*reacts* runs after commit, driven by the outbox (ADR-018). "Dispatched" is the
staging; delivery is the dispatcher's job later.

**Domain events and integration events are different things** and conflating
them is one of the most consequential mistakes in this architecture:

| | Domain event | Integration event |
|---|---|---|
| Scope | Inside one service | Across services |
| Coupling | Free to change with the code | A published contract, versioned |
| Content | Rich domain types | Primitives and simple DTOs only |
| Delivery | In-process after commit, via the outbox's `Local` lane (§7.5) | Message broker, via the outbox's `Broker` lane |
| Naming | `*DomainEvent` suffix | Bare name, in a versioned namespace (§9.2) |
| Example | `OrderConfirmedDomainEvent` | `Common.Contracts.Ordering.V1.OrderConfirmed` |

A domain event may be translated into an integration event. Never publish a
domain event directly onto the bus — it exposes your internal model as a public
contract and you will not be able to refactor afterwards.

**They must not share a type name.** `OrderPlacedDomainEvent` and
`Common.Contracts.Ordering.V1.OrderPlaced` describe the same business fact in
two shapes — one carries `Money`, the other a `decimal` and a currency code —
and a single name for both makes the mapper in §9.3 read as an identity
function. Namespace versioning (ADR-012) distinguishes contract *versions*, not
contracts from domain events; the suffix does that.

## 5.6 Repositories

Defined in Domain, implemented in Infrastructure. One per aggregate root.

```csharp
namespace Ordering.Domain.Orders;

public interface IOrderRepository
{
    Task<Order?> GetAsync(OrderId id, CancellationToken ct);
    Task<Order?> GetByPaymentReferenceAsync(PaymentReference reference, CancellationToken ct);
    void Add(Order order);
}
```

There is no `Update` — the unit of work tracks changes — and no `GetAll`,
`Find(Expression<...>)` or `IQueryable`. A repository that returns `IQueryable`
lets callers build arbitrary queries against the domain model, which leaks
persistence concerns everywhere and makes the aggregate boundary unenforceable.

**Reads do not go through repositories.** Query handlers use Dapper directly
against read models (section 6.5). Repositories exist only to load aggregates
for the purpose of changing them.

## 5.7 Anti-patterns

| Anti-pattern | Why it fails | Instead |
|---|---|---|
| Anaemic domain model — entities of public setters, logic in services | The model enforces nothing; invariants scatter and drift | Behaviour on the aggregate; setters private |
| Aggregate referencing another aggregate by object | Loads half the database; transaction spans two consistency boundaries | Reference by typed ID |
| Huge aggregate (`Customer` owning all orders) | Every write locks everything; concurrency collapses | Split by invariant, not by "belongs to" |
| Repository per entity | Lets callers modify children outside the root | Repository per aggregate root only |
| Injecting `IEmailSender` into an aggregate | Domain now depends on infrastructure and cannot be unit tested | Raise a domain event; handle it outside |
| `DateTime.UtcNow` inside the domain | Non-deterministic tests, hidden dependency | Pass time in as a parameter |
| Domain exceptions used for validation of user input | Exceptions for control flow; poor error messages | Validate in the application layer; domain exceptions signal bugs |

---

[← §4 Solution structure](04-solution-structure.md) · [Index](README.md) · [§6 CQRS →](06-cqrs.md)
