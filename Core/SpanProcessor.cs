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
    public void Process(string path, string json)
    {
        if (!path.StartsWith("/v1/traces", StringComparison.OrdinalIgnoreCase))
            return; // only process trace requests

        // Write raw body to file before parsing (if exporter is configured)
        TraceFileExporter.Write(json);

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
            HandleChatSpan(simplified.Attributes!);
    }

    private static void HandleChatSpan(SpanAttributes attrs)
    {
        PrintTokens(attrs);
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
        // Sub-agent spans inherit interaction_id from sibling spans but have no
        // corresponding assistant.message event in session state — skip patching.
        if (attrs.IsSubAgent) return;

        if (string.IsNullOrEmpty(attrs.ConversationId) ||
            string.IsNullOrEmpty(attrs.InteractionId))
            return;

        var folder = SessionStatePatcher.FindSessionFolder(attrs.ConversationId);
        if (folder == null) return;

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
