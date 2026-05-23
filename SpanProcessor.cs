namespace CopilotUsageFilter;

/// <summary>
/// Processes incoming JSON bodies from the OTLP HTTP receiver.
/// Supports two formats:
///   1. Simplified: {"type":"span","attributes":{...}}
///   2. Real OTLP:  {"resourceSpans":[{"scopeSpans":[{"spans":[...]}]}]}
/// Only spans with gen_ai.operation.name="chat" are acted upon.
/// </summary>
public sealed class SpanProcessor
{
    public void Process(string path, string json)
    {
        // Log path so the user can see what Copilot CLI is sending
        if (path.Contains("metrics", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("logs", StringComparison.OrdinalIgnoreCase))
        {
            // Silently ignore — only traces are of interest
            return;
        }

        // 1. Try real OTLP resourceSpans format
        var spans = OtlpTraceParser.ExtractChatSpans(json);
        if (spans.Count > 0)
        {
            foreach (var attrs in spans)
                HandleChatSpan(attrs);
            return;
        }

        // 2. Try simplified {"type":"span",...} format
        var simplified = SpanPayload.TryParse(json);
        if (simplified?.IsChat == true)
        {
            HandleChatSpan(simplified.Attributes!);
            return;
        }

        // 3. Unknown / non-trace payload — silently ignore
        return;
    }

    private static void HandleChatSpan(SpanAttributes attrs)
    {
        PrintTokens(attrs);
        PatchSession(attrs);
    }

    private static void PrintTokens(SpanAttributes attrs)
    {
        // Format: YYYY-MM-DDTHH:mm:ss Session=… Interaction=… input=n output=n cache_write=n cache_read=n
        var ts = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

        var line = ts;
        line += $" Session={attrs.ConversationId ?? "-"}";
        line += $" Interaction={attrs.InteractionId ?? "-"}";
        line += $" input={attrs.InputTokens ?? 0}";
        line += $" output={attrs.OutputTokens ?? 0}";
        line += $" cache_write={attrs.CacheCreationTokens ?? 0}";
        line += $" cache_read={attrs.CacheReadTokens ?? 0}";

        Console.WriteLine(line);
    }

    private static void PatchSession(SpanAttributes attrs)
    {
        if (string.IsNullOrEmpty(attrs.ConversationId) ||
            string.IsNullOrEmpty(attrs.InteractionId))
            return;

        var folder = SessionStatePatcher.FindSessionFolder(attrs.ConversationId);
        if (folder == null) return;

        SessionStatePatcher.PatchAssistantMessage(
            folder,
            attrs.InteractionId,
            attrs.InputTokens,
            attrs.CacheCreationTokens,
            attrs.CacheReadTokens,
            attrs.OutputTokens);
    }
}
