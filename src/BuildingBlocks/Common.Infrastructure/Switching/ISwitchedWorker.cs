namespace Common.Infrastructure.Switching;

/// <summary>A worker loop an operator can switch off; a host test fails for a loop that does not implement
/// it.</summary>
public interface ISwitchedWorker
{
    /// <summary>The name listed under <see cref="OperatorSwitchOptions.Off"/>, unique within a host.</summary>
    string SwitchName { get; }
}
