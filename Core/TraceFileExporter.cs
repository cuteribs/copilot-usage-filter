using System.Text.Json;

namespace CopilotUsageFilter;

/// <summary>
/// Appends one clean JSON line per chat span to
/// <c>%USERPROFILE%\.copilot\token-usage.jsonl</c>.
/// Only token-usage-relevant fields are written; all OTLP noise is discarded.
/// Thread-safe; file opened once at startup in append mode.
/// </summary>
public static class TraceFileExporter
{
    public static readonly string FilePath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot", "token-usage.jsonl");

    private static StreamWriter? s_writer;
    private static readonly object s_lock = new();

    static TraceFileExporter()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            s_writer = new StreamWriter(FilePath, append: true, System.Text.Encoding.UTF8)
            {
                AutoFlush = true,
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TraceExporter] Cannot open '{FilePath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Appends a single clean JSON line for this chat span.
    /// Only non-null / non-zero token fields are included.
    /// </summary>
    public static void Write(SpanAttributes attrs)
    {
        if (s_writer == null) return;

        var line = BuildLine(attrs);
        lock (s_lock)
        {
            try { s_writer.WriteLine(line); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[TraceExporter] Write error: {ex.Message}");
            }
        }
    }

    private static string BuildLine(SpanAttributes attrs)
    {
        using var ms = new System.IO.MemoryStream();
        using var w = new Utf8JsonWriter(ms);

        w.WriteStartObject();
        w.WriteString("ts",          DateTime.UtcNow.ToString("O"));
        if (attrs.ConversationId  != null) w.WriteString("sess", attrs.ConversationId);
        if (attrs.InteractionId   != null) w.WriteString("intr", attrs.InteractionId);

        if (attrs.IsSubAgent)
            w.WriteString("turn", "s");
        else if (attrs.TurnId != null)
            w.WriteString("turn", attrs.TurnId);

        if (attrs.Model           != null) w.WriteString("model",   attrs.Model);
        if (attrs.InputTokens        > 0)  w.WriteNumber("input",   attrs.InputTokens!.Value);
        if (attrs.OutputTokens       > 0)  w.WriteNumber("output",  attrs.OutputTokens!.Value);
        if (attrs.CacheReadTokens    > 0)  w.WriteNumber("cread",   attrs.CacheReadTokens!.Value);
        if (attrs.CacheCreationTokens > 0) w.WriteNumber("cwrite",  attrs.CacheCreationTokens!.Value);
        if (attrs.ReasoningOutputTokens > 0) w.WriteNumber("reasoning",   attrs.ReasoningOutputTokens!.Value);
        w.WriteEndObject();

        w.Flush();
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Flushes and closes the export file. Safe to call multiple times.</summary>
    public static void Shutdown()
    {
        lock (s_lock)
        {
            try { s_writer?.Close(); } catch { }
            s_writer = null;
        }
    }
}
