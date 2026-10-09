namespace Platform.IntegrationTests.Journey;

/// <summary>
/// The saga transition a journey test drives: an event in a state, named as the machine names them (§9.6).
/// </summary>
/// <remarks>
/// <see cref="SagaCoverageTests"/> enumerates the machine's transitions and finds each here or in its own table of
/// the ones a journey cannot drive, so a transition added to the saga fails until it is classified.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class CoversAttribute(string state, string @event) : Attribute
{
    public string State { get; } = state;

    public string Event { get; } = @event;
}
