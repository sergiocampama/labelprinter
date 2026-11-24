namespace LabelPrinter;

/// <summary>
/// The tray icon and app lifetime. The window is created on first use and only
/// hidden when closed, so it keeps its state; "Salir" ends the app.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(6);

    private readonly NotifyIcon _tray;
    private readonly SynchronizationContext _ui;
    private readonly RegisteredWaitHandle _showWait;
    private readonly System.Windows.Forms.Timer _updateTimer;
    private MainForm? _window;

    public TrayApp(EventWaitHandle showEvent, bool openWindow)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        var menu = new ContextMenuStrip();
        menu.Items.Add($"Abrir {AppInfo.Name}", null, (_, _) => ShowWindow());
        menu.Items.Add("Buscar actualizaciones", null, async (_, _) => await CheckForUpdatesAsync(userInitiated: true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Salir", null, (_, _) => Exit());

        _tray = new NotifyIcon
        {
            Icon = AppInfo.LoadIcon(SystemInformation.SmallIconSize),
            Text = AppInfo.Name,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };

        // Remove the icon even on hard exits (update restarts) to avoid ghost icons.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { _tray.Visible = false; _tray.Dispose(); } catch { /* ignore */ }
        };

        // A second launch signals this event; open the window on the UI thread.
        _showWait = ThreadPool.RegisterWaitForSingleObject(showEvent,
            (_, _) => _ui.Post(_ => ShowWindow(), null), null, Timeout.Infinite, executeOnlyOnce: false);

        // Look for updates shortly after start, then every few hours.
        _updateTimer = new System.Windows.Forms.Timer { Interval = 15_000 };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = (int)UpdateInterval.TotalMilliseconds;
            await CheckForUpdatesAsync(userInitiated: false);
        };
        _updateTimer.Start();

        if (openWindow)
            _ui.Post(_ => ShowWindow(), null);
    }

    private void ShowWindow()
    {
        if (_window is null || _window.IsDisposed)
            _window = new MainForm();

        _window.Show();
        if (_window.WindowState == FormWindowState.Minimized)
            _window.WindowState = FormWindowState.Normal;
        _window.Activate();
        _window.BringToFront();
    }

    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        // Don't restart out from under someone who has the window open; the
        // next background check retries.
        bool canRestart = userInitiated || _window is null || !_window.Visible;
        await UpdateService.CheckAsync(userInitiated, canRestart,
            msg => _ui.Post(_ => _tray.ShowBalloonTip(5000, AppInfo.Name, msg, ToolTipIcon.Info), null));
    }

    private void Exit()
    {
        _updateTimer.Stop();
        _showWait.Unregister(null);
        _tray.Visible = false;
        _tray.Dispose();
        _window?.Dispose();
        ExitThread();
    }
}
