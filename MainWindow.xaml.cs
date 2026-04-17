using System.ComponentModel;
using System.Windows.Input;
using Wpf = System.Windows;

namespace SteamGameCustomStatus;

public partial class MainWindow : Wpf.Window
{
    private bool _forceClose;

    public MainWindow()
    {
        InitializeComponent();
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
