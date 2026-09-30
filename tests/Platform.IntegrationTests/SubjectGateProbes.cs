using Common.Contracts;

namespace Platform.IntegrationTests;

/// <summary>Synthetic contracts that drive ADR-028's gate where the live contracts cannot.</summary>
/// <remarks>Outside the contract assembly and namespace, so <see cref="ContractTests"/> never finds them.</remarks>
internal static class SubjectGateProbes
{
    /// <summary>A payload carried by a command and by an event.</summary>
    internal sealed record SharedLine(Guid ProductId, Guid CustomerId, int Quantity);

    /// <summary>A payload only the event carries.</summary>
    internal sealed record EventOnlyLine(Guid ProductId, Guid CustomerId);

    /// <summary>A command reaching its payload through a two-argument generic.</summary>
    internal sealed record ProbeCommand(
        Guid OrderId,
        IReadOnlyDictionary<string, SharedLine> Lines);

    /// <summary>An event carrying the shared payload and one of its own.</summary>
    internal sealed record ProbeEvent : IIntegrationEvent
    {
        public required Guid MessageId { get; init; }

        public required Guid CorrelationId { get; init; }

        public required DateTimeOffset OccurredAt { get; init; }

        public required IReadOnlyList<SharedLine> Shared { get; init; }

        public required IReadOnlyList<EventOnlyLine> Own { get; init; }
    }

    /// <summary>One member per declared subject spelling, not all <c>*Id</c>, as the match is a substring.</summary>
    internal sealed record EverySpelling(
        Guid CustomerId,
        Guid BuyerReference,
        Guid PayerId,
        Guid SubjectIdentifier,
        Guid UserId,
        Guid PrincipalId);

    /// <summary>The universe those four form, in the order a reader expects.</summary>
    internal static Type[] Universe =>
    [
        typeof(ProbeCommand),
        typeof(ProbeEvent),
        typeof(SharedLine),
        typeof(EventOnlyLine)
    ];

    /// <summary>A command that an event also carries.</summary>
    internal sealed record CarriedCommand(Guid OrderId, Guid CustomerId);

    /// <summary>The event carrying it, which takes <see cref="CarriedCommand"/> out of root inference.</summary>
    internal sealed record CommandCarryingEvent(Guid OrderId, CarriedCommand Echo) : IIntegrationEvent
    {
        public Guid MessageId => Guid.Empty;

        public Guid CorrelationId => Guid.Empty;

        public DateTimeOffset OccurredAt => DateTimeOffset.MinValue;
    }

    /// <summary>The two of them, as a universe the roots test can drive.</summary>
    internal static Type[] EventCarriesCommandUniverse =>
    [
        typeof(CarriedCommand),
        typeof(CommandCarryingEvent)
    ];
}
