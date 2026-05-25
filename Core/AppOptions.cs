namespace CopilotUsageFilter;

/// <summary>
/// Runtime-mutable configuration shared across all components.
/// A single instance is created at startup and passed by reference everywhere
/// so that live changes (e.g. from the tray icon) take effect immediately.
/// </summary>
public sealed class AppOptions
{
    /// <summary>Local OTLP listener port (default 4318).</summary>
    public int Port { get; set; } = 4318;

    /// <summary>
    /// Remote OTLP/HTTP collector base URL (e.g. "http://my-collector:4318").
    /// When set, every incoming request is forwarded to this endpoint after
    /// local processing. Null or empty means no forwarding.
    /// </summary>
    public string? ForwardTo { get; set; }

    // ── Parsing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if any of the args is a help flag (<c>--help</c>, <c>-h</c>, <c>-?</c>).
    /// </summary>
    public static bool IsHelp(string[] args) =>
        args.Any(a => a is "--help" or "-h" or "-?" or "/?");

    /// <summary>
    /// Prints usage to stdout.
    /// </summary>
    public static void PrintHelp(string exe = "copilot-usage-filter")
    {
        Console.WriteLine($"Usage: {exe} [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -p, --port <n>          Local OTLP/HTTP listener port (default: 4318)");
        Console.WriteLine("  -f, --forward-to <url>  Forward all OTLP signals to a remote collector");
        Console.WriteLine("                          e.g. http://my-collector:4318");
        Console.WriteLine("  -h, --help              Show this help and exit");
        Console.WriteLine();
        Console.WriteLine("Environment:");
        Console.WriteLine("  Set OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:<port> in the");
        Console.WriteLine("  Copilot CLI environment so its traces are sent here.");
        Console.WriteLine();
        Console.WriteLine("Output files:");
        Console.WriteLine($"  %USERPROFILE%\\.copilot\\token-usage.jsonl  (per-span JSONL export)");
    }

    /// <summary>
    /// Parses command-line args. Supported flags:
    /// <list type="bullet">
    ///   <item><c>--port &lt;n&gt;</c> or <c>-p &lt;n&gt;</c></item>
    ///   <item><c>--forward-to &lt;url&gt;</c> or <c>-f &lt;url&gt;</c></item>
    /// </list>
    /// A bare numeric first argument is treated as port (backwards compat).
    /// </summary>
    public static AppOptions Parse(string[] args)
    {
        var opts = new AppOptions();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--port" or "-p" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var port)) opts.Port = port;
                    break;

                case "--forward-to" or "-f" when i + 1 < args.Length:
                    opts.ForwardTo = args[++i].TrimEnd('/');
                    break;

                default:
                    // Backwards compat: first bare numeric arg = port
                    if (i == 0 && int.TryParse(args[i], out var legacyPort))
                        opts.Port = legacyPort;
                    break;
            }
        }

        return opts;
    }
}
