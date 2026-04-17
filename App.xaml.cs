using System;
using System.Diagnostics;
using System.Reflection;
using SteamGameCustomStatus.Infrastructure;
using SteamGameCustomStatus.Steam;
using SteamGameCustomStatus.UI.Windows;
using SteamGameCustomStatus.Workflows;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Threading = System.Windows.Threading;
using Wpf = System.Windows;

namespace SteamGameCustomStatus;

public partial class App : Wpf.Application
{
    private static readonly TimeSpan ActiveTrayRefreshInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SteamLaunchActiveGracePeriod = TimeSpan.FromMinutes(2);
    private const string ActiveTrayIconResourceName = "SteamGameCustomStatus.Assets.Icon.ico";
    private const string InactiveTrayIconResourceName = "SteamGameCustomStatus.Assets.IconInactive.ico";

    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ToolStripMenuItem? _renameMenuItem;
    private Forms.ToolStripMenuItem? _createDesktopShortcutMenuItem;
    private Forms.ToolStripMenuItem? _launchViaSteamMenuItem;
    private Forms.ToolStripSeparator? _actionsSeparator;
    private MainWindow? _mainWindow;
    private SingleInstanceCoordinator? _singleInstanceCoordinator;
    private Threading.DispatcherTimer? _activeTrayRefreshTimer;
    private Drawing.Icon? _activeTrayIcon;
    private Drawing.Icon? _inactiveTrayIcon;
    private DateTime? _steamLaunchActiveGraceDeadlineUtc;
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
        _steamLaunchActiveGraceDeadlineUtc = _isSteamLaunch
            ? DateTime.UtcNow.Add(SteamLaunchActiveGracePeriod)
            : null;
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
        if (_activeTrayRefreshTimer is not null)
        {
            _activeTrayRefreshTimer.Stop();
            _activeTrayRefreshTimer.Tick -= ActiveTrayRefreshTimer_Tick;
            _activeTrayRefreshTimer = null;
        }

        _singleInstanceCoordinator?.Dispose();
        _singleInstanceCoordinator = null;

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _activeTrayIcon?.Dispose();
        _activeTrayIcon = null;

        _inactiveTrayIcon?.Dispose();
        _inactiveTrayIcon = null;

