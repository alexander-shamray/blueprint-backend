using Payments.TestSupport;
using Xunit;

namespace Payments.Api.Tests;

/// <summary>
/// §12.4's per-assembly declaration: xUnit resolves collections within an
/// assembly, so each consuming test project declares its own over the
/// shared <see cref="ServiceFixture"/>. Two assemblies mean two container
/// sets per run — the stated price of the pyramid's levels mapping onto
/// projects. The category sits here rather than on each member class:
/// xUnit v3 applies a collection's traits to every test in it, so joining
/// the collection is carrying the category, with no per-class attribute for
/// a new test class to forget.
/// </summary>
[CollectionDefinition(nameof(IntegrationCollection))]
[Trait("Category", "Integration")]
public sealed class IntegrationCollection : ICollectionFixture<ServiceFixture>;
