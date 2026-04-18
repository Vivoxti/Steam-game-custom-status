using System.ComponentModel;
using SteamGameCustomStatus.Steam;
using SteamGameCustomStatus.Workflows;
using System.Windows.Input;
using Media = System.Windows.Media;
using Animation = System.Windows.Media.Animation;
using Threading = System.Windows.Threading;
using Wpf = System.Windows;

namespace SteamGameCustomStatus.UI.Windows;

public partial class MainWindow
{
    private static readonly TimeSpan InlineMessageDisplayDuration = TimeSpan.FromSeconds(4);
    private static readonly Wpf.Duration InlineMessageFadeInDuration = new(TimeSpan.FromMilliseconds(220));
    private static readonly Wpf.Duration InlineMessageFadeOutDuration = new(TimeSpan.FromMilliseconds(800));

    private bool _forceClose;
    private readonly Threading.DispatcherTimer _inlineMessageTimer;
    private readonly Queue<InlineMessageRequest> _inlineMessageQueue = new();
    private InlineMessageRequest? _activeInlineMessage;
    private bool _isInlineMessageHiding;

    public MainWindow()
    {
        InitializeComponent();

        _inlineMessageTimer = new Threading.DispatcherTimer
        {
            Interval = InlineMessageDisplayDuration
        };
        _inlineMessageTimer.Tick += InlineMessageTimer_Tick;

        RefreshSteamRegistrationStatus();
    }

    public void RefreshSteamRegistrationStatus()
    {
        var status = SteamShortcutRenamer.GetCurrentShortcutRegistrationStatus();
        if (status.IsRegistered)
        {
            RegisteredStatusDescription.Text = status.Description;
            RegisteredStatusCard.Visibility = Wpf.Visibility.Visible;
            RegisteredStatusCard.ToolTip = status.CurrentName;

            MissingStatusCard.Visibility = Wpf.Visibility.Collapsed;
            MissingStatusCard.ToolTip = null;
            MissingStatusDescription.Text = string.Empty;
            MissingStatusHint.Text = string.Empty;
            RegisteredActionsPanel.Visibility = Wpf.Visibility.Visible;
            MissingActionsPanel.Visibility = Wpf.Visibility.Collapsed;
            OpenSteamAddGameButton.ToolTip = null;

            var app = Wpf.Application.Current as App;
            var shouldShowLaunchViaSteamAction = app?.ShouldShowLaunchViaSteamAction(status.IsRegistered) == true;
            LaunchViaSteamButton.Visibility = shouldShowLaunchViaSteamAction
                ? Wpf.Visibility.Visible
                : Wpf.Visibility.Collapsed;

            var isActiveInSteam = app?.IsCurrentShortcutActiveForDisplay(status.IsRegistered) == true;
            UpdateSteamStatusIndicator(
                isActiveInSteam,
                isActiveInSteam
                    ? "Active in Steam: this app is currently being used as your Steam status."
                    : "Inactive in Steam: launch this app through Steam to use it as your Steam status.");
        }

        else
        {
            MissingStatusDescription.Text = status.Description;
            MissingStatusHint.Text = status.Hint ?? string.Empty;
            MissingStatusCard.ToolTip = status.Tooltip;
            MissingStatusCard.Visibility = Wpf.Visibility.Visible;
            OpenSteamAddGameButton.ToolTip = Environment.ProcessPath;
            MissingActionsPanel.Visibility = Wpf.Visibility.Visible;

            RegisteredStatusCard.Visibility = Wpf.Visibility.Collapsed;
            RegisteredStatusCard.ToolTip = null;
            RegisteredStatusDescription.Text = string.Empty;
            RegisteredActionsPanel.Visibility = Wpf.Visibility.Collapsed;

            UpdateSteamStatusIndicator(
                isActive: false,
                tooltip: "Inactive in Steam: this executable is not currently added to Steam as a non-Steam game.");
        }

        (Wpf.Application.Current as App)?.RefreshTrayMenuState(status.IsRegistered);
    }

    private void UpdateSteamStatusIndicator(bool isActive, string tooltip)
    {
        var fillResourceKey = isActive ? "SteamActiveIndicatorBrush" : "SteamInactiveIndicatorBrush";
        var borderResourceKey = isActive ? "SteamActiveIndicatorBorderBrush" : "SteamInactiveIndicatorBorderBrush";

        SteamStatusIndicator.Fill = (Media.Brush)FindResource(fillResourceKey);
        SteamStatusIndicator.Stroke = (Media.Brush)FindResource(borderResourceKey);
        SteamStatusIndicator.ToolTip = tooltip;
    }

    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    public void ShowRenameDialog()
    {
        RenameShortcutWorkflow.Run(this);
    }

    public void ShowInlineMessage(string message, bool isWarning = false, Action? onDismissed = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _inlineMessageQueue.Enqueue(new InlineMessageRequest(message, isWarning, onDismissed));

        if (_activeInlineMessage is null && !_isInlineMessageHiding)
        {
            ShowNextInlineMessage();
        }
    }

