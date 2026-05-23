using CopilotUsageFilter;

namespace CopilotUsageFilter.Tests;

public class SpanPayloadTests
{
    [Fact]
    public void TryParse_ChatSpan_IsChat_WithCorrectTokens()
    {
        var json = """
            {
              "type": "span",
              "attributes": {
                "gen_ai.operation.name": "chat",
                "gen_ai.usage.input_tokens": 100,
                "gen_ai.usage.output_tokens": 50,
                "gen_ai.conversation.id": "session-1",
                "github.copilot.interaction_id": "interaction-1",
                "github.copilot.turn_id": "0"
              }
            }
            """;

        var payload = SpanPayload.TryParse(json);

        Assert.NotNull(payload);
        Assert.True(payload.IsChat);
        var a = payload.Attributes!;
        Assert.Equal(100,           a.InputTokens);
        Assert.Equal(50,            a.OutputTokens);
        Assert.Equal("session-1",     a.ConversationId);
        Assert.Equal("interaction-1", a.InteractionId);
        Assert.Equal("0",             a.TurnId);
    }

    [Fact]
    public void TryParse_NonChatOperationName_IsChatFalse()
    {
        var json = """{"type":"span","attributes":{"gen_ai.operation.name":"completion"}}""";

        var payload = SpanPayload.TryParse(json);

        Assert.NotNull(payload);
        Assert.False(payload.IsChat);
    }

    [Fact]
    public void TryParse_TypeNotSpan_IsChatFalse()
    {
        var json = """{"type":"metric","attributes":{"gen_ai.operation.name":"chat"}}""";

        var payload = SpanPayload.TryParse(json);

        Assert.NotNull(payload);
        Assert.False(payload.IsChat);
    }

    [Fact]
    public void TryParse_MissingType_IsChatFalse()
    {
        var json = """{"attributes":{"gen_ai.operation.name":"chat"}}""";

        var payload = SpanPayload.TryParse(json);

        Assert.NotNull(payload);
        Assert.False(payload.IsChat);
    }

    [Fact]
    public void TryParse_InvalidJson_ReturnsNull()
    {
        var payload = SpanPayload.TryParse("not-valid {{ json");
        Assert.Null(payload);
    }

    [Fact]
    public void TryParse_EmptyObject_IsChatFalse()
    {
        var payload = SpanPayload.TryParse("{}");
        Assert.NotNull(payload);
        Assert.False(payload.IsChat);
    }

    [Fact]
    public void TryParse_CaseInsensitiveOperationName_Chat_IsChat()
    {
        // gen_ai.operation.name matching is case-insensitive
        var json = """{"type":"span","attributes":{"gen_ai.operation.name":"CHAT"}}""";

        var payload = SpanPayload.TryParse(json);

        Assert.NotNull(payload);
        Assert.True(payload.IsChat);
    }
}
