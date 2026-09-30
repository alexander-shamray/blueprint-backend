using Payments.TestSupport;
using Xunit;

namespace Payments.Api.Tests;

/// <summary>§12.4's per-assembly collection; xUnit v3 gives its trait to every test in it.</summary>
[CollectionDefinition(nameof(IntegrationCollection))]
[Trait("Category", "Integration")]
public sealed class IntegrationCollection : ICollectionFixture<ServiceFixture>;
