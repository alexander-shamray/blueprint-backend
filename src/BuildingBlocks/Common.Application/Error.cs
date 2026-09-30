namespace Common.Application;

/// <summary>A failure a handler returns rather than throws; <see cref="Type"/> selects the status (§10.5).</summary>
/// <remarks><see cref="Code"/> is a metric dimension, so its value set is closed (§10.5).</remarks>
public sealed record Error(string Code, string Description, ErrorType Type)
{
    public static Error NotFound(string code, string description) =>
        new(code, description, ErrorType.NotFound);

    public static Error Rule(string code, string description) =>
        new(code, description, ErrorType.Rule);

    public static Error Unavailable(string code, string description) =>
        new(code, description, ErrorType.Unavailable);
}

/// <summary>No Validation member: a malformed request never reaches a handler to return one (§10.5).</summary>
public enum ErrorType { NotFound, Rule, Unavailable }