    public void HideInlineMessage()
    {
        if (_activeInlineMessage is null || _isInlineMessageHiding)
        {
            return;
        }

        HideActiveInlineMessage();
    }

    private void ShowNextInlineMessage()
    {
        if (_activeInlineMessage is not null || _isInlineMessageHiding || _inlineMessageQueue.Count == 0)
        {
            return;
        }

        _activeInlineMessage = _inlineMessageQueue.Dequeue();
        ResetInlineMessageTimer();
        StopInlineMessageAnimations();

        var backgroundResourceKey = _activeInlineMessage.IsWarning ? "WarningBg" : "SuccessBg";
        var borderResourceKey = _activeInlineMessage.IsWarning ? "WarningBorder" : "SuccessBorder";
        var foregroundResourceKey = _activeInlineMessage.IsWarning ? "WarningText" : "SuccessText";

        InlineMessageCard.Background = (Media.Brush)FindResource(backgroundResourceKey);
        InlineMessageCard.BorderBrush = (Media.Brush)FindResource(borderResourceKey);
        InlineMessageText.Foreground = (Media.Brush)FindResource(foregroundResourceKey);
        InlineMessageText.Text = _activeInlineMessage.Message;
        InlineMessageCard.ToolTip = _activeInlineMessage.Message;
        InlineMessageText.ToolTip = _activeInlineMessage.Message;
        InlineMessageCard.Opacity = 0;
        InlineMessageCard.Visibility = Wpf.Visibility.Visible;

        var fadeInAnimation = new Animation.DoubleAnimation(1, InlineMessageFadeInDuration)
        {
            EasingFunction = new Animation.CubicEase { EasingMode = Animation.EasingMode.EaseOut }
        };

        InlineMessageCard.BeginAnimation(Wpf.UIElement.OpacityProperty, fadeInAnimation);
        _inlineMessageTimer.Start();
    }

    private void HideActiveInlineMessage()
    {
        ResetInlineMessageTimer();

        if (_activeInlineMessage is null)
        {
            return;
        }

        _isInlineMessageHiding = true;

        if (InlineMessageCard.Visibility != Wpf.Visibility.Visible)
        {
            CompleteInlineMessageHide();
            return;
        }

        var currentOpacity = InlineMessageCard.Opacity;
        StopInlineMessageAnimations();
        InlineMessageCard.Opacity = currentOpacity;
        var fadeOutAnimation = new Animation.DoubleAnimation(0, InlineMessageFadeOutDuration)
        {
            EasingFunction = new Animation.SineEase { EasingMode = Animation.EasingMode.EaseInOut }
        };

        fadeOutAnimation.Completed += (_, _) => CompleteInlineMessageHide();

        InlineMessageCard.BeginAnimation(Wpf.UIElement.OpacityProperty, fadeOutAnimation);
    }

    private void CompleteInlineMessageHide()
    {
        StopInlineMessageAnimations();
        InlineMessageText.Text = string.Empty;
        InlineMessageText.ToolTip = null;
        InlineMessageCard.Opacity = 0;
        InlineMessageCard.ToolTip = null;
        InlineMessageCard.Visibility = Wpf.Visibility.Collapsed;

        var dismissedAction = _activeInlineMessage?.OnDismissed;
        _activeInlineMessage = null;
        _isInlineMessageHiding = false;

        dismissedAction?.Invoke();

        if ((Wpf.Application.Current as App)?.IsExiting == true)
        {
            return;
        }

        ShowNextInlineMessage();
    }

    private void InlineMessageTimer_Tick(object? sender, EventArgs e)
    {
        HideInlineMessage();
    }

    private void ResetInlineMessageTimer()
    {
        _inlineMessageTimer.Stop();
    }

    private void StopInlineMessageAnimations()
    {
        InlineMessageCard.BeginAnimation(Wpf.UIElement.OpacityProperty, null);
    }

    private sealed record InlineMessageRequest(string Message, bool IsWarning, Action? OnDismissed);

    private void RenameButton_Click(object sender, Wpf.RoutedEventArgs e)
    {
        ShowRenameDialog();
        RefreshSteamRegistrationStatus();
    }

    private void CreateDesktopShortcut_Click(object sender, Wpf.RoutedEventArgs e)
    {
        DesktopShortcutWorkflow.Run(this);
        RefreshSteamRegistrationStatus();
    }

    private void OpenSteamAddGame_Click(object sender, Wpf.RoutedEventArgs e)
    {
        OpenSteamAddGameWorkflow.Run(this);
    }

    private void LaunchViaSteam_Click(object sender, Wpf.RoutedEventArgs e)
    {
        (Wpf.Application.Current as App)?.RunLaunchViaSteamWorkflow();
    }

    private void Hide_Click(object sender, Wpf.RoutedEventArgs e)
    {
        Hide();
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_forceClose || (Wpf.Application.Current as App)?.IsExiting == true)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        Hide();
    }
}
