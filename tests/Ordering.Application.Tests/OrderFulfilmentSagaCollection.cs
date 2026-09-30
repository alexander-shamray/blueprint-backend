using Xunit;

namespace Ordering.Application.Tests;

/// <summary>§12.5's suite as one collection, so buses side by side do not eat into its timeout bounds.</summary>
[CollectionDefinition(nameof(OrderFulfilmentSagaCollection))]
public sealed class OrderFulfilmentSagaCollection;
