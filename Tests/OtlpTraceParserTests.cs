using CopilotUsageFilter;

namespace CopilotUsageFilter.Tests;

public class OtlpTraceParserTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────
    private static string OtlpJson(
        string operationName    = "chat",
        string inputTokens      = "100",
        string outputTokens     = "50",
        bool   numericIntValue  = false,
        bool   oldFieldName     = false,
        string? conversationId  = "session-abc",
        string? interactionId   = "interaction-xyz",
        string? turnId          = "0",
        string? extraAttrs      = null)
    {
        // Build intValue either as JSON string or JSON number
        var inputFmt = numericIntValue
            ? $"{{\"intValue\":{inputTokens}}}"
            : $"{{\"intValue\":\"{inputTokens}\"}}";

        var fieldName = oldFieldName ? "instrumentationLibrarySpans" : "scopeSpans";

        var sb = new System.Text.StringBuilder();
        sb.Append($"{{\"key\":\"gen_ai.operation.name\",\"value\":{{\"stringValue\":\"{operationName}\"}}}}");
        if (conversationId != null)
            sb.Append($",{{\"key\":\"gen_ai.conversation.id\",\"value\":{{\"stringValue\":\"{conversationId}\"}}}}");
        if (interactionId != null)
            sb.Append($",{{\"key\":\"github.copilot.interaction_id\",\"value\":{{\"stringValue\":\"{interactionId}\"}}}}");
        if (turnId != null)
            sb.Append($",{{\"key\":\"github.copilot.turn_id\",\"value\":{{\"stringValue\":\"{turnId}\"}}}}");
        sb.Append($",{{\"key\":\"gen_ai.usage.input_tokens\",\"value\":{inputFmt}}}");
        sb.Append($",{{\"key\":\"gen_ai.usage.output_tokens\",\"value\":{{\"intValue\":\"{outputTokens}\"}}}}");
        if (extraAttrs != null)
            sb.Append($",{extraAttrs}");

        return $$"""
            {
              "resourceSpans": [{
                "{{fieldName}}": [{
                  "spans": [{ "attributes": [{{sb}}] }]
                }]
              }]
            }
            """;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────
    [Fact]
    public void ExtractChatSpans_RealOtlpJson_ReturnsChatSpanWithAllFields()
    {
        var spans = OtlpTraceParser.ExtractChatSpans(OtlpJson());

        Assert.Single(spans);
        var s = spans[0];
        Assert.Equal("session-abc",    s.ConversationId);
        Assert.Equal("interaction-xyz", s.InteractionId);
        Assert.Equal("0",              s.TurnId);
        Assert.Equal(100,              s.InputTokens);
        Assert.Equal(50,               s.OutputTokens);
    }

    [Fact]
    public void ExtractChatSpans_NonChatOperationName_ReturnsEmpty()
    {
        var spans = OtlpTraceParser.ExtractChatSpans(OtlpJson(operationName: "completion"));
        Assert.Empty(spans);
    }

    [Fact]
    public void ExtractChatSpans_MetricsPayload_ReturnsEmpty()
    {
        var spans = OtlpTraceParser.ExtractChatSpans("""{"resourceMetrics":[]}""");
        Assert.Empty(spans);
    }

    [Fact]
    public void ExtractChatSpans_LogsPayload_ReturnsEmpty()
    {
        var spans = OtlpTraceParser.ExtractChatSpans("""{"resourceLogs":[]}""");
        Assert.Empty(spans);
    }

    [Fact]
    public void ExtractChatSpans_IntValueAsJsonNumber_ParsesCorrectly()
    {
        var spans = OtlpTraceParser.ExtractChatSpans(
            OtlpJson(inputTokens: "200", numericIntValue: true));

        Assert.Single(spans);
        Assert.Equal(200, spans[0].InputTokens);
    }

    [Fact]
    public void ExtractChatSpans_OldInstrumentationLibrarySpansFieldName_Works()
    {
        var spans = OtlpTraceParser.ExtractChatSpans(OtlpJson(oldFieldName: true));

        Assert.Single(spans);
        Assert.Equal(100, spans[0].InputTokens);
    }

    [Fact]
    public void ExtractChatSpans_InvalidJson_ReturnsEmpty()
    {
        var spans = OtlpTraceParser.ExtractChatSpans("not-json {{{{");
        Assert.Empty(spans);
    }

    [Fact]
    public void ExtractChatSpans_CacheTokenAttributes_ParsedCorrectly()
    {
        var extra = """
            {"key":"gen_ai.usage.cache_creation.input_tokens","value":{"intValue":"20"}},
            {"key":"gen_ai.usage.cache_read.input_tokens","value":{"intValue":"5"}}
            """;

        var spans = OtlpTraceParser.ExtractChatSpans(OtlpJson(extraAttrs: extra));

        Assert.Single(spans);
        Assert.Equal(20, spans[0].CacheCreationTokens);
        Assert.Equal(5,  spans[0].CacheReadTokens);
    }

    [Fact]
    public void ExtractChatSpans_MultipleSpans_ReturnsOnlyChatOnes()
    {
        var json = """
            {
              "resourceSpans": [{
                "scopeSpans": [{
                  "spans": [
                    { "attributes": [
                        {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}}
                    ]},
                    { "attributes": [
                        {"key":"gen_ai.operation.name","value":{"stringValue":"embedding"}}
                    ]},
                    { "attributes": [
                        {"key":"gen_ai.operation.name","value":{"stringValue":"CHAT"}}
                    ]}
                  ]
                }]
              }]
            }
            """;

        var spans = OtlpTraceParser.ExtractChatSpans(json);
        // Case-insensitive match: "chat" and "CHAT" both match
        Assert.Equal(2, spans.Count);
    }
}
