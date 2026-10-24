namespace Common.Assistant.Tools;

/// <summary>
/// The tools one conversation offers: every host source's and the client's, each admitted by the policy (AI assistant
/// design §6.3), names unique across both sides, sorted by name so the cached prefix is deterministic (design
/// §6.5). A client tool cannot shadow a server tool, and a client cannot hand in a tool that claims to be the host's.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, IToolSource> servers;

    private ToolRegistry(IReadOnlyList<ToolDescriptor> all, Dictionary<string, IToolSource> servers)
    {
        All = all;
        this.servers = servers;
    }

    /// <summary>Every tool, server and client, by name.</summary>
    public IReadOnlyList<ToolDescriptor> All { get; }

    /// <exception cref="ToolRefusedException">A tool the policy refuses, or a name used twice.</exception>
    public static ToolRegistry Build(
        IEnumerable<IToolSource> sources,
        IEnumerable<ToolDescriptor> clientTools,
        ToolPolicy policy)
    {
        Dictionary<string, IToolSource> servers = new(StringComparer.Ordinal);
        Dictionary<string, ToolDescriptor> all = new(StringComparer.Ordinal);

        foreach (IToolSource source in sources)
        {
            foreach (ToolDescriptor tool in source.Tools)
            {
                if (tool.Side != ToolSide.Server)
                {
                    throw new ToolRefusedException(
                        $"{tool.Name} is offered by a host source but is not a server tool.");
                }

                Admit(tool, policy, all);
                servers[tool.Name] = source;
            }
        }

        foreach (ToolDescriptor tool in clientTools)
        {
            // The client says what its tools are; it cannot say one runs on the host.
            Admit(tool with { Side = ToolSide.Client }, policy, all);
        }

        return new ToolRegistry([.. all.Values.OrderBy(t => t.Name, StringComparer.Ordinal)], servers);
    }

    public bool TryServer(string name, out IToolSource? source) => servers.TryGetValue(name, out source);

    public ToolSide? SideOf(string name) => All.FirstOrDefault(t => t.Name == name)?.Side;

    private static void Admit(ToolDescriptor tool, ToolPolicy policy, Dictionary<string, ToolDescriptor> all)
    {
        if (policy.Refusal(tool) is { } refusal)
        {
            throw new ToolRefusedException(refusal);
        }

        if (!all.TryAdd(tool.Name, tool))
        {
            throw new ToolRefusedException(
                $"{tool.Name} is registered twice; a tool name is unique across the host and the client.");
        }
    }
}

/// <summary>A tool the registry would not take, with the reason.</summary>
public sealed class ToolRefusedException(string message) : Exception(message);
