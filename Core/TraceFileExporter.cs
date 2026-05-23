namespace CopilotUsageFilter;

/// <summary>
/// Appends raw OTLP trace JSON bodies to a file when the
/// <c>COPILOT_OTEL_FILE_EXPORTER_PATH</c> environment variable is set.
/// Each request body is written as one line (JSONL format).
/// Thread-safe; opened once at startup in append mode.
/// </summary>
public static class TraceFileExporter
{
    private static readonly string? s_filePath =
        Environment.GetEnvironmentVariable("COPILOT_OTEL_FILE_EXPORTER_PATH");

    private static StreamWriter? s_writer;
    private static readonly object s_lock = new();

    static TraceFileExporter()
    {
        if (s_filePath == null) return;
        try
        {
            s_writer = new StreamWriter(s_filePath, append: true, System.Text.Encoding.UTF8)
            {
                AutoFlush = true,
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TraceExporter] Cannot open '{s_filePath}': {ex.Message}");
        }
    }

    /// <summary>True when the file path env var is set and the file was opened successfully.</summary>
    public static bool IsEnabled => s_writer != null;

    /// <summary>The resolved file path (null when disabled).</summary>
    public static string? FilePath => IsEnabled ? s_filePath : null;

    /// <summary>Appends a raw trace JSON body as a single line to the export file.</summary>
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
