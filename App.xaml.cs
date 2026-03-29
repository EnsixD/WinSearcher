using System.Windows;
using System.Drawing;
using System.Windows.Forms;
using WinSearcher.Services;

namespace WinSearcher;

public partial class App : System.Windows.Application
{
    private NotifyIcon? _trayIcon;
    private GlobalHotkey? _hotkey;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mainWindow = new MainWindow();

        SetupTrayIcon();
        RegisterHotkey();
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new NotifyIcon
        {
            Text = "WinSearcher (Win+Q)",
            Visible = true,
            Icon = SystemIcons.Application
        };

        var contextMenu = new ContextMenuStrip();

        var openItem = new ToolStripMenuItem("Open Search (Win+Q)");
        openItem.Click += (_, _) => ShowMainWindow();
        contextMenu.Items.Add(openItem);

        contextMenu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApplication();
        contextMenu.Items.Add(exitItem);

        _trayIcon.ContextMenuStrip = contextMenu;
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    private void RegisterHotkey()
    {
        // Low-level keyboard hook — intercepts Win+Q before the OS can handle it
        _hotkey = new GlobalHotkey(Dispatcher);
        _hotkey.HotkeyPressed += OnHotkeyPressed;
    }

    private void OnHotkeyPressed()
    {
        // Toggle: Win+Q opens the window; Win+Q again closes it
        if (_mainWindow != null && _mainWindow.IsVisible)
            _mainWindow.HideWindow();
        else
            ShowMainWindow();
    }

    public void ShowMainWindow()
    {
        if (_mainWindow == null)
        {
            _mainWindow = new MainWindow();
        }

        _mainWindow.ShowAndActivate();
    }

    private void ExitApplication()
    {
        _hotkey?.Unregister(1);
        _hotkey?.Dispose();
        _trayIcon?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkey?.Unregister(1);
        _hotkey?.Dispose();
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
