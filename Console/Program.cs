using CopilotUsageFilter;

// Cross-platform console entry point.
// No GUI — runs until Ctrl+C (SIGINT) or SIGTERM.

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding  = System.Text.Encoding.UTF8;

var opts = AppOptions.Parse(args);

var processor = new SpanProcessor();
var receiver  = new OtlpHttpReceiver(opts,
    async (path, json) => await Task.Run(() => processor.Process(path, json)));

using var cts = new CancellationTokenSource();

// Ctrl+C on all platforms
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
// SIGTERM on Linux / macOS (process manager, Docker, etc.)
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

try
{
    receiver.Start();
    var url = $"http://localhost:{opts.Port}";

    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.Write(DateTime.Now.ToString("s"));
    Console.ResetColor();
    Console.Write('\t');
    Console.ForegroundColor = ConsoleColor.Green;
    Console.Write("Listening");
    Console.ResetColor();
    Console.Write('\t');
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine(url);
    Console.ResetColor();

    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.Write(DateTime.Now.ToString("s"));
    Console.ResetColor();
    Console.Write("\texporting traces\t");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine(TraceFileExporter.FilePath);
    Console.ResetColor();

    if (opts.ForwardTo != null)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(DateTime.Now.ToString("s"));
        Console.ResetColor();
        Console.Write("\tforwarding to\t");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(opts.ForwardTo);
        Console.ResetColor();
    }

    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException) { /* normal Ctrl+C / SIGTERM exit */ }
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"{DateTime.Now:s}\tERROR\t{ex.Message}");
    Console.ResetColor();
}
finally
{
    receiver.Stop();
    TraceFileExporter.Shutdown();
}
