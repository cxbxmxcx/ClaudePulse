using System.Drawing;
using System.Drawing.Drawing2D;
using WinForms = System.Windows.Forms;

namespace ClaudePulse;

public partial class App : System.Windows.Application
{
    private WinForms.NotifyIcon? _tray;
    private MainWindow? _window;
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;
    private Icon? _iconIdle, _iconBusy, _iconNone;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single instance: a second launch just tells the first one to show.
        _instanceMutex = new Mutex(true, "ClaudePulse_Instance", out bool isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "ClaudePulse_Show");
        if (!isFirst)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }
        var waitThread = new Thread(() =>
        {
            while (_showSignal.WaitOne())
                Dispatcher.BeginInvoke(() => _window?.ShowPanel());
        }) { IsBackground = true };
        waitThread.Start();

        _window = new MainWindow();

        _iconIdle = CreateTrayIcon(Color.FromArgb(51, 209, 122));   // green: sessions, none busy
        _iconBusy = CreateTrayIcon(Color.FromArgb(229, 165, 10));   // amber: at least one busy
        _iconNone = CreateTrayIcon(Color.FromArgb(120, 120, 140));  // gray: no sessions

        _tray = new WinForms.NotifyIcon
        {
            Icon = _iconNone,
            Text = "ClaudePulse — Claude Code sessions (Ctrl+Alt+C)",
            Visible = true,
        };
        _window.SessionsRefreshed += (total, busy) =>
        {
            _tray.Icon = busy > 0 ? _iconBusy : total > 0 ? _iconIdle : _iconNone;
            _tray.Text = total == 0
                ? "ClaudePulse — no Claude Code sessions"
                : $"ClaudePulse — {total} session{(total == 1 ? "" : "s")}, {busy} busy";
        };
        _tray.MouseUp += (_, a) =>
        {
            if (a.Button == WinForms.MouseButtons.Left) _window.Toggle();
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Show / Hide  (Ctrl+Alt+C)", null, (_, _) => _window.Toggle());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Remember current sessions", null, (_, _) => _window.SaveSessionSet());
        menu.Items.Add("Relaunch saved sessions", null, (_, _) => _window.LaunchSessionSet());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;

        _window.ShowPanel();
    }

    private void ExitApp()
    {
        if (_tray is not null) _tray.Visible = false;
        _tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        if (_tray is not null) _tray.Visible = false;
        _tray?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Draws the tray icon in code so no .ico asset needs shipping.</summary>
    private static Icon CreateTrayIcon(Color dotColor)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var ring = new Pen(Color.FromArgb(236, 236, 241), 3.5f);
            g.DrawEllipse(ring, 4, 4, 24, 24);
            using var dot = new SolidBrush(dotColor);
            g.FillEllipse(dot, 11, 11, 10, 10);
        }
        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
