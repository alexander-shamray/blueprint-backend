namespace Common.Application;

/// <summary>A command modified more than one aggregate root (§2.3, principle 3), caught at run time (§6.3).</summary>
public sealed class InvariantViolationException(string message) : Exception(message);
