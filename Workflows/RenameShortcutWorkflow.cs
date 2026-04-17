using SteamGameCustomStatus.Infrastructure;
using SteamGameCustomStatus.Steam;
using SteamGameCustomStatus.UI.Dialogs;
using SteamGameCustomStatus.UI.Windows;
using Wpf = System.Windows;

namespace SteamGameCustomStatus.Workflows;

internal static class RenameShortcutWorkflow
{
    public static void Run(Wpf.Window? owner, bool suppressWindowForSilentResult = false)
    {
        var lookupResult = SteamShortcutRenamer.FindCurrentShortcut();
        if (!lookupResult.Success)
        {
            ShowMessage(owner, lookupResult.Message, isWarning: true);
            return;
        }

        var currentName = lookupResult.CurrentName ?? "Steam Game Custom Status";

        var dialog = new RenameDialog(currentName, owner);
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.ResultName))
        {
            return;
        }

        var newName = dialog.ResultName!.Trim();
        if (string.Equals(newName, currentName, StringComparison.Ordinal))
        {
            if (suppressWindowForSilentResult && owner is null)
            {
                return;
            }

            ShowMessage(owner, "The current name is the same");
            return;
        }

        var app = Wpf.Application.Current as App;
        var result = SteamRestartWorkflow.RunAfterRename(newName, app?.IsSteamLaunch ?? LaunchContextDetector.IsSteamLaunch());
        var isWarning = !result.IsSuccess || result.IsWarning;

        if (suppressWindowForSilentResult && owner is null && result.IsSuccess && !result.IsWarning)
        {
            if (result.ShouldExitApplication)
            {
                app?.ExitForSteamRelaunch();
            }

            return;
        }

        Action? onDismissed = result.ShouldExitApplication && app is not null
            ? () => app.ExitForSteamRelaunch()
            : null;

        ShowMessage(owner, result.Message, isWarning, onDismissed);
    }

    private static void ShowMessage(Wpf.Window? owner, string message, bool isWarning = false, Action? onDismissed = null)
    {
        if (owner is MainWindow mainWindow)
        {
            mainWindow.ShowInlineMessage(message, isWarning, onDismissed);
            return;
        }

        if (Wpf.Application.Current is App app)
        {
            app.ShowMainWindowInlineMessage(message, isWarning, onDismissed);
            return;
        }

        var image = isWarning ? Wpf.MessageBoxImage.Warning : Wpf.MessageBoxImage.Information;

        if (owner is not null)
        {
            Wpf.MessageBox.Show(owner, message, "Steam Game Custom Status", Wpf.MessageBoxButton.OK, image);
        }
        else
        {
            Wpf.MessageBox.Show(message, "Steam Game Custom Status", Wpf.MessageBoxButton.OK, image);
        }

        onDismissed?.Invoke();
    }
}
