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
    // Console output uses ForegroundColor + Write + ResetColor which is not atomic.
    // Multiple Task.Run handlers can race and interleave color changes → garbled output.
    private static readonly object s_consoleLock = new();

    // Cross-batch cache: conversationId → interactionId.
    // Populated by direct-agent chat spans; used to supply interactionId to sub-agent
    // chat spans that arrive in a later batch without a direct sibling.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string>
        s_interactionCache = new(StringComparer.OrdinalIgnoreCase);

    public void Process(string path, string json)
    {
        if (!path.StartsWith("/v1/traces", StringComparison.OrdinalIgnoreCase))
            return; // only process trace requests

        // 1. Try real OTLP resourceSpans format
        var spans = OtlpTraceParser.ExtractChatSpans(json);
        if (spans.Count > 0)
        {
            // Update the cross-batch cache with any direct-agent interactionIds found.
            foreach (var s in spans)
            {
                if (!s.IsSubAgent &&
                    s.ConversationId != null &&
                    s.InteractionId  != null)
                {
                    s_interactionCache.TryAdd(s.ConversationId, s.InteractionId);
                }
            }

            // Apply cache to sub-agent spans still missing interactionId
            // (i.e. their sibling direct-agent span arrived in an earlier batch).
            foreach (var s in spans)
            {
                if (s.InteractionId == null &&
                    s.ConversationId != null &&
                    s_interactionCache.TryGetValue(s.ConversationId, out var cached))
                {
                    s.InteractionId = cached;
                    s.IsSubAgent    = true;
                }
            }

            foreach (var attrs in spans)
                HandleChatSpan(attrs);
            return;
        }

        // 2. Try simplified {"type":"span",...} format
        var simplified = SpanPayload.TryParse(json);
        if (simplified?.IsChat == true)
            HandleChatSpan(simplified.Attributes!);
    }

    private static void HandleChatSpan(SpanAttributes attrs)
    {
        PrintTokens(attrs);
        TraceFileExporter.Write(attrs);
        PatchSession(attrs);
    }

    private static void PrintTokens(SpanAttributes attrs)
    {
        // Truncate GUIDs to first 8 chars for readability
        static string Short(string? s) => s is { Length: > 8 } ? s[..8] : (s ?? "-");

        lock (s_consoleLock)
        {
        // Timestamp in dark gray
        WriteColored(DateTime.Now.ToString("s"), ConsoleColor.DarkGray);

        WriteKV("\tsession",     Short(attrs.ConversationId),                    ConsoleColor.Yellow);
        WriteKV("\tinteraction", Short(attrs.InteractionId),                     ConsoleColor.Cyan);
        WriteKV("\tturn",        attrs.IsSubAgent ? "sub" : (attrs.TurnId ?? "-"), ConsoleColor.Magenta);
        WriteKV("\tinput",       (attrs.InputTokens          ?? 0).ToString(),   ConsoleColor.Green);
        WriteKV("\toutput",      (attrs.OutputTokens         ?? 0).ToString(),   ConsoleColor.Blue);
        WriteKV("\tcache_write", (attrs.CacheCreationTokens  ?? 0).ToString(),   ConsoleColor.DarkYellow);
        WriteKV("\tcache_read",  (attrs.CacheReadTokens      ?? 0).ToString(),   ConsoleColor.DarkCyan);

        Console.WriteLine();
        } // end lock
    }

    // Writes "key=VALUE" where VALUE gets the specified color; key is in default color.
    private static void WriteKV(string key, string value, ConsoleColor valueColor)
    {
        Console.Write(key + '=');
        WriteColored(value, valueColor);
    }

    private static void WriteColored(string text, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ResetColor();
    }

    private static void PatchSession(SpanAttributes attrs)
    {
        if (string.IsNullOrEmpty(attrs.ConversationId) ||
            string.IsNullOrEmpty(attrs.InteractionId))
            return;

        var folder = SessionStatePatcher.FindSessionFolder(attrs.ConversationId);
        if (folder == null) return;

        if (attrs.IsSubAgent)
        {
            // Patch the subagent.completed session event with the detailed token
            // breakdown (inputTokens, outputTokens, cache, reasoning) that Copilot
            // records only as an opaque totalTokens sum.
            // Primary path: SubAgentToolCallId known → match by toolCallId.
            // Fallback path: SubAgentToolCallId null (flat OTLP hierarchy) →
            //   match by totalTokens + model (uniqueness required).
            SessionStatePatcher.PatchSubAgentCompleted(
                folder,
                attrs.SubAgentToolCallId,
                attrs.InputTokens,
                attrs.CacheCreationTokens,
                attrs.CacheReadTokens,
                attrs.OutputTokens,
                attrs.ReasoningOutputTokens,
                attrs.Model);
            return;
        }

        // Direct (non-sub-agent) spans: patch by interactionId + turnId.
        SessionStatePatcher.PatchAssistantMessage(
            folder,
            attrs.InteractionId,
            attrs.TurnId,       // may be null — falls back to last-match behaviour
            attrs.InputTokens,
            attrs.CacheCreationTokens,
            attrs.CacheReadTokens,
            attrs.OutputTokens);
    }
}
