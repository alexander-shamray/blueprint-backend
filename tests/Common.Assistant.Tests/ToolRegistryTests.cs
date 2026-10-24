using Common.Assistant.Tools;
using Shouldly;

namespace Common.Assistant.Tests;

/// <summary>
/// What may not be registered (AI assistant design §6.3): refused at registration, not argued out of a prompt.
/// </summary>
public sealed class ToolRegistryTests
{
    [Theory]
    [InlineData("stack_up")]
    [InlineData("stack_down")]
    [InlineData("wipe")]
    [InlineData("reset_projection")]
    [InlineData("replay_dead_letter")]
    [InlineData("discard_dead_letter")]
    [InlineData("execute_runbook")]
    [InlineData("delete_page")]
    [InlineData("run_scenario")]
    [InlineData("send_request")]
    public void A_tool_named_for_one_of_the_consoles_actions_is_refused_on_either_side(string name)
    {
        Should.Throw<ToolRefusedException>(
            () => ToolRegistry.Build([], [Given.ReadOnly(name, ToolSide.Client)], ToolPolicy.ReadOnly));
        Should.Throw<ToolRefusedException>(
            () => ToolRegistry.Build([new Offering(Given.ReadOnly(name, ToolSide.Server))], [], ToolPolicy.ReadOnly));
    }

    [Theory]
    [InlineData("list_runbooks")]
    [InlineData("lookup_order")]
    [InlineData("open_screen")]
    [InlineData("recent_logs")]
    public void A_name_that_merely_contains_an_action_word_is_admitted(string name) =>
        ToolRegistry.Build([], [Given.ReadOnly(name, ToolSide.Client)], ToolPolicy.ReadOnly)
            .All.Single().Name.ShouldBe(name);

    [Fact]
    public void A_server_tool_that_is_not_read_only_or_is_destructive_is_refused()
    {
        ToolDescriptor writes = Given.ReadOnly("stack_status", ToolSide.Server) with
        {
            Annotations = new ToolAnnotations(
                ReadOnlyHint: false,
                DestructiveHint: false,
                IdempotentHint: true,
                OpenWorldHint: false),
        };
        ToolDescriptor destroys = Given.ReadOnly("queue_depths", ToolSide.Server) with
        {
            Annotations = new ToolAnnotations(
                ReadOnlyHint: true,
                DestructiveHint: true,
                IdempotentHint: true,
                OpenWorldHint: false),
        };

        Should.Throw<ToolRefusedException>(() => ToolRegistry.Build([new Offering(writes)], [], ToolPolicy.ReadOnly))
            .Message.ShouldContain("read-only");
        Should.Throw<ToolRefusedException>(() => ToolRegistry.Build([new Offering(destroys)], [], ToolPolicy.ReadOnly))
            .Message.ShouldContain("read-only");
    }

    [Fact]
    public void A_core_whose_host_offers_a_refused_tool_fails_at_construction()
    {
        ToolDescriptor writes = Given.ReadOnly("stack_status", ToolSide.Server) with
        {
            Annotations = new ToolAnnotations(
                ReadOnlyHint: false,
                DestructiveHint: true,
                IdempotentHint: false,
                OpenWorldHint: false),
        };

        Should.Throw<ToolRefusedException>(() => new AssistantCore(
            new ScriptedProvider(),
            [new Offering(writes)],
            ToolPolicy.ReadOnly,
            new PlantedRedactor(),
            "",
            new AssistantLimits(),
            TimeProvider.System));
    }

    [Fact]
    public void A_client_tool_cannot_shadow_a_server_tool_or_claim_to_be_the_hosts()
    {
        Offering host = new(Given.ReadOnly("stack_status", ToolSide.Server));

        Should.Throw<ToolRefusedException>(
            () => ToolRegistry.Build([host], [Given.ReadOnly("stack_status", ToolSide.Client)], ToolPolicy.ReadOnly))
            .Message.ShouldContain("twice");

        ToolRegistry registry =
            ToolRegistry.Build([host], [Given.ReadOnly("open_screen", ToolSide.Server)], ToolPolicy.ReadOnly);
        registry.SideOf("open_screen").ShouldBe(ToolSide.Client);
        registry.TryServer("open_screen", out _).ShouldBeFalse();
        registry.TryServer("stack_status", out IToolSource? source).ShouldBeTrue();
        source.ShouldBeSameAs(host);
    }

    [Fact]
    public void The_tools_are_sorted_by_name_so_the_cached_prefix_is_deterministic() =>
        ToolRegistry.Build(
                [new Offering(
                    Given.ReadOnly("stack_status", ToolSide.Server),
                    Given.ReadOnly("firing_alerts", ToolSide.Server))],
                [Given.ReadOnly("open_screen", ToolSide.Client)],
                ToolPolicy.ReadOnly)
            .All.Select(t => t.Name).ShouldBe(["firing_alerts", "open_screen", "stack_status"]);

    [Theory]
    [InlineData("OpenScreen")]
    [InlineData("open-screen")]
    [InlineData("_open")]
    [InlineData("")]
    public void A_name_that_is_not_snake_case_is_refused(string name) =>
        Should.Throw<ToolRefusedException>(
            () => ToolRegistry.Build([], [Given.ReadOnly(name, ToolSide.Client)], ToolPolicy.ReadOnly));

    private sealed class Offering(params ToolDescriptor[] tools) : IToolSource
    {
        public IReadOnlyList<ToolDescriptor> Tools { get; } = tools;

        public ValueTask<ToolOutcome> InvokeAsync(
            string name,
            System.Text.Json.Nodes.JsonObject input,
            ToolContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ToolOutcome(true, ""));
    }
}
