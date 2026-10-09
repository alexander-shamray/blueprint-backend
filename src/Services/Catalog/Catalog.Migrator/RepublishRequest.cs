namespace Catalog.Migrator;

/// <summary>What a republish run was asked for (ADR-090): one id, or null for everything the service holds.</summary>
/// <remarks>A class, since a record's synthesised equality would reach past §4.2's allow-list.</remarks>
public sealed class RepublishRequest(Guid? id)
{
    public Guid? Id { get; } = id;
}
