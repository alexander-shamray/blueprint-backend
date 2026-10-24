using System.Text.Json;
using System.Text.Json.Nodes;
using Common.Assistant.Protocol;
using Common.Assistant.Tools;
using Shouldly;

namespace Common.Assistant.Tests;

/// <summary>
/// The protocol's JSON, pinned (AI assistant design §14, "Protocol"): every body and event the core writes serializes
/// to its golden file under <c>src/BuildingBlocks/Common.Assistant/Protocol/golden/</c>, and every body it reads
/// deserializes from one.
/// </summary>
public sealed class ProtocolGoldenTests
{
    // The SPA's protocol.spec.ts reads the same files, so the C# owner and the TypeScript mirror cannot drift.
    // How the host writes both its JSON answers and its event data.
    private static readonly JsonSerializerOptions Web = new(JsonSerializerOptions.Web);

    private static readonly JsonObject Logs = new() { ["path"] = "logs" };

    public static TheoryData<string, object> Written => new()
    {
        { "status-enabled", new AssistantStatus(true, "fake", "scripted", null) },
        { "status-off", AssistantStatus.Off },
        { "conversation-opened", new ConversationOpened("0123456789abcdef0123456789abcdef") },
        {
            "conversation-view",
            new ConversationView(
                "0123456789abcdef0123456789abcdef",
                [
                    new OperatorItem("где логи?"),
                    new ReplyItem("Открываю «Logs». "),
                    new ToolItem("call-1", "open_screen", ToolSide.Client, Logs.DeepClone().AsObject(), true),
                    new ReplyItem("Экран «Logs» открыт."),
                    new EndItem(TurnEndReason.Done, null),
                ],
                false,
                7,
                [])
        },
        { "event-text", new TextEvent("Открываю «Logs». ") },
        {
            "event-tool-started",
            new ToolStartedEvent("call-1", "open_screen", ToolSide.Client, Logs.DeepClone().AsObject())
        },
        { "event-tool-finished", new ToolFinishedEvent("call-1", true) },
        { "event-client-tool-call", new ClientToolCallEvent("call-1", "open_screen", Logs.DeepClone().AsObject()) },
        {
            "event-turn-ended",
            new TurnEndedEvent(TurnEndReason.Refused, "cyber", new Usage(120, 8, 100), new Usage(240, 30, 100))
        },
        { "event-error", new ErrorEvent("provider-error", "The assistant's provider failed", 502, "connection reset") },
        {
            "event-confirmation-required",
            new ConfirmationRequiredEvent("call-2", "stack_down", new JsonObject { ["wipe"] = true }, "down -v")
        },
    };

    [Theory]
    [MemberData(nameof(Written))]
    public void What_the_core_writes_is_its_golden_file(string golden, object value)
    {
        JsonNode written = JsonSerializer.SerializeToNode(value, value.GetType(), Web)!;

        JsonNode.DeepEquals(written, Golden(golden)).ShouldBeTrue($"{golden}.json: wrote {written.ToJsonString()}");
    }

    [Fact]
    public void An_open_body_reads_from_its_golden_file()
    {
        OpenConversation open = Read<OpenConversation>("open-conversation");

        open.Client.ShouldBe(new ClientInfo("blueprint-admin", "1"));
        open.Locale.ShouldBe("ru-RU");
        open.Catalogue.ShouldBe([new CatalogueEntry("logs", "Logs", "/logs", "Follows the platform's log lines.")]);
        ToolDescriptor tool = open.ClientTools.Single();
        tool.Name.ShouldBe("open_screen");
        tool.Side.ShouldBe(ToolSide.Client);
        tool.Annotations.ShouldBe(
            new ToolAnnotations(
                ReadOnlyHint: true,
                DestructiveHint: false,
                IdempotentHint: true,
                OpenWorldHint: false));
        tool.InputSchema["required"]!.AsArray().Single()!.GetValue<string>().ShouldBe("path");
        open.HostOptions!["identity"]!.GetValue<string>().ShouldBe("demo");
        JsonNode.DeepEquals(JsonSerializer.SerializeToNode(open, Web), Golden("open-conversation")).ShouldBeTrue();
    }

    [Fact]
    public void A_turn_body_reads_from_its_golden_file()
    {
        StartTurn turn = Read<StartTurn>("start-turn");

        turn.Text.ShouldBe("где логи?");
        turn.Context!["page"]!.GetValue<string>().ShouldBe("stack");
        turn.Locale.ShouldBe("ru-RU");
        turn.HostOptions.ShouldBeNull();
    }

    [Fact]
    public void A_tool_result_body_reads_from_its_golden_file() =>
        Read<ClientToolResult>("client-tool-result").ShouldBe(new ClientToolResult("call-1", true, "Opened /logs."));

    [Fact]
    public void Every_golden_file_is_pinned_by_a_test()
    {
        string[] pinned =
        [
            .. Written.Select(row => (string)((ITheoryDataRow)row).GetData()[0]!),
            "open-conversation",
            "start-turn",
            "client-tool-result"
        ];

        Directory.GetFiles(GoldenDirectory(), "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Order(StringComparer.Ordinal)
            .ShouldBe(pinned.Order(StringComparer.Ordinal));
    }

    private static T Read<T>(string golden) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(GoldenDirectory(), $"{golden}.json")), Web)!;

    private static JsonNode Golden(string golden) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(GoldenDirectory(), $"{golden}.json")))!;

    private static string GoldenDirectory()
    {
        string root = AppContext.BaseDirectory;

        while (!File.Exists(Path.Combine(root, "Platform.slnx")))
        {
            root = Path.GetDirectoryName(root)
                ?? throw new InvalidOperationException("Platform.slnx not found above the test output.");
        }

        return Path.Combine(root, "src", "BuildingBlocks", "Common.Assistant", "Protocol", "golden");
    }
}
