using Payments.TestSupport;
using Xunit;

namespace Payments.Api.Tests;

/// <summary>
/// §12.4's per-assembly declaration: xUnit resolves collections within an
/// assembly, so each consuming test project declares its own over the shared
/// <see cref="ServiceFixture"/>. Two assemblies mean two container sets per
/// run — the stated price of the pyramid's levels mapping onto projects.
/// </summary>
/// <remarks>
/// <b>The category sits here rather than on each member class, and that is
/// the whole design.</b> xUnit v3 applies a collection's traits to every test
/// in it, so joining the collection <i>is</i> carrying the category and there
/// is no per-class attribute for a new test class to forget. It decides which
/// stage runs a test and never whether it may be absent — a skip on a missing
/// daemon fails open and is still refused (§12.4). See <c>docs/testing.md</c>
/// for the filters this makes available.
/// </remarks>
[CollectionDefinition(nameof(IntegrationCollection))]
[Trait("Category", "Integration")]
public sealed class IntegrationCollection : ICollectionFixture<ServiceFixture>;
