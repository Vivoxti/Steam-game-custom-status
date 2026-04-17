using Microsoft.VisualBasic;
using Wpf = System.Windows;

namespace SteamGameCustomStatus;

internal static class RenameShortcutWorkflow
{
    public static void Run(Wpf.Window? owner)
    {
        var lookupResult = SteamShortcutRenamer.FindCurrentShortcut();
        if (!lookupResult.Success)
        {
            ShowMessage(owner, lookupResult.Message, Wpf.MessageBoxImage.Warning);
            return;
        }

        var currentName = lookupResult.CurrentName ?? "Steam Game Custom Status";
        var newName = Interaction.InputBox(
            "Введите новое отображаемое имя non-Steam игры в Steam.",
            "Steam Game Custom Status",
            currentName);

        if (string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        newName = newName.Trim();
        if (string.Equals(newName, currentName, StringComparison.Ordinal))
        {
            ShowMessage(owner, "Название не изменилось.", Wpf.MessageBoxImage.Information);
            return;
        }

        var app = Wpf.Application.Current as App;
        var result = SteamRestartWorkflow.RunAfterRename(newName, app?.IsSteamLaunch ?? LaunchContextDetector.IsSteamLaunch());
        var image = !result.IsSuccess || result.IsWarning
            ? Wpf.MessageBoxImage.Warning
            : Wpf.MessageBoxImage.Information;

        ShowMessage(owner, result.Message, image);

        if (result.ShouldExitApplication)
        {
            app?.ExitForSteamRelaunch();
        }
    }

    private static void ShowMessage(Wpf.Window? owner, string message, Wpf.MessageBoxImage image)
    {
        if (owner is not null)
        {
            Wpf.MessageBox.Show(owner, message, "Steam Game Custom Status", Wpf.MessageBoxButton.OK, image);
            return;
        }

        Wpf.MessageBox.Show(message, "Steam Game Custom Status", Wpf.MessageBoxButton.OK, image);
    }
}
