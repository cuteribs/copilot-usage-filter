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
                        var attrs = ParseAttributes(span?["attributes"]?.AsArray());
                        if (attrs == null) continue;

                        // Filter: only chat spans
                        if (!string.Equals(attrs.GenAiOperationName, "chat",
                                StringComparison.OrdinalIgnoreCase)) continue;

                        result.Add(attrs);
                    }
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
        if (dict.TryGetValue("gen_ai.usage.input_tokens", out var inp) && TryParseInt(inp, out var inputTokens))
            result.InputTokens = inputTokens;
        if (dict.TryGetValue("gen_ai.usage.output_tokens", out var out_) && TryParseInt(out_, out var outputTokens))
            result.OutputTokens = outputTokens;
        if (dict.TryGetValue("gen_ai.usage.cache_creation.input_tokens", out var cc) && TryParseInt(cc, out var cct))
            result.CacheCreationTokens = cct;
        if (dict.TryGetValue("gen_ai.usage.cache_read.input_tokens", out var cr) && TryParseInt(cr, out var crt))
            result.CacheReadTokens = crt;

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
