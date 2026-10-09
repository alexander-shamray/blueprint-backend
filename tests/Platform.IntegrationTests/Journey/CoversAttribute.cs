namespace Platform.IntegrationTests.Journey;

/// <summary>The saga transition a journey drives: an event in a state, named as the machine names them (§9.6).</summary>
/// <remarks><see cref="SagaCoverageTests"/> reads these against the machine's own transitions.</remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class CoversAttribute(string state, string @event) : Attribute
{
    public string State { get; } = state;

    public string Event { get; } = @event;
}
