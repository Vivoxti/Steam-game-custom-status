using System;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;

namespace SteamGameCustomStatus;

public partial class App : Wpf.Application
{
    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ToolStripMenuItem? _renameMenuItem;
    private Forms.ToolStripMenuItem? _createDesktopShortcutMenuItem;
    private Forms.ToolStripSeparator? _actionsSeparator;
    private MainWindow? _mainWindow;
    private SingleInstanceCoordinator? _singleInstanceCoordinator;
    private bool _isSteamLaunch;
    private bool _isExiting;

    protected override void OnStartup(Wpf.StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown;

        if (SteamRestartWorkflow.TryHandleHelperLaunch(e.Args))
        {
            Shutdown();
            return;
        }

        _isSteamLaunch = LaunchContextDetector.IsSteamLaunch();
        _singleInstanceCoordinator = new SingleInstanceCoordinator(HandleInstanceStartupRequest);

        var startupResult = _singleInstanceCoordinator.RegisterCurrentInstance(_isSteamLaunch);
        if (!startupResult.ShouldContinueAsPrimary)
        {
            Shutdown();
            return;
        }

        InitializeTrayIcon();
    }

    protected override void OnExit(Wpf.ExitEventArgs e)
    {
        _singleInstanceCoordinator?.Dispose();
        _singleInstanceCoordinator = null;

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        base.OnExit(e);
    }

    private void InitializeTrayIcon()
    {
        var contextMenu = new Forms.ContextMenuStrip();
        contextMenu.Items.Add("Open", null, (_, _) => ShowMainWindow());
        _renameMenuItem = new Forms.ToolStripMenuItem("Rename", null, (_, _) => RunRenameWorkflow());
        _createDesktopShortcutMenuItem = new Forms.ToolStripMenuItem(
            "Create Desktop Shortcut",
            null,
            (_, _) => RunCreateDesktopShortcutWorkflow());
        _actionsSeparator = new Forms.ToolStripSeparator();
        contextMenu.Items.Add(_renameMenuItem);
        contextMenu.Items.Add(_createDesktopShortcutMenuItem);
        contextMenu.Items.Add(_actionsSeparator);
        contextMenu.Items.Add("Exit", null, (_, _) => ExitApplication());
        contextMenu.Opening += (_, _) => RefreshTrayMenuState();

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = GetTrayIcon(),
            Text = "Steam Game Custom Status",
            Visible = true,
            ContextMenuStrip = contextMenu
        };

        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();
        RefreshTrayMenuState();
    }

    private void ShowMainWindow()
    {
        _mainWindow ??= new MainWindow();
        _mainWindow.RefreshSteamRegistrationStatus();
        RefreshTrayMenuState();

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        if (_mainWindow.WindowState == Wpf.WindowState.Minimized)
        {
            _mainWindow.WindowState = Wpf.WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    private void RunRenameWorkflow()
    {
        _mainWindow ??= new MainWindow();
        RenameShortcutWorkflow.Run(_mainWindow);
        RefreshTrayMenuState();
    }

    private void RunCreateDesktopShortcutWorkflow()
    {
        var result = SteamShortcutRenamer.CreateDesktopShortcutForCurrentShortcut();
        if (!result.Success)
        {
            ShowMainWindow();
            _mainWindow?.ShowInlineMessage(result.Message, isWarning: true);
        }

        RefreshTrayMenuState();
    }

    public void RefreshTrayMenuState()
    {
        if (_renameMenuItem is null || _createDesktopShortcutMenuItem is null || _actionsSeparator is null)
        {
            return;
        }

        var status = SteamShortcutRenamer.GetCurrentShortcutRegistrationStatus();
        _renameMenuItem.Visible = status.IsRegistered;
        _createDesktopShortcutMenuItem.Visible = status.IsRegistered;
        _actionsSeparator.Visible = status.IsRegistered;
    }

    private InstanceStartupResponse HandleInstanceStartupRequest(InstanceStartupRequest request)
    {
        var shouldKeepCurrentInstance = _isSteamLaunch && !request.IsSteamLaunch;
        if (shouldKeepCurrentInstance)
        {
            Dispatcher.BeginInvoke(new Action(ShowMainWindow));
            return new InstanceStartupResponse(InstanceTransferAction.ActivateExistingInstance);
        }

        Dispatcher.BeginInvoke(new Action(ExitApplication));
        return new InstanceStartupResponse(InstanceTransferAction.YieldToNewInstance);
    }

    private static Drawing.Icon GetTrayIcon()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var extractedIcon = Drawing.Icon.ExtractAssociatedIcon(processPath);
            if (extractedIcon is not null)
            {
                return extractedIcon;
            }
        }

        return Drawing.SystemIcons.Application;
    }

    private void ExitApplication()
    {
        _isExiting = true;
        _mainWindow?.ForceClose();
        Shutdown();
    }

    public bool IsExiting => _isExiting;

    internal bool IsSteamLaunch => _isSteamLaunch;

    internal void ExitForSteamRelaunch()
    {
        ExitApplication();
    }
}
