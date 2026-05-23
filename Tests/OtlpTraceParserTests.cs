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

    /// <summary>
    /// Builds an OTLP batch with a 3-span chain:
    ///   execute_tool (spanId=exec-span, parentId=root-span, toolCallId=callId)
    ///   → invoke_agent (spanId=invoke-span, parentId=exec-span)
    ///     → chat (spanId=chat-sub, parentId=invoke-span, conversationId, no interactionId)
    /// Plus an optional direct chat span (spanId=chat-direct, parentId=root-span,
    /// conversationId + interactionId) so the sub-agent can inherit interactionId.
    /// Uses $$$ raw literals so {{ / }} in JSON content are treated as literal braces.
    /// </summary>
    private static string OtlpSubAgentJson(
        string callId            = "call_ABC123",
        string conversationId    = "conv-xyz",
        string interactionId     = "inter-abc",
        bool   includeDirectChat = true)
    {
        var directSpan = includeDirectChat ? $$$"""
            ,{"spanId":"chat-direct","parentSpanId":"root-span","attributes":[
                {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
                {"key":"gen_ai.conversation.id","value":{"stringValue":"{{{conversationId}}}"}},
                {"key":"github.copilot.interaction_id","value":{"stringValue":"{{{interactionId}}}"}},
                {"key":"github.copilot.turn_id","value":{"stringValue":"0"}},
                {"key":"gen_ai.usage.input_tokens","value":{"intValue":"500"}},
                {"key":"gen_ai.usage.output_tokens","value":{"intValue":"100"}}
            ]}
            """ : "";

        return $$$"""
            {
              "resourceSpans": [{
                "scopeSpans": [{
                  "spans": [
                    {"spanId":"exec-span","parentSpanId":"root-span","attributes":[
                        {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
                        {"key":"gen_ai.tool.call.id","value":{"stringValue":"{{{callId}}}"}}
                    ]},
                    {"spanId":"invoke-span","parentSpanId":"exec-span","attributes":[
                        {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}}
                    ]},
                    {"spanId":"chat-sub","parentSpanId":"invoke-span","attributes":[
                        {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
                        {"key":"gen_ai.conversation.id","value":{"stringValue":"{{{conversationId}}}"}},
                        {"key":"gen_ai.usage.input_tokens","value":{"intValue":"1000"}},
                        {"key":"gen_ai.usage.output_tokens","value":{"intValue":"200"}}
                    ]}
                    {{{directSpan}}}
                  ]
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

    // ── Sub-agent SubAgentToolCallId resolution tests ─────────────────────────

    [Fact]
    public void ExtractChatSpans_SubAgentChain_SetsSubAgentToolCallId()
    {
        // Chain: execute_tool(callId) → invoke_agent → chat(sub-agent)
        //        + direct chat (provides interactionId for inheritance)
        var spans = OtlpTraceParser.ExtractChatSpans(
            OtlpSubAgentJson(callId: "call_XYZ", includeDirectChat: true));

        Assert.Equal(2, spans.Count);

        var sub    = spans.Single(s => s.IsSubAgent);
        var direct = spans.Single(s => !s.IsSubAgent);

        Assert.Equal("call_XYZ",  sub.SubAgentToolCallId);
        Assert.Equal("inter-abc", sub.InteractionId);     // inherited
        Assert.Null(direct.SubAgentToolCallId);
    }

    [Fact]
    public void ExtractChatSpans_SubAgentChain_NoDirectChatSibling_IsSubAgentFalse()
    {
        // Without a direct-chat sibling, sub-agent cannot inherit interactionId.
        // IsSubAgent stays false (no inherited interactionId), but SubAgentToolCallId
        // is still resolved because the chain info is independent of inheritance.
        var spans = OtlpTraceParser.ExtractChatSpans(
            OtlpSubAgentJson(includeDirectChat: false));

        Assert.Single(spans);
        var sub = spans[0];
        // interactionId could not be inherited → IsSubAgent remains false
        Assert.False(sub.IsSubAgent);
        // But the execute_tool → invoke_agent linkage is still resolved
        Assert.Equal("call_ABC123", sub.SubAgentToolCallId);
    }

    [Fact]
    public void ExtractChatSpans_InvokeAgentParentIsNotExecuteTool_SubAgentToolCallIdNull()
    {
        // invoke_agent whose parent is a regular root span (not execute_tool):
        // no toolCallId can be determined.
        var json = """
            {
              "resourceSpans": [{
                "scopeSpans": [{
                  "spans": [
                    {"spanId":"root-span","parentSpanId":"","attributes":[
                        {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}}
                    ]},
                    {"spanId":"invoke-span","parentSpanId":"root-span","attributes":[
                        {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}}
                    ]},
                    {"spanId":"chat-sub","parentSpanId":"invoke-span","attributes":[
                        {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
                        {"key":"gen_ai.conversation.id","value":{"stringValue":"conv-abc"}},
                        {"key":"gen_ai.usage.input_tokens","value":{"intValue":"100"}}
                    ]},
                    {"spanId":"chat-direct","parentSpanId":"root-span","attributes":[
                        {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
                        {"key":"gen_ai.conversation.id","value":{"stringValue":"conv-abc"}},
                        {"key":"github.copilot.interaction_id","value":{"stringValue":"inter-abc"}},
                        {"key":"github.copilot.turn_id","value":{"stringValue":"0"}}
                    ]}
                  ]
                }]
              }]
            }
            """;

        var spans = OtlpTraceParser.ExtractChatSpans(json);
        Assert.Equal(2, spans.Count);

        var sub = spans.Single(s => s.IsSubAgent);
        Assert.Null(sub.SubAgentToolCallId);
    }
}
