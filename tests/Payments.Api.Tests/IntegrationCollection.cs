using Payments.TestSupport;
using Xunit;

namespace Payments.Api.Tests;

/// <summary>
/// §12.4's per-assembly declaration: xUnit resolves collections within an
/// assembly, so a test project that needs containers declares its own over
/// the shared <see cref="ServiceFixture"/>. The category sits here rather
/// than on each member class:
/// xUnit v3 applies a collection's traits to every test in it, so joining
/// the collection is carrying the category, with no per-class attribute for
/// a new test class to forget.
/// </summary>
[CollectionDefinition(nameof(IntegrationCollection))]
[Trait("Category", "Integration")]
public sealed class IntegrationCollection : ICollectionFixture<ServiceFixture>;
