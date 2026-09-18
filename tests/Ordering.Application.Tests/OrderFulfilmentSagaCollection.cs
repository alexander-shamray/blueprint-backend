using Xunit;

namespace Ordering.Application.Tests;

/// <summary>
/// §12.5's suite as one collection, so its classes run one at a time: every
/// test starts its own in-memory bus and waits on it against
/// <see cref="OrderFulfilmentSagaHarness.InactivityTimeout"/>, and buses
/// running side by side are load those bounds would otherwise have to absorb.
/// </summary>
[CollectionDefinition(nameof(OrderFulfilmentSagaCollection))]
public sealed class OrderFulfilmentSagaCollection;
