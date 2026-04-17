using System;
using System.ComponentModel;
using SteamGameCustomStatus.Steam;
using SteamGameCustomStatus.Workflows;
using System.Windows.Input;
using Media = System.Windows.Media;
using Animation = System.Windows.Media.Animation;
using Threading = System.Windows.Threading;
using Wpf = System.Windows;

namespace SteamGameCustomStatus.UI.Windows;

public partial class MainWindow : Wpf.Window
{
    private static readonly TimeSpan InlineMessageDisplayDuration = TimeSpan.FromSeconds(4);
    private static readonly Wpf.Duration InlineMessageFadeInDuration = new(TimeSpan.FromMilliseconds(220));
    private static readonly Wpf.Duration InlineMessageFadeOutDuration = new(TimeSpan.FromMilliseconds(800));

    private bool _forceClose;
    private readonly Threading.DispatcherTimer _inlineMessageTimer;
    private int _inlineMessageVersion;

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
        }

        (Wpf.Application.Current as App)?.RefreshTrayMenuState();
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

    public void ShowInlineMessage(string message, bool isWarning = false)
    {
        var backgroundResourceKey = isWarning ? "WarningBg" : "SuccessBg";
        var borderResourceKey = isWarning ? "WarningBorder" : "SuccessBorder";
        var foregroundResourceKey = isWarning ? "WarningText" : "SuccessText";

        _inlineMessageVersion++;
        ResetInlineMessageTimer();
        StopInlineMessageAnimations();

        InlineMessageCard.Background = (Media.Brush)FindResource(backgroundResourceKey);
        InlineMessageCard.BorderBrush = (Media.Brush)FindResource(borderResourceKey);
        InlineMessageText.Foreground = (Media.Brush)FindResource(foregroundResourceKey);
        InlineMessageText.Text = message;
        InlineMessageCard.Opacity = 0;
        InlineMessageCard.Visibility = Wpf.Visibility.Visible;

        var fadeInAnimation = new Animation.DoubleAnimation(1, InlineMessageFadeInDuration)
        {
            EasingFunction = new Animation.CubicEase { EasingMode = Animation.EasingMode.EaseOut }
        };

        InlineMessageCard.BeginAnimation(Wpf.UIElement.OpacityProperty, fadeInAnimation);
        _inlineMessageTimer.Start();
    }

    public void HideInlineMessage()
    {
        var expectedVersion = _inlineMessageVersion;
        HideInlineMessage(expectedVersion);
    }

    private void HideInlineMessage(int expectedVersion)
    {
        ResetInlineMessageTimer();

        if (InlineMessageCard.Visibility != Wpf.Visibility.Visible)
        {
            InlineMessageText.Text = string.Empty;
            InlineMessageCard.Opacity = 0;
            InlineMessageCard.Visibility = Wpf.Visibility.Collapsed;
            return;
        }


        var fadeOutAnimation = new Animation.DoubleAnimation(0, InlineMessageFadeOutDuration)
        {
            EasingFunction = new Animation.SineEase { EasingMode = Animation.EasingMode.EaseInOut }
        };

        fadeOutAnimation.Completed += (_, _) =>
        {
            if (expectedVersion != _inlineMessageVersion)
            {
                return;
            }

            StopInlineMessageAnimations();
            InlineMessageText.Text = string.Empty;
            InlineMessageCard.Opacity = 0;
            InlineMessageCard.Visibility = Wpf.Visibility.Collapsed;
        };

        InlineMessageCard.BeginAnimation(Wpf.UIElement.OpacityProperty, fadeOutAnimation);
    }

    private void InlineMessageTimer_Tick(object? sender, EventArgs e)
    {
        var expectedVersion = _inlineMessageVersion;
        HideInlineMessage(expectedVersion);
    }

    private void ResetInlineMessageTimer()
    {
        _inlineMessageTimer.Stop();
    }

    private void StopInlineMessageAnimations()
    {
        InlineMessageCard.BeginAnimation(Wpf.UIElement.OpacityProperty, null);
    }

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
