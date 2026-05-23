namespace CopilotUsageFilter;

/// <summary>
/// Appends raw OTLP trace JSON bodies to
/// <c>%USERPROFILE%\.copilot\token-usage.jsonl</c> (always on).
/// Each request body is written as one line (JSONL format).
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
            // Ensure the .copilot directory exists
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

    /// <summary>Appends a raw trace JSON body as a single line.</summary>
    public static void Write(string json)
    {
        if (s_writer == null) return;
        lock (s_lock)
        {
            try
            {
                s_writer.WriteLine(json);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[TraceExporter] Write error: {ex.Message}");
            }
        }
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
