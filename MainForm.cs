namespace CopilotUsageFilter;

/// <summary>
/// Hidden main form — just hosts the tray icon lifetime.
/// The window never becomes visible; the app lives in the system tray.
/// </summary>
public partial class MainForm : Form
{
    private readonly NotifyIcon _trayIcon;
    private readonly OtlpHttpReceiver _receiver;
    private readonly SpanProcessor _processor = new();
    private readonly int _port;

    public MainForm(int port = 4318)
    {
        _port = port;
        InitializeComponent();

        // Keep the form hidden
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        Opacity = 0;

        _trayIcon = BuildTrayIcon();
        _receiver = new OtlpHttpReceiver(_port, OnSpanReceived);
    }

    private async Task OnSpanReceived(string path, string json)
    {
        await Task.Run(() => _processor.Process(path, json));
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Visible = false;

        try
        {
            _receiver.Start();
            _trayIcon.Text = $"Copilot Usage Filter\nListening on :{_port}";
        }
        catch (Exception ex)
        {
            _trayIcon.Text = $"Copilot Usage Filter\nERROR: {ex.Message}";
            Console.Error.WriteLine($"[server] Failed to start: {ex.Message}");
            ShowBalloon("Start Error", ex.Message, ToolTipIcon.Error);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _receiver.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.OnFormClosing(e);
    }

    private NotifyIcon BuildTrayIcon()
    {
        var icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "Copilot Usage Filter (starting…)",
            ContextMenuStrip = BuildContextMenu(),
        };

        icon.DoubleClick += (_, _) => ShowStatus();
        return icon;
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        var statusItem = new ToolStripMenuItem($"Port: {_port}") { Enabled = false };
        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());

        // Run at Windows startup toggle
        var startupItem = new ToolStripMenuItem("Run at startup")
        {
            Checked = StartupManager.IsEnabled,
            CheckOnClick = false,
        };
        startupItem.Click += (_, _) =>
        {
            try
            {
                StartupManager.Toggle();
                startupItem.Checked = StartupManager.IsEnabled;
                ShowBalloon(
                    "Run at startup",
                    StartupManager.IsEnabled ? "Enabled — will start with Windows." : "Disabled.",
                    ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                ShowBalloon("Startup toggle failed", ex.Message, ToolTipIcon.Error);
            }
        };
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()));

        return menu;
    }

    private void ShowStatus()
    {
        ShowBalloon(
            "Copilot Usage Filter",
            $"Listening on http://localhost:{_port}/\nSession state: %USERPROFILE%\\.copilot\\session-state\\",
            ToolTipIcon.Info);
    }

    private void ShowBalloon(string title, string text, ToolTipIcon icon)
    {
        _trayIcon.BalloonTipTitle = title;
        _trayIcon.BalloonTipText = text;
        _trayIcon.BalloonTipIcon = icon;
        _trayIcon.ShowBalloonTip(3000);
    }

    private void ExitApp()
    {
        _receiver.Stop();
        _trayIcon.Visible = false;
        Application.Exit();
    }

    // Designer stub — no visual controls needed
    private void InitializeComponent()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1, 1);
        FormBorderStyle = FormBorderStyle.None;
        Name = "MainForm";
        Text = "Copilot Usage Filter";
        ResumeLayout(false);
    }
}
