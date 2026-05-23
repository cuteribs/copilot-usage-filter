using System.Text.Json;
using System.Text.Json.Nodes;

namespace CopilotUsageFilter;

/// <summary>
/// Parses real OTLP/HTTP JSON trace payloads (POST /v1/traces).
/// OTLP JSON schema: { "resourceSpans": [ { "scopeSpans": [ { "spans": [ { "attributes": [...] } ] } ] } ] }
/// Attributes are encoded as: { "key": "foo", "value": { "stringValue"|"intValue"|"doubleValue"|"boolValue": ... } }
/// </summary>
public static class OtlpTraceParser
{
    /// <summary>
    /// Returns all spans from the payload that have gen_ai.operation.name = "chat".
    /// Returns an empty list for non-trace payloads (e.g. resourceMetrics).
    /// Also resolves the execute_tool → invoke_agent → chat linkage so that
    /// sub-agent spans carry a SubAgentToolCallId when the chain is deterministic.
    /// </summary>
    public static List<SpanAttributes> ExtractChatSpans(string json)
    {
        var result = new List<SpanAttributes>();
        try
        {
            var root = JsonNode.Parse(json);
            if (root == null) return result;

            // Skip metrics/logs payloads
            if (root["resourceMetrics"] != null || root["resourceLogs"] != null)
                return result;

            var resourceSpans = root["resourceSpans"]?.AsArray();
            if (resourceSpans == null) return result;

            // ── Pass 1: scan ALL spans to build span-relationship maps ────────────
            // spanId → (opName, toolCallId)  — for execute_tool / invoke_agent lookups
            var spanMeta   = new Dictionary<string, (string? OpName, string? ToolCallId)>(
                                 StringComparer.OrdinalIgnoreCase);
            // spanId → parentSpanId
            var spanParent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // chat spans deferred for Pass 3
            var chatNodes  = new List<(string SpanId, string ParentId, JsonArray? Attrs)>();

            foreach (var rs in resourceSpans)
            {
                var scopeSpans = rs?["scopeSpans"]?.AsArray()
                              ?? rs?["instrumentationLibrarySpans"]?.AsArray(); // older proto name
                if (scopeSpans == null) continue;

                foreach (var ss in scopeSpans)
                {
                    var spans = ss?["spans"]?.AsArray();
                    if (spans == null) continue;

                    foreach (var span in spans)
                    {
                        var spanId   = span?["spanId"]?.GetValue<string>()       ?? "";
                        var parentId = span?["parentSpanId"]?.GetValue<string>() ?? "";
                        var attrsArr = span?["attributes"]?.AsArray();

                        string? opName = null, toolCallId = null;
                        if (attrsArr != null)
                        {
                            foreach (var item in attrsArr)
                            {
                                var key = item?["key"]?.GetValue<string>();
                                if (key == null) continue;
                                if (key == "gen_ai.operation.name")
                                    opName = ExtractValue(item?["value"]);
                                else if (key == "gen_ai.tool.call.id")
                                    toolCallId = ExtractValue(item?["value"]);
                            }
                        }

                        if (!string.IsNullOrEmpty(spanId))
                        {
                            spanMeta[spanId]   = (opName, toolCallId);
                            spanParent[spanId] = parentId;
                        }

                        if (string.Equals(opName, "chat", StringComparison.OrdinalIgnoreCase))
                            chatNodes.Add((spanId, parentId, attrsArr));
                    }
                }
            }

            // ── Pass 2: build invoke_agent → toolCallId lookup ────────────────────
            // An invoke_agent span gets a toolCallId only when its direct parent is
            // an execute_tool span that carries gen_ai.tool.call.id.
            var invokeAgentCallId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in spanMeta)
            {
                if (!string.Equals(kvp.Value.OpName, "invoke_agent",
                        StringComparison.OrdinalIgnoreCase)) continue;
                if (!spanParent.TryGetValue(kvp.Key, out var parentId) ||
                        string.IsNullOrEmpty(parentId)) continue;
                if (!spanMeta.TryGetValue(parentId, out var parentMeta)) continue;
                if (!string.Equals(parentMeta.OpName, "execute_tool",
                        StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(parentMeta.ToolCallId)) continue;

                invokeAgentCallId[kvp.Key] = parentMeta.ToolCallId;
            }

            // ── Pass 3: parse chat spans and set SubAgentToolCallId ───────────────
            foreach (var (spanId, parentId, attrsArr) in chatNodes)
            {
                var attrs = ParseAttributes(attrsArr);
                if (attrs == null) continue;
                result.Add(attrs);

                // chat → invoke_agent → execute_tool chain
                if (!string.IsNullOrEmpty(parentId) &&
                    spanMeta.TryGetValue(parentId, out var parentMeta) &&
                    string.Equals(parentMeta.OpName, "invoke_agent",
                        StringComparison.OrdinalIgnoreCase) &&
                    invokeAgentCallId.TryGetValue(parentId, out var callId))
                {
                    attrs.SubAgentToolCallId = callId;
                }
            }

            // ── Pass 4: inherit interaction_id for sub-agent spans ────────────────
            // Sub-agent chat spans have no interaction_id of their own; inherit from
            // a direct-agent span in the same batch sharing the same conversation_id.
            var knownInteractions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in result)
            {
                if (s.ConversationId != null && s.InteractionId != null)
                    knownInteractions.TryAdd(s.ConversationId, s.InteractionId);
            }

            foreach (var s in result)
            {
                if (s.InteractionId == null &&
                    s.ConversationId != null &&
                    knownInteractions.TryGetValue(s.ConversationId, out var inherited))
                {
                    s.InteractionId = inherited;
                    s.IsSubAgent = true;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[OtlpParser] Parse error: {ex.Message}");
        }
        return result;
    }

    private static SpanAttributes? ParseAttributes(JsonArray? arr)
    {
        if (arr == null) return null;

        // Build a flat key→value dictionary
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in arr)
        {
            var key = item?["key"]?.GetValue<string>();
            if (key == null) continue;
            var valNode = item?["value"];
            var val = ExtractValue(valNode);
            dict[key] = val;
        }

        if (!dict.TryGetValue("gen_ai.operation.name", out var opName)) return null;

        var result = new SpanAttributes { GenAiOperationName = opName };

        if (dict.TryGetValue("gen_ai.conversation.id", out var convId))
            result.ConversationId = convId;
        if (dict.TryGetValue("github.copilot.interaction_id", out var interactionId))
            result.InteractionId = interactionId;
        if (dict.TryGetValue("github.copilot.turn_id", out var turnId))
            result.TurnId = turnId;
        if (dict.TryGetValue("gen_ai.response.model", out var model))
            result.Model = model;
        if (dict.TryGetValue("gen_ai.usage.input_tokens", out var inp) && TryParseInt(inp, out var inputTokens))
            result.InputTokens = inputTokens;
        if (dict.TryGetValue("gen_ai.usage.output_tokens", out var out_) && TryParseInt(out_, out var outputTokens))
            result.OutputTokens = outputTokens;
        if (dict.TryGetValue("gen_ai.usage.cache_creation.input_tokens", out var cc) && TryParseInt(cc, out var cct))
            result.CacheCreationTokens = cct;
        if (dict.TryGetValue("gen_ai.usage.cache_read.input_tokens", out var cr) && TryParseInt(cr, out var crt))
            result.CacheReadTokens = crt;
        if (dict.TryGetValue("gen_ai.usage.reasoning.output_tokens", out var ro) && TryParseInt(ro, out var rot))
            result.ReasoningOutputTokens = rot;

        return result;
    }

    /// <summary>
    /// OTLP value nodes look like: {"stringValue":"foo"} or {"intValue":"123"} or {"intValue":123}
    /// </summary>
    private static string? ExtractValue(JsonNode? valueNode)
    {
        if (valueNode == null) return null;

        if (valueNode["stringValue"] is { } sv)
            return sv.GetValue<string>();

        if (valueNode["intValue"] is { } iv)
        {
            // intValue can be a JSON number or a quoted string (OTLP spec quirk)
            try { return iv.GetValue<long>().ToString(); } catch { }
            try { return iv.GetValue<string>(); } catch { }
        }

        if (valueNode["doubleValue"] is { } dv)
            try { return dv.GetValue<double>().ToString(); } catch { }

        if (valueNode["boolValue"] is { } bv)
            try { return bv.GetValue<bool>().ToString(); } catch { }

        return null;
    }

    private static bool TryParseInt(string? s, out long value)
    {
        value = 0;
        return s != null && long.TryParse(s, out value);
    }
}
