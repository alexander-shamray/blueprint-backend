using Xunit;

namespace Web.Bff.Tests;

/// <summary>§12.4's collection over SQL Server and the broker; xUnit v3 gives its trait to every test in it.</summary>
[CollectionDefinition(nameof(BffIntegrationCollection))]
[Trait("Category", "Integration")]
public sealed class BffIntegrationCollection : ICollectionFixture<BffServiceFixture>;
