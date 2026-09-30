using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>§12.4's per-assembly declaration, whose trait every member class's tests inherit.</summary>
/// <remarks>The trait selects a stage and is never a skip, which fails open on a missing daemon (§12.4).</remarks>
[CollectionDefinition(nameof(IntegrationCollection))]
[Trait("Category", "Integration")]
public sealed class IntegrationCollection : ICollectionFixture<RedisFixture>;
