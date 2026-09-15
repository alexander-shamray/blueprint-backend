namespace Common.Domain;

/// <summary>
/// An object whose identity persists through change (§5.1): equal by
/// <see cref="Id"/>, where a value object is equal by its values.
/// </summary>
/// <remarks>
/// <typeparamref name="TId"/> is a struct because §5.2's identifiers are
/// readonly record structs, which keeps a raw <c>Guid</c> or <c>string</c> out.
/// </remarks>
public abstract class Entity<TId> : IEquatable<Entity<TId>>
    where TId : struct
{
    /// <summary>
    /// Assigned once, by whatever creates the entity. The setter is protected
    /// rather than init-only because §5.4's aggregates assign it inside a
    /// factory after the base constructor has run, and because EF Core
    /// materialises through a private parameterless constructor.
    /// </summary>
    public TId Id { get; protected set; }

    /// <summary>
    /// Type as well as identifier: two entity types keyed by the same
    /// identifier are different things, and a comparison that ignored the type
    /// would make an <c>OrderId</c>-keyed line equal to its own order.
    /// </summary>
    public bool Equals(Entity<TId>? other) =>
        other is not null &&
        GetType() == other.GetType() &&
        EqualityComparer<TId>.Default.Equals(Id, other.Id);

    public override bool Equals(object? obj) => Equals(obj as Entity<TId>);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);

    /// <summary>
    /// Declared, not inherited. Without these two, <c>==</c> would keep
    /// comparing references while <c>Equals</c> compared identifiers — the same
    /// two entities equal by one operator and not the other, in a language
    /// where both spellings read identically.
    /// </summary>
    public static bool operator ==(Entity<TId>? left, Entity<TId>? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !(left == right);
}
