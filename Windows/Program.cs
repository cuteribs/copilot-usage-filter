using System.Runtime.InteropServices;
using CopilotUsageFilter;

// Attach to parent console (terminal) or allocate a new one,
// then re-bind Console.Out/Error so WinExe actually writes to the shell.
if (!AttachConsole(0xFFFFFFFF))
    AllocConsole();

// Re-open the standard streams so they point at the (now live) console.
// Force UTF-8 so Unicode characters (ellipsis, etc.) render correctly.
Console.OutputEncoding = System.Text.Encoding.UTF8;
var stdout = new StreamWriter(Console.OpenStandardOutput(), System.Text.Encoding.UTF8) { AutoFlush = true };
var stderr = new StreamWriter(Console.OpenStandardError(),  System.Text.Encoding.UTF8) { AutoFlush = true };
Console.SetOut(stdout);
Console.SetError(stderr);

ApplicationConfiguration.Initialize();

if (AppOptions.IsHelp(args))
{
    AppOptions.PrintHelp("CopilotUsageFilter");
    return;
}

var opts = AppOptions.Parse(args);

Application.Run(new MainForm(opts));

[DllImport("kernel32.dll")] static extern bool AllocConsole();
[DllImport("kernel32.dll")] static extern bool AttachConsole(uint pid);
