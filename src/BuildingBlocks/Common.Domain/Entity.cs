namespace Common.Domain;

/// <summary>Equal by type and <see cref="Id"/>, where a value object is equal by its values (§5.1).</summary>
public abstract class Entity<TId> : IEquatable<Entity<TId>>
    where TId : struct
{
    /// <summary>Protected, not init: §5.4's factories set it after construction.</summary>
    public TId Id { get; protected set; }

    public bool Equals(Entity<TId>? other) =>
        other is not null &&
        GetType() == other.GetType() &&
        EqualityComparer<TId>.Default.Equals(Id, other.Id);

    public override bool Equals(object? obj) => Equals(obj as Entity<TId>);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);

    /// <summary>Declared, so <c>==</c> and <c>Equals</c> agree rather than one comparing references.</summary>
    public static bool operator ==(Entity<TId>? left, Entity<TId>? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !(left == right);
}