        base.OnExit(e);
    }

    private void InitializeTrayIcon()
    {
        _activeTrayIcon = LoadEmbeddedIcon(ActiveTrayIconResourceName) ?? GetFallbackProcessIcon();
        _inactiveTrayIcon = LoadEmbeddedIcon(InactiveTrayIconResourceName) ?? (Drawing.Icon)_activeTrayIcon.Clone();
        _activeTrayRefreshTimer = new Threading.DispatcherTimer
        {
            Interval = ActiveTrayRefreshInterval
        };
        _activeTrayRefreshTimer.Tick += ActiveTrayRefreshTimer_Tick;

        var contextMenu = new Forms.ContextMenuStrip();
        contextMenu.Items.Add("Open", null, (_, _) => ShowMainWindow());
        _renameMenuItem = new Forms.ToolStripMenuItem("Rename", null, (_, _) => RunRenameWorkflow());
        _createDesktopShortcutMenuItem = new Forms.ToolStripMenuItem(
            "Create Desktop Shortcut",
            null,
            (_, _) => RunCreateDesktopShortcutWorkflow());
        _launchViaSteamMenuItem = new Forms.ToolStripMenuItem(
            "Launch via Steam",
            null,
            (_, _) => RunLaunchViaSteamWorkflow());
        _actionsSeparator = new Forms.ToolStripSeparator();
        contextMenu.Items.Add(_renameMenuItem);
        contextMenu.Items.Add(_createDesktopShortcutMenuItem);
        contextMenu.Items.Add(_launchViaSteamMenuItem);
        contextMenu.Items.Add(_actionsSeparator);
        contextMenu.Items.Add("Exit", null, (_, _) => ExitApplication());
        contextMenu.Opening += (_, _) => RefreshTrayMenuState();

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _inactiveTrayIcon,
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

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_mainWindow is null || !_mainWindow.IsVisible)
            {
                return;
            }

            _mainWindow.RefreshSteamRegistrationStatus();
        }));
    }

    private void RunRenameWorkflow()
    {
        var visibleOwner = _mainWindow is { IsVisible: true, WindowState: not Wpf.WindowState.Minimized }
            ? _mainWindow
            : null;

        var suppressWindowForSilentResult = visibleOwner is null;
        RenameShortcutWorkflow.Run(visibleOwner, suppressWindowForSilentResult);
        RefreshTrayMenuState();
    }

    private void RunCreateDesktopShortcutWorkflow()
    {
        var result = SteamShortcutRenamer.CreateDesktopShortcutForCurrentShortcut();
        if (!result.Success)
        {
            ShowMainWindowInlineMessage(result.Message, isWarning: true);
        }

        RefreshTrayMenuState();
    }

    internal void RunLaunchViaSteamWorkflow()
    {
        var shortcutInfoResult = SteamShortcutRenamer.GetCurrentShortcutInfoForLaunch();
        if (!shortcutInfoResult.Success || shortcutInfoResult.ShortcutInfo is null)
        {
            ShowMainWindowInlineMessage(shortcutInfoResult.Message, isWarning: true);
            RefreshTrayMenuState();
            return;
        }

        var runGameId = shortcutInfoResult.ShortcutInfo.RunGameId;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"steam://rungameid/{runGameId}",
                UseShellExecute = true
            });
        }
        catch
        {
            ShowMainWindowInlineMessage("Failed to launch via Steam.", isWarning: true);
            RefreshTrayMenuState();
            return;
        }

        ExitForSteamRelaunch();
    }

    internal void ShowMainWindowInlineMessage(string message, bool isWarning = false, Action? onDismissed = null)
    {
        ShowMainWindow();
        _mainWindow?.ShowInlineMessage(message, isWarning, onDismissed);
    }

    public void RefreshTrayMenuState(bool? isRegisteredOverride = null)
    {
        var isRegistered = isRegisteredOverride ?? SteamShortcutRenamer.GetCurrentShortcutRegistrationStatus().IsRegistered;

        if (_renameMenuItem is not null &&
            _createDesktopShortcutMenuItem is not null &&
            _launchViaSteamMenuItem is not null &&
            _actionsSeparator is not null)
        {
            var showLaunchViaSteamAction = ShouldShowLaunchViaSteamAction(isRegistered);
            _renameMenuItem.Visible = isRegistered;
            _createDesktopShortcutMenuItem.Visible = isRegistered;
            _launchViaSteamMenuItem.Visible = showLaunchViaSteamAction;
            _actionsSeparator.Visible = isRegistered;
        }

        RefreshTrayVisualState(isRegistered);
    }

    internal bool ShouldShowLaunchViaSteamAction(bool isRegistered)
    {
        return isRegistered && !_isSteamLaunch;
    }

    internal bool IsCurrentShortcutActiveForDisplay(bool isRegistered)
    {
        if (!isRegistered)
        {
            return false;
        }

        var shortcutInfoResult = SteamShortcutRenamer.GetCurrentShortcutInfoForLaunch();
        if (!shortcutInfoResult.Success || shortcutInfoResult.ShortcutInfo is null)
        {
            return false;
        }

        var runningAppId = SteamShortcutRenamer.GetRunningSteamAppId();
        if (runningAppId != 0 && runningAppId == shortcutInfoResult.ShortcutInfo.AppId)
        {
            return true;
        }

        return _isSteamLaunch &&
               _steamLaunchActiveGraceDeadlineUtc is { } graceDeadline &&
               DateTime.UtcNow <= graceDeadline;
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

    private void RefreshTrayVisualState(bool isRegistered)
    {
        if (_notifyIcon is null)
        {
            return;
        }

        var isActive = IsCurrentShortcutActiveForDisplay(isRegistered);
        var desiredIcon = isActive ? _activeTrayIcon : _inactiveTrayIcon;
        if (desiredIcon is not null && !ReferenceEquals(_notifyIcon.Icon, desiredIcon))
        {
            _notifyIcon.Icon = desiredIcon;
        }

        if (_activeTrayRefreshTimer is not null)
        {
            if (isActive)
            {
                if (!_activeTrayRefreshTimer.IsEnabled)
                {
                    _activeTrayRefreshTimer.Start();
                }
            }
            else if (_activeTrayRefreshTimer.IsEnabled)
            {
                _activeTrayRefreshTimer.Stop();
            }
        }
    }

    private void ActiveTrayRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_mainWindow is { IsVisible: true })
        {
            _mainWindow.RefreshSteamRegistrationStatus();
            return;
        }

        RefreshTrayMenuState();
    }

    private static Drawing.Icon? LoadEmbeddedIcon(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        using var icon = new Drawing.Icon(stream);
        return (Drawing.Icon)icon.Clone();
    }

    private static Drawing.Icon GetFallbackProcessIcon()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            using var extractedIcon = Drawing.Icon.ExtractAssociatedIcon(processPath);
            if (extractedIcon is not null)
            {
                return (Drawing.Icon)extractedIcon.Clone();
            }
        }

        return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
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
