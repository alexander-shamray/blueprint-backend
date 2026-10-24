using System.Text.RegularExpressions;

namespace Common.Assistant.Tools;

/// <summary>
/// What may be registered (AI assistant design §6.3). The refusal is structural: a tool that changes anything is
/// refused when it is registered, not argued out of a prompt. The host passes the policy in, and nothing reads it from
/// configuration; allowing an action is a later note's code and spec change, never a setting.
/// </summary>
public sealed partial class ToolPolicy
{
    // A whole snake_case segment of a name: `stack_up` is refused, `lookup` is not.
    private readonly string[] actionWords;

    // A verb that starts a longer name: `run_scenario` and `send_request` are refused, `runbooks` is not.
    private readonly string[] actionVerbs;

    private ToolPolicy(string[] actionWords, string[] actionVerbs)
    {
        this.actionWords = actionWords;
        this.actionVerbs = actionVerbs;
    }

    /// <summary>
    /// Read-only, on both sides: every tool must say it reads and does not destroy, and no name may read as an action.
    /// A client tool changes the view, never the platform, so it is read-only in the platform's sense.
    /// </summary>
    public static ToolPolicy ReadOnly { get; } =
        new(["up", "down", "wipe", "reset", "replay", "discard", "execute", "delete"], ["run", "send"]);

    /// <summary>Why <paramref name="tool"/> may not be registered, or null when it may.</summary>
    public string? Refusal(ToolDescriptor tool)
    {
        if (tool.Name.Length > 64 || !SnakeCase().IsMatch(tool.Name))
        {
            return $"{tool.Name} is not a snake_case tool name of at most 64 characters.";
        }

        if (!tool.Annotations.ReadOnlyHint || tool.Annotations.DestructiveHint)
        {
            return $"{tool.Name} is not annotated read-only and non-destructive; " +
                "the assistant explains, navigates and reads.";
        }

        string[] segments = tool.Name.Split('_');

        if (segments.FirstOrDefault(s => actionWords.Contains(s, StringComparer.Ordinal)) is { } word)
        {
            return $"{tool.Name} names an action ({word}); the assistant has no tool that changes the platform.";
        }

        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (actionVerbs.Contains(segments[i], StringComparer.Ordinal))
            {
                return $"{tool.Name} names an action ({segments[i]}_); " +
                    "the assistant has no tool that changes the platform.";
            }
        }

        return null;
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(?:_[a-z0-9]+)*$")]
    private static partial Regex SnakeCase();
}
